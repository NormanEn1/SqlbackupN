using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using SqlBackupN.Core.Abstractions;
using SqlBackupN.Core.Domain;
using SqlBackupN.Core.Infrastructure;
using SqlBackupN.Core.Services;
using SqlBackupN.Presentation.Infrastructure;

namespace SqlBackupN.Presentation.Jobs;

/// <summary>
/// Edita todas las opciones de un <see cref="BackupJob"/> y las valida mientras se escribe.
/// Trabaja sobre una copia: el job original no cambia hasta que <see cref="SaveCommand"/>
/// produce <see cref="Result"/>. Las reglas viven en <see cref="JobValidator"/>; este view-model
/// solo convierte texto a valores y reparte los mensajes por campo.
/// </summary>
public sealed class JobEditorViewModel : ViewModelBase
{
    /// <summary>Marca interna: "hay una contraseña nueva pendiente de cifrar".</summary>
    private const string PendingPassword = "\u0001pendiente";
    private const string PendingWebhookSecret = "\u0001pendiente-token";

    private readonly BackupJob _original;
    private readonly bool _isNew;
    private readonly string[] _otherNames;
    private readonly EditorServices _svc;
    private bool _loading = true;

    // General / servidor
    private string _name;
    private bool _enabled;
    private string _server;
    private bool _useWindowsAuthentication;
    private string _userName;
    private string _password = string.Empty;
    private bool _trustServerCertificate;
    private string _connectionTestResult = string.Empty;

    // Bases de datos
    private DatabaseSelectionMode _dbMode;
    private string _namesText;
    private string _pattern;
    private string _excludeText;

    // Opciones
    private bool _compression;
    private bool _zipOutput;
    private bool _temporaryPrimary;
    private bool _checksum;
    private bool _copyOnly;
    private bool _verifyAfterBackup;
    private string _timeoutText;
    private string _nameTemplate;
    private string _pathPreview = string.Empty;

    // Retención
    private bool _useKeepDays;
    private string _keepDaysText;
    private bool _useKeepLast;
    private string _keepLastText;

    // Avisos
    private string _webhookUrl;
    private bool _onSuccess;
    private bool _onWarning;
    private bool _onFailure;
    private bool _useMissedRun;
    private string _missedRunHoursText;
    private string _notificationTestResult = string.Empty;
    private string _notificationPreview = string.Empty;
    private WebhookFormat _webhookFormat;
    private string _webhookBodyTemplate;
    private WebhookAuth _webhookAuth;
    private string _webhookAuthHeader;
    private string _webhookSecret = string.Empty;
    private string _webhookHeadersText;
    private string _webhookRetriesText;
    private string _webhookTimeoutText;

    // Resultado de la validación
    private IReadOnlyDictionary<string, string> _errors = new Dictionary<string, string>();
    private IReadOnlyDictionary<string, string> _warnings = new Dictionary<string, string>();
    private int _errorCount;
    private int _warningCount;
    private int _missingCount;
    private IReadOnlyDictionary<string, string> _pending = new Dictionary<string, string>();

    // Credenciales de Google compartidas por todos los destinos de Drive
    private string _googleClientId = string.Empty;
    private string _googleClientSecret = string.Empty;
    private readonly bool _hasSharedGoogleClient;
    private bool _showGoogleSetup;

    /// <summary>Crea el editor.</summary>
    /// <param name="job">Job a editar (se trabaja sobre esta instancia; pase una copia).</param>
    /// <param name="isNew"><c>true</c> si es un job nuevo.</param>
    /// <param name="otherJobNames">Nombres de los demás jobs, para exigir unicidad.</param>
    /// <param name="services">Servicios del editor.</param>
    public JobEditorViewModel(BackupJob job, bool isNew, IEnumerable<string> otherJobNames, EditorServices services)
    {
        _original = job;
        _isNew = isNew;
        _otherNames = otherJobNames.ToArray();
        _svc = services;

        _name = job.Name;
        _enabled = job.Enabled;
        _server = job.Connection.Server;
        _useWindowsAuthentication = job.Connection.UseWindowsAuthentication;
        _userName = job.Connection.UserName ?? string.Empty;
        _trustServerCertificate = job.Connection.TrustServerCertificate;

        _dbMode = job.Databases.Mode;
        _namesText = string.Join(Environment.NewLine, job.Databases.Names);
        _pattern = job.Databases.Pattern ?? string.Empty;
        _excludeText = string.Join(Environment.NewLine, job.Databases.Exclude);

        _compression = job.Options.Compression;
        _zipOutput = job.Options.ZipOutput;
        _temporaryPrimary = job.Options.TemporaryPrimary;
        _checksum = job.Options.Checksum;
        _copyOnly = job.Options.CopyOnly;
        _verifyAfterBackup = job.Options.VerifyAfterBackup;
        _timeoutText = job.Options.CommandTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        _nameTemplate = job.Options.PathTemplate;

        _useKeepDays = job.Retention.KeepDays is not null;
        _keepDaysText = (job.Retention.KeepDays ?? 30).ToString(CultureInfo.InvariantCulture);
        _useKeepLast = job.Retention.KeepLast is not null;
        _keepLastText = (job.Retention.KeepLast ?? 3).ToString(CultureInfo.InvariantCulture);

        _webhookUrl = job.Notifications.WebhookUrl ?? string.Empty;
        _onSuccess = job.Notifications.OnSuccess;
        _onWarning = job.Notifications.OnWarning;
        _onFailure = job.Notifications.OnFailure;
        _useMissedRun = job.Notifications.MissedRunHours > 0;
        _webhookFormat = job.Notifications.WebhookFormat;
        _webhookBodyTemplate = job.Notifications.WebhookBodyTemplate ?? "{\"text\": \"{message}\"}";
        _webhookAuth = job.Notifications.WebhookAuth;
        _webhookAuthHeader = job.Notifications.WebhookAuthHeader ?? string.Empty;
        _webhookHeadersText = string.Join(Environment.NewLine, job.Notifications.WebhookHeaders);
        _webhookRetriesText = job.Notifications.WebhookRetries.ToString(CultureInfo.InvariantCulture);
        _webhookTimeoutText = job.Notifications.WebhookTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        _missedRunHoursText = (job.Notifications.MissedRunHours > 0 ? job.Notifications.MissedRunHours : 26).ToString(CultureInfo.InvariantCulture);

        _hasSharedGoogleClient = services.GoogleClients.GetDefault() is not null;
        _showGoogleSetup = !_hasSharedGoogleClient &&
                           job.Destinations.Any(d => d.Kind == DestinationKind.GoogleDrive && string.IsNullOrWhiteSpace(d.Drive?.ClientId));

        foreach (var step in job.Steps) Steps.Add(NewStep(step));
        foreach (var destination in job.Destinations) Destinations.Add(NewDestination(destination));

        SaveCommand = new RelayCommand(Save, () => CanSave);
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(false));
        AddStepCommand = new RelayCommand(AddStep);
        ApplyPlanCommand = new RelayCommand(p => ApplyPlan(ParsePreset(p)));
        SetNameTemplateCommand = new RelayCommand(p =>
            NameTemplate = p as string is { Length: > 0 } t ? (t == "default" ? new BackupOptions().PathTemplate : t) : NameTemplate);
        AddDestinationCommand = new RelayCommand(AddDestination);
        // Drive solo puede ser una copia: si aún no hay carpeta principal, se agrega una antes (ver AddDriveDestination).
        AddDriveDestinationCommand = new RelayCommand(AddDriveDestination);
        ToggleGoogleSetupCommand = new RelayCommand(() => ShowGoogleSetup = !ShowGoogleSetup);
        OpenGoogleConsoleCommand = new RelayCommand(() => _svc.Dialogs.OpenUrl(HelpLinks.GoogleCloudCredentials));
        OpenGoogleGuideCommand = new RelayCommand(() => _svc.Dialogs.OpenUrl(HelpLinks.GoogleDriveGuide));
        TestConnectionCommand = new AsyncRelayCommand(TestConnectionAsync);
        TestNotificationCommand = new AsyncRelayCommand(TestNotificationAsync);
        LoadDatabasesCommand = new AsyncRelayCommand(LoadDatabasesAsync);
        SelectAllDatabasesCommand = new RelayCommand(() => SelectDatabases(userOnly: true));
        ClearDatabasesCommand = new RelayCommand(() => SelectDatabases(userOnly: false));
        ToggleManualNamesCommand = new RelayCommand(() => ShowManualNames = !ShowManualNames);

