using SqlBackupN.Core.Abstractions;
using SqlBackupN.Core.Domain;
using SqlBackupN.Presentation.Infrastructure;

namespace SqlBackupN.Presentation.Jobs;

/// <summary>Servicios que necesita el editor de jobs (agrupados para no alargar los constructores).</summary>
/// <param name="Protector">Cifra contraseñas y secretos antes de guardarlos.</param>
/// <param name="Executor">Prueba la conexión con SQL Server.</param>
/// <param name="Files">Prueba que un destino de carpeta sea escribible.</param>
/// <param name="Remote">Prueba los destinos en la nube (Google Drive).</param>
/// <param name="Authorizer">Conecta la cuenta de Google.</param>
/// <param name="Dialogs">Selector de carpetas y mensajes.</param>
/// <param name="Clock">Reloj (para calcular próximas ejecuciones).</param>
public sealed record EditorServices(
    ISecretProtector Protector,
    IBackupExecutor Executor,
    IFileStore Files,
    IRemoteStorage Remote,
    IGoogleAuthorizer Authorizer,
    IDialogService Dialogs,
    TimeProvider Clock);

/// <summary>Opción de la lista de tipos de backup.</summary>
/// <param name="Value">Tipo de backup.</param>
/// <param name="Label">Texto visible.</param>
public sealed record TypeOption(BackupType Value, string Label)
{
    /// <summary>Todas las opciones, en el orden en que se muestran.</summary>
    public static IReadOnlyList<TypeOption> All { get; } =
    [
        new(BackupType.Full, "Completo"),
        new(BackupType.Differential, "Diferencial"),
        new(BackupType.TransactionLog, "Log de transacciones")
    ];

    /// <summary>Texto visible de un tipo de backup.</summary>
    public static string LabelOf(BackupType type) => All.First(o => o.Value == type).Label;
}

/// <summary>Opción de la lista de tipos de destino.</summary>
/// <param name="Value">Tipo de destino.</param>
/// <param name="Label">Texto visible.</param>
public sealed record KindOption(DestinationKind Value, string Label)
{
    /// <summary>Todas las opciones, en el orden en que se muestran.</summary>
    public static IReadOnlyList<KindOption> All { get; } =
    [
        new(DestinationKind.Folder, "Carpeta (local, unidad o red)"),
        new(DestinationKind.GoogleDrive, "Google Drive")
    ];
}

/// <summary>Fila del resumen de problemas de validación.</summary>
/// <param name="IsError"><c>true</c> para errores, <c>false</c> para advertencias.</param>
/// <param name="Area">Pestaña o sección afectada, p. ej. "Programación (paso 2)".</param>
/// <param name="Message">Texto del problema.</param>
public sealed record IssueRow(bool IsError, string Area, string Message)
{
    /// <summary>Símbolo que acompaña al mensaje.</summary>
    public string Icon => IsError ? "✖" : "⚠";

    /// <summary>Texto completo de la fila: área y mensaje.</summary>
    public string Text => $"{Area}: {Message}";
}

/// <summary>Un paso de programación dentro del editor.</summary>
public sealed class StepEditorViewModel : ViewModelBase
{
    private readonly Action _changed;
    private BackupType _type;
    private string _cron;
    private bool _enabled;
    private string _nextRuns = string.Empty;
    private string _error = string.Empty;

