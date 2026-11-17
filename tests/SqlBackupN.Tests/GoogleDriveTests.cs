using Microsoft.Extensions.Logging.Abstractions;
using SqlBackupN.Core.Abstractions;
using SqlBackupN.Core.Domain;
using SqlBackupN.Core.Services;
using SqlBackupN.Presentation.Jobs;

namespace SqlBackupN.Tests;

/// <summary>Reglas puras de rutas y consultas de Google Drive.</summary>
public class DrivePathTests
{
    [Fact]
    public void Split_accepts_both_separators_and_ignores_empty_segments()
    {
        Assert.Equal(["SRV", "Ventas", "FULL"], DrivePath.Split(@"SRV\Ventas/FULL\"));
        Assert.Empty(DrivePath.Split("  "));
    }

    [Fact]
    public void SplitFile_separates_folders_from_the_file_name()
    {
        var (folders, name) = DrivePath.SplitFile(@"SRV\Ventas\FULL\Ventas_FULL_20261117.bak");

        Assert.Equal(["SRV", "Ventas", "FULL"], folders);
        Assert.Equal("Ventas_FULL_20261117.bak", name);
        Assert.Equal(("a.bak", 0), (DrivePath.SplitFile("a.bak").FileName, DrivePath.SplitFile("a.bak").Folders.Length));
    }

    [Fact]
    public void SplitFile_rejects_an_empty_path()
    {
        Assert.Throws<ArgumentException>(() => DrivePath.SplitFile(""));
    }

    [Theory]
    [InlineData("simple", "simple")]
    [InlineData("O'Brien", @"O\'Brien")]
    [InlineData(@"a\b", @"a\\b")]
    [InlineData("x' or name contains '", @"x\' or name contains \'")]
    public void EscapeQuery_prevents_breaking_out_of_the_quoted_value(string input, string expected)
    {
        Assert.Equal(expected, DrivePath.EscapeQuery(input));
    }

    [Theory]
    [InlineData("SQL Backup N")]
    [InlineData("Empresa/Backups/SQL")]
    [InlineData(@"Empresa\Backups")]
    public void Valid_folder_names_are_accepted(string name)
    {
        Assert.Null(DrivePath.FolderNameProblem(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("//")]
    [InlineData("../fuera")]
    [InlineData("a/./b")]
    [InlineData("a:b")]
    [InlineData("a|b")]
    public void Invalid_folder_names_are_rejected(string name)
    {
        Assert.NotNull(DrivePath.FolderNameProblem(name));
    }
}

/// <summary>Validación de destinos de Google Drive.</summary>
public class DriveValidationTests
{
    private static DestinationConfig Drive(Action<DriveSettings>? tweak = null)
    {
        var settings = new DriveSettings
        {
            ClientId = "id.apps.googleusercontent.com",
            ProtectedClientSecret = "enc:secreto",
            ProtectedRefreshToken = "enc:token",
            AccountEmail = "demo@example.test",
            FolderName = "SQL Backup N"
        };
        tweak?.Invoke(settings);
        return new DestinationConfig { Name = "Drive", Kind = DestinationKind.GoogleDrive, Drive = settings };
    }

    private static BackupJob Job(params DestinationConfig[] destinations) => new()
    {
        Name = "Ventas",
        Connection = new SqlServerConnection { Server = "srv01" },
        Steps = [new BackupStep { Type = BackupType.Full, Cron = "0 23 * * *" }],
        Destinations = [new DestinationConfig { Name = "Local", Path = @"D:\Backups" }, .. destinations],
        Notifications = new NotificationSettings { WebhookUrl = "https://hooks.example.test/x" }
    };

    private static List<string> ErrorFields(BackupJob job) =>
        JobValidator.Validate(job).Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Field).ToList();

    [Fact]
    public void A_connected_drive_copy_is_valid()
    {
        Assert.Empty(ErrorFields(Job(Drive())));
    }

    [Fact]
    public void Drive_cannot_be_the_primary_destination()
    {
        var job = Job();
        job.Destinations.Insert(0, Drive());

        Assert.Contains("Destinations[0].Kind", ErrorFields(job));
    }

    [Fact]
    public void Missing_credentials_and_account_are_reported_per_field()
    {
        var job = Job(Drive(d =>
        {
            d.ClientId = "";
            d.ProtectedClientSecret = null;
            d.ProtectedRefreshToken = null;
        }));

        var fields = ErrorFields(job);
        Assert.Contains("Destinations[1].Drive.Client", fields);
        Assert.Contains("Destinations[1].Drive.Account", fields);
    }

    [Fact]
    public void Shared_google_credentials_make_the_per_destination_ones_unnecessary()
    {
        var job = Job(Drive(d =>
        {
            d.ClientId = "";
            d.ProtectedClientSecret = null;
        }));

        Assert.DoesNotContain(JobValidator.Validate(job, null, googleClientAvailable: true),
            i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void Own_client_id_without_secret_is_an_error_even_with_shared_credentials()
    {
        var job = Job(Drive(d => d.ProtectedClientSecret = null));

        Assert.Contains(JobValidator.Validate(job, null, googleClientAvailable: true),
            i => i.Field == "Destinations[1].Drive.Client" && i.Severity == IssueSeverity.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../x")]
    [InlineData("a|b")]
    public void Invalid_drive_folder_is_rejected(string folder)
    {
        Assert.Contains("Destinations[1].Drive.Folder", ErrorFields(Job(Drive(d => d.FolderName = folder))));
    }

    [Fact]
    public void Two_drive_destinations_cannot_share_a_folder()
    {
        var job = Job(Drive(), Drive());
        job.Destinations[2].Name = "Drive 2";

        Assert.Contains("Destinations[2].Drive.Folder", ErrorFields(job));
    }

    [Fact]
    public void Drive_destination_does_not_need_a_local_path()
    {
        var drive = Drive();
        drive.Path = string.Empty;

        Assert.DoesNotContain("Destinations[1].Path", ErrorFields(Job(drive)));
    }

    [Fact]
    public void Drive_settings_survive_a_json_round_trip_without_plain_secrets()
    {
        var job = Job(Drive());
        var copy = JobFactory.Clone(job);

        Assert.Equal(DestinationKind.GoogleDrive, copy.Destinations[1].Kind);
        Assert.Equal("enc:secreto", copy.Destinations[1].Drive!.ProtectedClientSecret);
        Assert.Equal("demo@example.test", copy.Destinations[1].Drive!.AccountEmail);
    }
}

/// <summary>El envío a Google Drive dentro del JobRunner (con almacenamiento remoto simulado).</summary>
public class DriveJobRunnerTests
{
    private sealed class Executor : IBackupExecutor
    {
        public Task<IReadOnlyList<string>> ListDatabasesAsync(SqlServerConnection c, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(["Ventas"]);
        public Task BackupAsync(SqlServerConnection c, BackupRequest r, CancellationToken ct) => Task.CompletedTask;
        public Task VerifyAsync(SqlServerConnection c, string p, bool k, int t, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Files : IFileStore
    {
        public List<string> Copies { get; } = [];
        public void EnsureDirectory(string path) { }
        public void VerifyWritable(string directory) { }
        public void Copy(string source, string destination) => Copies.Add(destination);
        public void Delete(string path) { }
        public long GetSize(string path) => 2048;
        public IReadOnlyList<StoredFile> List(string directory, string searchPattern) => [];
    }

    private sealed class History : IHistoryStore
    {
        public Task AppendAsync(JobRunRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<JobRunRecord>> GetRecentAsync(Guid? jobId, int max, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<JobRunRecord>>([]);
        public Task<DateTime?> GetLastRunUtcAsync(Guid jobId, Guid stepId, CancellationToken ct = default) =>
            Task.FromResult<DateTime?>(null);
    }

    private static readonly DateTime Now = DateTime.UtcNow;

    private static BackupJob Job(bool driveFirst = false)
    {
        var drive = new DestinationConfig
        {
            Name = "Drive",
            Kind = DestinationKind.GoogleDrive,
            Drive = new DriveSettings { ClientId = "id", ProtectedClientSecret = "s", ProtectedRefreshToken = "t" }
        };
        var local = new DestinationConfig { Name = "Local", Path = Path.Combine("C:", "bk") };
        return new BackupJob
        {
            Name = "Ventas",
            Connection = new SqlServerConnection { Server = "SRV" },
            Destinations = driveFirst ? [drive, local] : [local, drive],
            Retention = new RetentionPolicy { KeepDays = 30, KeepLast = 1 }
        };
    }

    private static (JobRunner Runner, StubRemote Remote, Files Files) Build()
    {
        var remote = new StubRemote();
        var files = new Files();
        var runner = new JobRunner(new Executor(), files, remote, new History(), new StubNotifier(),
            TimeProvider.System, NullLogger<JobRunner>.Instance);
        return (runner, remote, files);
    }

    [Fact]
    public async Task Backup_is_uploaded_to_drive_with_the_relative_path_and_extension()
    {
        var (runner, remote, files) = Build();

        var record = await runner.RunStepAsync(Job(), new BackupStep { Type = BackupType.Full }, false, CancellationToken.None);

        Assert.Equal(RunStatus.Succeeded, record.Status);
        var upload = Assert.Single(remote.Uploads);
        Assert.Equal("Drive", upload.Destination);
        Assert.Equal(record.Results[0].FilePath, upload.Local);
        Assert.EndsWith(".bak", upload.Relative);
        Assert.Contains("Ventas", upload.Relative);
        Assert.Contains("FULL", upload.Relative);
        Assert.Empty(files.Copies); // el destino de Drive no es una copia de carpeta
    }

    [Fact]
    public async Task Log_backups_are_uploaded_with_the_trn_extension()
    {
        var (runner, remote, _) = Build();

        await runner.RunStepAsync(Job(), new BackupStep { Type = BackupType.TransactionLog }, false, CancellationToken.None);

        Assert.EndsWith(".trn", Assert.Single(remote.Uploads).Relative);
    }

    [Fact]
    public async Task A_failed_upload_is_a_warning_because_the_local_backup_exists()
    {
        var (runner, remote, _) = Build();
        remote.UploadFailure = new IOException("No hay espacio disponible en Google Drive.");

        var record = await runner.RunStepAsync(Job(), new BackupStep(), false, CancellationToken.None);

        Assert.Equal(RunStatus.Warning, record.Status);
        Assert.Contains("Drive", record.Results[0].Warnings[0]);
        Assert.Contains("No hay espacio", record.Results[0].Warnings[0]);
        Assert.NotNull(record.Results[0].FilePath);
    }

    [Fact]
    public async Task Retention_on_drive_deletes_only_expired_files_and_keeps_the_newest()
    {
        var (runner, remote, _) = Build();
        remote.ExistingFiles.AddRange(
        [
            new RemoteFile("nuevo", "a_1.bak", Now.AddDays(-1), 1),
            new RemoteFile("viejo1", "a_2.bak", Now.AddDays(-45), 1),
            new RemoteFile("viejo2", "a_3.bak", Now.AddDays(-90), 1),
            new RemoteFile("otro-tipo", "a_4.trn", Now.AddDays(-90), 1)
        ]);

        await runner.RunStepAsync(Job(), new BackupStep { Type = BackupType.Full }, false, CancellationToken.None);

        Assert.Equal(["viejo1", "viejo2"], remote.Deleted.Order());
        Assert.Contains("Ventas", Assert.Single(remote.ListedDirectories));
    }

    [Fact]
    public async Task Drive_as_primary_destination_fails_with_a_clear_message()
    {
        var (runner, remote, _) = Build();

        var record = await runner.RunStepAsync(Job(driveFirst: true), new BackupStep(), false, CancellationToken.None);

        Assert.Equal(RunStatus.Failed, record.Status);
        Assert.Contains("carpeta", record.Results[0].Error);
        Assert.Empty(remote.Uploads);
    }
}

/// <summary>Editor: destinos de Google Drive, credenciales compartidas (una sola vez) y conexión de la cuenta.</summary>
public class DriveEditorTests
{
    private static BackupJob BaseJob() => new()
    {
        Name = "Ventas",
        Connection = new SqlServerConnection { Server = "srv01" },
        Steps = [new BackupStep { Type = BackupType.Full, Cron = "0 23 * * *" }],
        Destinations = [new DestinationConfig { Name = "Local", Path = @"D:\Backups" }],
        Notifications = new NotificationSettings { WebhookUrl = "https://hooks.example.test/x" }
    };

    private sealed record Rig(
        JobEditorViewModel Editor, StubRemote Remote, StubAuthorizer Authorizer, StubGoogleClients Clients, StubDialogs Dialogs);

    private static Rig Create(BackupJob? job = null, GoogleClient? shared = null)
    {
        var remote = new StubRemote();
        var authorizer = new StubAuthorizer();
        var clients = new StubGoogleClients { Default = shared };
        var dialogs = new StubDialogs();
        var services = new EditorServices(new StubProtector(), new StubExecutor(), new StubFiles(),
            remote, authorizer, clients, dialogs, TimeProvider.System);
        return new Rig(new JobEditorViewModel(JobFactory.Clone(job ?? BaseJob()), false, [], services),
            remote, authorizer, clients, dialogs);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 150 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition(), "La condición no se cumplió a tiempo.");
    }

    private static DestinationEditorViewModel AddDrive(Rig rig)
    {
        rig.Editor.AddDriveDestinationCommand.Execute(null);
        return rig.Editor.Destinations[^1];
    }

    private static readonly GoogleClient Shared = new("compartido.apps.googleusercontent.com", "secreto-compartido");

    // ------------------------------------------------------------ Estructura: principal = carpeta

    [Fact]
    public void The_primary_destination_is_always_a_folder_and_does_not_offer_a_type_choice()
    {
        var rig = Create();
        AddDrive(rig);

        Assert.True(rig.Editor.Destinations[0].IsPrimary);
        Assert.False(rig.Editor.Destinations[0].CanChooseKind);
        Assert.False(rig.Editor.Destinations[1].IsPrimary);
        Assert.True(rig.Editor.Destinations[1].CanChooseKind);
    }

    [Fact]
    public void Google_drive_cannot_be_added_before_the_primary_folder_exists()
    {
        var job = BaseJob();
        job.Destinations.Clear();
        var rig = Create(job);

        Assert.False(rig.Editor.AddDriveDestinationCommand.CanExecute(null));

        rig.Editor.AddDestinationCommand.Execute(null);

        Assert.True(rig.Editor.AddDriveDestinationCommand.CanExecute(null));
    }

    [Fact]
    public void A_hand_edited_job_with_drive_as_primary_still_lets_the_user_fix_the_type()
    {
        var job = BaseJob();
        job.Destinations.Insert(0, new DestinationConfig
        {
            Name = "Drive", Kind = DestinationKind.GoogleDrive, Drive = new DriveSettings()
        });
        var rig = Create(job);

        Assert.True(rig.Editor.Destinations[0].CanChooseKind);
        Assert.NotEmpty(rig.Editor.Destinations[0].KindError);
    }

    // ------------------------------------------------------------ Credenciales de Google: una sola vez

    [Fact]
    public void Adding_drive_without_credentials_opens_the_setup_and_shows_one_clear_error()
    {
        var rig = Create();

        var drive = AddDrive(rig);

        Assert.True(rig.Editor.HasDrive);
        Assert.True(rig.Editor.ShowGoogleSetup);
        Assert.False(rig.Editor.GoogleClientConfigured);
        Assert.Equal("Pendiente de configurar", rig.Editor.GoogleStatusText);
        Assert.Contains("credenciales", drive.ClientError);
        Assert.NotEmpty(drive.AccountError);
        Assert.False(rig.Editor.CanSave);
        // Sin la cascada de antes: un error de credenciales y uno de cuenta, nada más para este destino.
        Assert.Equal(2, rig.Editor.Issues.Count(i => i.IsError && i.Area.StartsWith("Destinos")));
    }

    [Fact]
    public void With_shared_credentials_available_there_is_nothing_to_configure()
    {
        var rig = Create(shared: Shared);

        var drive = AddDrive(rig);

        Assert.True(rig.Editor.GoogleClientConfigured);
        Assert.False(rig.Editor.ShowGoogleSetup);
        Assert.Equal("Configuradas", rig.Editor.GoogleStatusText);
        Assert.Equal(string.Empty, drive.ClientError);
        Assert.NotEmpty(drive.AccountError);   // solo falta conectar la cuenta
        Assert.Equal("Cambiar", rig.Editor.GoogleSetupButtonText);
    }

    [Fact]
    public void Typing_the_credentials_clears_the_error_and_marks_them_configured()
    {
        var rig = Create();
        var drive = AddDrive(rig);

        rig.Editor.GoogleClientId = "id.apps.googleusercontent.com";
        rig.Editor.GoogleClientSecret = "secreto";

        Assert.True(rig.Editor.GoogleClientConfigured);
        Assert.Equal(string.Empty, drive.ClientError);
    }

    [Fact]
    public void The_setup_panel_can_be_toggled_and_opens_the_guides()
    {
        var rig = Create(shared: Shared);
        AddDrive(rig);

        rig.Editor.ToggleGoogleSetupCommand.Execute(null);
        Assert.True(rig.Editor.ShowGoogleSetup);
        Assert.Equal("Ocultar", rig.Editor.GoogleSetupButtonText);

        rig.Editor.OpenGoogleConsoleCommand.Execute(null);
        rig.Editor.OpenGoogleGuideCommand.Execute(null);

        Assert.Equal([HelpLinks.GoogleCloudCredentials, HelpLinks.GoogleDriveGuide], rig.Dialogs.OpenedUrls);
    }

    // ------------------------------------------------------------ Conectar la cuenta

    [Fact]
    public async Task Connecting_with_typed_credentials_stores_the_protected_token_and_shows_the_account()
    {
        var rig = Create();
        var drive = AddDrive(rig);
        rig.Editor.GoogleClientId = "id.apps.googleusercontent.com";
        rig.Editor.GoogleClientSecret = "secreto-plano";

        drive.ConnectCommand.Execute(null);
        await WaitUntil(() => drive.IsConnected);

        Assert.Equal("id.apps.googleusercontent.com", rig.Authorizer.LastClientId);
        Assert.Equal("secreto-plano", rig.Authorizer.LastClientSecret);
        Assert.Equal("Conectado como demo@example.test", drive.AccountText);
        Assert.StartsWith("✔", drive.ConnectResult);
        Assert.Equal(string.Empty, drive.AccountError);
        Assert.True(rig.Editor.CanSave);
    }

    [Fact]
    public async Task Connecting_uses_the_shared_credentials_when_nothing_was_typed()
    {
        var rig = Create(shared: Shared);
        var drive = AddDrive(rig);

        drive.ConnectCommand.Execute(null);
        await WaitUntil(() => drive.IsConnected);

        Assert.Equal(Shared.ClientId, rig.Authorizer.LastClientId);
        Assert.Equal(Shared.ClientSecret, rig.Authorizer.LastClientSecret);
        Assert.True(rig.Editor.CanSave);
    }

    [Fact]
    public void Connecting_without_credentials_points_to_the_setup_and_opens_it()
    {
        var rig = Create();
        var drive = AddDrive(rig);
        rig.Editor.ShowGoogleSetup = false;

        drive.ConnectCommand.Execute(null);

        Assert.StartsWith("✖", drive.ConnectResult);
        Assert.Contains("credenciales", drive.ConnectResult);
        Assert.True(rig.Editor.ShowGoogleSetup);
        Assert.False(drive.IsConnected);
        Assert.Null(rig.Authorizer.LastClientId);
    }

    [Fact]
    public async Task A_failed_connection_shows_a_one_line_error()
    {
        var rig = Create(shared: Shared);
        var drive = AddDrive(rig);
        rig.Authorizer.Failure = new InvalidOperationException("Google rechazó la autorización\nDetalle largo");

        drive.ConnectCommand.Execute(null);
        await WaitUntil(() => drive.ConnectResult.StartsWith('✖'));

        Assert.Equal("✖ Google rechazó la autorización", drive.ConnectResult);
        Assert.False(drive.IsConnected);
    }

    // ------------------------------------------------------------ Guardar

    [Fact]
    public async Task Saving_stores_the_typed_credentials_once_for_everyone_and_keeps_the_token_protected()
    {
        var rig = Create();
        var drive = AddDrive(rig);
        rig.Editor.GoogleClientId = "id.apps.googleusercontent.com";
        rig.Editor.GoogleClientSecret = "secreto-plano";
        drive.ConnectCommand.Execute(null);
        await WaitUntil(() => drive.IsConnected);

        rig.Editor.SaveCommand.Execute(null);

        var shared = Assert.Single(rig.Clients.Saved);
        Assert.Equal(new GoogleClient("id.apps.googleusercontent.com", "secreto-plano"), shared);

        var saved = Assert.Single(rig.Editor.Result!.Destinations, d => d.Kind == DestinationKind.GoogleDrive);
        Assert.Equal("enc:refresh-123", saved.Drive!.ProtectedRefreshToken);
        Assert.Equal("demo@example.test", saved.Drive.AccountEmail);
        Assert.Equal("SQL Backup N", saved.Drive.FolderName);
        // El destino ya no arrastra credenciales propias: usa las compartidas.
        Assert.Equal(string.Empty, saved.Drive.ClientId);
        Assert.Null(saved.Drive.ProtectedClientSecret);
        Assert.Equal(string.Empty, saved.Path);
    }

    [Fact]
    public async Task Saving_without_typing_new_credentials_does_not_overwrite_the_shared_ones()
    {
        var rig = Create(shared: Shared);
        var drive = AddDrive(rig);
        drive.ConnectCommand.Execute(null);
        await WaitUntil(() => drive.IsConnected);

        rig.Editor.SaveCommand.Execute(null);

        Assert.Empty(rig.Clients.Saved);
        Assert.NotNull(rig.Editor.Result);
    }

    [Fact]
    public void A_destination_with_its_own_credentials_from_an_older_version_keeps_working()
    {
        var job = BaseJob();
        job.Destinations.Add(new DestinationConfig
        {
            Name = "Drive",
            Kind = DestinationKind.GoogleDrive,
            Drive = new DriveSettings
            {
                ClientId = "propio", ProtectedClientSecret = "enc:guardado", ProtectedRefreshToken = "enc:token",
                AccountEmail = "demo@example.test", FolderName = "Backups/SQL"
            }
        });
        var rig = Create(job);   // sin credenciales compartidas

        Assert.True(rig.Editor.CanSave);
        Assert.True(rig.Editor.Destinations[1].IsConnected);

        rig.Editor.SaveCommand.Execute(null);

        var saved = rig.Editor.Result!.Destinations[1].Drive!;
        Assert.Equal("propio", saved.ClientId);
        Assert.Equal("enc:guardado", saved.ProtectedClientSecret);
        Assert.Equal("Backups/SQL", saved.FolderName);
        Assert.Empty(rig.Clients.Saved);
    }

    [Fact]
    public void Disconnecting_forgets_the_account_and_blocks_saving_again()
    {
        var job = BaseJob();
        job.Destinations.Add(new DestinationConfig
        {
            Name = "Drive",
            Kind = DestinationKind.GoogleDrive,
            Drive = new DriveSettings { ProtectedRefreshToken = "enc:t", AccountEmail = "demo@example.test" }
        });
        var rig = Create(job, Shared);
        var drive = rig.Editor.Destinations[1];
        Assert.True(rig.Editor.CanSave);

        drive.DisconnectCommand.Execute(null);

        Assert.False(drive.IsConnected);
        Assert.Equal("Sin conectar", drive.AccountText);
        Assert.NotEmpty(drive.AccountError);
        Assert.False(rig.Editor.CanSave);
    }

    // ------------------------------------------------------------ Probar

    [Fact]
    public async Task Testing_drive_requires_a_connected_account_and_uses_the_credentials_in_use()
    {
        var rig = Create();
        var drive = AddDrive(rig);
        rig.Editor.GoogleClientId = "id";
        rig.Editor.GoogleClientSecret = "secret";

        drive.TestCommand.Execute(null);
        await WaitUntil(() => drive.TestResult.Length > 0);
        Assert.Contains("Conecte primero", drive.TestResult);
        Assert.Empty(rig.Remote.Tested);

        drive.ConnectCommand.Execute(null);
        await WaitUntil(() => drive.IsConnected);

        drive.TestCommand.Execute(null);
        await WaitUntil(() => drive.TestResult.StartsWith('✔'));
        Assert.Contains("demo@example.test", drive.TestResult);
        // Se prueba con las credenciales escritas aunque aún no se hayan guardado, y siempre cifradas.
        var tested = Assert.Single(rig.Remote.Tested).Drive!;
        Assert.Equal("id", tested.ClientId);
        Assert.Equal("enc:secret", tested.ProtectedClientSecret);

        rig.Remote.TestFailure = new IOException("Google Drive: sin permiso");
        drive.TestCommand.Execute(null);
        await WaitUntil(() => drive.TestResult.StartsWith('✖'));
        Assert.Contains("sin permiso", drive.TestResult);
    }

    [Fact]
    public void Summary_rows_point_to_the_drive_destination()
    {
        var rig = Create();
        AddDrive(rig);

        var texts = rig.Editor.Issues.Where(i => i.IsError).Select(i => i.Text).ToList();

        Assert.Contains(texts, t => t.StartsWith("Destinos (destino 2): ", StringComparison.Ordinal));
    }
}

/// <summary>Credenciales de Google compartidas: guardado cifrado y credenciales incluidas con la aplicación.</summary>
public sealed class GoogleClientProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlbackupn-test-" + Guid.NewGuid().ToString("N"));

    public GoogleClientProviderTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string DataFile => Path.Combine(_dir, "data", "google-client.json");
    private string BundledFile => Path.Combine(_dir, "bundled.json");

    private SqlBackupN.Core.Infrastructure.FileGoogleClientProvider Create() =>
        new(new StubProtector(), DataFile, BundledFile);

    [Fact]
    public void Nothing_configured_returns_null()
    {
        Assert.Null(Create().GetDefault());
    }

    [Fact]
    public void Saved_credentials_round_trip_and_the_secret_is_never_stored_in_plain_text()
    {
        var provider = Create();

        provider.SaveDefault(new GoogleClient("mi-id", "mi-secreto"));

        Assert.Equal(new GoogleClient("mi-id", "mi-secreto"), provider.GetDefault());
        // El secreto solo se guarda como salida del cifrador (el doble de prueba antepone "enc:"),
        // en el campo "protectedClientSecret" y nunca en un campo de texto plano "clientSecret".
        var text = File.ReadAllText(DataFile);
        Assert.Contains("\"protectedClientSecret\": \"enc:mi-secreto\"", text);
        Assert.DoesNotContain("\"clientSecret\"", text);
        Assert.Contains("\"clientId\": \"mi-id\"", text);
    }

    [Fact]
    public void Credentials_bundled_with_the_app_are_used_when_the_user_saved_none()
    {
        File.WriteAllText(BundledFile, "{\"clientId\": \"incluido\", \"clientSecret\": \"secreto-incluido\"}");

        Assert.Equal(new GoogleClient("incluido", "secreto-incluido"), Create().GetDefault());
    }

    [Fact]
    public void The_users_credentials_win_over_the_bundled_ones()
    {
        File.WriteAllText(BundledFile, "{\"clientId\": \"incluido\", \"clientSecret\": \"x\"}");
        var provider = Create();

        provider.SaveDefault(new GoogleClient("propio", "secreto"));

        Assert.Equal("propio", provider.GetDefault()!.ClientId);
    }

    [Fact]
    public void A_corrupt_file_is_ignored_instead_of_breaking_the_app()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DataFile)!);
        File.WriteAllText(DataFile, "{ esto no es json");

        Assert.Null(Create().GetDefault());
    }
}
