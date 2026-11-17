namespace SqlBackupN.Core.Domain;

/// <summary>
/// Un job agrupa todo lo necesario para proteger un conjunto de bases de datos:
/// de dónde (servidor y bases), cuándo (pasos programados), dónde guardar
/// (destinos), cuánto conservar (retención) y a quién avisar.
/// </summary>
public sealed class BackupJob
{
    /// <summary>Identificador único e inmutable del job.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Nombre visible para el usuario.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Si es <c>false</c> el scheduler ignora el job.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Fecha de creación (UTC). Es la línea base para el primer disparo.</summary>
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Servidor SQL y credenciales.</summary>
    public SqlServerConnection Connection { get; set; } = new();

    /// <summary>Qué bases de datos incluye el job.</summary>
    public DatabaseSelection Databases { get; set; } = new();

    /// <summary>Pasos programados (completo, diferencial, log) de este job.</summary>
    public List<BackupStep> Steps { get; set; } = new();

    /// <summary>Destinos. El primero lo escribe SQL Server; el resto son copias.</summary>
    public List<DestinationConfig> Destinations { get; set; } = new();

    /// <summary>Opciones del comando BACKUP y de la verificación.</summary>
    public BackupOptions Options { get; set; } = new();

    /// <summary>Política de retención aplicada a cada destino.</summary>
    public RetentionPolicy Retention { get; set; } = new();

    /// <summary>Reglas de notificación.</summary>
    public NotificationSettings Notifications { get; set; } = new();
}

/// <summary>Un paso programado de un job: un tipo de backup con su horario.</summary>
public sealed class BackupStep
{
    /// <summary>Identificador del paso (se usa para calcular la última ejecución).</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tipo de backup que ejecuta el paso.</summary>
    public BackupType Type { get; set; } = BackupType.Full;

    /// <summary>
    /// Expresión cron estándar de 5 campos (minuto hora día mes díaSemana),
    /// evaluada en la zona horaria local del equipo. Ej.: <c>0 23 * * *</c>.
    /// </summary>
    public string Cron { get; set; } = "0 23 * * *";

    /// <summary>Si es <c>false</c> el paso no se programa.</summary>
    public bool Enabled { get; set; } = true;
}
