using System.Net;
using System.Text.Json;
using SqlBackupN.Core.Abstractions;
using SqlBackupN.Core.Domain;
using SqlBackupN.Core.Infrastructure;
using SqlBackupN.Core.Services;
using SqlBackupN.Presentation.Jobs;

namespace SqlBackupN.Tests;

/// <summary>Opciones del webhook: formato del cuerpo, plantilla propia, autenticación, cabeceras, reintentos y tiempo de espera.</summary>
public class WebhookOptionsTests
{
    // ------------------------------------------------------------- Datos de ejemplo

    private static BackupJob Job(Action<NotificationSettings>? configure = null)
    {
        var job = new BackupJob
        {
            Name = "Centinela",
            Connection = new SqlServerConnection { Server = "OMEN" },
            Steps = [new BackupStep { Type = BackupType.Full, Cron = "0 23 * * *" }],
            Destinations = [new DestinationConfig { Name = "Local", Path = @"D:\Backups" }],
            Retention = new RetentionPolicy { KeepDays = 30, KeepLast = 3 },
            Notifications = new NotificationSettings { WebhookUrl = "https://helpdesk.example.test/api/integraciones/sqlbackupn" }
        };
        configure?.Invoke(job.Notifications);
        return job;
    }

    private static JobRunRecord Record() => new()
    {
        JobId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        JobName = "Centinela",
        Type = BackupType.Full,
        Status = RunStatus.Failed,
        StartedUtc = new DateTime(2026, 11, 17, 23, 0, 0, DateTimeKind.Utc),
        FinishedUtc = new DateTime(2026, 11, 17, 23, 0, 52, DateTimeKind.Utc),
        Results =
        [
            new DatabaseBackupResult
            {
                Database = "Ventas", Status = RunStatus.Succeeded, SizeBytes = 3 * 1048576, Duration = TimeSpan.FromSeconds(48),
                FilePath = @"D:\Backups\Backup20261117.zip"
            },
            new DatabaseBackupResult { Database = "Compras", Status = RunStatus.Failed, Error = "no hay \"espacio\"" }
        ]
    };

    // ------------------------------------------------------------- Cuerpo según el formato

