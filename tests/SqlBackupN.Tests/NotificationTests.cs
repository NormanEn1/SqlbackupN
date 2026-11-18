using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SqlBackupN.Core.Abstractions;
using SqlBackupN.Core.Domain;
using SqlBackupN.Core.Infrastructure;
using SqlBackupN.Core.Services;
using SqlBackupN.Presentation.Jobs;

namespace SqlBackupN.Tests;

/// <summary>Avisos: envío con reintentos, mensaje, aviso de prueba, job que no se ejecuta y resultado en el registro.</summary>
public class NotificationTests
{
    // ------------------------------------------------------------- Webhook

    private sealed class FakeHandler(params HttpStatusCode[] responses) : HttpMessageHandler
    {
        private int _next;
        public List<string> Bodies { get; } = [];
        public int Calls => Bodies.Count;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct));
            var code = responses[Math.Min(_next++, responses.Length - 1)];
            return new HttpResponseMessage(code);
        }
    }

    private static readonly TimeSpan[] NoWait = [TimeSpan.Zero, TimeSpan.Zero];

    private static NotificationSettings Hook => new() { WebhookUrl = "https://hooks.example.test/aviso" };

    [Fact]
    public async Task The_message_is_posted_as_json_text()
    {
        var handler = new FakeHandler(HttpStatusCode.OK);

        await new WebhookNotifier(null, handler, NoWait).SendMessageAsync(Hook, NoticeKind.Test, "hola", CancellationToken.None);

        Assert.Equal("hola", JsonDocument.Parse(Assert.Single(handler.Bodies)).RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_failing_send_is_retried_and_succeeds_on_the_third_attempt()
    {
        var handler = new FakeHandler(HttpStatusCode.InternalServerError, HttpStatusCode.BadGateway, HttpStatusCode.OK);

        await new WebhookNotifier(null, handler, NoWait).SendMessageAsync(Hook, NoticeKind.Test, "hola", CancellationToken.None);

        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task After_the_retries_the_error_is_reported()
    {
        var handler = new FakeHandler(HttpStatusCode.InternalServerError);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new WebhookNotifier(null, handler, NoWait).SendMessageAsync(Hook, NoticeKind.Test, "hola", CancellationToken.None));

        Assert.Equal(3, handler.Calls);                              // el intento inicial y dos reintentos
    }

    [Fact]
    public async Task A_missing_webhook_is_an_error_for_a_test_but_silently_ignored_for_a_run()
    {
        var handler = new FakeHandler(HttpStatusCode.OK);
        var notifier = new WebhookNotifier(null, handler, NoWait);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            notifier.SendMessageAsync(new NotificationSettings(), NoticeKind.Test, "hola", CancellationToken.None));
        await notifier.NotifyAsync(new BackupJob(), new JobRunRecord(), CancellationToken.None);

        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void The_message_lists_every_database_with_its_result()
    {
        var record = new JobRunRecord
        {
            JobName = "Centinela",
            Type = BackupType.Full,
            Status = RunStatus.Failed,
            Results =
            [
                new DatabaseBackupResult
                {
                    Database = "Ventas", Status = RunStatus.Succeeded, SizeBytes = 3 * 1048576, Duration = TimeSpan.FromSeconds(48),
                    FilePath = Path.Combine("D:", "tmp", "Backup20261117.zip")
                },
                new DatabaseBackupResult { Database = "Compras", Status = RunStatus.Failed, Error = "no hay espacio" },
                new DatabaseBackupResult
                {
                    Database = "Otra", Status = RunStatus.Warning, SizeBytes = 1048576, Warnings = ["falló la copia a Drive"]
                }
            ]
        };

        var message = WebhookNotifier.BuildMessage(record);

        Assert.StartsWith("[FALLO] SQL Backup N - Centinela (Full)", message);
        Assert.Contains("- Ventas: correcto · 3.0 MB · 00:00:48 · Backup20261117.zip", message);
        Assert.Contains("- Compras: FALLO · no hay espacio", message);
        Assert.Contains("- Otra: ADVERTENCIA · 1.0 MB", message);
        Assert.Contains("falló la copia a Drive", message);
    }

    // ------------------------------------------------------------- Job que no se ejecuta

    private static readonly DateTime Now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

    private static BackupJob Watched(int hours = 24) => new()
    {
        Name = "Centinela",
        CreatedUtc = Now.AddDays(-30),
        Steps = [new BackupStep { Type = BackupType.Full, Cron = "0 23 * * *" }],
        Notifications = new NotificationSettings { WebhookUrl = "https://hooks.example.test/aviso", MissedRunHours = hours }
    };

    [Theory]
    [InlineData(23, false)]
    [InlineData(24, true)]
    [InlineData(40, true)]
    public void A_job_is_overdue_from_the_configured_hours_after_its_last_run(int hoursSinceLastRun, bool expected)
    {
        Assert.Equal(expected, MissedRunChecker.IsOverdue(Watched(24), Now.AddHours(-hoursSinceLastRun), Now));
    }

    [Fact]
    public void A_job_that_never_ran_counts_from_its_creation_date()
    {
        var job = Watched(24);
        job.CreatedUtc = Now.AddHours(-3);

        Assert.False(MissedRunChecker.IsOverdue(job, null, Now));

        job.CreatedUtc = Now.AddHours(-30);
        Assert.True(MissedRunChecker.IsOverdue(job, null, Now));
    }

    [Fact]
    public void Nothing_is_overdue_when_the_alert_is_off_the_job_is_disabled_or_there_is_no_webhook()
    {
        var old = Now.AddDays(-5);

        Assert.False(MissedRunChecker.IsOverdue(Watched(0), old, Now));

        var disabled = Watched();
        disabled.Enabled = false;
        Assert.False(MissedRunChecker.IsOverdue(disabled, old, Now));

        var noSteps = Watched();
        noSteps.Steps[0].Enabled = false;
        Assert.False(MissedRunChecker.IsOverdue(noSteps, old, Now));

        var noHook = Watched();
        noHook.Notifications.WebhookUrl = null;
        Assert.False(MissedRunChecker.IsOverdue(noHook, old, Now));
    }

    private sealed class MovableHistory : IHistoryStore
    {
        public DateTime? Last { get; set; }
        public Task AppendAsync(JobRunRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<JobRunRecord>> GetRecentAsync(Guid? jobId, int max, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<JobRunRecord>>([]);
        public Task<int> DeleteAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> ClearAsync(Guid? jobId, CancellationToken ct = default) => Task.FromResult(0);
        public Task<DateTime?> GetLastRunUtcAsync(Guid jobId, Guid stepId, CancellationToken ct = default) =>
            Task.FromResult(Last);
    }

    [Fact]
    public async Task The_monitor_alerts_once_and_again_only_after_the_job_ran_and_fell_behind_again()
    {
        var history = new MovableHistory { Last = Now.AddHours(-30) };
        var notifier = new StubNotifier();
        var monitor = new MissedRunMonitor(history, notifier, NullLogger<MissedRunMonitor>.Instance);
        var job = Watched(24);

        await monitor.CheckAsync(job, Now, CancellationToken.None);
        await monitor.CheckAsync(job, Now.AddMinutes(1), CancellationToken.None);   // sigue igual: no repite

        var first = Assert.Single(notifier.Messages);
        Assert.Contains("Centinela lleva 30 h sin ejecutarse", first);
        Assert.Contains("límite: 24 h", first);

        history.Last = Now;                                                         // el job volvió a correr...
        await monitor.CheckAsync(job, Now.AddHours(1), CancellationToken.None);
        Assert.Single(notifier.Messages);                                           // ...y ya no está atrasado

        await monitor.CheckAsync(job, Now.AddHours(25), CancellationToken.None);    // se atrasó otra vez
        Assert.Equal(2, notifier.Messages.Count);
    }

    [Fact]
    public async Task A_failed_alert_is_retried_after_an_hour_not_every_tick()
    {
        var history = new MovableHistory { Last = Now.AddHours(-30) };
        var notifier = new StubNotifier { SendFailure = new IOException("sin red") };
        var monitor = new MissedRunMonitor(history, notifier, NullLogger<MissedRunMonitor>.Instance);
        var job = Watched(24);

        await monitor.CheckAsync(job, Now, CancellationToken.None);
        notifier.SendFailure = null;
        await monitor.CheckAsync(job, Now.AddMinutes(30), CancellationToken.None);   // todavía dentro de la hora de espera
        Assert.Empty(notifier.Messages);

        await monitor.CheckAsync(job, Now.AddMinutes(61), CancellationToken.None);
        Assert.Single(notifier.Messages);
    }

    // ------------------------------------------------------------- El resultado del aviso queda en el registro

    private sealed class Executor : IBackupExecutor
    {
        public Task<IReadOnlyList<string>> ListDatabasesAsync(SqlServerConnection c, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(["Ventas"]);
        public Task BackupAsync(SqlServerConnection c, BackupRequest r, CancellationToken ct) => Task.CompletedTask;
        public Task VerifyAsync(SqlServerConnection c, string p, bool k, int t, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Files : IFileStore
    {
        public void EnsureDirectory(string path) { }
        public void VerifyWritable(string directory) { }
        public void CreateZip(string sourceFile, string zipPath) { }
        public void Copy(string source, string destination) { }
        public void Delete(string path) { }
        public long GetSize(string path) => 1;
        public IReadOnlyList<StoredFile> List(string directory, string searchPattern) => [];
    }

    private sealed class SavedHistory : IHistoryStore
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

    private static BackupJob RunJob() => new()
    {
        Name = "Prueba",
        Connection = new SqlServerConnection { Server = "SRV" },
        Destinations = [new DestinationConfig { Name = "Local", Path = Path.Combine("D:", "bk") }],
        Notifications = new NotificationSettings { WebhookUrl = "https://hooks.example.test/aviso", OnSuccess = true }
    };

    [Fact]
    public async Task A_delivered_notice_is_logged_in_the_run()
    {
        var history = new SavedHistory();
        var runner = new JobRunner(new Executor(), new Files(), new StubRemote(), history, new StubNotifier(),
            TimeProvider.System, NullLogger<JobRunner>.Instance);

        var record = await runner.RunStepAsync(RunJob(), new BackupStep(), false, CancellationToken.None);

        Assert.Equal("Aviso enviado.", record.Log[^1].Message);
        Assert.Same(record, Assert.Single(history.Records));
    }

    [Fact]
    public async Task A_notice_that_could_not_be_delivered_is_logged_as_a_warning_and_saved_in_the_history()
    {
        var history = new SavedHistory();
        var notifier = new StubNotifier { NotifyFailure = new HttpRequestException("503") };
        var runner = new JobRunner(new Executor(), new Files(), new StubRemote(), history, notifier,
            TimeProvider.System, NullLogger<JobRunner>.Instance);

        var record = await runner.RunStepAsync(RunJob(), new BackupStep(), false, CancellationToken.None);

        var last = record.Log[^1];
        Assert.Equal(RunLogLevel.Warning, last.Level);
        Assert.Contains("No se pudo enviar el aviso: 503", last.Message);
        Assert.Equal(RunStatus.Succeeded, record.Status);              // el backup salió bien: solo falló el aviso
        Assert.Contains(Assert.Single(history.Records).Log, l => l.Message.Contains("No se pudo enviar el aviso"));
    }

    // ------------------------------------------------------------- Validación

    private static BackupJob Valid() => new()
    {
        Name = "Ventas",
        Connection = new SqlServerConnection { Server = "srv01" },
        Steps = [new BackupStep { Type = BackupType.Full, Cron = "0 23 * * *" }],
        Destinations = [new DestinationConfig { Name = "Local", Path = @"D:\Backups" }],
        Retention = new RetentionPolicy { KeepDays = 30, KeepLast = 3 },
        Notifications = new NotificationSettings { WebhookUrl = "https://hooks.example.test/aviso" }
    };

    [Fact]
    public void The_missed_run_hours_must_be_in_range_and_need_a_webhook()
    {
        var job = Valid();
        job.Notifications.MissedRunHours = 9000;
        Assert.Contains(JobValidator.Validate(job), i => i.Field == "Notifications.MissedRunHours" && i.Severity == IssueSeverity.Error);

        job.Notifications.MissedRunHours = 24;
        Assert.DoesNotContain(JobValidator.Validate(job), i => i.Field == "Notifications.MissedRunHours");

        job.Notifications.WebhookUrl = null;
        Assert.Contains(JobValidator.Validate(job), i => i.Field == "Notifications.MissedRunHours" && i.Severity == IssueSeverity.Warning);
    }

    [Fact]
    public void Turning_off_failure_notices_is_a_warning()
    {
        var job = Valid();
        job.Notifications.OnFailure = false;
        job.Notifications.OnWarning = true;

        Assert.Contains(JobValidator.Validate(job), i => i.Field == "Notifications" && i.Message.Contains("fallos no avisan"));
    }

    // ------------------------------------------------------------- Editor: aviso de prueba y vista previa

    private static (JobEditorViewModel Editor, StubNotifier Notifier) Editor(BackupJob? job = null)
    {
        var notifier = new StubNotifier();
        var services = new EditorServices(new StubProtector(), new StubExecutor(), new StubFiles(), new StubRemote(),
            new StubAuthorizer(), new StubGoogleClients(), new StubDialogs(), TimeProvider.System, notifier);
        return (new JobEditorViewModel(job ?? Valid(), false, [], services), notifier);
    }

    private static async Task WaitAsync(JobEditorViewModel editor)
    {
        for (var i = 0; i < 300 && editor.TestNotificationCommand.IsRunning; i++) await Task.Delay(10);
    }

    [Fact]
    public async Task The_test_button_sends_a_test_message_and_reports_success()
    {
        var (editor, notifier) = Editor();

        editor.TestNotificationCommand.Execute(null);
        await WaitAsync(editor);

        Assert.Contains("[PRUEBA]", Assert.Single(notifier.Messages));
        Assert.StartsWith("✔ Enviado", editor.NotificationTestResult);
    }

    [Fact]
    public async Task The_test_button_reports_the_reason_when_it_fails()
    {
        var (editor, notifier) = Editor();
        notifier.SendFailure = new HttpRequestException("404 no encontrado");

        editor.TestNotificationCommand.Execute(null);
        await WaitAsync(editor);

        Assert.Equal("✖ No se pudo enviar: 404 no encontrado", editor.NotificationTestResult);
    }

    [Fact]
    public async Task The_test_button_asks_for_a_webhook_when_the_field_is_empty()
    {
        var (editor, notifier) = Editor();
        editor.WebhookUrl = "";

        editor.TestNotificationCommand.Execute(null);
        await WaitAsync(editor);

        Assert.Empty(notifier.Messages);
        Assert.Contains("Escriba primero", editor.NotificationTestResult);
    }

    [Fact]
    public void The_preview_is_built_with_the_real_message_code_and_the_job_name()
    {
        var (editor, _) = Editor();

        Assert.Contains("[FALLO] SQL Backup N - Ventas (Full)", editor.NotificationPreview);
        Assert.Contains("- Compras: FALLO · no hay espacio en disco", editor.NotificationPreview);
    }

    [Fact]
    public void The_missed_run_option_round_trips_into_the_saved_job()
    {
        var job = Valid();
        job.Notifications.MissedRunHours = 36;
        var (editor, _) = Editor(job);

        Assert.True(editor.UseMissedRun);
        Assert.Equal("36", editor.MissedRunHoursText);

        editor.UseMissedRun = false;
        editor.Destinations[0].Path = @"D:\Backups";
        editor.SaveCommand.Execute(null);

        Assert.Equal(0, editor.Result!.Notifications.MissedRunHours);
    }
}