        _loading = false;
        Revalidate();
    }

    // ---------------------------------------------------------------- Estado general

    /// <summary>Se solicita cerrar la ventana; el argumento indica si se guardó.</summary>
    public event Action<bool>? CloseRequested;

    /// <summary>Job resultante tras guardar (<c>null</c> mientras no se haya guardado).</summary>
    public BackupJob? Result { get; private set; }

    /// <summary>Título de la ventana.</summary>
    public string Title => _isNew ? "Nuevo job" : $"Editar job · {_original.Name}";

    /// <summary>Tipos de destino disponibles.</summary>
    public IReadOnlyList<KindOption> DestinationKinds => KindOption.All;

    /// <summary>Tipos de backup disponibles para los pasos.</summary>
    public IReadOnlyList<TypeOption> StepTypes => TypeOption.All;

    /// <summary>Pasos de programación.</summary>
    public ObservableCollection<StepEditorViewModel> Steps { get; } = [];

    /// <summary>Destinos.</summary>
    public ObservableCollection<DestinationEditorViewModel> Destinations { get; } = [];

    /// <summary>Resumen de errores y advertencias (errores primero).</summary>
    public ObservableCollection<IssueRow> Issues { get; } = [];

    /// <summary>Mensajes de error por campo (clave = campo del validador, p. ej. <c>Connection.Server</c>).</summary>
    public IReadOnlyDictionary<string, string> Errors { get => _errors; private set => Set(ref _errors, value); }

    /// <summary>Mensajes de advertencia por campo.</summary>
    public IReadOnlyDictionary<string, string> Warnings { get => _warnings; private set => Set(ref _warnings, value); }

    /// <summary>Cantidad de errores.</summary>
    public int ErrorCount { get => _errorCount; private set => Set(ref _errorCount, value); }

    /// <summary>Cantidad de advertencias.</summary>
    public int WarningCount { get => _warningCount; private set => Set(ref _warningCount, value); }

    /// <summary>Cantidad de datos obligatorios que todavía faltan por rellenar.</summary>
    public int MissingCount { get => _missingCount; private set => Set(ref _missingCount, value); }

    /// <summary>Datos que faltan por campo (mismas claves que <see cref="Errors"/>); se muestran en tono neutro, no como error.</summary>
    public IReadOnlyDictionary<string, string> Pending { get => _pending; private set => Set(ref _pending, value); }

    /// <summary>Guardar solo es posible sin errores y sin datos pendientes (las advertencias no lo impiden).</summary>
    public bool CanSave => ErrorCount == 0 && MissingCount == 0;

    /// <summary>
    /// Texto del resumen: "Todo correcto", "Faltan 2 datos para poder guardar", "1 error · 1 advertencia"…
    /// Lo pendiente se redacta como algo por completar, no como una equivocación.
    /// </summary>
    public string SummaryText
    {
        get
        {
            var parts = new List<string>();
            if (ErrorCount > 0) parts.Add($"{ErrorCount} {(ErrorCount == 1 ? "error" : "errores")}");
            if (MissingCount > 0)
            {
                parts.Add(ErrorCount > 0
                    ? $"{MissingCount} por completar"
                    : MissingCount == 1 ? "Falta 1 dato para poder guardar" : $"Faltan {MissingCount} datos para poder guardar");
            }
            if (WarningCount > 0) parts.Add($"{WarningCount} {(WarningCount == 1 ? "advertencia" : "advertencias")}");

            if (parts.Count == 0) return "Todo correcto";
            if (ErrorCount == 0 && MissingCount == 0) parts.Insert(0, "Sin errores");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Clave de color del resumen: <c>error</c>, <c>pending</c>, <c>warning</c> u <c>ok</c>.</summary>
    public string SummaryKind =>
        ErrorCount > 0 ? "error" : MissingCount > 0 ? "pending" : WarningCount > 0 ? "warning" : "ok";

    /// <summary>Símbolo del resumen: ✖ error, ○ pendiente, ⚠ recomendación, ✔ todo correcto.</summary>
    public string SummaryIcon =>
        ErrorCount > 0 ? "✖" : MissingCount > 0 ? "○" : WarningCount > 0 ? "⚠" : "✔";

    /// <summary>Guarda el job (solo si no hay errores) y cierra la ventana.</summary>
    public RelayCommand SaveCommand { get; }

    /// <summary>Cierra la ventana sin guardar.</summary>
    public RelayCommand CancelCommand { get; }

    /// <summary>Agrega un paso de programación.</summary>
    public RelayCommand AddStepCommand { get; }

    /// <summary>Reemplaza los pasos por un plan predefinido (parámetro: <see cref="PlanPreset"/>).</summary>
    public RelayCommand ApplyPlanCommand { get; }

    /// <summary>Agrega un destino de tipo carpeta.</summary>
    public RelayCommand AddDestinationCommand { get; }

    /// <summary>
    /// Agrega un destino de tipo Google Drive. Como Drive siempre es una copia, si todavía no hay carpeta principal
    /// se agrega una primero (SQL Server escribe allí el backup).
    /// </summary>
    public RelayCommand AddDriveDestinationCommand { get; }

    /// <summary>Muestra u oculta los pasos y campos de las credenciales de Google.</summary>
    public RelayCommand ToggleGoogleSetupCommand { get; }

    /// <summary>Abre la página de credenciales de Google Cloud en el navegador.</summary>
    public RelayCommand OpenGoogleConsoleCommand { get; }

    /// <summary>Abre la guía paso a paso.</summary>
    public RelayCommand OpenGoogleGuideCommand { get; }

    /// <summary>Prueba la conexión con el servidor (y carga la lista de bases para elegirlas).</summary>
    public AsyncRelayCommand TestConnectionCommand { get; }

    /// <summary>Lee las bases del servidor para elegirlas de una lista.</summary>
    public AsyncRelayCommand LoadDatabasesCommand { get; }

    /// <summary>Marca todas las bases de usuario de la lista.</summary>
    public RelayCommand SelectAllDatabasesCommand { get; }

    /// <summary>Desmarca todas las bases de la lista.</summary>
    public RelayCommand ClearDatabasesCommand { get; }

    /// <summary>Alterna entre la lista y escribir los nombres a mano.</summary>
    public RelayCommand ToggleManualNamesCommand { get; }

    // ---------------------------------------------------------------- General y servidor

    /// <summary>Nombre del job.</summary>
    public string Name { get => _name; set => Edit(ref _name, value ?? string.Empty); }

    /// <summary>Si el job está habilitado.</summary>
    public bool Enabled { get => _enabled; set => Edit(ref _enabled, value); }

    /// <summary>Servidor o instancia.</summary>
    public string Server
    {
        get => _server;
        set { if (Edit(ref _server, value ?? string.Empty)) InvalidateServerDatabases(); }
    }

    /// <summary>Autenticación de Windows (si es <c>false</c>, autenticación SQL).</summary>
    public bool UseWindowsAuthentication
    {
        get => _useWindowsAuthentication;
        set
        {
            if (!Edit(ref _useWindowsAuthentication, value)) return;
            Raise(nameof(UseSqlAuthentication));
            InvalidateServerDatabases();
        }
    }

    /// <summary>Inverso de <see cref="UseWindowsAuthentication"/> (para habilitar campos).</summary>
    public bool UseSqlAuthentication => !_useWindowsAuthentication;

    /// <summary>Usuario SQL.</summary>
    public string UserName
    {
        get => _userName;
        set { if (Edit(ref _userName, value ?? string.Empty)) InvalidateServerDatabases(); }
    }

    /// <summary>Contraseña SQL nueva (texto en claro, solo en memoria; se cifra al guardar).</summary>
    public string Password
    {
        get => _password;
        set
        {
            if (!Edit(ref _password, value ?? string.Empty)) return;
            Raise(nameof(PasswordHint));
            InvalidateServerDatabases();
        }
    }

    /// <summary>Indica si el job ya tiene una contraseña guardada.</summary>
    public bool HasStoredPassword => !string.IsNullOrEmpty(_original.Connection.ProtectedPassword);

    /// <summary>Ayuda bajo el campo de contraseña.</summary>
    public string PasswordHint => HasStoredPassword && _password.Length == 0
        ? "Hay una contraseña guardada (cifrada). Déjelo en blanco para conservarla."
        : "Se guarda cifrada con DPAPI; nunca en texto plano.";

    /// <summary>Confía en el certificado del servidor.</summary>
    public bool TrustServerCertificate
    {
        get => _trustServerCertificate;
        set { if (Edit(ref _trustServerCertificate, value)) InvalidateServerDatabases(); }
    }

    /// <summary>Resultado de la última prueba de conexión.</summary>
    public string ConnectionTestResult { get => _connectionTestResult; private set => Set(ref _connectionTestResult, value); }

    // ---------------------------------------------------------------- Bases de datos

    /// <summary>Todas las bases de usuario.</summary>
    public bool IsAllDatabases { get => _dbMode == DatabaseSelectionMode.AllUserDatabases; set { if (value) SetMode(DatabaseSelectionMode.AllUserDatabases); } }

    /// <summary>Solo las bases indicadas por nombre.</summary>
    public bool IsExplicitDatabases { get => _dbMode == DatabaseSelectionMode.Explicit; set { if (value) SetMode(DatabaseSelectionMode.Explicit); } }

    /// <summary>Bases cuyo nombre coincide con un patrón.</summary>
    public bool IsPatternDatabases { get => _dbMode == DatabaseSelectionMode.Pattern; set { if (value) SetMode(DatabaseSelectionMode.Pattern); } }

    /// <summary>Nombres de bases, uno por línea.</summary>
    public string NamesText
    {
        get => _namesText;
        set
        {
            if (Edit(ref _namesText, value ?? string.Empty)) SyncChoicesFromNames();
        }
    }

    /// <summary>Patrón de nombres (<c>*</c> y <c>?</c>).</summary>
    public string Pattern { get => _pattern; set => Edit(ref _pattern, value ?? string.Empty); }

    /// <summary>Bases excluidas, una por línea.</summary>
    public string ExcludeText { get => _excludeText; set => Edit(ref _excludeText, value ?? string.Empty); }

    // ---------------------------------------------------------------- Opciones

    /// <summary>Comprimir el backup.</summary>
    public bool Compression { get => _compression; set => Edit(ref _compression, value); }

    /// <summary>Guardar cada backup como un .zip (se borra el .bak de la carpeta principal).</summary>
    public bool ZipOutput { get => _zipOutput; set => Edit(ref _zipOutput, value); }

    /// <summary>La carpeta principal es una unidad temporal: se vacía cuando las copias a los destinos finales están completas.</summary>
    public bool TemporaryPrimary { get => _temporaryPrimary; set => Edit(ref _temporaryPrimary, value); }

    /// <summary>Calcular y verificar sumas de comprobación.</summary>
    public bool Checksum { get => _checksum; set => Edit(ref _checksum, value); }

    /// <summary>Usar COPY_ONLY.</summary>
    public bool CopyOnly { get => _copyOnly; set => Edit(ref _copyOnly, value); }

    /// <summary>Verificar el backup al terminar.</summary>
    public bool VerifyAfterBackup { get => _verifyAfterBackup; set => Edit(ref _verifyAfterBackup, value); }

    /// <summary>Tiempo máximo por comando en segundos (0 = sin límite).</summary>
    public string TimeoutText { get => _timeoutText; set => Edit(ref _timeoutText, value ?? string.Empty); }

    /// <summary>Pone una plantilla de nombre lista para usar (el texto, o <c>default</c> para la predeterminada).</summary>
    public RelayCommand SetNameTemplateCommand { get; }

    /// <summary>Plantilla de ruta y nombre de archivo.</summary>
    public string NameTemplate { get => _nameTemplate; set => Edit(ref _nameTemplate, value ?? string.Empty); }

    /// <summary>Ejemplo de la ruta resultante con la plantilla actual.</summary>
    public string PathPreview { get => _pathPreview; private set => Set(ref _pathPreview, value); }

    // ---------------------------------------------------------------- Retención

    /// <summary>Borrar por antigüedad.</summary>
    public bool UseKeepDays { get => _useKeepDays; set => Edit(ref _useKeepDays, value); }

    /// <summary>Días a conservar.</summary>
    public string KeepDaysText { get => _keepDaysText; set => Edit(ref _keepDaysText, value ?? string.Empty); }

    /// <summary>Conservar un mínimo de archivos recientes.</summary>
    public bool UseKeepLast { get => _useKeepLast; set => Edit(ref _useKeepLast, value); }

    /// <summary>Cantidad mínima de archivos recientes.</summary>
    public string KeepLastText { get => _keepLastText; set => Edit(ref _keepLastText, value ?? string.Empty); }

    // ---------------------------------------------------------------- Avisos

    /// <summary>URL del webhook.</summary>
    public string WebhookUrl { get => _webhookUrl; set => Edit(ref _webhookUrl, value ?? string.Empty); }

    /// <summary>Avisar si todo sale bien.</summary>
    public bool OnSuccess { get => _onSuccess; set => Edit(ref _onSuccess, value); }

    /// <summary>Avisar si hay advertencias.</summary>
    public bool OnWarning { get => _onWarning; set => Edit(ref _onWarning, value); }

    /// <summary>Avisar si hay fallos.</summary>
    public bool OnFailure { get => _onFailure; set => Edit(ref _onFailure, value); }

    /// <summary>Avisar si el job pasa muchas horas sin ejecutarse.</summary>
    public bool UseMissedRun { get => _useMissedRun; set => Edit(ref _useMissedRun, value); }

    /// <summary>Horas sin ejecutarse a partir de las cuales se avisa.</summary>
    public string MissedRunHoursText { get => _missedRunHoursText; set => Edit(ref _missedRunHoursText, value ?? string.Empty); }

    /// <summary>Formatos disponibles para el cuerpo del mensaje.</summary>
    public IReadOnlyList<WebhookFormatOption> WebhookFormats => WebhookFormatOption.All;

    /// <summary>Modos de autenticación disponibles.</summary>
    public IReadOnlyList<WebhookAuthOption> WebhookAuths => WebhookAuthOption.All;

    /// <summary>Forma del cuerpo que se envía al webhook.</summary>
    public WebhookFormat WebhookFormat
    {
        get => _webhookFormat;
        set
        {
            if (!Edit(ref _webhookFormat, value)) return;
            Raise(nameof(ShowCustomTemplate));
            Raise(nameof(WebhookFormatHint));
        }
    }

    /// <summary>Explicación del formato elegido.</summary>
    public string WebhookFormatHint => _webhookFormat switch
    {
        WebhookFormat.Json => "Un JSON con el job, el estado, el servidor, las horas y una entrada por base de datos. Para HelpDesk, n8n, Zapier, Make o un servicio propio.",
        WebhookFormat.Custom => "Usted arma el JSON con una plantilla y valores como {job} o {message}. Útil para canales con un formato propio.",
        _ => "Un JSON {\"text\": \"…\"} con el aviso en texto. Es lo que esperan Slack, Teams y la mayoría de los webhooks entrantes."
    };

    /// <summary><c>true</c> si el formato es la plantilla propia.</summary>
    public bool ShowCustomTemplate => _webhookFormat == WebhookFormat.Custom;

    /// <summary>Plantilla JSON del formato personalizado.</summary>
    public string WebhookBodyTemplate { get => _webhookBodyTemplate; set => Edit(ref _webhookBodyTemplate, value ?? string.Empty); }

    /// <summary>Cómo se autentica el envío.</summary>
    public WebhookAuth WebhookAuth
    {
        get => _webhookAuth;
        set
        {
            if (!Edit(ref _webhookAuth, value)) return;
            Raise(nameof(ShowAuthHeaderName));
            Raise(nameof(ShowAuthSecret));
        }
    }

    /// <summary><c>true</c> si hay que pedir el nombre de la cabecera.</summary>
    public bool ShowAuthHeaderName => _webhookAuth == WebhookAuth.Header;

    /// <summary><c>true</c> si hay que pedir el token o la clave.</summary>
    public bool ShowAuthSecret => _webhookAuth != WebhookAuth.None;

    /// <summary>Nombre de la cabecera de autenticación propia.</summary>
    public string WebhookAuthHeader { get => _webhookAuthHeader; set => Edit(ref _webhookAuthHeader, value ?? string.Empty); }

    /// <summary>Token o clave escritos (la pantalla nunca muestra el guardado).</summary>
    public string WebhookSecret
    {
        get => _webhookSecret;
        set
        {
            if (!Edit(ref _webhookSecret, value ?? string.Empty)) return;
            Raise(nameof(WebhookSecretHint));
        }
    }

    /// <summary><c>true</c> si el job ya tenía un token guardado.</summary>
    public bool HasStoredWebhookSecret => !string.IsNullOrEmpty(_original.Notifications.ProtectedWebhookSecret);

    /// <summary>Texto bajo el campo del token.</summary>
    public string WebhookSecretHint => HasStoredWebhookSecret && _webhookSecret.Length == 0
        ? "Hay un token guardado (cifrado). Déjelo vacío para conservarlo o escriba uno nuevo para cambiarlo."
        : "Se guarda cifrado en este equipo; no se vuelve a mostrar.";

    /// <summary>Cabeceras adicionales, una por línea (<c>Nombre: valor</c>).</summary>
    public string WebhookHeadersText { get => _webhookHeadersText; set => Edit(ref _webhookHeadersText, value ?? string.Empty); }

    /// <summary>Reintentos ante fallos temporales.</summary>
    public string WebhookRetriesText { get => _webhookRetriesText; set => Edit(ref _webhookRetriesText, value ?? string.Empty); }

    /// <summary>Segundos máximos de espera por intento.</summary>
    public string WebhookTimeoutText { get => _webhookTimeoutText; set => Edit(ref _webhookTimeoutText, value ?? string.Empty); }

    /// <summary>Envía un mensaje de prueba al webhook escrito.</summary>
    public AsyncRelayCommand TestNotificationCommand { get; }

    /// <summary>Resultado del último aviso de prueba.</summary>
    public string NotificationTestResult { get => _notificationTestResult; private set => Set(ref _notificationTestResult, value); }

    /// <summary>Ejemplo del mensaje que llegará cuando un backup falle (armado con el mismo código que el aviso real).</summary>
    public string NotificationPreview { get => _notificationPreview; private set => Set(ref _notificationPreview, value); }

    // ---------------------------------------------------------------- Credenciales de Google (compartidas)

    /// <summary><c>true</c> si hay al menos un destino de Google Drive (se muestra el recuadro de credenciales).</summary>
    public bool HasDrive => Destinations.Any(d => d.IsDrive);

    /// <summary>ID de cliente de Google (se guarda una sola vez, para todos los destinos).</summary>
    public string GoogleClientId { get => _googleClientId; set => Edit(ref _googleClientId, value ?? string.Empty); }

    /// <summary>Secreto de cliente de Google (texto en claro, solo en memoria; se guarda cifrado).</summary>
    public string GoogleClientSecret
    {
        get => _googleClientSecret;
        set
        {
            if (!Edit(ref _googleClientSecret, value ?? string.Empty)) return;
            Raise(nameof(GoogleSecretHint));
        }
    }

    /// <summary><c>true</c> si ya se puede conectar con Google (credenciales guardadas, incluidas o recién escritas).</summary>
    public bool GoogleClientConfigured =>
        _hasSharedGoogleClient || GoogleClientTyped ||
        (HasDrive && Destinations.Where(d => d.IsDrive).All(d => d.HasOwnCredentials));

    /// <summary>Texto del estado de las credenciales.</summary>
    public string GoogleStatusText => GoogleClientConfigured ? "Configuradas" : "Pendiente de configurar";

    /// <summary>Muestra los pasos y los campos de las credenciales (se abre sola si faltan).</summary>
    public bool ShowGoogleSetup
    {
        get => _showGoogleSetup;
        set
        {
            if (Set(ref _showGoogleSetup, value)) Raise(nameof(GoogleSetupButtonText));
        }
    }

    /// <summary>Texto del botón que muestra u oculta los campos.</summary>
    public string GoogleSetupButtonText => _showGoogleSetup ? "Ocultar" : GoogleClientConfigured ? "Cambiar" : "Configurar";

    /// <summary>Ayuda bajo el campo del secreto.</summary>
    public string GoogleSecretHint => _hasSharedGoogleClient && _googleClientSecret.Length == 0
        ? "Ya hay credenciales guardadas (cifradas). Escriba otras solo si quiere cambiarlas."
        : "Se guarda cifrado con DPAPI; nunca en texto plano.";

    private bool GoogleClientTyped => _googleClientId.Trim().Length > 0 && _googleClientSecret.Length > 0;

    /// <summary>Credenciales que se usarán al conectar o probar: las recién escritas o las guardadas.</summary>
    private GoogleClient? ResolveSharedClient() =>
        GoogleClientTyped
            ? new GoogleClient(_googleClientId.Trim(), _googleClientSecret)
            : _svc.GoogleClients.GetDefault();

    // ---------------------------------------------------------------- Bases del servidor (lista para elegir)

    private bool _showManualNames;
    private bool _syncingNames;
    private string _databasesLoadResult = string.Empty;

    /// <summary>Bases del servidor, para marcarlas (se llena al probar la conexión o al pulsar "Cargar bases").</summary>
    public ObservableCollection<DatabaseChoice> ServerDatabases { get; } = [];

    /// <summary><c>true</c> si ya se leyó la lista de bases del servidor.</summary>
    public bool HasServerDatabases => ServerDatabases.Count > 0;

    /// <summary>Elegir escribir los nombres a mano aunque haya lista.</summary>
    public bool ShowManualNames
    {
        get => _showManualNames;
        set
        {
            if (!Set(ref _showManualNames, value)) return;
            RaiseDatabaseList();
        }
    }

    /// <summary>Se muestra la lista de bases con casillas (hay lista y no se eligió escribir a mano).</summary>
    public bool ShowDatabaseList => HasServerDatabases && !_showManualNames;

    /// <summary>Se muestra el cuadro para escribir los nombres a mano.</summary>
    public bool ShowManualEditor => !ShowDatabaseList;

    /// <summary>Resultado de la última lectura de bases (vacío si fue bien).</summary>
    public string DatabasesLoadResult { get => _databasesLoadResult; private set => Set(ref _databasesLoadResult, value); }

    /// <summary>Cantidad de bases marcadas en la lista.</summary>
    public int SelectedDatabaseCount => ServerDatabases.Count(c => c.IsSelected);

    /// <summary>Texto de ayuda sobre la lista de bases.</summary>
    public string DatabasesHint => HasServerDatabases
        ? $"{ServerDatabases.Count} bases en el servidor · {SelectedDatabaseCount} elegidas."
        : "Pruebe la conexión en «General y servidor» o pulse «Cargar bases del servidor» para elegirlas de una lista.";

    private void SetServerDatabases(IEnumerable<string> names)
    {
        var chosen = SplitLines(_namesText).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ServerDatabases.Clear();
        foreach (var name in names.Order(StringComparer.OrdinalIgnoreCase))
            ServerDatabases.Add(new DatabaseChoice(name, chosen.Contains(name), _ => UpdateNamesFromChoices()));

        _showManualNames = false;
        Raise(nameof(ShowManualNames));
        RaiseDatabaseList();
    }

    /// <summary>La lista ya no corresponde al servidor/credenciales actuales: se descarta.</summary>
    private void InvalidateServerDatabases()
    {
        if (ServerDatabases.Count == 0 && ConnectionTestResult.Length == 0 && DatabasesLoadResult.Length == 0) return;

        ServerDatabases.Clear();
        ConnectionTestResult = string.Empty;
        DatabasesLoadResult = string.Empty;
        RaiseDatabaseList();
    }

    private void RaiseDatabaseList()
    {
        Raise(nameof(HasServerDatabases));
        Raise(nameof(ShowDatabaseList));
        Raise(nameof(ShowManualEditor));
        Raise(nameof(SelectedDatabaseCount));
        Raise(nameof(DatabasesHint));
    }

    /// <summary>Al marcar/desmarcar en la lista: reescribe los nombres, respetando los que se escribieron a mano y no están en el servidor.</summary>
    private void UpdateNamesFromChoices()
    {
        if (_syncingNames) return;
        _syncingNames = true;
        try
        {
            var known = ServerDatabases.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var extras = SplitLines(_namesText).Where(n => !known.Contains(n));
            var names = ServerDatabases.Where(c => c.IsSelected).Select(c => c.Name).Concat(extras);
            NamesText = string.Join(Environment.NewLine, names);
        }
        finally
        {
            _syncingNames = false;
        }

        Raise(nameof(SelectedDatabaseCount));
        Raise(nameof(DatabasesHint));
    }

    /// <summary>Al escribir los nombres a mano: marca en la lista las bases que coinciden.</summary>
    private void SyncChoicesFromNames()
    {
        if (_syncingNames || ServerDatabases.Count == 0) return;
        _syncingNames = true;
        try
        {
            var chosen = SplitLines(_namesText).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var choice in ServerDatabases) choice.SetSelected(chosen.Contains(choice.Name));
        }
        finally
        {
            _syncingNames = false;
        }

        Raise(nameof(SelectedDatabaseCount));
        Raise(nameof(DatabasesHint));
    }

    private void SelectDatabases(bool userOnly)
    {
        foreach (var choice in ServerDatabases) choice.SetSelected(userOnly && !choice.IsSystem);
        UpdateNamesFromChoices();
    }

    // ---------------------------------------------------------------- Lógica

    private bool Edit<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (!Set(ref field, value, name)) return false;
        Revalidate();
        return true;
    }

    private void SetMode(DatabaseSelectionMode mode)
    {
        if (!Edit(ref _dbMode, mode, nameof(_dbMode))) return;
        Raise(nameof(IsAllDatabases));
        Raise(nameof(IsExplicitDatabases));
        Raise(nameof(IsPatternDatabases));
    }

    private StepEditorViewModel NewStep(BackupStep step) => new(step, Revalidate, RemoveStep);

    private DestinationEditorViewModel NewDestination(DestinationConfig d) =>
        new(d, Revalidate, RemoveDestination, MoveDestination, _svc, ResolveSharedClient, () => ShowGoogleSetup = true);

    private void AddStep()
    {
        var hasFull = Steps.Any(s => s.Type == BackupType.Full);
        Steps.Add(NewStep(new BackupStep
        {
            Type = hasFull ? BackupType.Differential : BackupType.Full,
            Cron = hasFull ? "0 22 * * 1-6" : "0 23 * * *"
        }));
        Revalidate();
    }

    private void RemoveStep(StepEditorViewModel step)
    {
        Steps.Remove(step);
        Revalidate();
    }

    private void ApplyPlan(PlanPreset preset)
    {
        Steps.Clear();
        foreach (var step in JobFactory.CreateSteps(preset)) Steps.Add(NewStep(step));
        Revalidate();
    }

    private static PlanPreset ParsePreset(object? parameter) => parameter switch
    {
        PlanPreset p => p,
        string s => Enum.Parse<PlanPreset>(s),
        _ => throw new ArgumentException("Plan no válido.", nameof(parameter))
    };

    private void AddDestination()
    {
        Destinations.Add(NewDestination(new DestinationConfig
        {
            Name = Destinations.Count == 0 ? "Disco local" : $"Copia {Destinations.Count}"
        }));
        Revalidate();
    }

    private void AddDriveDestination()
    {
        // Drive nunca es el destino principal: se crea la carpeta principal si falta y la copia va después.
        if (Destinations.Count == 0) AddDestination();
        if (!GoogleClientConfigured) ShowGoogleSetup = true;
        Destinations.Add(NewDestination(new DestinationConfig
        {
            Name = "Google Drive",
            Kind = DestinationKind.GoogleDrive,
            Drive = new DriveSettings()
        }));
        Revalidate();
    }

    private void RemoveDestination(DestinationEditorViewModel destination)
    {
        Destinations.Remove(destination);
        Revalidate();
    }

    private void MoveDestination(DestinationEditorViewModel destination, int delta)
    {
        var index = Destinations.IndexOf(destination);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Destinations.Count) return;
        Destinations.Move(index, target);
        Revalidate();
    }

    private async Task TestNotificationAsync()
    {
        if (string.IsNullOrWhiteSpace(WebhookUrl))
        {
            NotificationTestResult = "Escriba primero la dirección del webhook.";
            return;
        }
        if (_svc.Notifier is null)
        {
            NotificationTestResult = "No hay un canal de avisos disponible en esta ventana.";
            return;
        }

        // Se prueba con lo que hay en pantalla (formato, plantilla, autenticación…), no con lo último guardado.
        if (Errors.FirstOrDefault(e => e.Key.StartsWith("Notifications.", StringComparison.Ordinal)) is { Value: not null } problem)
        {
            NotificationTestResult = "✖ Corrija primero: " + problem.Value;
            return;
        }
        if (WebhookAuth != WebhookAuth.None && _webhookSecret.Length == 0 && !HasStoredWebhookSecret)
        {
            NotificationTestResult = "Escriba primero el token o la clave de autorización.";
            return;
        }

        NotificationTestResult = "Enviando…";
        var settings = BuildDraft(new List<ValidationIssue>()).Notifications;
        var name = string.IsNullOrWhiteSpace(Name) ? "sin nombre" : Name.Trim();
        try
        {
            if (settings.ProtectedWebhookSecret == PendingWebhookSecret)
                settings.ProtectedWebhookSecret = _svc.Protector.Protect(_webhookSecret);
            await _svc.Notifier.SendMessageAsync(
                settings, NoticeKind.Test,
                $"[PRUEBA] SQL Backup N - aviso de prueba del job '{name}' desde {Environment.MachineName}. Si lo está leyendo, los avisos funcionan.",
                CancellationToken.None);
            NotificationTestResult = "✔ Enviado. Compruebe que llegó al canal.";
        }
        catch (Exception ex)
        {
            NotificationTestResult = "✖ No se pudo enviar: " + ex.Message;
        }
    }

    /// <summary>
    /// Lo que se enviaría ante un fallo, armado con el mismo código que usa el aviso real (con el formato, la plantilla y los
    /// valores de la pantalla).
    /// </summary>
    private static string BuildNotificationPreview(BackupJob draft)
    {
        var started = new DateTime(2026, 11, 17, 23, 0, 0, DateTimeKind.Utc);
        var record = new JobRunRecord
        {
            JobId = draft.Id,
            JobName = string.IsNullOrWhiteSpace(draft.Name) ? "Mi job" : draft.Name.Trim(),
            Type = BackupType.Full,
            Status = RunStatus.Failed,
            StartedUtc = started,
            FinishedUtc = started.AddSeconds(52),
            Results =
            [
                new DatabaseBackupResult { Database = "Ventas", Status = RunStatus.Succeeded, SizeBytes = 308_000_000, Duration = TimeSpan.FromSeconds(48), FilePath = "Backup20261117.zip" },
                new DatabaseBackupResult { Database = "Compras", Status = RunStatus.Failed, Error = "no hay espacio en disco" }
            ]
        };
        return WebhookPayload.ForRun(draft, record, Environment.MachineName);
    }

    private async Task TestConnectionAsync()
    {
        ConnectionTestResult = "Probando conexión…";
        ConnectionTestResult = await RefreshServerDatabasesAsync(describe: true);
    }

    private async Task LoadDatabasesAsync()
    {
        DatabasesLoadResult = "Leyendo las bases del servidor…";
        var result = await RefreshServerDatabasesAsync(describe: false);
        DatabasesLoadResult = result.StartsWith('✖') ? result : string.Empty;
    }

    /// <summary>
    /// Se conecta al servidor, lee sus bases y las deja disponibles para elegirlas en la lista.
    /// Devuelve el mensaje para el usuario (empieza por ✔ o ✖).
    /// </summary>
    private async Task<string> RefreshServerDatabasesAsync(bool describe)
    {
        if (string.IsNullOrWhiteSpace(Server))
            return "✖ Indique primero el servidor (pestaña «General y servidor»).";

        try
        {
            var connection = new SqlServerConnection
            {
                Server = Server.Trim(),
                UseWindowsAuthentication = UseWindowsAuthentication,
                UserName = UserName.Trim(),
                TrustServerCertificate = TrustServerCertificate,
                ProtectedPassword = UseWindowsAuthentication ? null
                    : _password.Length > 0 ? _svc.Protector.Protect(_password)
                    : _original.Connection.ProtectedPassword
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var databases = await _svc.Executor.ListDatabasesAsync(connection, cts.Token);
            SetServerDatabases(databases);

            if (!describe) return $"✔ {databases.Count} bases leídas.";

            var sample = string.Join(", ", databases.Take(12)) + (databases.Count > 12 ? "…" : string.Empty);
            return $"✔ Conexión correcta · {databases.Count} bases: {sample}. Ya puede elegirlas en la pestaña «Bases de datos».";
        }
        catch (Exception ex)
        {
            return "✖ " + ex.Message.Split('\n')[0].Trim();
        }
    }

    private void Save()
    {
        if (!CanSave) return;

        var job = BuildDraft(new List<ValidationIssue>());
        try
        {
            if (job.Connection.ProtectedPassword == PendingPassword)
                job.Connection.ProtectedPassword = _svc.Protector.Protect(_password);
            if (job.Notifications.ProtectedWebhookSecret == PendingWebhookSecret)
                job.Notifications.ProtectedWebhookSecret = _svc.Protector.Protect(_webhookSecret);

            // Credenciales de Google: se guardan una sola vez, cifradas, para todos los destinos.
            if (GoogleClientTyped && HasDrive)
                _svc.GoogleClients.SaveDefault(new GoogleClient(_googleClientId.Trim(), _googleClientSecret));
        }
        catch (Exception ex)
        {
            _svc.Dialogs.ShowMessage("No se pudo guardar un dato protegido", ex.Message);
            return;
        }

        Result = job;
        CloseRequested?.Invoke(true);
    }

    /// <summary>
    /// Convierte el texto editado en un <see cref="BackupJob"/>. Los números mal escritos se
    /// reportan en <paramref name="parseIssues"/> y se sustituyen por un valor válido para que el
    /// validador no duplique el mensaje.
    /// </summary>
    private BackupJob BuildDraft(List<ValidationIssue> parseIssues)
    {
        var sqlAuth = !UseWindowsAuthentication;
        var job = new BackupJob
        {
            Id = _original.Id,
            CreatedUtc = _original.CreatedUtc,
            Name = Name.Trim(),
            Enabled = Enabled,
            Connection = new SqlServerConnection
            {
                Server = Server.Trim(),
                UseWindowsAuthentication = UseWindowsAuthentication,
                UserName = sqlAuth ? UserName.Trim() : null,
                ProtectedPassword = !sqlAuth ? null
                    : _password.Length > 0 ? PendingPassword
                    : _original.Connection.ProtectedPassword,
                TrustServerCertificate = TrustServerCertificate
            },
            Databases = new DatabaseSelection
            {
                Mode = _dbMode,
                Names = SplitLines(NamesText),
                Pattern = string.IsNullOrWhiteSpace(Pattern) ? null : Pattern.Trim(),
                Exclude = SplitLines(ExcludeText)
            },
            Steps = Steps.Select(s => s.ToStep()).ToList(),
            Destinations = Destinations.Select(d => d.ToDestination()).ToList(),
            Options = new BackupOptions
            {
                Compression = Compression,
                ZipOutput = ZipOutput,
                TemporaryPrimary = TemporaryPrimary,
                Checksum = Checksum,
                CopyOnly = CopyOnly,
                VerifyAfterBackup = VerifyAfterBackup,
                CommandTimeoutSeconds = ParseInt(TimeoutText, "Options.CommandTimeoutSeconds", "El tiempo máximo", parseIssues, 0) ?? 0,
                PathTemplate = NameTemplate.Trim()
            },
            Retention = new RetentionPolicy
            {
                KeepDays = UseKeepDays ? ParseInt(KeepDaysText, "Retention.KeepDays", "Los días", parseIssues, 30) : null,
                KeepLast = UseKeepLast ? ParseInt(KeepLastText, "Retention.KeepLast", "La cantidad", parseIssues, 3) : null
            },
            Notifications = new NotificationSettings
            {
                WebhookUrl = string.IsNullOrWhiteSpace(WebhookUrl) ? null : WebhookUrl.Trim(),
                OnSuccess = OnSuccess,
                OnWarning = OnWarning,
                OnFailure = OnFailure,
                MissedRunHours = UseMissedRun
                    ? ParseInt(MissedRunHoursText, "Notifications.MissedRunHours", "Las horas", parseIssues, 26) ?? 0
                    : 0,
                WebhookFormat = WebhookFormat,
                WebhookBodyTemplate = WebhookFormat == WebhookFormat.Custom && !string.IsNullOrWhiteSpace(WebhookBodyTemplate)
                    ? WebhookBodyTemplate.Trim() : null,
                WebhookAuth = WebhookAuth,
                WebhookAuthHeader = WebhookAuth == WebhookAuth.Header ? WebhookAuthHeader.Trim() : null,
                ProtectedWebhookSecret = WebhookAuth == WebhookAuth.None ? null
                    : _webhookSecret.Length > 0 ? PendingWebhookSecret
                    : _original.Notifications.ProtectedWebhookSecret,
                WebhookHeaders = SplitLines(WebhookHeadersText),
                WebhookRetries = ParseInt(WebhookRetriesText, "Notifications.WebhookRetries", "Los reintentos", parseIssues, 2) ?? 2,
                WebhookTimeoutSeconds = ParseInt(WebhookTimeoutText, "Notifications.WebhookTimeoutSeconds", "El tiempo de espera", parseIssues, 15) ?? 15
            }
        };
        // Un paso por días (semanal, cada pocas horas/minutos) sin ningún día marcado no genera cron válido.
        for (var i = 0; i < Steps.Count; i++)
        {
            if (Steps[i].NoDays)
                parseIssues.Add(new ValidationIssue(IssueSeverity.Missing, $"Steps[{i}].Cron", "Marque al menos un día de la semana."));
        }

        return job;
    }

    private static int? ParseInt(string text, string field, string label, List<ValidationIssue> issues, int fallback)
    {
        if (int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)) return value;
        issues.Add(new ValidationIssue(IssueSeverity.Error, field, $"{label} debe ser un número entero (0-9)."));
        return fallback;
    }

    private static List<string> SplitLines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>Recalcula todos los mensajes de validación y las vistas previas.</summary>
    private void Revalidate()
    {
        if (_loading) return;

        var issues = new List<ValidationIssue>();
        var draft = BuildDraft(issues);
        issues.AddRange(JobValidator.Validate(draft, _otherNames, GoogleClientConfigured));

        Errors = ToMap(issues, IssueSeverity.Error);
        Pending = ToMap(issues, IssueSeverity.Missing);
        Warnings = ToMap(issues, IssueSeverity.Warning);

        Issues.Clear();
        foreach (var issue in issues.OrderBy(i => i.Severity))
            Issues.Add(new IssueRow(issue.Severity, AreaOf(issue.Field), issue.Message));

        ErrorCount = issues.Count(i => i.Severity == IssueSeverity.Error);
        MissingCount = issues.Count(i => i.Severity == IssueSeverity.Missing);
        WarningCount = issues.Count(i => i.Severity == IssueSeverity.Warning);
        Raise(nameof(CanSave));
        Raise(nameof(SummaryText));
        Raise(nameof(SummaryKind));
        Raise(nameof(SummaryIcon));
        SaveCommand.NotifyCanExecuteChanged();

        var now = _svc.Clock.GetUtcNow().UtcDateTime;
        for (var i = 0; i < Steps.Count; i++)
        {
            var step = Steps[i];
            step.Error = Errors.GetValueOrDefault($"Steps[{i}].Cron", string.Empty);
            step.NextRuns = step.Error.Length > 0 ? string.Empty : DescribeNextRuns(step, now);
        }

        for (var i = 0; i < Destinations.Count; i++)
        {
            Destinations[i].Role = i == 0 ? "Principal · lo escribe SQL Server" : "Copia";
            var d = Destinations[i];
            d.NameError = Errors.GetValueOrDefault($"Destinations[{i}].Name", string.Empty);
            d.PathError = Errors.GetValueOrDefault($"Destinations[{i}].Path", string.Empty);
            d.KindError = Errors.GetValueOrDefault($"Destinations[{i}].Kind", string.Empty);
            d.NamePending = Pending.GetValueOrDefault($"Destinations[{i}].Name", string.Empty);
            d.PathPending = Pending.GetValueOrDefault($"Destinations[{i}].Path", string.Empty);
            d.ClientPending = Pending.GetValueOrDefault($"Destinations[{i}].Drive.Client", string.Empty);
            d.AccountPending = Pending.GetValueOrDefault($"Destinations[{i}].Drive.Account", string.Empty);
            d.FolderPending = Pending.GetValueOrDefault($"Destinations[{i}].Drive.Folder", string.Empty);
            d.IsPrimary = i == 0;
            d.ClientError = Errors.GetValueOrDefault($"Destinations[{i}].Drive.Client", string.Empty);
            d.AccountError = Errors.GetValueOrDefault($"Destinations[{i}].Drive.Account", string.Empty);
            d.FolderError = Errors.GetValueOrDefault($"Destinations[{i}].Drive.Folder", string.Empty);
        }

        Raise(nameof(HasDrive));
        Raise(nameof(GoogleClientConfigured));
        Raise(nameof(GoogleStatusText));
        Raise(nameof(GoogleSetupButtonText));

        PathPreview = BuildPathPreview(draft);
        NotificationPreview = BuildNotificationPreview(draft);
    }

    /// <summary>Traduce el campo del validador a la pestaña donde el usuario lo corrige.</summary>
    private static string AreaOf(string field)
    {
        var item = System.Text.RegularExpressions.Regex.Match(field, @"\[(\d+)\]");
        var number = item.Success ? int.Parse(item.Groups[1].Value, CultureInfo.InvariantCulture) + 1 : 0;

        return field switch
        {
            _ when field.StartsWith("Steps[", StringComparison.Ordinal) => $"Programación (paso {number})",
            _ when field.StartsWith("Destinations[", StringComparison.Ordinal) => $"Destinos (destino {number})",
            "Steps" => "Programación",
            "Destinations" => "Destinos",
            "Name" => "General",
            _ when field.StartsWith("Connection", StringComparison.Ordinal) => "Servidor",
            _ when field.StartsWith("Databases", StringComparison.Ordinal) => "Bases de datos",
            _ when field.StartsWith("Options", StringComparison.Ordinal) => "Opciones",
            _ when field.StartsWith("Retention", StringComparison.Ordinal) => "Retención",
            _ when field.StartsWith("Notifications", StringComparison.Ordinal) => "Avisos",
            _ => "Job"
        };
    }

    private string DescribeNextRuns(StepEditorViewModel step, DateTime nowUtc)
    {
        if (!step.Enabled) return "Paso desactivado";
        var next = ScheduleEvaluator.NextOccurrences(step.Cron, nowUtc, _svc.Clock.LocalTimeZone, 3);
        if (next.Count == 0) return string.Empty;

        var local = next.Select(t => TimeZoneInfo.ConvertTimeFromUtc(t, _svc.Clock.LocalTimeZone)
            .ToString("ddd dd/MM HH:mm", CultureInfo.CurrentCulture));
        return "Próximas: " + string.Join(" · ", local);
    }

    private string BuildPathPreview(BackupJob draft)
    {
        if (Errors.ContainsKey("Options.PathTemplate") && draft.Options.PathTemplate.Length == 0) return string.Empty;
        try
        {
            var server = draft.Connection.Server.Length > 0 ? draft.Connection.Server : "SERVIDOR";
            var relative = PathTemplate.Render(draft.Options.PathTemplate,
                new TemplateContext(server, "Ventas", BackupType.Full, _svc.Clock.GetLocalNow().DateTime));
            var folder = draft.Destinations.Count > 0 && draft.Destinations[0].Path.Length > 0
                ? draft.Destinations[0].Path.TrimEnd('\\', '/')
                : "<carpeta>";
            return $"Ejemplo: {folder}{Path.DirectorySeparatorChar}{relative}{(draft.Options.ZipOutput ? ".zip" : ".bak")}";
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }

    private static Dictionary<string, string> ToMap(IEnumerable<ValidationIssue> issues, IssueSeverity severity) =>
        issues.Where(i => i.Severity == severity)
              .GroupBy(i => i.Field)
              .ToDictionary(g => g.Key, g => string.Join(" ", g.Select(i => i.Message)));
}