    [Fact]
    public void The_text_format_is_a_json_with_only_the_text()
    {
        var body = WebhookPayload.ForRun(Job(), Record(), "SRV");

        using var doc = JsonDocument.Parse(body);
        Assert.Equal(["text"], doc.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.StartsWith("[FALLO] SQL Backup N - Centinela (Full) en SRV", doc.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public void A_skipped_step_is_reported_together_with_the_successful_runs()
    {
        var on = new NotificationSettings { WebhookUrl = "https://x.test/h", OnSuccess = true };
        var off = new NotificationSettings { WebhookUrl = "https://x.test/h", OnSuccess = false };

        Assert.True(on.ShouldNotify(RunStatus.Skipped));
        Assert.False(off.ShouldNotify(RunStatus.Skipped));
    }

    [Fact]
    public void The_full_json_format_lists_the_destinations_with_kind_name_and_drive_account()
    {
        var job = Job(n => n.WebhookFormat = WebhookFormat.Json);
        job.Destinations.Add(new DestinationConfig
        {
            Name = "Carpeta backups", Kind = DestinationKind.GoogleDrive, Drive = new DriveSettings { AccountEmail = "backup@empresa.com" }
        });

        using var doc = JsonDocument.Parse(WebhookPayload.ForRun(job, Record(), "SRV"));
        var destinations = doc.RootElement.GetProperty("destinations").EnumerateArray().ToList();

        Assert.Equal(2, destinations.Count);
        Assert.Equal("folder", destinations[0].GetProperty("kind").GetString());
        Assert.Equal("Local", destinations[0].GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, destinations[0].GetProperty("accountEmail").ValueKind);
        Assert.Equal("gdrive", destinations[1].GetProperty("kind").GetString());
        Assert.Equal("backup@empresa.com", destinations[1].GetProperty("accountEmail").GetString());
    }

    [Fact]
    public void The_full_json_format_carries_the_job_the_status_and_every_database()
    {
        var job = Job(n => n.WebhookFormat = WebhookFormat.Json);

        using var doc = JsonDocument.Parse(WebhookPayload.ForRun(job, Record(), "SRV"));
        var root = doc.RootElement;

        Assert.Equal("backup.run", root.GetProperty("event").GetString());
        Assert.Equal("failed", root.GetProperty("status").GetString());
        Assert.Equal("Centinela", root.GetProperty("job").GetString());
        Assert.Equal("Full", root.GetProperty("type").GetString());
        Assert.Equal("OMEN", root.GetProperty("server").GetString());
        Assert.Equal("SRV", root.GetProperty("machine").GetString());
        Assert.Equal(52, root.GetProperty("durationSeconds").GetDouble());
        Assert.Contains("[FALLO]", root.GetProperty("message").GetString());

        var databases = root.GetProperty("databases").EnumerateArray().ToList();
        Assert.Equal(2, databases.Count);
        Assert.Equal("Ventas", databases[0].GetProperty("name").GetString());
        Assert.Equal("succeeded", databases[0].GetProperty("status").GetString());
        Assert.Equal(3 * 1048576, databases[0].GetProperty("sizeBytes").GetInt64());
        Assert.Equal("failed", databases[1].GetProperty("status").GetString());
        Assert.Equal("no hay \"espacio\"", databases[1].GetProperty("error").GetString());
    }

    [Fact]
    public void A_custom_template_gets_its_values_escaped_so_the_result_is_always_valid_json()
    {
        var job = Job(n =>
        {
            n.WebhookFormat = WebhookFormat.Custom;
            n.WebhookBodyTemplate = "{\"channel\": \"#backups\", \"title\": \"{job} - {status}\", \"text\": \"{message}\"}";
        });

        using var doc = JsonDocument.Parse(WebhookPayload.ForRun(job, Record(), "SRV"));   // las comillas y saltos de línea no lo rompen

        Assert.Equal("#backups", doc.RootElement.GetProperty("channel").GetString());
        Assert.Equal("Centinela - failed", doc.RootElement.GetProperty("title").GetString());
        Assert.Contains("- Compras: FALLO · no hay \"espacio\"", doc.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public void A_custom_format_without_a_template_falls_back_to_the_text_format()
    {
        var job = Job(n => n.WebhookFormat = WebhookFormat.Custom);

        using var doc = JsonDocument.Parse(WebhookPayload.ForRun(job, Record(), "SRV"));

        Assert.True(doc.RootElement.TryGetProperty("text", out _));
    }

    [Theory]
    [InlineData(NoticeKind.Test, "test", "info")]
    [InlineData(NoticeKind.MissedRun, "backup.missed-run", "warning")]
    public void Loose_messages_say_what_they_are_in_the_full_json_format(NoticeKind kind, string eventName, string status)
    {
        var settings = new NotificationSettings { WebhookFormat = WebhookFormat.Json };

        using var doc = JsonDocument.Parse(WebhookPayload.ForMessage(settings, kind, "hola", "SRV"));

        Assert.Equal(eventName, doc.RootElement.GetProperty("event").GetString());
        Assert.Equal(status, doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("hola", doc.RootElement.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("{\"text\": \"{message}\"}", null)]
    [InlineData("{\"a\": \"{job}\", \"b\": \"{summary}\"}", null)]
    public void Valid_templates_are_accepted(string template, string? expected)
    {
        Assert.Equal(expected, WebhookPayload.ValidateTemplate(template));
    }

    [Fact]
    public void A_template_with_an_unknown_value_or_broken_json_is_rejected_with_the_reason()
    {
        Assert.Contains("{nope}", WebhookPayload.ValidateTemplate("{\"text\": \"{nope}\"}"));
        Assert.Contains("JSON válido", WebhookPayload.ValidateTemplate("{\"text\": {message}}"));   // sin comillas
        Assert.Contains("JSON válido", WebhookPayload.ValidateTemplate("no es json"));
    }

    // ------------------------------------------------------------- Envío: cabeceras, autenticación, reintentos

    private sealed class SpyHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Dictionary<string, string>> Headers { get; } = [];
        public List<string> Bodies { get; } = [];
        public int Calls => Bodies.Count;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Headers.Add(request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase));
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct));
            return respond(Calls);
        }
    }

    private static SpyHandler Always(HttpStatusCode code, string body = "") =>
        new(_ => new HttpResponseMessage(code) { Content = new StringContent(body) });

    private static readonly TimeSpan[] NoWait = [TimeSpan.Zero];

    private sealed class BrokenProtector : ISecretProtector
    {
        public string Protect(string plainText) => plainText;
        public string Unprotect(string protectedText) => throw new InvalidOperationException("otra máquina");
    }

    [Fact]
    public async Task A_bearer_token_is_decrypted_and_sent_in_the_authorization_header()
    {
        var spy = Always(HttpStatusCode.OK);
        var settings = Job(n =>
        {
            n.WebhookAuth = WebhookAuth.Bearer;
            n.ProtectedWebhookSecret = "enc:s3cr3t-token";
        }).Notifications;

        await new WebhookNotifier(new StubProtector(), spy, NoWait).SendMessageAsync(settings, NoticeKind.Test, "hola", CancellationToken.None);

        Assert.Equal("Bearer s3cr3t-token", spy.Headers[0]["Authorization"]);
    }

    [Fact]
    public async Task A_custom_auth_header_and_the_extra_headers_are_sent_too()
    {
        var spy = Always(HttpStatusCode.OK);
        var settings = Job(n =>
        {
            n.WebhookAuth = WebhookAuth.Header;
            n.WebhookAuthHeader = "x-api-key";
            n.ProtectedWebhookSecret = "enc:clave-123";
            n.WebhookHeaders = ["X-Origen: sqlbackupn", "X-Entorno:  produccion "];
        }).Notifications;

        await new WebhookNotifier(new StubProtector(), spy, NoWait).SendMessageAsync(settings, NoticeKind.Test, "hola", CancellationToken.None);

        Assert.Equal("clave-123", spy.Headers[0]["x-api-key"]);
        Assert.Equal("sqlbackupn", spy.Headers[0]["X-Origen"]);
        Assert.Equal("produccion", spy.Headers[0]["X-Entorno"]);
        Assert.False(spy.Headers[0].ContainsKey("Authorization"));
    }

    [Fact]
    public async Task Without_auth_no_secret_is_needed_or_sent()
    {
        var spy = Always(HttpStatusCode.OK);

        await new WebhookNotifier(null, spy, NoWait).SendMessageAsync(Job().Notifications, NoticeKind.Test, "hola", CancellationToken.None);

        Assert.False(spy.Headers[0].ContainsKey("Authorization"));
    }

    [Fact]
    public async Task A_missing_or_undecryptable_token_fails_with_a_clear_reason_and_sends_nothing()
    {
        var spy = Always(HttpStatusCode.OK);
        var noSecret = Job(n => n.WebhookAuth = WebhookAuth.Bearer).Notifications;
        var withSecret = Job(n => { n.WebhookAuth = WebhookAuth.Bearer; n.ProtectedWebhookSecret = "xx"; }).Notifications;

        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WebhookNotifier(new StubProtector(), spy, NoWait).SendMessageAsync(noSecret, NoticeKind.Test, "hola", CancellationToken.None));
        var broken = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WebhookNotifier(new BrokenProtector(), spy, NoWait).SendMessageAsync(withSecret, NoticeKind.Test, "hola", CancellationToken.None));

        Assert.Contains("Falta el token", missing.Message);
        Assert.Contains("otro equipo", broken.Message);
        Assert.Equal(0, spy.Calls);
    }