    /// <summary>Crea el view-model a partir de un paso existente.</summary>
    /// <param name="step">Paso de origen (se conserva su identificador).</param>
    /// <param name="changed">Se invoca al cambiar cualquier campo editable.</param>
    /// <param name="remove">Se invoca al pulsar "Quitar".</param>
    public StepEditorViewModel(BackupStep step, Action changed, Action<StepEditorViewModel> remove)
    {
        Id = step.Id;
        _type = step.Type;
        _cron = step.Cron;
        _enabled = step.Enabled;
        _changed = changed;
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    /// <summary>Identificador del paso (se conserva para no perder el historial de última ejecución).</summary>
    public Guid Id { get; }

    /// <summary>Tipo de backup.</summary>
    public BackupType Type { get => _type; set { if (Set(ref _type, value)) _changed(); } }

    /// <summary>Expresión cron de 5 campos.</summary>
    public string Cron { get => _cron; set { if (Set(ref _cron, value ?? string.Empty)) _changed(); } }

    /// <summary>Si el paso está habilitado.</summary>
    public bool Enabled { get => _enabled; set { if (Set(ref _enabled, value)) _changed(); } }

    /// <summary>Texto con las próximas ejecuciones (lo calcula el editor).</summary>
    public string NextRuns { get => _nextRuns; set => Set(ref _nextRuns, value); }

    /// <summary>Error de validación de este paso (vacío si es correcto).</summary>
    public string Error { get => _error; set => Set(ref _error, value); }

    /// <summary>Quita el paso del job.</summary>
    public RelayCommand RemoveCommand { get; }

    /// <summary>Convierte el estado editado en un paso del dominio.</summary>
    public BackupStep ToStep() => new() { Id = Id, Type = Type, Cron = Cron.Trim(), Enabled = Enabled };
}

/// <summary>
/// Un destino dentro del editor (carpeta o Google Drive), con sus acciones: examinar, probar,
/// conectar la cuenta de Google, mover y quitar.
/// </summary>
public sealed class DestinationEditorViewModel : ViewModelBase
{
    /// <summary>Marca interna: "hay un secreto de cliente nuevo pendiente de cifrar".</summary>
    internal const string PendingSecret = "\u0001pendiente";

    private readonly Action _changed;
    private readonly EditorServices _svc;

    private DestinationKind _kind;
    private string _name;
    private string _path;
    private string _clientId;
    private string _clientSecret = string.Empty;
    private string? _protectedSecret;
    private string? _protectedRefreshToken;
    private string? _accountEmail;
    private string _folderName;

    private string _role = string.Empty;
    private string _nameError = string.Empty;
    private string _pathError = string.Empty;
    private string _kindError = string.Empty;
    private string _clientIdError = string.Empty;
    private string _clientSecretError = string.Empty;
    private string _accountError = string.Empty;
    private string _folderError = string.Empty;
    private string _testResult = string.Empty;
    private string _connectResult = string.Empty;

    /// <summary>Crea el view-model a partir de un destino existente.</summary>
    public DestinationEditorViewModel(
        DestinationConfig destination,
        Action changed,
        Action<DestinationEditorViewModel> remove,
        Action<DestinationEditorViewModel, int> move,
        EditorServices services)
    {
        _kind = destination.Kind;
        _name = destination.Name;
        _path = destination.Path;
        _clientId = destination.Drive?.ClientId ?? string.Empty;
        _protectedSecret = destination.Drive?.ProtectedClientSecret;
        _protectedRefreshToken = destination.Drive?.ProtectedRefreshToken;
        _accountEmail = destination.Drive?.AccountEmail;
        _folderName = destination.Drive?.FolderName ?? new DriveSettings().FolderName;
        _changed = changed;
        _svc = services;

        RemoveCommand = new RelayCommand(() => remove(this));
        MoveUpCommand = new RelayCommand(() => move(this, -1));
        MoveDownCommand = new RelayCommand(() => move(this, +1));
        BrowseCommand = new RelayCommand(() =>
        {
            if (services.Dialogs.PickFolder(string.IsNullOrWhiteSpace(Path) ? null : Path.Trim()) is { } picked)
                Path = picked;
        });
        TestCommand = new AsyncRelayCommand(TestAsync, onError: ex => TestResult = "✖ " + ex.Message);
        ConnectCommand = new AsyncRelayCommand(ConnectAsync);
        DisconnectCommand = new RelayCommand(Disconnect);
    }

    // ---------------------------------------------------------------- Comunes

    /// <summary>Tipos de destino disponibles.</summary>
    public IReadOnlyList<KindOption> Kinds => KindOption.All;

    /// <summary>Tipo de destino.</summary>
    public DestinationKind Kind
    {
        get => _kind;
        set
        {
            if (!Set(ref _kind, value)) return;
            TestResult = string.Empty;
            Raise(nameof(IsFolder));
            Raise(nameof(IsDrive));
            _changed();
        }
    }

    /// <summary><c>true</c> si es una carpeta.</summary>
    public bool IsFolder => _kind == DestinationKind.Folder;

    /// <summary><c>true</c> si es Google Drive.</summary>
    public bool IsDrive => _kind == DestinationKind.GoogleDrive;

