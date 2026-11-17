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
        Assert.Contains("Destinations[1].Drive.ClientId", fields);
        Assert.Contains("Destinations[1].Drive.ClientSecret", fields);
        Assert.Contains("Destinations[1].Drive.Account", fields);
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

/// <summary>Editor: destinos de Google Drive, conexión de la cuenta y protección de secretos.</summary>
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

    private sealed record Rig(JobEditorViewModel Editor, StubRemote Remote, StubAuthorizer Authorizer);

    private static Rig Create(BackupJob? job = null)
    {
        var remote = new StubRemote();
        var authorizer = new StubAuthorizer();
        var services = new EditorServices(new StubProtector(), new StubExecutor(), new StubFiles(),
            remote, authorizer, new StubDialogs(), TimeProvider.System);
        return new Rig(new JobEditorViewModel(JobFactory.Clone(job ?? BaseJob()), false, [], services), remote, authorizer);
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

    [Fact]
    public void Adding_a_drive_destination_asks_for_credentials_and_connection()
    {
        var rig = Create();

        var drive = AddDrive(rig);

        Assert.True(drive.IsDrive);
        Assert.False(drive.IsFolder);
        Assert.Equal("SQL Backup N", drive.FolderName);
        Assert.False(rig.Editor.CanSave);
        Assert.NotEmpty(drive.ClientIdError);
        Assert.NotEmpty(drive.ClientSecretError);
        Assert.NotEmpty(drive.AccountError);
        Assert.Equal("Sin conectar", drive.AccountText);
    }

    [Fact]
    public void Switching_a_folder_to_drive_changes_which_fields_are_validated()
    {
        var rig = Create();
        rig.Editor.AddDestinationCommand.Execute(null);
        var copy = rig.Editor.Destinations[1];
        Assert.NotEmpty(copy.PathError);

        copy.Kind = DestinationKind.GoogleDrive;

        Assert.Equal(string.Empty, copy.PathError);
        Assert.NotEmpty(copy.ClientIdError);
    }

    [Fact]
    public void Drive_as_first_destination_shows_the_kind_error()
    {
        var rig = Create();
        rig.Editor.Destinations[0].Kind = DestinationKind.GoogleDrive;

        Assert.Contains("carpeta", rig.Editor.Destinations[0].KindError);
        Assert.False(rig.Editor.CanSave);
    }

    [Fact]
    public async Task Connecting_stores_the_protected_token_and_shows_the_account()
    {
        var rig = Create();
        var drive = AddDrive(rig);
        drive.ClientId = "id.apps.googleusercontent.com";
        drive.ClientSecret = "secreto-plano";

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
    public void Connecting_without_credentials_explains_what_is_missing()
    {
        var rig = Create();
        var drive = AddDrive(rig);

        drive.ConnectCommand.Execute(null);

        Assert.StartsWith("✖", drive.ConnectResult);
        Assert.Contains("ID y el secreto", drive.ConnectResult);
        Assert.False(drive.IsConnected);
    }

    [Fact]
    public async Task A_failed_connection_shows_a_one_line_error()
    {
        var rig = Create();
        var drive = AddDrive(rig);
        drive.ClientId = "id";
        drive.ClientSecret = "s";
        rig.Authorizer.Failure = new InvalidOperationException("Google rechazó la autorización\nDetalle largo");

        drive.ConnectCommand.Execute(null);
        await WaitUntil(() => drive.ConnectResult.StartsWith('✖'));

        Assert.Equal("✖ Google rechazó la autorización", drive.ConnectResult);
        Assert.False(drive.IsConnected);
    }

    [Fact]
    public async Task Saving_protects_the_new_client_secret_and_keeps_the_token()
    {
        var rig = Create();
        var drive = AddDrive(rig);
        drive.ClientId = "id.apps.googleusercontent.com";
        drive.ClientSecret = "secreto-plano";
        drive.ConnectCommand.Execute(null);
        await WaitUntil(() => drive.IsConnected);

        rig.Editor.SaveCommand.Execute(null);

        var saved = Assert.Single(rig.Editor.Result!.Destinations, d => d.Kind == DestinationKind.GoogleDrive);
        Assert.Equal("enc:secreto-plano", saved.Drive!.ProtectedClientSecret);
        Assert.Equal("enc:refresh-123", saved.Drive.ProtectedRefreshToken);
        Assert.Equal("demo@example.test", saved.Drive.AccountEmail);
        Assert.Equal("SQL Backup N", saved.Drive.FolderName);
        Assert.Equal(string.Empty, saved.Path);
    }

    [Fact]
    public void Existing_drive_destination_keeps_its_stored_secret_when_left_blank()
    {
        var job = BaseJob();
        job.Destinations.Add(new DestinationConfig
        {
            Name = "Drive",
            Kind = DestinationKind.GoogleDrive,
            Drive = new DriveSettings
            {
                ClientId = "id", ProtectedClientSecret = "enc:guardado", ProtectedRefreshToken = "enc:token",
                AccountEmail = "demo@example.test", FolderName = "Backups/SQL"
            }
        });
        var rig = Create(job);
        var drive = rig.Editor.Destinations[1];

        Assert.True(rig.Editor.CanSave);
        Assert.True(drive.IsConnected);
        Assert.Contains("Déjelo en blanco", drive.SecretHint);

        rig.Editor.SaveCommand.Execute(null);

        var saved = rig.Editor.Result!.Destinations[1].Drive!;
        Assert.Equal("enc:guardado", saved.ProtectedClientSecret);
        Assert.Equal("Backups/SQL", saved.FolderName);
    }

    [Fact]
    public void Disconnecting_forgets_the_account_and_blocks_saving_again()
    {
        var job = BaseJob();
        job.Destinations.Add(new DestinationConfig
        {
            Name = "Drive",
            Kind = DestinationKind.GoogleDrive,
            Drive = new DriveSettings { ClientId = "id", ProtectedClientSecret = "enc:s", ProtectedRefreshToken = "enc:t" }
        });
        var rig = Create(job);
        var drive = rig.Editor.Destinations[1];

        drive.DisconnectCommand.Execute(null);

        Assert.False(drive.IsConnected);
        Assert.Equal("Sin conectar", drive.AccountText);
        Assert.NotEmpty(drive.AccountError);
        Assert.False(rig.Editor.CanSave);
    }

    [Fact]
    public async Task Testing_drive_requires_a_connected_account_then_reports_success_or_failure()
    {
        var rig = Create();
        var drive = AddDrive(rig);
        drive.ClientId = "id";
        drive.ClientSecret = "s";

        drive.TestCommand.Execute(null);
        await WaitUntil(() => drive.TestResult.Length > 0);
        Assert.Contains("Conecte primero", drive.TestResult);
        Assert.Empty(rig.Remote.Tested);

        drive.ConnectCommand.Execute(null);
        await WaitUntil(() => drive.IsConnected);

        drive.TestCommand.Execute(null);
        await WaitUntil(() => drive.TestResult.StartsWith('✔'));
        Assert.Contains("demo@example.test", drive.TestResult);
        // La prueba usa secretos ya cifrados, nunca el texto en claro ni la marca interna.
        var tested = Assert.Single(rig.Remote.Tested).Drive!;
        Assert.Equal("enc:s", tested.ProtectedClientSecret);

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
