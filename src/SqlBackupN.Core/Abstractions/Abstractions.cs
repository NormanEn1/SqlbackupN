using SqlBackupN.Core.Domain;

namespace SqlBackupN.Core.Abstractions;

/// <summary>Parámetros de un backup concreto de una base de datos.</summary>
/// <param name="Database">Base de datos a respaldar.</param>
/// <param name="Type">Tipo de backup.</param>
/// <param name="FilePath">Ruta del archivo, tal como la ve el servidor SQL.</param>
/// <param name="Options">Opciones del backup.</param>
public sealed record BackupRequest(string Database, BackupType Type, string FilePath, BackupOptions Options);

/// <summary>Un archivo almacenado en un destino (para aplicar retención).</summary>
/// <param name="Path">Ruta completa.</param>
/// <param name="LastWriteUtc">Última escritura (UTC).</param>
/// <param name="SizeBytes">Tamaño en bytes.</param>
public sealed record StoredFile(string Path, DateTime LastWriteUtc, long SizeBytes);

/// <summary>Un archivo guardado en un destino remoto (para aplicar retención).</summary>
/// <param name="Id">Identificador remoto (se usa para borrarlo).</param>
/// <param name="Name">Nombre del archivo.</param>
/// <param name="CreatedUtc">Fecha de creación (UTC).</param>
/// <param name="SizeBytes">Tamaño en bytes.</param>
public sealed record RemoteFile(string Id, string Name, DateTime CreatedUtc, long SizeBytes);

/// <summary>
/// Destino remoto (nube). El backup se escribe primero en una carpeta local y desde ahí se sube una copia;
/// por eso un destino remoto nunca es el principal.
/// </summary>
public interface IRemoteStorage
{
    /// <summary>
    /// Sube un archivo local. <paramref name="relativePath"/> incluye las subcarpetas y el nombre
    /// (con <c>\</c> o <c>/</c>); las carpetas que falten se crean. Verifica la integridad al terminar.
    /// </summary>
    Task UploadAsync(DestinationConfig destination, string localFilePath, string relativePath, CancellationToken ct);

    /// <summary>Lista los archivos con la extensión indicada de una carpeta (vacío si la carpeta no existe).</summary>
    Task<IReadOnlyList<RemoteFile>> ListAsync(
        DestinationConfig destination, string relativeDirectory, string extension, CancellationToken ct);

    /// <summary>Borra definitivamente un archivo remoto.</summary>
    Task DeleteAsync(DestinationConfig destination, RemoteFile file, CancellationToken ct);

    /// <summary>
    /// Comprueba la conexión y que la carpeta raíz exista (la crea si falta).
    /// Devuelve un texto descriptivo para mostrar al usuario.
    /// </summary>
    Task<string> TestAsync(DestinationConfig destination, CancellationToken ct);
}

/// <summary>Resultado de conectar una cuenta de Google.</summary>
/// <param name="RefreshToken">Token de actualización (en claro; el llamador debe cifrarlo).</param>
/// <param name="Email">Correo de la cuenta, si se pudo obtener.</param>
public sealed record GoogleAuthResult(string RefreshToken, string? Email);

/// <summary>Conecta una cuenta de Google: abre el navegador para que el usuario autorice una sola vez.</summary>
public interface IGoogleAuthorizer
{
    /// <summary>Ejecuta el flujo de autorización y devuelve el token de actualización.</summary>
    Task<GoogleAuthResult> AuthorizeAsync(string clientId, string clientSecret, CancellationToken ct);
}

/// <summary>Identificador de la aplicación ante Google (ID y secreto de cliente OAuth, tipo "Aplicación de escritorio").</summary>
/// <param name="ClientId">ID de cliente.</param>
/// <param name="ClientSecret">Secreto de cliente, en claro (solo en memoria).</param>
public sealed record GoogleClient(string ClientId, string ClientSecret);

/// <summary>
/// Credenciales de Google compartidas por todos los destinos de Google Drive, para pedirlas
/// una sola vez. Pueden venir incluidas con la aplicación (archivo <c>google-client.json</c> junto al ejecutable)
/// o haberlas guardado el usuario (cifradas) en la carpeta de datos.
/// </summary>
public interface IGoogleClientProvider
{
    /// <summary>Credenciales vigentes (las del usuario y, si no hay, las incluidas con la aplicación), o <c>null</c>.</summary>
    GoogleClient? GetDefault();

    /// <summary>Guarda las credenciales del usuario (cifradas) para todos los destinos.</summary>
    void SaveDefault(GoogleClient client);
}

/// <summary>Habla con SQL Server: lista bases, ejecuta y verifica backups.</summary>
public interface IBackupExecutor
{
    /// <summary>Lista las bases en línea (sin tempdb ni snapshots).</summary>
    Task<IReadOnlyList<string>> ListDatabasesAsync(SqlServerConnection connection, CancellationToken ct);

