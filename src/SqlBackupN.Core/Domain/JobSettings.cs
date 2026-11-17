namespace SqlBackupN.Core.Domain;

/// <summary>Datos de conexión a una instancia de SQL Server.</summary>
public sealed class SqlServerConnection
{
    /// <summary>Servidor o instancia, p. ej. <c>.\SQL2019</c> o <c>srv01,1433</c>.</summary>
    public string Server { get; set; } = ".";

    /// <summary>Si es <c>true</c> usa la identidad de Windows (recomendado).</summary>
    public bool UseWindowsAuthentication { get; set; } = true;

    /// <summary>Usuario SQL (solo con autenticación SQL).</summary>
    public string? UserName { get; set; }

    /// <summary>
    /// Contraseña protegida con <see cref="Abstractions.ISecretProtector"/>.
    /// Nunca se guarda en texto plano.
    /// </summary>
    public string? ProtectedPassword { get; set; }

    /// <summary>Confía en el certificado del servidor (útil en redes internas).</summary>
    public bool TrustServerCertificate { get; set; } = true;
}

/// <summary>Cómo se eligen las bases de datos de un job.</summary>
public enum DatabaseSelectionMode
{
    /// <summary>Todas las bases de usuario, incluidas las que se creen después.</summary>
    AllUserDatabases = 0,

    /// <summary>Solo las bases indicadas por nombre (permite bases de sistema).</summary>
    Explicit = 1,

    /// <summary>Bases de usuario cuyo nombre coincide con un patrón (<c>*</c> y <c>?</c>).</summary>
    Pattern = 2
}

/// <summary>Selección de bases de datos de un job.</summary>
public sealed class DatabaseSelection
{
    /// <summary>Modo de selección.</summary>
    public DatabaseSelectionMode Mode { get; set; } = DatabaseSelectionMode.AllUserDatabases;

    /// <summary>Nombres, usado en modo <see cref="DatabaseSelectionMode.Explicit"/>.</summary>
    public List<string> Names { get; set; } = new();

    /// <summary>Patrón, usado en modo <see cref="DatabaseSelectionMode.Pattern"/>.</summary>
    public string? Pattern { get; set; }

    /// <summary>Bases excluidas en cualquier modo.</summary>
    public List<string> Exclude { get; set; } = new();
}

/// <summary>Tipos de destino disponibles.</summary>
public enum DestinationKind
{
    /// <summary>Carpeta local, unidad mapeada o ruta UNC.</summary>
    Folder = 0
}

/// <summary>Un lugar donde se guardan los backups.</summary>
public sealed class DestinationConfig
{
    /// <summary>Nombre descriptivo (p. ej. "NAS oficina").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Tipo de destino.</summary>
    public DestinationKind Kind { get; set; } = DestinationKind.Folder;

    /// <summary>Carpeta raíz del destino. Debe ser visible para SQL Server si es el primero.</summary>
    public string Path { get; set; } = string.Empty;
}

/// <summary>Opciones del backup y de su verificación.</summary>
public sealed class BackupOptions
{
    /// <summary>Usa <c>WITH COMPRESSION</c>.</summary>
    public bool Compression { get; set; } = true;

    /// <summary>Usa <c>WITH CHECKSUM</c> para detectar corrupción.</summary>
    public bool Checksum { get; set; } = true;

    /// <summary>Usa <c>COPY_ONLY</c> (no altera la cadena de backups). Se ignora en diferenciales.</summary>
    public bool CopyOnly { get; set; }

    /// <summary>Ejecuta <c>RESTORE VERIFYONLY</c> tras cada backup.</summary>
    public bool VerifyAfterBackup { get; set; } = true;

    /// <summary>Tiempo máximo del comando en segundos (0 = sin límite).</summary>
    public int CommandTimeoutSeconds { get; set; }

    /// <summary>
    /// Plantilla de ruta relativa (sin extensión) bajo cada destino.
    /// Tokens: {Server} {Database} {Type} {Date} {Time} {Timestamp}.
    /// </summary>
    public string PathTemplate { get; set; } = @"{Server}\{Database}\{Type}\{Database}_{Type}_{Timestamp}";
}

/// <summary>
/// Retención por destino. Un archivo se borra solo si es más viejo que
/// <see cref="KeepDays"/> Y no está entre los <see cref="KeepLast"/> más recientes.
/// Siempre se conserva el archivo más reciente.
/// </summary>
public sealed class RetentionPolicy
{
    /// <summary>Días a conservar (null = sin límite por antigüedad).</summary>
    public int? KeepDays { get; set; } = 30;

    /// <summary>Cantidad mínima de archivos recientes a conservar (null = sin mínimo).</summary>
    public int? KeepLast { get; set; }
}

/// <summary>Cuándo y adónde se envían avisos.</summary>
public sealed class NotificationSettings
{
    /// <summary>URL de webhook (Teams, Slack, Telegram vía puente, etc.). Vacío = sin avisos.</summary>
    public string? WebhookUrl { get; set; }

    /// <summary>Avisar cuando todo sale bien.</summary>
    public bool OnSuccess { get; set; }

    /// <summary>Avisar cuando hay advertencias.</summary>
    public bool OnWarning { get; set; } = true;

    /// <summary>Avisar cuando hay fallos.</summary>
    public bool OnFailure { get; set; } = true;

    /// <summary>Indica si un resultado con el estado dado debe generar un aviso.</summary>
    public bool ShouldNotify(RunStatus status) =>
        !string.IsNullOrWhiteSpace(WebhookUrl) && status switch
        {
            RunStatus.Succeeded => OnSuccess,
            RunStatus.Warning => OnWarning,
            RunStatus.Failed => OnFailure,
            _ => false
        };
}
