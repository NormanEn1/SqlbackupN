using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using SqlBackupN.Core.Abstractions;
using SqlBackupN.Core.Domain;
using SqlBackupN.Core.Infrastructure;
using SqlBackupN.Core.Services;

namespace SqlBackupN.Tests;

/// <summary>Salida en .zip: el .bak se comprime, se borra y el zip es lo que se copia, sube y somete a retención.</summary>
public class ZipOutputTests
{
    private sealed class Executor(params string[] databases) : IBackupExecutor
    {
        public List<string> BackedUpTo { get; } = [];
        public List<string> VerifiedFiles { get; } = [];

        public Task<IReadOnlyList<string>> ListDatabasesAsync(SqlServerConnection c, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(databases);

        public Task BackupAsync(SqlServerConnection c, BackupRequest r, CancellationToken ct)
        {
            BackedUpTo.Add(r.FilePath);
            return Task.CompletedTask;
        }

        public Task VerifyAsync(SqlServerConnection c, string p, bool k, int t, CancellationToken ct)
        {
            VerifiedFiles.Add(p);
            return Task.CompletedTask;
        }
    }

    private sealed class Files : IFileStore
    {
        public List<(string Source, string Zip)> Zips { get; } = [];
        public List<string> Deleted { get; } = [];
        public List<string> Copies { get; } = [];
        public List<string> ListedPatterns { get; } = [];
        public List<string> Events { get; } = [];
        public bool FailZip { get; set; }
        public bool FailDeleteBak { get; set; }
        public bool CopyIsTruncated { get; set; }

        public void EnsureDirectory(string path) { }
        public void VerifyWritable(string directory) { }

        public void CreateZip(string sourceFile, string zipPath)
        {
            if (FailZip) throw new IOException("disco temporal lleno");
            Zips.Add((sourceFile, zipPath));
            Events.Add("zip");
        }

        public void Copy(string source, string destination)
        {
            Copies.Add(destination);
            Events.Add("copy");
        }

        public void Delete(string path)
        {
            if (FailDeleteBak && path.EndsWith(".bak", StringComparison.Ordinal)) throw new IOException("archivo en uso");
            Deleted.Add(path);
            Events.Add("delete:" + Path.GetExtension(path));
        }

        public long GetSize(string path) =>
            CopyIsTruncated && path.StartsWith(Path.Combine("E:", "bk"), StringComparison.Ordinal) ? 10 : 1048576;

        public IReadOnlyList<StoredFile> List(string directory, string searchPattern)
        {
            ListedPatterns.Add(searchPattern);
            return [];
        }
    }