    /// <summary>Ejecuta <c>BACKUP DATABASE/LOG</c>.</summary>
    Task BackupAsync(SqlServerConnection connection, BackupRequest request, CancellationToken ct);

    /// <summary>Ejecuta <c>RESTORE VERIFYONLY</c> sobre un archivo de backup.</summary>
    Task VerifyAsync(SqlServerConnection connection, string filePath, bool checksum, int timeoutSeconds, CancellationToken ct);
}

/// <summary>Acceso al sistema de archivos de los destinos (se abstrae para poder probar).</summary>
public interface IFileStore
{
    /// <summary>Crea el directorio si no existe.</summary>
    void EnsureDirectory(string path);

    /// <summary>
    /// Comprueba que se puede crear y borrar un archivo en el directorio (lo crea si no existe).
    /// Lanza una excepción con el motivo si no es posible. Se prueba con el usuario actual,
    /// que puede diferir de la cuenta del servicio.
    /// </summary>
    void VerifyWritable(string directory);

    /// <summary>Copia un archivo sobrescribiendo el destino.</summary>
    void Copy(string source, string destination);

    /// <summary>
    /// Crea un <c>.zip</c> con un solo archivo dentro (con el mismo nombre del original). Si algo falla no deja un zip a medias.
    /// No borra el original.
    /// </summary>
    void CreateZip(string sourceFile, string zipPath);

    /// <summary>Borra un archivo.</summary>
    void Delete(string path);

    /// <summary>Tamaño de un archivo en bytes.</summary>
    long GetSize(string path);

    /// <summary>Lista archivos de un directorio (no recursivo) que cumplen el patrón.</summary>
    IReadOnlyList<StoredFile> List(string directory, string searchPattern);
}

/// <summary>Persistencia de la configuración de jobs.</summary>
public interface IJobStore
{
    /// <summary>Carga todos los jobs.</summary>
    Task<IReadOnlyList<BackupJob>> LoadAsync(CancellationToken ct = default);

    /// <summary>Guarda todos los jobs reemplazando la configuración anterior.</summary>
    Task SaveAsync(IEnumerable<BackupJob> jobs, CancellationToken ct = default);
}

/// <summary>Guarda las preferencias del usuario de la interfaz (modo claro/oscuro…).</summary>
public interface IUserSettingsStore
{
    /// <summary>Lee las preferencias; si no existen o están dañadas devuelve las predeterminadas (nunca lanza).</summary>
    UserSettings Load();

    /// <summary>Guarda las preferencias.</summary>
    void Save(UserSettings settings);
}

/// <summary>Persistencia del historial de ejecuciones.</summary>
public interface IHistoryStore
{
    /// <summary>Agrega un registro al historial.</summary>
    Task AppendAsync(JobRunRecord record, CancellationToken ct = default);

    /// <summary>Borra del historial las ejecuciones con esos identificadores. Devuelve cuántas se borraron.</summary>
    Task<int> DeleteAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);

    /// <summary>Borra el historial de un job (o de todos los jobs si <paramref name="jobId"/> es <c>null</c>). Devuelve cuántas ejecuciones se borraron.</summary>
    Task<int> ClearAsync(Guid? jobId, CancellationToken ct = default);

    /// <summary>Devuelve los registros más recientes primero, opcionalmente de un solo job.</summary>
    Task<IReadOnlyList<JobRunRecord>> GetRecentAsync(Guid? jobId, int max, CancellationToken ct = default);

    /// <summary>Fin (UTC) de la última ejecución de un paso, o null si nunca se ejecutó.</summary>
    Task<DateTime?> GetLastRunUtcAsync(Guid jobId, Guid stepId, CancellationToken ct = default);
}

/// <summary>Envía avisos sobre el resultado de una ejecución.</summary>
public interface INotifier
{
    /// <summary>Notifica el resultado de una ejecución. Lanza si no se pudo entregar (el llamador lo registra).</summary>
    Task NotifyAsync(BackupJob job, JobRunRecord record, CancellationToken ct);

    /// <summary>
    /// Envía un mensaje cualquiera por el canal configurado (aviso de prueba, job que no se ejecuta…).
    /// Lanza con el motivo si no se pudo entregar.
    /// </summary>
    /// <param name="settings">Canal (webhook, formato, autenticación…).</param>
    /// <param name="kind">Qué clase de mensaje es.</param>
    /// <param name="message">Texto del mensaje.</param>
    /// <param name="ct">Cancelación.</param>
    Task SendMessageAsync(NotificationSettings settings, NoticeKind kind, string message, CancellationToken ct);
}

/// <summary>Protege secretos (contraseñas) en reposo.</summary>
public interface ISecretProtector
{
    /// <summary>Cifra un texto en claro y devuelve un texto seguro para guardar.</summary>
    string Protect(string plainText);

    /// <summary>Descifra un texto producido por <see cref="Protect"/>.</summary>
    string Unprotect(string protectedText);
}