    /// <summary>Nombre descriptivo del destino.</summary>
    public string Name { get => _name; set { if (Set(ref _name, value ?? string.Empty)) _changed(); } }

    /// <summary>"Principal" para el primero (lo escribe SQL Server) o "Copia" para el resto.</summary>
    public string Role { get => _role; set => Set(ref _role, value); }

    /// <summary>Error de validación del nombre.</summary>
    public string NameError { get => _nameError; set => Set(ref _nameError, value); }

    /// <summary>Error de validación del tipo (p. ej. Drive como destino principal).</summary>
    public string KindError { get => _kindError; set => Set(ref _kindError, value); }

    /// <summary>Resultado de la última prueba del destino.</summary>
    public string TestResult { get => _testResult; set => Set(ref _testResult, value); }

    /// <summary>Quita el destino.</summary>
    public RelayCommand RemoveCommand { get; }

    /// <summary>Sube el destino una posición.</summary>
    public RelayCommand MoveUpCommand { get; }

    /// <summary>Baja el destino una posición.</summary>
    public RelayCommand MoveDownCommand { get; }

    /// <summary>Prueba el destino: escritura en la carpeta o conexión con Google Drive.</summary>
    public AsyncRelayCommand TestCommand { get; }

    // ---------------------------------------------------------------- Carpeta

    /// <summary>Carpeta (local, unidad o UNC).</summary>
    public string Path
    {
        get => _path;
        set
        {
            if (!Set(ref _path, value ?? string.Empty)) return;
            TestResult = string.Empty;
            _changed();
        }
    }

    /// <summary>Error de validación de la carpeta.</summary>
    public string PathError { get => _pathError; set => Set(ref _pathError, value); }

    /// <summary>Abre el selector de carpetas.</summary>
    public RelayCommand BrowseCommand { get; }

    // ---------------------------------------------------------------- Google Drive

    /// <summary>ID de cliente OAuth del proyecto de Google Cloud.</summary>
    public string ClientId { get => _clientId; set { if (Set(ref _clientId, value ?? string.Empty)) _changed(); } }

    /// <summary>Secreto de cliente nuevo (texto en claro, solo en memoria; se cifra al guardar).</summary>
    public string ClientSecret
    {
        get => _clientSecret;
        set
        {
            if (!Set(ref _clientSecret, value ?? string.Empty)) return;
            Raise(nameof(SecretHint));
            _changed();
        }
    }

    /// <summary>Ayuda bajo el campo del secreto.</summary>
    public string SecretHint => _protectedSecret is { Length: > 0 } && _clientSecret.Length == 0
        ? "Hay un secreto guardado (cifrado). Déjelo en blanco para conservarlo."
        : "Se guarda cifrado con DPAPI; nunca en texto plano.";

    /// <summary>Carpeta raíz dentro de "Mi unidad" (se crea si no existe).</summary>
    public string FolderName { get => _folderName; set { if (Set(ref _folderName, value ?? string.Empty)) _changed(); } }

    /// <summary><c>true</c> si la cuenta de Google está conectada.</summary>
    public bool IsConnected => !string.IsNullOrEmpty(_protectedRefreshToken);

    /// <summary>Estado de la conexión, para mostrar.</summary>
    public string AccountText => IsConnected
        ? $"Conectado como {(string.IsNullOrWhiteSpace(_accountEmail) ? "su cuenta de Google" : _accountEmail)}"
        : "Sin conectar";

    /// <summary>Resultado del último intento de conexión.</summary>
    public string ConnectResult { get => _connectResult; private set => Set(ref _connectResult, value); }

    /// <summary>Error de validación del ID de cliente.</summary>
    public string ClientIdError { get => _clientIdError; set => Set(ref _clientIdError, value); }

    /// <summary>Error de validación del secreto de cliente.</summary>
    public string ClientSecretError { get => _clientSecretError; set => Set(ref _clientSecretError, value); }

    /// <summary>Error de validación de la cuenta (no conectada).</summary>
    public string AccountError { get => _accountError; set => Set(ref _accountError, value); }

    /// <summary>Error de validación de la carpeta de Drive.</summary>
    public string FolderError { get => _folderError; set => Set(ref _folderError, value); }

    /// <summary>Abre el navegador para autorizar el acceso a Google Drive.</summary>
    public AsyncRelayCommand ConnectCommand { get; }