    private sealed class History : IHistoryStore
    {
        public Task AppendAsync(JobRunRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<JobRunRecord>> GetRecentAsync(Guid? jobId, int max, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<JobRunRecord>>([]);
        public Task<int> DeleteAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> ClearAsync(Guid? jobId, CancellationToken ct = default) => Task.FromResult(0);
        public Task<DateTime?> GetLastRunUtcAsync(Guid jobId, Guid stepId, CancellationToken ct = default) =>
            Task.FromResult<DateTime?>(null);
    }

    private static BackupJob NewJob(bool zip, string template = "Backup{Date}_{Database}") => new()
    {
        Name = "Prueba",
        Connection = new SqlServerConnection { Server = "SRV" },
        Destinations =
        [
            new DestinationConfig { Name = "Temporal", Path = Path.Combine("D:", "tmp") },
            new DestinationConfig { Name = "Respaldo", Path = Path.Combine("E:", "bk") }
        ],
        Options = new BackupOptions { ZipOutput = zip, PathTemplate = template }
    };

    private static (JobRunner Runner, Executor Exec, Files Files) Build(params string[] databases) =>
        Build(new StubRemote(), databases);

    private static (JobRunner Runner, Executor Exec, Files Files) Build(StubRemote remote, params string[] databases)
    {
        var exec = new Executor(databases);
        var files = new Files();
        var runner = new JobRunner(exec, files, remote, new History(), new StubNotifier(), TimeProvider.System,
            NullLogger<JobRunner>.Instance);
        return (runner, exec, files);
    }

    private static BackupJob TempJob(bool zip, bool withDrive = false)
    {
        var job = NewJob(zip);
        job.Options.TemporaryPrimary = true;
        if (withDrive)
            job.Destinations.Add(new DestinationConfig { Name = "Mi Drive", Kind = DestinationKind.GoogleDrive, Drive = new DriveSettings() });
        return job;
    }

    private static string Today => DateTime.Now.ToString("yyyyMMdd");

    [Fact]
    public async Task Zip_is_made_from_the_verified_bak_and_the_bak_is_deleted()
    {
        var (runner, exec, files) = Build("Ventas");

        var record = await runner.RunStepAsync(NewJob(zip: true), new BackupStep(), false, CancellationToken.None);

        var bak = Path.Combine("D:", "tmp", $"Backup{Today}_Ventas.bak");
        var zip = Path.Combine("D:", "tmp", $"Backup{Today}_Ventas.zip");
        Assert.Equal([bak], exec.BackedUpTo);
        Assert.Equal([bak], exec.VerifiedFiles);                 // se verifica el .bak, antes de comprimir
        Assert.Equal([(bak, zip)], files.Zips);
        Assert.Contains(bak, files.Deleted);
        Assert.Equal(RunStatus.Succeeded, record.Status);
        Assert.Equal(zip, record.Results[0].FilePath);
    }

    [Fact]
    public async Task The_zip_not_the_bak_is_copied_and_retention_looks_for_zip_files()
    {
        var (runner, _, files) = Build("Ventas");

        await runner.RunStepAsync(NewJob(zip: true), new BackupStep(), false, CancellationToken.None);

        Assert.Equal([Path.Combine("E:", "bk", $"Backup{Today}_Ventas.zip")], files.Copies);
        Assert.All(files.ListedPatterns, p => Assert.Equal("*.zip", p));
        Assert.Equal(2, files.ListedPatterns.Count);               // una por destino
    }

    [Fact]
    public async Task Without_the_option_nothing_is_zipped_and_the_bak_stays()
    {
        var (runner, exec, files) = Build("Ventas");

        var record = await runner.RunStepAsync(NewJob(zip: false), new BackupStep(), false, CancellationToken.None);

        Assert.Empty(files.Zips);
        Assert.Empty(files.Deleted);
        Assert.EndsWith(".bak", exec.BackedUpTo[0], StringComparison.Ordinal);
        Assert.EndsWith(".bak", record.Results[0].FilePath!, StringComparison.Ordinal);
        Assert.All(files.ListedPatterns, p => Assert.Equal("*.bak", p));
    }

    [Fact]
    public async Task Each_database_gets_its_own_zip()
    {
        var (runner, _, files) = Build("A", "B");

        await runner.RunStepAsync(NewJob(zip: true), new BackupStep(), false, CancellationToken.None);

        Assert.Equal(
            [$"Backup{Today}_A.zip", $"Backup{Today}_B.zip"],
            files.Zips.Select(z => Path.GetFileName(z.Zip)));
    }

    [Fact]
    public async Task A_zip_failure_fails_that_database_and_keeps_the_bak()
    {
        var (runner, _, files) = Build("Ventas");
        files.FailZip = true;

        var record = await runner.RunStepAsync(NewJob(zip: true), new BackupStep(), false, CancellationToken.None);

        Assert.Equal(RunStatus.Failed, record.Status);
        Assert.Contains("se conservó", record.Results[0].Error);
        Assert.Contains("disco temporal lleno", record.Results[0].Error);
        Assert.Empty(files.Deleted);          // el .bak sigue ahí
        Assert.Empty(files.Copies);           // y nada se copia a medias
    }

    [Fact]
    public async Task Failing_to_delete_the_bak_is_only_a_warning_because_the_zip_exists()
    {
        var (runner, _, files) = Build("Ventas");
        files.FailDeleteBak = true;

        var record = await runner.RunStepAsync(NewJob(zip: true), new BackupStep(), false, CancellationToken.None);

        Assert.Equal(RunStatus.Warning, record.Status);
        Assert.Contains("No se pudo borrar el .bak", Assert.Single(record.Results[0].Warnings));
        Assert.Single(files.Copies);          // el zip igual se copió
    }

    // ------------------------------------------------------------- Archivo real (carpeta temporal)

    [Fact]
    public void The_local_file_store_writes_a_readable_zip_with_the_original_name_and_leaves_no_temp_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sqlbackupn-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var bak = Path.Combine(dir, "Backup20261117.bak");
            File.WriteAllText(bak, new string('x', 10_000));
            var zipPath = Path.Combine(dir, "Backup20261117.zip");

            new LocalFileStore().CreateZip(bak, zipPath);

            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var entry = Assert.Single(zip.Entries);
                Assert.Equal("Backup20261117.bak", entry.Name);
                Assert.Equal(10_000, entry.Length);
            }
            Assert.True(File.Exists(bak));                       // no borra el original
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_failed_zip_leaves_no_partial_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sqlbackupn-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var zipPath = Path.Combine(dir, "x.zip");

