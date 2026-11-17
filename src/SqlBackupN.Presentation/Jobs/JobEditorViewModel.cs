using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using SqlBackupN.Core.Domain;
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

    // Resultado de la validación
    private IReadOnlyDictionary<string, string> _errors = new Dictionary<string, string>();
    private IReadOnlyDictionary<string, string> _warnings = new Dictionary<string, string>();
    private int _errorCount;
    private int _warningCount;

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

        foreach (var step in job.Steps) Steps.Add(NewStep(step));
        foreach (var destination in job.Destinations) Destinations.Add(NewDestination(destination));

        SaveCommand = new RelayCommand(Save, () => CanSave);
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(false));
        AddStepCommand = new RelayCommand(AddStep);
        ApplyPlanCommand = new RelayCommand(p => ApplyPlan(ParsePreset(p)));
        AddDestinationCommand = new RelayCommand(AddDestination);
        AddDriveDestinationCommand = new RelayCommand(AddDriveDestination);
        TestConnectionCommand = new AsyncRelayCommand(TestConnectionAsync);

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

    /// <summary>Guardar solo es posible sin errores (las advertencias no lo impiden).</summary>
    public bool CanSave => ErrorCount == 0;

    /// <summary>Texto del resumen: "2 errores · 1 advertencia" o "Todo correcto".</summary>
    public string SummaryText => (ErrorCount, WarningCount) switch
    {
        (0, 0) => "Todo correcto",
        (0, var w) => $"Sin errores · {w} {(w == 1 ? "advertencia" : "advertencias")}",
        var (e, w) => $"{e} {(e == 1 ? "error" : "errores")} · {w} {(w == 1 ? "advertencia" : "advertencias")}"
    };

    /// <summary>Clave de color del resumen: <c>error</c>, <c>warning</c> u <c>ok</c>.</summary>
    public string SummaryKind => ErrorCount > 0 ? "error" : WarningCount > 0 ? "warning" : "ok";

    /// <summary>Símbolo del resumen.</summary>
    public string SummaryIcon => ErrorCount > 0 ? "✖" : WarningCount > 0 ? "⚠" : "✔";

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

    /// <summary>Agrega un destino de tipo Google Drive.</summary>
    public RelayCommand AddDriveDestinationCommand { get; }

    /// <summary>Prueba la conexión con el servidor.</summary>
    public AsyncRelayCommand TestConnectionCommand { get; }

    // ---------------------------------------------------------------- General y servidor

    /// <summary>Nombre del job.</summary>
    public string Name { get => _name; set => Edit(ref _name, value ?? string.Empty); }

    /// <summary>Si el job está habilitado.</summary>
    public bool Enabled { get => _enabled; set => Edit(ref _enabled, value); }

    /// <summary>Servidor o instancia.</summary>
    public string Server { get => _server; set => Edit(ref _server, value ?? string.Empty); }

    /// <summary>Autenticación de Windows (si es <c>false</c>, autenticación SQL).</summary>
    public bool UseWindowsAuthentication
    {
        get => _useWindowsAuthentication;
        set
        {
            if (!Edit(ref _useWindowsAuthentication, value)) return;
            Raise(nameof(UseSqlAuthentication));
        }
    }

    /// <summary>Inverso de <see cref="UseWindowsAuthentication"/> (para habilitar campos).</summary>
    public bool UseSqlAuthentication => !_useWindowsAuthentication;

    /// <summary>Usuario SQL.</summary>
    public string UserName { get => _userName; set => Edit(ref _userName, value ?? string.Empty); }

    /// <summary>Contraseña SQL nueva (texto en claro, solo en memoria; se cifra al guardar).</summary>
    public string Password
    {
        get => _password;
        set
        {
            if (!Edit(ref _password, value ?? string.Empty)) return;
            Raise(nameof(PasswordHint));
        }
    }

    /// <summary>Indica si el job ya tiene una contraseña guardada.</summary>
    public bool HasStoredPassword => !string.IsNullOrEmpty(_original.Connection.ProtectedPassword);

    /// <summary>Ayuda bajo el campo de contraseña.</summary>
    public string PasswordHint => HasStoredPassword && _password.Length == 0
        ? "Hay una contraseña guardada (cifrada). Déjelo en blanco para conservarla."
        : "Se guarda cifrada con DPAPI; nunca en texto plano.";

    /// <summary>Confía en el certificado del servidor.</summary>
    public bool TrustServerCertificate { get => _trustServerCertificate; set => Edit(ref _trustServerCertificate, value); }

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
    public string NamesText { get => _namesText; set => Edit(ref _namesText, value ?? string.Empty); }

    /// <summary>Patrón de nombres (<c>*</c> y <c>?</c>).</summary>
    public string Pattern { get => _pattern; set => Edit(ref _pattern, value ?? string.Empty); }

    /// <summary>Bases excluidas, una por línea.</summary>
    public string ExcludeText { get => _excludeText; set => Edit(ref _excludeText, value ?? string.Empty); }

    // ---------------------------------------------------------------- Opciones

    /// <summary>Comprimir el backup.</summary>
    public bool Compression { get => _compression; set => Edit(ref _compression, value); }

    /// <summary>Calcular y verificar sumas de comprobación.</summary>
    public bool Checksum { get => _checksum; set => Edit(ref _checksum, value); }

    /// <summary>Usar COPY_ONLY.</summary>
    public bool CopyOnly { get => _copyOnly; set => Edit(ref _copyOnly, value); }

    /// <summary>Verificar el backup al terminar.</summary>
    public bool VerifyAfterBackup { get => _verifyAfterBackup; set => Edit(ref _verifyAfterBackup, value); }

    /// <summary>Tiempo máximo por comando en segundos (0 = sin límite).</summary>
    public string TimeoutText { get => _timeoutText; set => Edit(ref _timeoutText, value ?? string.Empty); }

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
        new(d, Revalidate, RemoveDestination, MoveDestination, _svc);

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

    private async Task TestConnectionAsync()
    {
        if (string.IsNullOrWhiteSpace(Server))
        {
            ConnectionTestResult = "✖ Indique primero el servidor.";
            return;
        }

        ConnectionTestResult = "Probando conexión…";
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
            var sample = string.Join(", ", databases.Take(12)) + (databases.Count > 12 ? "…" : string.Empty);
            ConnectionTestResult = $"✔ Conexión correcta · {databases.Count} bases: {sample}";
        }
        catch (Exception ex)
        {
            ConnectionTestResult = "✖ " + ex.Message.Split('\n')[0].Trim();
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

            // El borrador y la lista de destinos tienen el mismo orden.
            for (var i = 0; i < Destinations.Count; i++)
                Destinations[i].ProtectPending(job.Destinations[i]);
        }
        catch (Exception ex)
        {
            _svc.Dialogs.ShowMessage("No se pudo cifrar la contraseña", ex.Message);
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
                OnFailure = OnFailure
            }
        };
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
        issues.AddRange(JobValidator.Validate(draft, _otherNames));

        Errors = ToMap(issues, IssueSeverity.Error);
        Warnings = ToMap(issues, IssueSeverity.Warning);

        Issues.Clear();
        foreach (var issue in issues.OrderBy(i => i.Severity))
            Issues.Add(new IssueRow(issue.Severity == IssueSeverity.Error, AreaOf(issue.Field), issue.Message));

        ErrorCount = issues.Count(i => i.Severity == IssueSeverity.Error);
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
            d.ClientIdError = Errors.GetValueOrDefault($"Destinations[{i}].Drive.ClientId", string.Empty);
            d.ClientSecretError = Errors.GetValueOrDefault($"Destinations[{i}].Drive.ClientSecret", string.Empty);
            d.AccountError = Errors.GetValueOrDefault($"Destinations[{i}].Drive.Account", string.Empty);
            d.FolderError = Errors.GetValueOrDefault($"Destinations[{i}].Drive.Folder", string.Empty);
        }

        PathPreview = BuildPathPreview(draft);
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
            return $"Ejemplo: {folder}{Path.DirectorySeparatorChar}{relative}.bak";
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
