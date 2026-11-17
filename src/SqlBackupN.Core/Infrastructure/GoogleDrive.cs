using System.Security.Cryptography;
using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Upload;
using Google.Apis.Util.Store;
using SqlBackupN.Core.Abstractions;
using SqlBackupN.Core.Domain;
using SqlBackupN.Core.Services;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace SqlBackupN.Core.Infrastructure;

/// <summary>
/// Destino remoto en Google Drive mediante su API oficial. No usa la unidad virtual <c>G:\</c>, así que
/// funciona desde un servicio de Windows sin sesión de usuario. Permiso <c>drive.file</c>: solo ve lo que crea.
/// </summary>
public sealed class GoogleDriveStorage(ISecretProtector protector) : IRemoteStorage
{
    private const string FolderMime = "application/vnd.google-apps.folder";

    /// <summary>Tamaño de cada trozo de la subida reanudable (debe ser múltiplo de 256 KB).</summary>
    private const int ChunkSize = 8 * 1024 * 1024;

    /// <inheritdoc />
    public async Task UploadAsync(
        DestinationConfig destination, string localFilePath, string relativePath, CancellationToken ct)
    {
        try
        {
            var drive = Require(destination);
            using var service = CreateService(drive);

            var (folders, fileName) = DrivePath.SplitFile(relativePath);
            var parentId = (await FindOrCreateFolderAsync(
                service, DrivePath.Split(drive.FolderName).Concat(folders), create: true, ct))!;

            var localMd5 = await Md5Async(localFilePath, ct);

            await using var stream = new FileStream(
                localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            var request = service.Files.Create(
                new DriveFile { Name = fileName, Parents = [parentId] }, stream, "application/octet-stream");
            request.Fields = "id,size,md5Checksum";
            request.ChunkSize = ChunkSize;

            var progress = await request.UploadAsync(ct);
            if (progress.Status != UploadStatus.Completed)
                throw progress.Exception ?? new IOException("La subida a Google Drive no se completó.");

            var uploaded = request.ResponseBody
                           ?? throw new IOException("Google Drive no confirmó el archivo subido.");
            if (!string.Equals(uploaded.Md5Checksum, localMd5, StringComparison.OrdinalIgnoreCase))
            {
                // Una copia corrupta es peor que ninguna: se descarta para no darla por buena.
                try { await service.Files.Delete(uploaded.Id).ExecuteAsync(ct); }
                catch (Exception) { /* el error principal es el de integridad */ }
                throw new IOException(
                    "Google Drive recibió un archivo distinto del original (la suma MD5 no coincide); se descartó la copia.");
            }
        }
        catch (Exception ex) when (ex is TokenResponseException or GoogleApiException)
        {
            throw Friendly(ex);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RemoteFile>> ListAsync(
        DestinationConfig destination, string relativeDirectory, string extension, CancellationToken ct)
    {
        try
        {
            var drive = Require(destination);
            using var service = CreateService(drive);

            var folderId = await FindOrCreateFolderAsync(
                service, DrivePath.Split(drive.FolderName).Concat(DrivePath.Split(relativeDirectory)), create: false, ct);
            if (folderId is null) return [];

            var files = new List<RemoteFile>();
            string? pageToken = null;
            do
            {
                var list = service.Files.List();
                list.Q = $"'{folderId}' in parents and trashed=false and mimeType!='{FolderMime}'";
                list.Fields = "nextPageToken,files(id,name,size,createdTime)";
                list.PageSize = 1000;
                list.PageToken = pageToken;

                var page = await list.ExecuteAsync(ct);
                foreach (var f in page.Files.Where(f => f.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
                {
                    files.Add(new RemoteFile(
                        f.Id, f.Name, (f.CreatedTimeDateTimeOffset ?? DateTimeOffset.UtcNow).UtcDateTime, f.Size ?? 0));
                }
                pageToken = page.NextPageToken;
            }
            while (pageToken is not null);

            return files;
        }
        catch (Exception ex) when (ex is TokenResponseException or GoogleApiException)
        {
            throw Friendly(ex);
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(DestinationConfig destination, RemoteFile file, CancellationToken ct)
    {
        try
        {
            using var service = CreateService(Require(destination));
            await service.Files.Delete(file.Id).ExecuteAsync(ct);
        }
        catch (Exception ex) when (ex is TokenResponseException or GoogleApiException)
        {
            throw Friendly(ex);
        }
    }

    /// <inheritdoc />
    public async Task<string> TestAsync(DestinationConfig destination, CancellationToken ct)
    {
        try
        {
            var drive = Require(destination);
            using var service = CreateService(drive);

            var about = service.About.Get();
            about.Fields = "user(emailAddress)";
            var info = await about.ExecuteAsync(ct);

            await FindOrCreateFolderAsync(service, DrivePath.Split(drive.FolderName), create: true, ct);
            return $"Conectado como {info.User?.EmailAddress ?? "su cuenta de Google"} · la carpeta «{drive.FolderName}» está lista.";
        }
        catch (Exception ex) when (ex is TokenResponseException or GoogleApiException)
        {
            throw Friendly(ex);
        }
    }

    private static DriveSettings Require(DestinationConfig destination)
    {
        var drive = destination.Drive;
        if (drive is null || string.IsNullOrWhiteSpace(drive.ClientId) ||
            string.IsNullOrEmpty(drive.ProtectedClientSecret) || string.IsNullOrEmpty(drive.ProtectedRefreshToken))
        {
            throw new InvalidOperationException(
                $"El destino '{destination.Name}' no tiene la cuenta de Google conectada. Edite el job y pulse «Conectar con Google».");
        }
        return drive;
    }

    private DriveService CreateService(DriveSettings drive)
    {
        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets
            {
                ClientId = drive.ClientId,
                ClientSecret = protector.Unprotect(drive.ProtectedClientSecret!)
            },
            Scopes = [DriveService.Scope.DriveFile]
        });

        // Solo se guarda el token de actualización; la biblioteca obtiene el de acceso cuando hace falta.
        var credential = new UserCredential(flow, "sqlbackupn",
            new TokenResponse { RefreshToken = protector.Unprotect(drive.ProtectedRefreshToken!) });

        return new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "SQL Backup N"
        });
    }

    /// <summary>Busca (y opcionalmente crea) la cadena de carpetas bajo "Mi unidad". Devuelve el id de la última.</summary>
    private static async Task<string?> FindOrCreateFolderAsync(
        DriveService service, IEnumerable<string> segments, bool create, CancellationToken ct)
    {
        var parent = "root";
        foreach (var segment in segments)
        {
            var list = service.Files.List();
            list.Q = $"mimeType='{FolderMime}' and name='{DrivePath.EscapeQuery(segment)}' " +
                     $"and '{parent}' in parents and trashed=false";
            list.Fields = "files(id)";
            list.PageSize = 1;

            var found = (await list.ExecuteAsync(ct)).Files.FirstOrDefault();
            if (found is not null)
            {
                parent = found.Id;
                continue;
            }

            if (!create) return null;

            var request = service.Files.Create(new DriveFile { Name = segment, MimeType = FolderMime, Parents = [parent] });
            request.Fields = "id";
            parent = (await request.ExecuteAsync(ct)).Id;
        }
        return parent;
    }

    private static async Task<string> Md5Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        using var md5 = MD5.Create();
        return Convert.ToHexString(await md5.ComputeHashAsync(stream, ct)).ToLowerInvariant();
    }

    /// <summary>Convierte los errores técnicos de Google en mensajes claros y accionables.</summary>
    private static Exception Friendly(Exception ex) => ex switch
    {
        TokenResponseException { Error.Error: "invalid_grant" } =>
            new InvalidOperationException(
                "La autorización de Google caducó o fue revocada. Edite el destino y pulse «Conectar con Google» otra vez. " +
                "Si el proyecto de Google Cloud está en modo «Prueba», la autorización caduca a los 7 días (ver docs/google-drive.md).", ex),
        TokenResponseException t =>
            new InvalidOperationException(
                $"Google rechazó la autorización: {t.Error?.ErrorDescription ?? t.Error?.Error ?? t.Message}", ex),
        GoogleApiException g when g.Error?.Errors?.Any(e => e.Reason is "storageQuotaExceeded" or "quotaExceeded") == true =>
            new IOException("No hay espacio disponible en Google Drive.", ex),
        GoogleApiException g =>
            new IOException($"Google Drive: {g.Error?.Message ?? g.Message}", ex),
        _ => ex
    };
}

/// <summary>
/// Conecta una cuenta de Google con el flujo de "aplicación de escritorio": abre el navegador, el usuario
/// autoriza una vez y se obtiene un token de actualización que luego se guarda cifrado.
/// </summary>
public sealed class GoogleAuthorizer : IGoogleAuthorizer
{
    private const string ClosePage =
        "<html><head><meta charset=\"utf-8\"><title>SQL Backup N</title></head>" +
        "<body style=\"font-family:Segoe UI,sans-serif;text-align:center;margin-top:15%\">" +
        "<h2>Cuenta de Google conectada</h2><p>Ya puede cerrar esta ventana y volver a SQL Backup N.</p></body></html>";

    /// <inheritdoc />
    public async Task<GoogleAuthResult> AuthorizeAsync(string clientId, string clientSecret, CancellationToken ct)
    {
        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets { ClientId = clientId, ClientSecret = clientSecret },
            Scopes = [DriveService.Scope.DriveFile],
            DataStore = new MemoryDataStore(),   // los tokens no se guardan en disco: lo hace la aplicación, cifrados
            Prompt = "consent"                   // fuerza que Google devuelva el token de actualización
        });

        var app = new AuthorizationCodeInstalledApp(flow, new LocalServerCodeReceiver(ClosePage));
        var credential = await app.AuthorizeAsync("sqlbackupn", ct);

        var refresh = credential.Token.RefreshToken
            ?? throw new InvalidOperationException(
                "Google no devolvió un token de actualización. Quite el acceso de «SQL Backup N» en " +
                "myaccount.google.com/permissions y vuelva a intentarlo.");

        string? email = null;
        try
        {
            using var service = new DriveService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "SQL Backup N"
            });
            var about = service.About.Get();
            about.Fields = "user(emailAddress)";
            email = (await about.ExecuteAsync(ct)).User?.EmailAddress;
        }
        catch (Exception)
        {
            // El correo es solo informativo: no debe impedir conectar.
        }

        return new GoogleAuthResult(refresh, email);
    }

    /// <summary>Almacén en memoria para que la biblioteca de Google no escriba tokens en el disco.</summary>
    private sealed class MemoryDataStore : IDataStore
    {
        private readonly Dictionary<string, string> _items = [];

        public Task StoreAsync<T>(string key, T value)
        {
            _items[key] = System.Text.Json.JsonSerializer.Serialize(value);
            return Task.CompletedTask;
        }

        public Task DeleteAsync<T>(string key)
        {
            _items.Remove(key);
            return Task.CompletedTask;
        }

        public Task<T> GetAsync<T>(string key) =>
            Task.FromResult(_items.TryGetValue(key, out var json)
                ? System.Text.Json.JsonSerializer.Deserialize<T>(json)!
                : default!);

        public Task ClearAsync()
        {
            _items.Clear();
            return Task.CompletedTask;
        }
    }
}