            Assert.ThrowsAny<IOException>(() => new LocalFileStore().CreateZip(Path.Combine(dir, "no-existe.bak"), zipPath));

            Assert.Empty(Directory.GetFiles(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ------------------------------------------------------------- Plantilla de nombre

    private static BackupJob ValidJob(DatabaseSelection? databases = null) => new()
    {
        Name = "Ventas",
        Connection = new SqlServerConnection { Server = "srv01" },
        Databases = databases ?? new DatabaseSelection { Mode = DatabaseSelectionMode.Explicit, Names = ["CentinelaApp"] },
        Steps = [new BackupStep { Type = BackupType.Full, Cron = "0 23 * * *" }],
        Destinations = [new DestinationConfig { Name = "Local", Path = @"D:\Backups" }],
        Retention = new RetentionPolicy { KeepDays = 30, KeepLast = 3 },
        Notifications = new NotificationSettings { WebhookUrl = "https://hooks.example.test/aviso" }
    };

    [Fact]
    public void Backup_with_only_the_date_is_valid_for_a_single_explicit_database_with_a_warning()
    {
        var job = ValidJob();
        job.Options.PathTemplate = "Backup{Date}";

        var issues = JobValidator.Validate(job);

        Assert.False(JobValidator.HasErrors(issues));
        Assert.Contains(issues, i => i.Field == "Options.PathTemplate" && i.Severity == IssueSeverity.Warning
                                     && i.Message.Contains("{Date}"));
    }

    [Theory]
    [InlineData(DatabaseSelectionMode.AllUserDatabases, new string[0])]
    [InlineData(DatabaseSelectionMode.Explicit, new[] { "A", "B" })]
    public void Without_database_in_the_name_several_databases_are_still_rejected(DatabaseSelectionMode mode, string[] names)
    {
        var job = ValidJob(new DatabaseSelection { Mode = mode, Names = [.. names] });
        job.Options.PathTemplate = "Backup{Date}";

        Assert.Contains(JobValidator.Validate(job), i => i.Field == "Options.PathTemplate" && i.Severity != IssueSeverity.Warning
                                                         && i.Message.Contains("{Database}"));
    }

    [Fact]
    public void A_name_with_neither_timestamp_nor_date_is_still_rejected()
    {
        var job = ValidJob();
        job.Options.PathTemplate = "Backup";

        Assert.Contains(JobValidator.Validate(job), i => i.Field == "Options.PathTemplate" && i.Severity != IssueSeverity.Warning);
    }

    // ------------------------------------------------------------- Carpeta principal temporal

    [Fact]
    public async Task Temporary_primary_keeps_everything_until_the_copy_is_complete_then_deletes_zip_and_bak()
    {
        var (runner, _, files) = Build("Ventas");

        var record = await runner.RunStepAsync(TempJob(zip: true), new BackupStep(), false, CancellationToken.None);

        Assert.Equal(RunStatus.Succeeded, record.Status);
        // primero se comprime y se copia; solo después se borra todo (el zip y el .bak) de la carpeta temporal
        Assert.Equal(["zip", "copy", "delete:.zip", "delete:.bak"], files.Events);
        Assert.Contains(record.Log, l => l.Message.StartsWith("Copias completas en los destinos finales", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Temporary_primary_without_zip_deletes_the_bak_after_copying()
    {
        var (runner, _, files) = Build("Ventas");

        await runner.RunStepAsync(TempJob(zip: false), new BackupStep(), false, CancellationToken.None);

        Assert.Equal(["copy", "delete:.bak"], files.Events);
    }

    [Fact]
    public async Task Temporary_primary_keeps_the_files_when_a_copy_fails()
    {
        var remote = new StubRemote { UploadFailure = new IOException("sin conexión") };
        var (runner, _, files) = Build(remote, "Ventas");

        var record = await runner.RunStepAsync(TempJob(zip: true, withDrive: true), new BackupStep(), false, CancellationToken.None);

        Assert.Equal(RunStatus.Warning, record.Status);
        Assert.Equal(["zip", "copy"], files.Events);                // la copia a la carpeta salió bien, la de Drive no: no se borra nada
        Assert.Contains(record.Log, l => l.Level == RunLogLevel.Warning && l.Message.Contains("no se limpió"));
    }

    [Fact]
    public async Task A_truncated_folder_copy_counts_as_failed_and_nothing_is_deleted()
    {
        var (runner, _, files) = Build("Ventas");
        files.CopyIsTruncated = true;

        var record = await runner.RunStepAsync(TempJob(zip: true), new BackupStep(), false, CancellationToken.None);

        Assert.Equal(RunStatus.Warning, record.Status);
        Assert.Contains("mismo tamaño", Assert.Single(record.Results[0].Warnings));
        Assert.DoesNotContain(files.Events, e => e.StartsWith("delete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Temporary_primary_waits_for_every_final_destination()
    {
        var remote = new StubRemote();
        var (runner, _, files) = Build(remote, "Ventas");

        var record = await runner.RunStepAsync(TempJob(zip: true, withDrive: true), new BackupStep(), false, CancellationToken.None);

        Assert.Equal(RunStatus.Succeeded, record.Status);
        Assert.Single(remote.Uploads);                              // el zip llegó a Drive...
        Assert.Equal(["zip", "copy", "delete:.zip", "delete:.bak"], files.Events);   // ...y a la carpeta; recién entonces se borra
    }

    [Fact]
    public async Task Temporary_primary_does_not_apply_retention_to_the_primary_folder()
    {
        var (runner, _, files) = Build("Ventas");

        await runner.RunStepAsync(TempJob(zip: true), new BackupStep(), false, CancellationToken.None);

        Assert.Single(files.ListedPatterns);                        // solo el destino final
    }

    [Fact]
    public async Task With_a_single_destination_nothing_is_deleted_even_if_the_option_is_on()
    {
        var (runner, _, files) = Build("Ventas");
        var job = TempJob(zip: false);
        job.Destinations.RemoveAt(1);

        await runner.RunStepAsync(job, new BackupStep(), false, CancellationToken.None);

        Assert.Empty(files.Deleted);                                // protección: sería el único ejemplar
    }

    [Fact]
    public void Temporary_primary_requires_another_final_destination()
    {
        var job = ValidJob();
        job.Options.TemporaryPrimary = true;

        Assert.Contains(JobValidator.Validate(job), i => i.Field == "Options.TemporaryPrimary" && i.Severity != IssueSeverity.Warning);

        job.Destinations.Add(new DestinationConfig { Name = "NAS", Path = @"\\nas01\backups" });

        Assert.DoesNotContain(JobValidator.Validate(job), i => i.Field == "Options.TemporaryPrimary");
    }
}