    /// <summary>Olvida la cuenta conectada.</summary>
    public RelayCommand DisconnectCommand { get; }

    // ---------------------------------------------------------------- Conversión

    /// <summary>Convierte el estado editado en un destino del dominio (con el secreto nuevo marcado como pendiente).</summary>
    public DestinationConfig ToDestination() => IsDrive
        ? new DestinationConfig
        {
            Name = Name.Trim(),
            Kind = DestinationKind.GoogleDrive,
            Drive = new DriveSettings
            {
                ClientId = ClientId.Trim(),
                ProtectedClientSecret = _clientSecret.Length > 0 ? PendingSecret : _protectedSecret,
                ProtectedRefreshToken = _protectedRefreshToken,
                AccountEmail = _accountEmail,
                FolderName = FolderName.Trim()
            }
        }
        : new DestinationConfig { Name = Name.Trim(), Kind = DestinationKind.Folder, Path = Path.Trim() };

    /// <summary>Cifra el secreto de cliente pendiente (si lo hay) en el destino ya construido.</summary>
    internal void ProtectPending(DestinationConfig destination)
    {
        if (destination.Drive?.ProtectedClientSecret == PendingSecret)
            destination.Drive.ProtectedClientSecret = _svc.Protector.Protect(_clientSecret);
    }

    // ---------------------------------------------------------------- Acciones

    private async Task TestAsync()
    {
        if (IsDrive)
        {
            await TestDriveAsync();
            return;
        }

        if (!string.IsNullOrEmpty(PathError) || string.IsNullOrWhiteSpace(Path))
        {
            TestResult = "✖ Corrija primero la carpeta.";
            return;
        }

        TestResult = "Probando…";
        var folder = Path.Trim();
        await Task.Run(() => _svc.Files.VerifyWritable(folder));
        TestResult = "✔ Se puede escribir (probado con su usuario; la cuenta del servicio también necesita permiso).";
    }

    private async Task TestDriveAsync()
    {
        if (!IsConnected)
        {
            TestResult = "✖ Conecte primero la cuenta de Google.";
            return;
        }
        if (_clientId.Trim().Length == 0 || (_clientSecret.Length == 0 && string.IsNullOrEmpty(_protectedSecret)))
        {
            TestResult = "✖ Falta el ID o el secreto de cliente.";
            return;
        }

        TestResult = "Probando conexión con Google Drive…";
        var test = ToDestination();
        if (test.Drive!.ProtectedClientSecret == PendingSecret)
            test.Drive.ProtectedClientSecret = _svc.Protector.Protect(_clientSecret);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        TestResult = "✔ " + await _svc.Remote.TestAsync(test, cts.Token);
    }

    private async Task ConnectAsync()
    {
        if (_clientId.Trim().Length == 0 || (_clientSecret.Length == 0 && string.IsNullOrEmpty(_protectedSecret)))
        {
            ConnectResult = "✖ Escriba primero el ID y el secreto de cliente de Google (ver docs/google-drive.md).";
            return;
        }

        ConnectResult = "Se abrió el navegador: inicie sesión en Google y autorice el acceso…";
        try
        {
            var secret = _clientSecret.Length > 0 ? _clientSecret : _svc.Protector.Unprotect(_protectedSecret!);
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var result = await _svc.Authorizer.AuthorizeAsync(_clientId.Trim(), secret, cts.Token);

            _protectedRefreshToken = _svc.Protector.Protect(result.RefreshToken);
            _accountEmail = result.Email;
            Raise(nameof(IsConnected));
            Raise(nameof(AccountText));
            ConnectResult = "✔ Cuenta conectada.";
            _changed();
        }
        catch (OperationCanceledException)
        {
            ConnectResult = "✖ Se agotó el tiempo de espera (5 minutos) sin autorizar.";
        }
        catch (Exception ex)
        {
            ConnectResult = "✖ " + ex.Message.Split('\n')[0].Trim();
        }
    }

    private void Disconnect()
    {
        _protectedRefreshToken = null;
        _accountEmail = null;
        ConnectResult = string.Empty;
        TestResult = string.Empty;
        Raise(nameof(IsConnected));
        Raise(nameof(AccountText));
        _changed();
    }
}
