using System.Text.Json;
using SqlBackupN.Core.Abstractions;

namespace SqlBackupN.Core.Infrastructure;

/// <summary>
/// Credenciales de Google compartidas por todos los destinos. Orden de búsqueda:
/// <list type="number">
/// <item><c>google-client.json</c> en la carpeta de datos (las guarda el usuario desde el editor; el secreto va cifrado).</item>
/// <item><c>google-client.json</c> junto al ejecutable (las puede incluir quien distribuya la aplicación,
/// con <c>{"clientId": "...", "clientSecret": "..."}</c>; así el usuario final solo pulsa «Conectar con Google»).</item>
/// </list>
/// </summary>
public sealed class FileGoogleClientProvider(ISecretProtector protector, string? dataFile = null, string? bundledFile = null)
    : IGoogleClientProvider
{
    /// <summary>Al guardar se omiten los campos vacíos (p. ej. el secreto en claro, que solo existe en el archivo incluido).</summary>
    private static readonly JsonSerializerOptions WriteOptions =
        new(JsonDefaults.Indented) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private readonly string _dataFile = dataFile ?? Path.Combine(AppPaths.DataDirectory, "google-client.json");
    private readonly string _bundledFile = bundledFile ?? Path.Combine(AppContext.BaseDirectory, "google-client.json");

    private sealed class Stored
    {
        public string? ClientId { get; set; }
        public string? ProtectedClientSecret { get; set; }
        public string? ClientSecret { get; set; }   // solo en el archivo incluido con la aplicación
    }

    /// <inheritdoc />
    public GoogleClient? GetDefault()
    {
        if (Read(_dataFile) is { ClientId: { Length: > 0 } id, ProtectedClientSecret: { Length: > 0 } secret })
        {
            try
            {
                return new GoogleClient(id, protector.Unprotect(secret));
            }
            catch (Exception)
            {
                // Archivo de otro equipo o dañado: se ignora y se prueba el incluido con la aplicación.
            }
        }

        return Read(_bundledFile) is { ClientId: { Length: > 0 } bid, ClientSecret: { Length: > 0 } bsecret }
            ? new GoogleClient(bid, bsecret)
            : null;
    }

    /// <inheritdoc />
    public void SaveDefault(GoogleClient client)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dataFile)!);
        var stored = new Stored { ClientId = client.ClientId, ProtectedClientSecret = protector.Protect(client.ClientSecret) };
        var temp = _dataFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(stored, WriteOptions));
        File.Move(temp, _dataFile, overwrite: true);
    }

    private static Stored? Read(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Stored>(File.ReadAllText(path), JsonDefaults.Indented)
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
