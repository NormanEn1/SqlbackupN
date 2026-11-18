using Microsoft.Extensions.Logging.Abstractions;
using SqlBackupN.Core.Abstractions;
using SqlBackupN.Core.Domain;
using SqlBackupN.Core.Services;
using SqlBackupN.Presentation.Jobs;

namespace SqlBackupN.Tests;

/// <summary>El registro paso a paso de una ejecución: se informa en vivo, se guarda en el historial y se muestra.</summary>
public class RunLogTests
{
    /// <summary>Receptor síncrono: <see cref="Progress{T}"/> entrega en otro hilo y no sirve para comprobar el orden.</summary>
    private sealed class Collector : IProgress<RunLogEntry>
    {
        public List<RunLogEntry> Entries { get; } = [];
        public void Report(RunLogEntry value) => Entries.Add(value);
    }

    private sealed class Exploding : IProgress<RunLogEntry>
    {
        public void Report(RunLogEntry value) => throw new InvalidOperationException("receptor roto");
    }

    private sealed class Executor(params string[] databases) : IBackupExecutor
    {
        public HashSet<string> FailFor { get; } = [];

        public Task<IReadOnlyList<string>> ListDatabasesAsync(SqlServerConnection c, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(databases);

        public Task BackupAsync(SqlServerConnection c, BackupRequest r, CancellationToken ct) =>
            FailFor.Contains(r.Database) ? Task.FromException(new InvalidOperationException("disco lleno")) : Task.CompletedTask;

        public Task VerifyAsync(SqlServerConnection c, string p, bool k, int t, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Files : IFileStore
    {
        public bool FailCopy { get; set; }
        public void CreateZip(string sourceFile, string zipPath) { }
    public void EnsureDirectory(string path) { }
        public void VerifyWritable(string directory) { }
        public void Copy(string source, string destination)
        {
            if (FailCopy) throw new IOException("sin acceso");
        }
        public void Delete(string path) { }
        public long GetSize(string path) => 3 * 1048576;
        public IReadOnlyList<StoredFile> List(string directory, string searchPattern) => [];
    }

    private sealed class History : IHistoryStore
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

    private static BackupJob NewJob(int destinations = 1) => new()
    {
        Name = "Prueba",
        Connection = new SqlServerConnection { Server = "SRV" },
        Destinations = Enumerable.Range(0, destinations)
            .Select(i => new DestinationConfig { Name = $"D{i}", Path = Path.Combine("C:", "bk", $"d{i}") }).ToList()
    };

    private static (JobRunner Runner, Executor Exec, Files Files, History History) Build(params string[] databases)
    {
        var exec = new Executor(databases);
        var files = new Files();
        var history = new History();
        var runner = new JobRunner(exec, files, new StubRemote(), history, new StubNotifier(), TimeProvider.System,
            NullLogger<JobRunner>.Instance);
        return (runner, exec, files, history);
    }

    [Fact]
    public async Task A_successful_run_reports_every_step_in_order()
    {
        var (runner, _, _, _) = Build("Ventas");
        var live = new Collector();

        var record = await runner.RunStepAsync(NewJob(2), new BackupStep(), false, CancellationToken.None, live);

        var texts = live.Entries.Select(e => e.Message).ToList();
        string[] expected =
        [
            "Inicio del backup completo", "Conectando con SRV", "Bases a respaldar (1): Ventas", "Haciendo el backup completo",
            "Backup creado.", "Verificando el archivo", "Verificación correcta.", "Tamaño del archivo: 3.0 MB",
            "Aplicando la retención en 'D0'", "Copiando a 'D1' (carpeta)", "Copia completa en 'D1'.",
            "Aplicando la retención en 'D1'", "Base terminada: correcto", "Terminado: correcto"
        ];
        var position = -1;
        foreach (var step in expected)
        {
            var next = texts.FindIndex(position + 1, t => t.StartsWith(step, StringComparison.Ordinal));
            Assert.True(next > position, $"Falta o está fuera de orden: «{step}».\n{string.Join("\n", texts)}");
            position = next;
        }
        Assert.All(live.Entries.Where(e => e.Message.StartsWith("Haciendo", StringComparison.Ordinal)),
            e => Assert.Equal("Ventas", e.Database));
        Assert.All(live.Entries, e => Assert.Equal(RunLogLevel.Info, e.Level));
        Assert.Equal(record.Log.Select(e => e.Message), texts);
    }

    [Fact]
    public async Task The_log_is_saved_with_the_run_in_the_history()
    {
        var (runner, _, _, history) = Build("Ventas");

        await runner.RunStepAsync(NewJob(), new BackupStep(), false, CancellationToken.None);

        var saved = Assert.Single(history.Records);
        Assert.NotEmpty(saved.Log);
        Assert.Contains(saved.Log, e => e.Message.StartsWith("Terminado: correcto", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_backup_is_logged_as_an_error_with_the_reason()
    {
        var (runner, exec, _, _) = Build("Ventas", "Compras");
        exec.FailFor.Add("Compras");

        var record = await runner.RunStepAsync(NewJob(), new BackupStep(), false, CancellationToken.None);

        var error = Assert.Single(record.Log, e => e.Level == RunLogLevel.Error && e.Message.StartsWith("Falló el backup", StringComparison.Ordinal));
        Assert.Equal("Compras", error.Database);
        Assert.Contains("disco lleno", error.Message);
        Assert.Contains(record.Log, e => e.Database == "Ventas" && e.Message.StartsWith("Base terminada: correcto", StringComparison.Ordinal));
        Assert.Equal(RunLogLevel.Error, record.Log[^1].Level);   // un fallo en una base hace fallar la ejecución
    }

    [Fact]
    public async Task A_failed_copy_is_logged_as_a_warning()
    {
        var (runner, _, files, _) = Build("Ventas");
        files.FailCopy = true;

        var record = await runner.RunStepAsync(NewJob(2), new BackupStep(), false, CancellationToken.None);

        var warning = Assert.Single(record.Log, e => e.Level == RunLogLevel.Warning && e.Message.Contains("No se pudo copiar"));
        Assert.Contains("sin acceso", warning.Message);
        Assert.Equal(RunLogLevel.Warning, record.Log[^1].Level);
    }

    [Fact]
    public async Task A_superseded_step_logs_that_it_was_skipped()
    {
        var (runner, _, _, _) = Build("Ventas");

        var record = await runner.RunStepAsync(NewJob(), new BackupStep(), true, CancellationToken.None);

        Assert.Contains(record.Log, e => e.Message.StartsWith("Paso omitido", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_broken_listener_never_interrupts_the_backup()
    {
        var (runner, _, _, history) = Build("Ventas");

        var record = await runner.RunStepAsync(NewJob(), new BackupStep(), false, CancellationToken.None, new Exploding());

        Assert.Equal(RunStatus.Succeeded, record.Status);
        Assert.Single(history.Records);
    }

    // ------------------------------------------------------------- Pantalla principal

    private static (MainViewModel Main, StubDialogs Dialogs) CreateMain(BackupJob job, IHistoryStore? history = null)
    {
        var store = new MemoryJobStore { Jobs = [job] };
        var dialogs = new StubDialogs();
        var executor = new StubExecutor();
        var files = new StubFiles();
        history ??= new MemoryHistoryStore();
        var runner = new JobRunner(executor, files, new StubRemote(), history, new StubNotifier(), TimeProvider.System,
            NullLogger<JobRunner>.Instance);
        var main = new MainViewModel(store, history, runner, dialogs, new StubProtector(), executor, files,
            new StubRemote(), new StubAuthorizer(), new StubGoogleClients(), TimeProvider.System);
        return (main, dialogs);
    }

    private static BackupJob MainJob() => new()
    {
        Name = "Ventas",
        Connection = new SqlServerConnection { Server = "srv01" },
        Steps = [new BackupStep { Type = BackupType.Full, Cron = "0 23 * * *" }],
        Destinations = [new DestinationConfig { Name = "Local", Path = @"D:\Backups" }]
    };

    [Fact]
    public async Task Run_now_opens_the_log_window_right_away_and_fills_it_until_it_finishes()
    {
        // xUnit instala su propio contexto de sincronización, que puede entregar los informes en desorden.
        // La interfaz real usa un único hilo (en orden); aquí se quita el contexto para entregarlos de inmediato y en orden.
        SynchronizationContext.SetSynchronizationContext(null);
        var (main, dialogs) = CreateMain(MainJob());

        main.RunNowCommand.Execute(null);
        var log = Assert.Single(dialogs.RunLogs);                 // la ventana se abre antes de que termine el backup
        for (var i = 0; i < 300 && (main.RunNowCommand.IsRunning || log.IsRunning); i++) await Task.Delay(10);

        Assert.False(log.IsRunning);
        Assert.Contains("Ventas", log.Title);
        Assert.True(log.Lines.Any(l => l.Text.StartsWith("[Ventas] Backup creado", StringComparison.Ordinal)),
            string.Join(" | ", log.Lines.Select(l => l.Text)));
        Assert.StartsWith("Terminado: correcto", log.Lines[^1].Text);
        Assert.Equal("Terminó correctamente", log.Subtitle);
        Assert.Equal("ok", log.Kind);
        Assert.Contains("Backup creado", log.AsText);
    }

    [Fact]
    public void A_new_log_starts_running_and_finishing_sets_its_state()
    {
        var log = new RunLogViewModel("Registro", isRunning: true);

        Assert.True(log.IsRunning);
        Assert.Equal("En curso…", log.Subtitle);

        log.Finish(RunStatus.Failed);

        Assert.False(log.IsRunning);
        Assert.Equal("fail", log.Kind);
        Assert.Equal("Terminó con fallos", log.Subtitle);
    }

    [Fact]
    public void While_running_the_log_shows_the_elapsed_time()
    {
        var log = new RunLogViewModel("Registro", isRunning: true);

        log.Tick();

        Assert.Matches(@"^En curso… \d\d:\d\d$", log.Subtitle);
    }

    [Fact]
    public async Task A_log_still_running_catches_up_when_its_run_is_already_in_the_history()
    {
        var record = new JobRunRecord
        {
            Status = RunStatus.Succeeded,
            Log =
            [
                new RunLogEntry { TimeUtc = DateTime.UtcNow, Level = RunLogLevel.Info, Message = "Copia completa" },
                new RunLogEntry { TimeUtc = DateTime.UtcNow, Level = RunLogLevel.Info, Message = "Terminado: correcto" }
            ]
        };
        var log = new RunLogViewModel("Registro", isRunning: true) { Probe = () => Task.FromResult<JobRunRecord?>(record) };
        log.Add(new RunLogEntry { TimeUtc = DateTime.UtcNow, Level = RunLogLevel.Info, Message = "Copiando a Drive" });

        Assert.True(await log.CheckCompletionAsync());

        Assert.False(log.IsRunning);
        Assert.Equal("Terminó correctamente", log.Subtitle);
        Assert.Equal(2, log.Lines.Count);
        Assert.Contains("Terminado: correcto", log.AsText);
    }

    [Fact]
    public async Task A_log_keeps_waiting_while_its_run_is_not_in_the_history_yet()
    {
        var log = new RunLogViewModel("Registro", isRunning: true) { Probe = () => Task.FromResult<JobRunRecord?>(null) };

        Assert.False(await log.CheckCompletionAsync());
        Assert.True(log.IsRunning);
    }

    [Fact]
    public void Clicking_a_history_entry_opens_its_log_in_its_own_window()
    {
        var (main, dialogs) = CreateMain(MainJob());
        var entries = new List<RunLogEntry>
        {
            new() { TimeUtc = DateTime.UtcNow, Level = RunLogLevel.Info, Message = "Inicio" },
            new() { TimeUtc = DateTime.UtcNow, Level = RunLogLevel.Error, Database = "Compras", Message = "Falló" }
        };
        var item = new HistoryItem("17 nov 10:00", "✖", "Completo · 0,0 MB", "Falló", RunStatus.Failed, entries);

        main.ShowLogCommand.Execute(item);

        var log = Assert.Single(dialogs.RunLogs);
        Assert.Contains("17 nov 10:00", log.Title);
        Assert.Equal(2, log.Lines.Count);
        Assert.Equal("[Compras] Falló", log.Lines[1].Text);
        Assert.Equal("fail", log.Lines[1].Kind);
        Assert.Equal("Terminó con fallos", log.Subtitle);

        main.ShowLogCommand.Execute(item);                         // se pueden abrir varias
        Assert.Equal(2, dialogs.RunLogs.Count);
    }

    // ------------------------------------------------------------- Ejecuciones anteriores al registro

    private static JobRunRecord OldRecord() => new()
    {
        JobName = "Centinela",
        Type = BackupType.Full,
        StartedUtc = new DateTime(2026, 11, 18, 0, 11, 0, DateTimeKind.Utc),
        FinishedUtc = new DateTime(2026, 11, 18, 0, 11, 40, DateTimeKind.Utc),
        Status = RunStatus.Warning,
        Results =
        [
            new DatabaseBackupResult
            {
                Database = "CentinelaApp", Status = RunStatus.Warning, FilePath = @"D:\BackupUSB\CentinelaApp.bak",
                SizeBytes = 308_000_000, Duration = TimeSpan.FromSeconds(38),
                Warnings = ["No se pudo copiar a 'Google Drive': sin conexión"]
            }
        ]
    };

    [Fact]
    public void A_run_without_a_saved_log_gets_a_log_rebuilt_from_its_summary()
    {
        var lines = RunLogBuilder.For(OldRecord(), out var reconstructed);

        Assert.True(reconstructed);
        Assert.Contains(lines, l => l.Message.StartsWith("Registro reconstruido", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Message.StartsWith("Inicio del backup completo del job 'Centinela'", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Database == "CentinelaApp" && l.Message.StartsWith("Backup creado: D:", StringComparison.Ordinal)
                                    && l.Message.Contains("00:00:38"));
        Assert.Contains(lines, l => l.Level == RunLogLevel.Warning && l.Message.Contains("sin conexión"));
        Assert.Equal("Terminado: con advertencias en 00:00:40.", lines[^1].Message);
    }

    [Fact]
    public void A_run_with_a_saved_log_keeps_its_own_log()
    {
        var record = OldRecord();
        record.Log.Add(new RunLogEntry { TimeUtc = DateTime.UtcNow, Message = "Propio" });

        var lines = RunLogBuilder.For(record, out var reconstructed);

        Assert.False(reconstructed);
        Assert.Equal("Propio", Assert.Single(lines).Message);
    }

    private sealed class OneRecordHistory(JobRunRecord record) : IHistoryStore
    {
        public Task AppendAsync(JobRunRecord r, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<JobRunRecord>> GetRecentAsync(Guid? jobId, int max, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<JobRunRecord>>([record]);
        public Task<int> DeleteAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> ClearAsync(Guid? jobId, CancellationToken ct = default) => Task.FromResult(0);
        public Task<DateTime?> GetLastRunUtcAsync(Guid jobId, Guid stepId, CancellationToken ct = default) =>
            Task.FromResult<DateTime?>(null);
    }

    [Fact]
    public void Clicking_an_old_history_entry_opens_a_rebuilt_log_and_says_so()
    {
        var job = MainJob();
        var record = OldRecord();
        record.JobId = job.Id;
        var (main, dialogs) = CreateMain(job, new OneRecordHistory(record));

        var item = Assert.Single(main.History);
        Assert.True(item.HasLog);
        Assert.True(item.Reconstructed);

        main.ShowLogCommand.Execute(item);

        var log = Assert.Single(dialogs.RunLogs);
        Assert.Contains("reconstruido", log.Subtitle);
        Assert.Contains(log.Lines, l => l.Text.StartsWith("[CentinelaApp] Backup creado", StringComparison.Ordinal));
    }
}
