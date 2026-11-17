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

    /// <summary>Copia un archivo sobrescribiendo el destino.</summary>
    void Copy(string source, string destination);

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

/// <summary>Persistencia del historial de ejecuciones.</summary>
public interface IHistoryStore
{
    /// <summary>Agrega un registro al historial.</summary>
    Task AppendAsync(JobRunRecord record, CancellationToken ct = default);

    /// <summary>Devuelve los registros más recientes primero, opcionalmente de un solo job.</summary>
    Task<IReadOnlyList<JobRunRecord>> GetRecentAsync(Guid? jobId, int max, CancellationToken ct = default);

    /// <summary>Fin (UTC) de la última ejecución de un paso, o null si nunca se ejecutó.</summary>
    Task<DateTime?> GetLastRunUtcAsync(Guid jobId, Guid stepId, CancellationToken ct = default);
}

/// <summary>Envía avisos sobre el resultado de una ejecución.</summary>
public interface INotifier
{
    /// <summary>Notifica el resultado. Las implementaciones no deben lanzar por fallos de red.</summary>
    Task NotifyAsync(BackupJob job, JobRunRecord record, CancellationToken ct);
}

/// <summary>Protege secretos (contraseñas) en reposo.</summary>
public interface ISecretProtector
{
    /// <summary>Cifra un texto en claro y devuelve un texto seguro para guardar.</summary>
    string Protect(string plainText);

    /// <summary>Descifra un texto producido por <see cref="Protect"/>.</summary>
    string Unprotect(string protectedText);
}
