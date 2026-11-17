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
/// <param name="GoogleClients">Credenciales de Google compartidas por todos los destinos.</param>
/// <param name="Dialogs">Selector de carpetas y mensajes.</param>
/// <param name="Clock">Reloj (para calcular próximas ejecuciones).</param>
public sealed record EditorServices(
    ISecretProtector Protector,
    IBackupExecutor Executor,
    IFileStore Files,
    IRemoteStorage Remote,
    IGoogleAuthorizer Authorizer,
    IGoogleClientProvider GoogleClients,
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

/// <summary>
/// Un destino dentro del editor (carpeta o Google Drive), con sus acciones: examinar, probar,
/// conectar la cuenta de Google, mover y quitar. Las credenciales de Google no se piden aquí: son
/// compartidas por todos los destinos y se configuran una sola vez en el editor.
/// </summary>
public sealed class DestinationEditorViewModel : ViewModelBase
{
    private readonly Action _changed;
    private readonly EditorServices _svc;
    private readonly Func<GoogleClient?> _sharedClient;
    private readonly Action _needsGoogleSetup;

    private DestinationKind _kind;
    private string _name;
    private string _path;
    private readonly string _legacyClientId;
    private readonly string? _legacyProtectedSecret;
    private string? _protectedRefreshToken;
    private string? _accountEmail;
    private string _folderName;
    private bool _isPrimary;

    private string _role = string.Empty;
    private string _nameError = string.Empty;
    private string _pathError = string.Empty;
    private string _kindError = string.Empty;
    private string _clientError = string.Empty;
    private string _accountError = string.Empty;
    private string _folderError = string.Empty;
    private string _testResult = string.Empty;
    private string _connectResult = string.Empty;

    /// <summary>Crea el view-model a partir de un destino existente.</summary>
    /// <param name="destination">Destino de origen.</param>
    /// <param name="changed">Se invoca al cambiar cualquier campo editable.</param>
    /// <param name="remove">Se invoca al pulsar "Quitar".</param>
    /// <param name="move">Se invoca al subir o bajar el destino.</param>
    /// <param name="services">Servicios del editor.</param>
    /// <param name="sharedClient">Devuelve las credenciales de Google que se van a usar (las escritas o las guardadas).</param>
    /// <param name="needsGoogleSetup">Pide al editor que muestre los pasos para configurar las credenciales de Google.</param>
    public DestinationEditorViewModel(
        DestinationConfig destination,
        Action changed,
        Action<DestinationEditorViewModel> remove,
        Action<DestinationEditorViewModel, int> move,
        EditorServices services,
        Func<GoogleClient?> sharedClient,
        Action needsGoogleSetup)
    {
        _kind = destination.Kind;
        _name = destination.Name;
        _path = destination.Path;
        _legacyClientId = destination.Drive?.ClientId ?? string.Empty;
        _legacyProtectedSecret = destination.Drive?.ProtectedClientSecret;
        _protectedRefreshToken = destination.Drive?.ProtectedRefreshToken;
        _accountEmail = destination.Drive?.AccountEmail;
        _folderName = destination.Drive?.FolderName ?? new DriveSettings().FolderName;
        _changed = changed;
        _svc = services;
        _sharedClient = sharedClient;
        _needsGoogleSetup = needsGoogleSetup;

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
            Raise(nameof(CanChooseKind));
            _changed();
        }
    }

    /// <summary><c>true</c> si es una carpeta.</summary>
    public bool IsFolder => _kind == DestinationKind.Folder;

    /// <summary><c>true</c> si es Google Drive.</summary>
    public bool IsDrive => _kind == DestinationKind.GoogleDrive;

    /// <summary>
    /// <c>true</c> para el destino principal (el primero): SQL Server escribe allí, así que siempre es una carpeta
    /// y no se ofrece elegir el tipo. Lo fija el editor.
    /// </summary>
    public bool IsPrimary
    {
        get => _isPrimary;
        set
        {
            if (!Set(ref _isPrimary, value)) return;
            Raise(nameof(CanChooseKind));
        }
    }

    /// <summary>
    /// Se ofrece elegir el tipo solo en las copias; en el destino principal únicamente si por error
    /// (JSON escrito a mano) es Google Drive, para poder corregirlo.
    /// </summary>
    public bool CanChooseKind => !_isPrimary || IsDrive;

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

    /// <summary>Carpeta raíz dentro de "Mi unidad" (se crea si no existe).</summary>
    public string FolderName { get => _folderName; set { if (Set(ref _folderName, value ?? string.Empty)) _changed(); } }

    /// <summary>
    /// <c>true</c> si el destino trae credenciales propias (creado con una versión anterior); entonces no
    /// necesita las compartidas.
    /// </summary>
    public bool HasOwnCredentials =>
        !string.IsNullOrWhiteSpace(_legacyClientId) && !string.IsNullOrEmpty(_legacyProtectedSecret);

    /// <summary><c>true</c> si la cuenta de Google está conectada.</summary>
    public bool IsConnected => !string.IsNullOrEmpty(_protectedRefreshToken);

    /// <summary>Estado de la conexión, para mostrar.</summary>
    public string AccountText => IsConnected
        ? $"Conectado como {(string.IsNullOrWhiteSpace(_accountEmail) ? "su cuenta de Google" : _accountEmail)}"
        : "Sin conectar";

    /// <summary>Resultado del último intento de conexión.</summary>
    public string ConnectResult { get => _connectResult; private set => Set(ref _connectResult, value); }

    /// <summary>Error de validación de las credenciales (faltan en el recuadro "Credenciales de Google").</summary>
    public string ClientError { get => _clientError; set => Set(ref _clientError, value); }

    /// <summary>Error de validación de la cuenta (no conectada).</summary>
    public string AccountError { get => _accountError; set => Set(ref _accountError, value); }

    /// <summary>Error de validación de la carpeta de Drive.</summary>
    public string FolderError { get => _folderError; set => Set(ref _folderError, value); }

    /// <summary>Abre el navegador para autorizar el acceso a Google Drive.</summary>
    public AsyncRelayCommand ConnectCommand { get; }

    /// <summary>Olvida la cuenta conectada.</summary>
    public RelayCommand DisconnectCommand { get; }

    // ---------------------------------------------------------------- Conversión

    /// <summary>Convierte el estado editado en un destino del dominio.</summary>
    public DestinationConfig ToDestination() => IsDrive
        ? new DestinationConfig
        {
            Name = Name.Trim(),
            Kind = DestinationKind.GoogleDrive,
            Drive = new DriveSettings
            {
                // Solo destinos creados con versiones anteriores traen credenciales propias; los nuevos usan las compartidas.
                ClientId = _legacyClientId,
                ProtectedClientSecret = _legacyProtectedSecret,
                ProtectedRefreshToken = _protectedRefreshToken,
                AccountEmail = _accountEmail,
                FolderName = FolderName.Trim()
            }
        }
        : new DestinationConfig { Name = Name.Trim(), Kind = DestinationKind.Folder, Path = Path.Trim() };

    // ---------------------------------------------------------------- Acciones

    /// <summary>Credenciales que se usarán: las del propio destino (versiones anteriores) o las compartidas.</summary>
    private GoogleClient? ResolveClient()
    {
        if (!string.IsNullOrWhiteSpace(_legacyClientId) && !string.IsNullOrEmpty(_legacyProtectedSecret))
            return new GoogleClient(_legacyClientId.Trim(), _svc.Protector.Unprotect(_legacyProtectedSecret));
        return _sharedClient();
    }

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
        if (ResolveClient() is not { } client)
        {
            TestResult = "✖ Falta configurar las credenciales de Google.";
            _needsGoogleSetup();
            return;
        }

        TestResult = "Probando conexión con Google Drive…";
        var test = ToDestination();
        // Se prueba con las credenciales en uso aunque todavía no se hayan guardado.
        test.Drive!.ClientId = client.ClientId;
        test.Drive.ProtectedClientSecret = _svc.Protector.Protect(client.ClientSecret);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        TestResult = "✔ " + await _svc.Remote.TestAsync(test, cts.Token);
    }

    private async Task ConnectAsync()
    {
        ClientErrorReset();
        ConnectResult = string.Empty;

        GoogleClient? client;
        try
        {
            client = ResolveClient();
        }
        catch (Exception ex)
        {
            ConnectResult = "✖ " + ex.Message.Split('\n')[0].Trim();
            return;
        }

        if (client is null)
        {
            ConnectResult = "✖ Primero configure las credenciales de Google (recuadro de arriba; se hace una sola vez).";
            _needsGoogleSetup();
            return;
        }

        ConnectResult = "Se abrió el navegador: inicie sesión en Google y pulse «Permitir»…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var result = await _svc.Authorizer.AuthorizeAsync(client.ClientId, client.ClientSecret, cts.Token);

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

    private void ClientErrorReset() => TestResult = string.Empty;

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