    [Fact]
    public async Task The_body_is_posted_as_json_content()
    {
        var spy = Always(HttpStatusCode.OK);
        var settings = Job(n => n.WebhookFormat = WebhookFormat.Json).Notifications;

        await new WebhookNotifier(null, spy, NoWait).SendMessageAsync(settings, NoticeKind.Test, "hola", CancellationToken.None);

        using var doc = JsonDocument.Parse(Assert.Single(spy.Bodies));
        Assert.Equal("test", doc.RootElement.GetProperty("event").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, 1)]
    [InlineData(HttpStatusCode.NotFound, 1)]
    [InlineData(HttpStatusCode.TooManyRequests, 3)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 3)]
    public async Task Only_temporary_errors_are_retried(HttpStatusCode code, int expectedCalls)
    {
        var spy = Always(code);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new WebhookNotifier(null, spy, NoWait).SendMessageAsync(Job().Notifications, NoticeKind.Test, "hola", CancellationToken.None));

        Assert.Equal(expectedCalls, spy.Calls);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(5, 6)]
    [InlineData(9, 6)]      // el máximo es 5 reintentos
    public async Task The_number_of_retries_comes_from_the_settings(int retries, int expectedCalls)
    {
        var spy = Always(HttpStatusCode.BadGateway);
        var settings = Job(n => n.WebhookRetries = retries).Notifications;

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new WebhookNotifier(null, spy, NoWait).SendMessageAsync(settings, NoticeKind.Test, "hola", CancellationToken.None));

        Assert.Equal(expectedCalls, spy.Calls);
    }

    [Fact]
    public async Task The_error_tells_what_the_service_answered()
    {
        var spy = Always(HttpStatusCode.Unauthorized, "token inválido");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            new WebhookNotifier(null, spy, NoWait).SendMessageAsync(Job().Notifications, NoticeKind.Test, "hola", CancellationToken.None));

        Assert.Contains("401", ex.Message);
        Assert.Contains("token inválido", ex.Message);
    }

    [Fact]
    public async Task A_service_that_does_not_answer_in_time_is_reported_with_the_wait_limit()
    {
        var slow = new SlowHandler();
        var settings = Job(n => { n.WebhookTimeoutSeconds = 1; n.WebhookRetries = 0; }).Notifications;

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            new WebhookNotifier(null, slow, NoWait).SendMessageAsync(settings, NoticeKind.Test, "hola", CancellationToken.None));

        Assert.Contains("no respondió en 1 s", ex.Message);
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    // ------------------------------------------------------------- Validación

    private static IReadOnlyList<ValidationIssue> Check(Action<NotificationSettings> configure) =>
        JobValidator.Validate(Job(configure));

    [Fact]
    public void A_custom_format_needs_a_valid_template()
    {
        Assert.Contains(Check(n => n.WebhookFormat = WebhookFormat.Custom),
            i => i.Field == "Notifications.WebhookBodyTemplate" && i.Severity == IssueSeverity.Missing);

        Assert.Contains(Check(n => { n.WebhookFormat = WebhookFormat.Custom; n.WebhookBodyTemplate = "{\"x\": {job}}"; }),
            i => i.Field == "Notifications.WebhookBodyTemplate" && i.Severity == IssueSeverity.Error);

        Assert.DoesNotContain(Check(n => { n.WebhookFormat = WebhookFormat.Custom; n.WebhookBodyTemplate = "{\"x\": \"{job}\"}"; }),
            i => i.Field == "Notifications.WebhookBodyTemplate");
    }

    [Fact]
    public void An_auth_mode_needs_its_secret_and_a_valid_header_name()
    {
        Assert.Contains(Check(n => n.WebhookAuth = WebhookAuth.Bearer),
            i => i.Field == "Notifications.WebhookSecret" && i.Severity == IssueSeverity.Missing);

        var issues = Check(n => { n.WebhookAuth = WebhookAuth.Header; n.WebhookAuthHeader = "x api key"; n.ProtectedWebhookSecret = "x"; });
        Assert.Contains(issues, i => i.Field == "Notifications.WebhookAuthHeader" && i.Severity == IssueSeverity.Error);
        Assert.DoesNotContain(issues, i => i.Field == "Notifications.WebhookSecret");

        Assert.DoesNotContain(Check(n => { n.WebhookAuth = WebhookAuth.Header; n.WebhookAuthHeader = "x-api-key"; n.ProtectedWebhookSecret = "x"; }),
            i => i.Field.StartsWith("Notifications.WebhookA", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("sin dos puntos", IssueSeverity.Error)]
    [InlineData("Nombre:", IssueSeverity.Error)]
    [InlineData("Content-Type: text/plain", IssueSeverity.Error)]
    [InlineData("Host: otro", IssueSeverity.Error)]
    [InlineData("Authorization: Bearer abc", IssueSeverity.Warning)]
    public void Bad_extra_headers_are_flagged(string line, IssueSeverity expected)
    {
        var issues = Check(n => n.WebhookHeaders = [line]);

        Assert.Contains(issues, i => i.Field == "Notifications.WebhookHeaders" && i.Severity == expected);
    }

    [Fact]
    public void Good_headers_retries_and_timeout_pass_and_out_of_range_values_do_not()
    {
        Assert.DoesNotContain(Check(n => n.WebhookHeaders = ["X-Origen: sqlbackupn"]), i => i.Field == "Notifications.WebhookHeaders");
        Assert.Contains(Check(n => n.WebhookRetries = 6), i => i.Field == "Notifications.WebhookRetries");
        Assert.Contains(Check(n => n.WebhookRetries = -1), i => i.Field == "Notifications.WebhookRetries");
        Assert.Contains(Check(n => n.WebhookTimeoutSeconds = 4), i => i.Field == "Notifications.WebhookTimeoutSeconds");
        Assert.Contains(Check(n => n.WebhookTimeoutSeconds = 121), i => i.Field == "Notifications.WebhookTimeoutSeconds");
        Assert.DoesNotContain(Check(_ => { }), i => i.Field.StartsWith("Notifications.Webhook", StringComparison.Ordinal) && i.Field != "Notifications.WebhookUrl");
    }

    [Fact]
    public void Jobs_saved_before_these_options_load_with_sensible_defaults()
    {
        var old = JsonSerializer.Deserialize<NotificationSettings>(
            "{\"webhookUrl\": \"https://x.test/h\", \"onFailure\": true}", JsonDefaults.Compact)!;

        Assert.Equal(WebhookFormat.Text, old.WebhookFormat);
        Assert.Equal(WebhookAuth.None, old.WebhookAuth);
        Assert.Equal(2, old.WebhookRetries);
        Assert.Equal(15, old.WebhookTimeoutSeconds);
        Assert.Empty(old.WebhookHeaders);
    }

    // ------------------------------------------------------------- Editor

    private static (JobEditorViewModel Editor, StubNotifier Notifier) Editor(BackupJob? job = null)
    {
        var notifier = new StubNotifier();
        var services = new EditorServices(new StubProtector(), new StubExecutor(), new StubFiles(), new StubRemote(),
            new StubAuthorizer(), new StubGoogleClients(), new StubDialogs(), TimeProvider.System, notifier);
        return (new JobEditorViewModel(job ?? Job(), false, [], services), notifier);
    }

    [Fact]
    public void A_typed_secret_is_encrypted_when_saving_and_an_empty_one_keeps_the_stored_secret()
    {
        var (editor, _) = Editor(Job(n => { n.WebhookAuth = WebhookAuth.Bearer; n.ProtectedWebhookSecret = "enc:guardado"; }));

        Assert.True(editor.HasStoredWebhookSecret);
        Assert.Contains("Hay un token guardado", editor.WebhookSecretHint);
        editor.SaveCommand.Execute(null);
        Assert.Equal("enc:guardado", editor.Result!.Notifications.ProtectedWebhookSecret);   // no se tocó

        var (second, _) = Editor(Job(n => { n.WebhookAuth = WebhookAuth.Bearer; n.ProtectedWebhookSecret = "enc:guardado"; }));
        second.WebhookSecret = "nuevo";
        second.SaveCommand.Execute(null);
        Assert.Equal("enc:nuevo", second.Result!.Notifications.ProtectedWebhookSecret);
    }

    [Fact]
    public void Choosing_no_auth_drops_the_secret_and_the_header_name_when_saving()
    {
        var (editor, _) = Editor(Job(n => { n.WebhookAuth = WebhookAuth.Header; n.WebhookAuthHeader = "x-api-key"; n.ProtectedWebhookSecret = "enc:x"; }));

        editor.WebhookAuth = WebhookAuth.None;
        editor.SaveCommand.Execute(null);

        var saved = editor.Result!.Notifications;
        Assert.Equal(WebhookAuth.None, saved.WebhookAuth);
        Assert.Null(saved.ProtectedWebhookSecret);
        Assert.Null(saved.WebhookAuthHeader);
    }

    [Fact]
    public void The_options_round_trip_and_the_dependent_fields_show_only_when_needed()
    {
        var (editor, _) = Editor();
        Assert.False(editor.ShowCustomTemplate);
        Assert.False(editor.ShowAuthSecret);

        editor.WebhookFormat = WebhookFormat.Custom;
        editor.WebhookBodyTemplate = "{\"text\": \"{job}: {status}\"}";
        editor.WebhookAuth = WebhookAuth.Header;
        editor.WebhookAuthHeader = "x-api-key";
        editor.WebhookSecret = "abc";
        editor.WebhookHeadersText = "X-Origen: sqlbackupn" + Environment.NewLine + "X-Entorno: prod";
        editor.WebhookRetriesText = "4";
        editor.WebhookTimeoutText = "30";

        Assert.True(editor.ShowCustomTemplate);
        Assert.True(editor.ShowAuthHeaderName);
        Assert.True(editor.ShowAuthSecret);
        editor.SaveCommand.Execute(null);

        var saved = editor.Result!.Notifications;
        Assert.Equal(WebhookFormat.Custom, saved.WebhookFormat);
        Assert.Equal("{\"text\": \"{job}: {status}\"}", saved.WebhookBodyTemplate);
        Assert.Equal("x-api-key", saved.WebhookAuthHeader);
        Assert.Equal(["X-Origen: sqlbackupn", "X-Entorno: prod"], saved.WebhookHeaders);
        Assert.Equal(4, saved.WebhookRetries);
        Assert.Equal(30, saved.WebhookTimeoutSeconds);
    }

    [Fact]
    public void The_preview_follows_the_chosen_format_and_template()
    {
        var (editor, _) = Editor();

        editor.WebhookFormat = WebhookFormat.Json;
        using (var doc = JsonDocument.Parse(editor.NotificationPreview))
            Assert.Equal("backup.run", doc.RootElement.GetProperty("event").GetString());

        editor.WebhookFormat = WebhookFormat.Custom;
        editor.WebhookBodyTemplate = "{\"asunto\": \"{job} => {status}\"}";
        using var custom = JsonDocument.Parse(editor.NotificationPreview);
        Assert.Equal("Centinela => failed", custom.RootElement.GetProperty("asunto").GetString());
    }

    [Fact]
    public async Task The_test_button_sends_with_what_is_on_screen_including_a_newly_typed_secret()
    {
        var (editor, notifier) = Editor();
        editor.WebhookFormat = WebhookFormat.Json;
        editor.WebhookAuth = WebhookAuth.Bearer;
        editor.WebhookSecret = "tok";

        editor.TestNotificationCommand.Execute(null);
        for (var i = 0; i < 300 && editor.TestNotificationCommand.IsRunning; i++) await Task.Delay(10);

        var sent = Assert.Single(notifier.SentWith);
        Assert.Equal(WebhookFormat.Json, sent.WebhookFormat);
        Assert.Equal("enc:tok", sent.ProtectedWebhookSecret);          // llega cifrado: el notificador lo descifra
        Assert.Equal(NoticeKind.Test, Assert.Single(notifier.Kinds));
    }

    [Fact]
    public async Task The_test_button_refuses_when_the_form_has_errors_or_the_token_is_missing()
    {
        var (editor, notifier) = Editor();
        editor.WebhookAuth = WebhookAuth.Bearer;              // falta el token

        editor.TestNotificationCommand.Execute(null);
        for (var i = 0; i < 300 && editor.TestNotificationCommand.IsRunning; i++) await Task.Delay(10);
        Assert.Contains("Escriba primero el token", editor.NotificationTestResult);

        editor.WebhookAuth = WebhookAuth.None;
        editor.WebhookRetriesText = "9";                       // fuera de rango
        editor.TestNotificationCommand.Execute(null);
        for (var i = 0; i < 300 && editor.TestNotificationCommand.IsRunning; i++) await Task.Delay(10);

        Assert.StartsWith("✖ Corrija primero", editor.NotificationTestResult);
        Assert.Empty(notifier.Messages);
    }
}
