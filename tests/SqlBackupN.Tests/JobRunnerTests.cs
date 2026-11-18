using Microsoft.Extensions.Logging.Abstractions;
using SqlBackupN.Core.Abstractions;
using SqlBackupN.Core.Domain;
using SqlBackupN.Core.Services;

namespace SqlBackupN.Tests;

/// <summary>Pruebas de orquestación con dobles de prueba: sin SQL Server ni disco reales.</summary>
public class JobRunnerTests
{
    private sealed class FakeExecutor(params string[] databases) : IBackupExecutor
    {
        public List<BackupRequest> Backups { get; } = [];
        public HashSet<string> FailFor { get; } = [];

        public Task<IReadOnlyList<string>> ListDatabasesAsync(SqlServerConnection c, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(databases);

        public Task BackupAsync(SqlServerConnection c, BackupRequest request, CancellationToken ct)
        {
            if (FailFor.Contains(request.Database)) throw new InvalidOperationException("disco lleno");
            Backups.Add(request);
            return Task.CompletedTask;
        }

        public Task VerifyAsync(SqlServerConnection c, string path, bool checksum, int timeout, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class FakeFiles : IFileStore
    {
        public List<string> Copies { get; } = [];
        public List<string> Deleted { get; } = [];
        public bool FailCopy { get; set; }
        public void CreateZip(string sourceFile, string zipPath) { }
    public void EnsureDirectory(string path) { }
        public void VerifyWritable(string directory) { }
        public void Copy(string source, string destination)
        {
            if (FailCopy) throw new IOException("sin acceso");
            Copies.Add(destination);
        }
        public void Delete(string path) => Deleted.Add(path);
        public long GetSize(string path) => 1024;
        public IReadOnlyList<StoredFile> List(string directory, string searchPattern) => [];
    }

    private sealed class FakeHistory : IHistoryStore
    {
        public List<JobRunRecord> Records { get; } = [];
        public Task AppendAsync(JobRunRecord record, CancellationToken ct = default) { Records.Add(record); return Task.CompletedTask; }
        public Task<IReadOnlyList<JobRunRecord>> GetRecentAsync(Guid? jobId, int max, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<JobRunRecord>>(Records);
        public Task<int> DeleteAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> ClearAsync(Guid? jobId, CancellationToken ct = default) => Task.FromResult(0);
        public Task<DateTime?> GetLastRunUtcAsync(Guid jobId, Guid stepId, CancellationToken ct = default) =>
            Task.FromResult<DateTime?>(null);
    }

    private sealed class FakeNotifier : INotifier
    {
        public int Calls { get; private set; }
        public Task NotifyAsync(BackupJob job, JobRunRecord record, CancellationToken ct) { Calls++; return Task.CompletedTask; }
        public Task SendMessageAsync(NotificationSettings settings, NoticeKind kind, string message, CancellationToken ct) => Task.CompletedTask;
    }

    private static BackupJob NewJob(int destinations = 1) => new()
    {
        Name = "Prueba",
        Connection = new SqlServerConnection { Server = "SRV" },
        Destinations = Enumerable.Range(0, destinations)
            .Select(i => new DestinationConfig { Name = $"D{i}", Path = Path.Combine("C:", "bk", $"d{i}") }).ToList(),
        Notifications = new NotificationSettings { WebhookUrl = "https://example.test/hook" }
    };

    private static (JobRunner runner, FakeExecutor exec, FakeFiles files, FakeHistory history, FakeNotifier notifier) Build(
        params string[] databases)
    {
        var exec = new FakeExecutor(databases);
        var files = new FakeFiles();
        var history = new FakeHistory();
        var notifier = new FakeNotifier();
        var runner = new JobRunner(exec, files, new StubRemote(), history, notifier, TimeProvider.System, NullLogger<JobRunner>.Instance);
        return (runner, exec, files, history, notifier);
    }

    [Fact]
    public async Task Backs_up_each_user_database_and_records_history()
    {
        var (runner, exec, _, history, _) = Build("master", "Ventas", "Compras");
        var job = NewJob();

        var record = await runner.RunStepAsync(job, new BackupStep { Type = BackupType.Full }, false, CancellationToken.None);

        Assert.Equal(RunStatus.Succeeded, record.Status);
        Assert.Equal(["Compras", "Ventas"], exec.Backups.Select(b => b.Database));
        Assert.Single(history.Records);
    }

    [Fact]
    public async Task A_failing_database_does_not_stop_the_others()
    {
        var (runner, exec, _, _, notifier) = Build("A", "B", "C");
        exec.FailFor.Add("B");

        var record = await runner.RunStepAsync(NewJob(), new BackupStep(), false, CancellationToken.None);

        Assert.Equal(RunStatus.Failed, record.Status);
        Assert.Equal(["A", "C"], exec.Backups.Select(b => b.Database));
        Assert.Equal("disco lleno", record.Results.Single(r => r.Status == RunStatus.Failed).Error);
        Assert.Equal(1, notifier.Calls);
    }

    [Fact]
    public async Task Failed_secondary_copy_is_a_warning_not_a_failure()
    {
        var (runner, _, files, _, _) = Build("A");
        files.FailCopy = true;

        var record = await runner.RunStepAsync(NewJob(destinations: 2), new BackupStep(), false, CancellationToken.None);

        Assert.Equal(RunStatus.Warning, record.Status);
        Assert.Single(record.Results[0].Warnings);
    }

    [Fact]
    public async Task Superseded_step_is_recorded_as_skipped_without_backing_up()
    {
        var (runner, exec, _, history, _) = Build("A");

        var record = await runner.RunStepAsync(
            NewJob(), new BackupStep { Type = BackupType.Differential }, superseded: true, CancellationToken.None);

        Assert.Equal(RunStatus.Skipped, record.Status);
        Assert.Empty(exec.Backups);
        Assert.Single(history.Records);
    }

    [Fact]
    public async Task Job_without_destinations_fails_with_clear_message()
    {
        var (runner, _, _, _, _) = Build("A");
        var job = NewJob();
        job.Destinations.Clear();

        var record = await runner.RunStepAsync(job, new BackupStep(), false, CancellationToken.None);

        Assert.Equal(RunStatus.Failed, record.Status);
        Assert.Contains("destinos", record.Results[0].Error);
    }
}
