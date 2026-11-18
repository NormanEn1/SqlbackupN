namespace SqlBackupN.Core.Domain;

/// <summary>Datos de conexión a una instancia de SQL Server.</summary>
public sealed class SqlServerConnection
{
    /// <summary>Servidor o instancia, p. ej. <c>.\SQL2019</c> o <c>srv01,1433</c>.</summary>
    public string Server { get; set; } = string.Empty;

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
    Folder = 0,

    /// <summary>
    /// Google Drive, mediante su API (sin depender de la unidad virtual <c>G:\</c>).
    /// Solo puede ser una copia: el destino principal siempre es una carpeta.
    /// </summary>
    GoogleDrive = 1
}

/// <summary>Un lugar donde se guardan los backups.</summary>
public sealed class DestinationConfig
{
    /// <summary>Nombre descriptivo (p. ej. "NAS oficina").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Tipo de destino.</summary>
    public DestinationKind Kind { get; set; } = DestinationKind.Folder;

    /// <summary>
    /// Carpeta raíz del destino (solo <see cref="DestinationKind.Folder"/>).
    /// Debe ser visible para SQL Server si es el primero.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Ajustes de Google Drive (solo <see cref="DestinationKind.GoogleDrive"/>).</summary>
    public DriveSettings? Drive { get; set; }
}

/// <summary>
/// Conexión con Google Drive. Se usa autorización de usuario (OAuth) con el permiso
/// <c>drive</c>: la aplicación puede usar carpetas que ya existen en la cuenta.
/// Los secretos se guardan cifrados (DPAPI), nunca en texto plano.
/// </summary>
public sealed class DriveSettings
{
    /// <summary>ID de cliente OAuth (tipo "Aplicación de escritorio") de su proyecto de Google Cloud.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Secreto del cliente OAuth, cifrado.</summary>
    public string? ProtectedClientSecret { get; set; }

    /// <summary>Token de actualización obtenido al conectar la cuenta, cifrado.</summary>
    public string? ProtectedRefreshToken { get; set; }

    /// <summary>Correo de la cuenta conectada (solo informativo).</summary>
    public string? AccountEmail { get; set; }

    /// <summary>
    /// Carpeta raíz dentro de "Mi unidad" (puede ser una ruta con <c>/</c>). Si ya existe se reutiliza (la más antigua
    /// si hay varias con el mismo nombre); si no, la aplicación la crea. Ver carpetas existentes exige el permiso
    /// completo de Drive; con un token antiguo (<c>drive.file</c>) solo ve las que ella creó.
    /// </summary>
    public string FolderName { get; set; } = "SQL Backup N";
}

/// <summary>Opciones del backup y de su verificación.</summary>
public sealed class BackupOptions
{
    /// <summary>Usa <c>WITH COMPRESSION</c>.</summary>
    public bool Compression { get; set; } = true;

    /// <summary>
    /// Guarda cada backup como un <c>.zip</c> (un zip por base): tras verificar el <c>.bak</c> se comprime en la carpeta
    /// principal, se borra el <c>.bak</c> y el zip es el archivo que se copia, se sube y se somete a retención.
    /// </summary>
    public bool ZipOutput { get; set; }

    /// <summary>
    /// La carpeta principal es una unidad temporal (p. ej. el disco temporal de una máquina virtual): solo sirve de paso.
    /// Cuando el archivo (el <c>.zip</c> o, sin zip, el <c>.bak</c>) está completo en TODOS los demás destinos (NAS, Drive…)
    /// se borra de la carpeta principal, junto con el <c>.bak</c>. Si alguna copia falla no se borra nada. Exige al menos otro destino.
    /// </summary>
    public bool TemporaryPrimary { get; set; }

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

/// <summary>Forma del cuerpo que se envía al webhook.</summary>
public enum WebhookFormat
{
    /// <summary>Un JSON <c>{"text": "..."}</c> con el aviso en texto (Slack, Teams y servicios parecidos).</summary>
    Text = 0,

