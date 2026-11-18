namespace SqlBackupN.Core.Domain;

/// <summary>Resultado de una ejecución o de una base dentro de ella.</summary>
public enum RunStatus
{
    /// <summary>Todo correcto.</summary>
    Succeeded = 0,

    /// <summary>El backup existe pero algo secundario falló (copia, retención).</summary>
    Warning = 1,

    /// <summary>El backup no se pudo completar.</summary>
    Failed = 2,

    /// <summary>No se ejecutó (p. ej. un diferencial reemplazado por un completo).</summary>
    Skipped = 3
}

/// <summary>Resultado del backup de una base de datos.</summary>
public sealed class DatabaseBackupResult
{
    /// <summary>Nombre de la base.</summary>
    public string Database { get; set; } = string.Empty;

    /// <summary>Estado del backup de esta base.</summary>
    public RunStatus Status { get; set; }

    /// <summary>Ruta del archivo en el destino primario.</summary>
    public string? FilePath { get; set; }

    /// <summary>Tamaño del archivo en bytes.</summary>
    public long SizeBytes { get; set; }

    /// <summary>Duración total, incluidas copias y retención.</summary>
    public TimeSpan Duration { get; set; }

    /// <summary>Mensaje de error si <see cref="Status"/> es <see cref="RunStatus.Failed"/>.</summary>
    public string? Error { get; set; }

    /// <summary>Advertencias no fatales.</summary>
    public List<string> Warnings { get; set; } = new();
}

/// <summary>Registro histórico de la ejecución de un paso de un job.</summary>
public sealed class JobRunRecord
{
    /// <summary>Identificador de la ejecución.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Job ejecutado.</summary>
    public Guid JobId { get; set; }

    /// <summary>Nombre del job al momento de ejecutar.</summary>
    public string JobName { get; set; } = string.Empty;

    /// <summary>Paso ejecutado.</summary>
    public Guid StepId { get; set; }

    /// <summary>Tipo de backup.</summary>
    public BackupType Type { get; set; }

    /// <summary>Inicio (UTC).</summary>
    public DateTime StartedUtc { get; set; }

    /// <summary>Fin (UTC).</summary>
    public DateTime FinishedUtc { get; set; }

    /// <summary>Estado agregado de todas las bases.</summary>
    public RunStatus Status { get; set; }

    /// <summary>Detalle por base de datos.</summary>
    public List<DatabaseBackupResult> Results { get; set; } = new();

    /// <summary>Registro paso a paso de la ejecución, en orden cronológico.</summary>
    public List<RunLogEntry> Log { get; set; } = new();

    /// <summary>Calcula el estado agregado a partir de los resultados por base.</summary>
    public static RunStatus Aggregate(IReadOnlyCollection<DatabaseBackupResult> results)
    {
        if (results.Count == 0) return RunStatus.Warning;
        if (results.Any(r => r.Status == RunStatus.Failed)) return RunStatus.Failed;
        if (results.Any(r => r.Status == RunStatus.Warning)) return RunStatus.Warning;
        return RunStatus.Succeeded;
    }
}

/// <summary>Gravedad de una línea del registro de ejecución.</summary>
public enum RunLogLevel
{
    /// <summary>Avance normal.</summary>
    Info = 0,

    /// <summary>Algo secundario falló o no se hizo (el backup puede existir).</summary>
    Warning = 1,

    /// <summary>Algo importante falló.</summary>
    Error = 2
}

/// <summary>Una línea del registro paso a paso de una ejecución.</summary>
public sealed class RunLogEntry
{
    /// <summary>Momento en que ocurrió (UTC).</summary>
    public DateTime TimeUtc { get; set; }

    /// <summary>Gravedad.</summary>
    public RunLogLevel Level { get; set; }

    /// <summary>Base de datos a la que se refiere, o <c>null</c> si es de toda la ejecución.</summary>
    public string? Database { get; set; }

    /// <summary>Texto para mostrar al usuario (nunca contiene contraseñas).</summary>
    public string Message { get; set; } = string.Empty;
}