    /// <summary>Un JSON completo con el job, el estado, las bases y el texto (HelpDesk, n8n, Zapier, Make…).</summary>
    Json = 1,

    /// <summary>Un JSON armado con la plantilla del usuario y valores como <c>{job}</c> o <c>{message}</c>.</summary>
    Custom = 2
}

/// <summary>Cómo se autentica el envío al webhook.</summary>
public enum WebhookAuth
{
    /// <summary>Sin autenticación (la URL ya lleva su propia clave, p. ej. Slack o Teams).</summary>
    None = 0,

    /// <summary>Cabecera <c>Authorization: Bearer &lt;token&gt;</c>.</summary>
    Bearer = 1,

    /// <summary>Una cabecera con el nombre que se indique (p. ej. <c>x-api-key</c>) y el secreto como valor.</summary>
    Header = 2
}

/// <summary>Qué clase de mensaje se envía por el canal de avisos (para que el receptor lo distinga).</summary>
public enum NoticeKind
{
    /// <summary>Resultado de la ejecución de un backup.</summary>
    Run = 0,

    /// <summary>Aviso de prueba pedido desde el editor.</summary>
    Test = 1,

    /// <summary>El job lleva demasiadas horas sin ejecutarse.</summary>
    MissedRun = 2
}

/// <summary>Cuándo y adónde se envían avisos.</summary>
public sealed class NotificationSettings
{
    /// <summary>URL de webhook (Teams, Slack, Telegram vía puente, etc.). Vacío = sin avisos.</summary>
    public string? WebhookUrl { get; set; }

    /// <summary>Forma del cuerpo que se envía.</summary>
    public WebhookFormat WebhookFormat { get; set; } = WebhookFormat.Text;

    /// <summary>
    /// Plantilla JSON para <see cref="WebhookFormat.Custom"/>. Valores: <c>{job} {jobId} {status} {type} {server} {machine}
    /// {message} {summary} {started} {finished} {duration} {event}</c>; se insertan ya escapados para JSON, así que se escriben
    /// dentro de comillas, p. ej. <c>{"text": "{message}"}</c>.
    /// </summary>
    public string? WebhookBodyTemplate { get; set; }

    /// <summary>Cómo se autentica el envío.</summary>
    public WebhookAuth WebhookAuth { get; set; } = WebhookAuth.None;

    /// <summary>Nombre de la cabecera cuando <see cref="WebhookAuth"/> es <see cref="Domain.WebhookAuth.Header"/>.</summary>
    public string? WebhookAuthHeader { get; set; }

    /// <summary>Token o clave (cifrado con DPAPI; nunca en texto plano).</summary>
    public string? ProtectedWebhookSecret { get; set; }

    /// <summary>Cabeceras adicionales, una por línea con el formato <c>Nombre: valor</c> (sin secretos: no van cifradas).</summary>
    public List<string> WebhookHeaders { get; set; } = new();

    /// <summary>Reintentos si el envío falla por red o por un error temporal del servicio (0–5).</summary>
    public int WebhookRetries { get; set; } = 2;

    /// <summary>Segundos máximos de espera por cada intento (5–120).</summary>
    public int WebhookTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Avisar cuando todo sale bien. Activo por defecto: si el webhook alimenta una bitácora o un seguimiento (riesgo operativo),
    /// necesita recibir también las ejecuciones correctas, no solo los problemas.
    /// </summary>
    public bool OnSuccess { get; set; } = true;

    /// <summary>Avisar cuando hay advertencias.</summary>
    public bool OnWarning { get; set; } = true;

    /// <summary>Avisar cuando hay fallos.</summary>
    public bool OnFailure { get; set; } = true;

    /// <summary>
    /// Avisar si el job pasa más de estas horas sin ejecutarse (0 = no avisar). Lo detecta el servicio de Windows mientras
    /// está en marcha; no puede detectar que el propio servicio o el servidor estén apagados.
    /// </summary>
    public int MissedRunHours { get; set; }

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
