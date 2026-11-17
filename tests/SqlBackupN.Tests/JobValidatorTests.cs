using SqlBackupN.Core.Domain;
using SqlBackupN.Core.Services;

namespace SqlBackupN.Tests;

public class JobValidatorTests
{
    /// <summary>Job completamente válido y sin advertencias (dos destinos, retención y webhook).</summary>
    private static BackupJob ValidJob() => new()
    {
        Name = "Ventas",
        Connection = new SqlServerConnection { Server = @".\SQL2019" },
        Steps = [new BackupStep { Type = BackupType.Full, Cron = "0 23 * * *" }],
        Destinations =
        [
            new DestinationConfig { Name = "Local", Path = @"D:\Backups" },
            new DestinationConfig { Name = "NAS", Path = @"\\nas01\backups\sql" }
        ],
        Retention = new RetentionPolicy { KeepDays = 30, KeepLast = 3 },
        Notifications = new NotificationSettings { WebhookUrl = "https://hooks.example.test/aviso" }
    };

    private static IReadOnlyList<ValidationIssue> Errors(BackupJob job, params string[] others) =>
        JobValidator.Validate(job, others).Where(i => i.Severity == IssueSeverity.Error).ToList();

    private static void AssertError(BackupJob job, string field) =>
        Assert.Contains(Errors(job), i => i.Field == field);

    [Fact]
    public void Valid_job_has_no_issues()
    {
        Assert.Empty(JobValidator.Validate(ValidJob()));
    }

    [Fact]
    public void New_job_only_lacks_a_name_and_a_destination()
    {
        var fields = Errors(JobFactory.CreateNew()).Select(e => e.Field).Distinct().Order().ToList();

        Assert.Equal(["Destinations", "Name"], fields);
    }

    // ------------------------------------------------------------- General

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Name_is_required(string name)
    {
        var job = ValidJob();
        job.Name = name;
        AssertError(job, "Name");
    }

    [Fact]
    public void Name_must_be_unique_ignoring_case()
    {
        var job = ValidJob();
        job.Name = "ventas";
        Assert.Contains(Errors(job, "VENTAS"), i => i.Field == "Name" && i.Message.Contains("Ya existe"));
        Assert.Empty(Errors(job, "Compras"));
    }

    [Fact]
    public void Name_cannot_exceed_100_characters()
    {
        var job = ValidJob();
        job.Name = new string('x', 101);
        AssertError(job, "Name");
    }

    // ------------------------------------------------------------- Conexión

    [Theory]
    [InlineData("")]
    [InlineData("srv;Database=x")]
    [InlineData("srv'--")]
    [InlineData(@"srv\")]
    [InlineData("srv,")]
    [InlineData("srv,0")]
    [InlineData("srv,70000")]
    [InlineData("srv,abc")]
    public void Invalid_servers_are_rejected(string server)
    {
        var job = ValidJob();
        job.Connection.Server = server;
        AssertError(job, "Connection.Server");
    }

    [Theory]
    [InlineData(".")]
    [InlineData("(local)")]
    [InlineData(@".\SQL2019")]
    [InlineData("srv01")]
    [InlineData(@"srv01\INSTANCIA")]
    [InlineData("srv01,1433")]
    [InlineData("tcp:srv01,1433")]
    [InlineData("192.168.1.10")]
    public void Valid_servers_are_accepted(string server)
    {
        var job = ValidJob();
        job.Connection.Server = server;
        Assert.DoesNotContain(Errors(job), i => i.Field == "Connection.Server");
    }

    [Fact]
    public void Sql_authentication_requires_user_and_password()
    {
        var job = ValidJob();
        job.Connection.UseWindowsAuthentication = false;

        var fields = Errors(job).Select(e => e.Field).ToList();
        Assert.Contains("Connection.UserName", fields);
        Assert.Contains("Connection.Password", fields);

        job.Connection.UserName = "sa";
        job.Connection.ProtectedPassword = "cifrada";
        Assert.Empty(Errors(job));
    }

    // ------------------------------------------------------------- Bases de datos

    [Fact]
    public void Explicit_mode_requires_at_least_one_name()
    {
        var job = ValidJob();
        job.Databases.Mode = DatabaseSelectionMode.Explicit;
        job.Databases.Names = ["  "];
        AssertError(job, "Databases.Names");

        job.Databases.Names = ["Ventas"];
        Assert.Empty(Errors(job));
    }

    [Fact]
    public void Pattern_mode_requires_a_pattern()
    {
        var job = ValidJob();
        job.Databases.Mode = DatabaseSelectionMode.Pattern;
        AssertError(job, "Databases.Pattern");

        job.Databases.Pattern = "Ventas*";
        Assert.Empty(Errors(job));
    }

    // ------------------------------------------------------------- Programación

    [Fact]
    public void At_least_one_step_is_required()
    {
        var job = ValidJob();
        job.Steps.Clear();
        AssertError(job, "Steps");
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("* * *")]
    [InlineData("61 * * * *")]
    [InlineData("0 25 * * *")]
    public void Invalid_cron_is_reported_on_the_step(string cron)
    {
        var job = ValidJob();
        job.Steps[0].Cron = cron;
        AssertError(job, "Steps[0].Cron");
    }

    [Theory]
    [InlineData("0 23 * * *")]
    [InlineData("*/15 * * * *")]
    [InlineData("0 22 * * 1-5")]
    [InlineData("30 2 1 * *")]
    public void Valid_cron_is_accepted(string cron)
    {
        var job = ValidJob();
        job.Steps[0].Cron = cron;
        Assert.Empty(Errors(job));
    }

    [Theory]
    [InlineData(BackupType.Differential)]
    [InlineData(BackupType.TransactionLog)]
    public void Differential_or_log_without_a_full_is_an_error(BackupType type)
    {
        var job = ValidJob();
        job.Steps = [new BackupStep { Type = type, Cron = "0 23 * * *" }];
        AssertError(job, "Steps");
    }

    [Fact]
    public void A_disabled_full_does_not_satisfy_the_dependency()
    {
        var job = ValidJob();
        job.Steps =
        [
            new BackupStep { Type = BackupType.Full, Cron = "0 22 * * 0", Enabled = false },
            new BackupStep { Type = BackupType.Differential, Cron = "0 22 * * 1-6" }
        ];
        AssertError(job, "Steps");
    }

    [Fact]
    public void All_steps_disabled_is_only_a_warning()
    {
        var job = ValidJob();
        job.Steps[0].Enabled = false;

        var issues = JobValidator.Validate(job);
        Assert.Contains(issues, i => i.Field == "Steps" && i.Severity == IssueSeverity.Warning);
        Assert.False(JobValidator.HasErrors(issues));
    }

    // ------------------------------------------------------------- Destinos

    [Fact]
    public void At_least_one_destination_is_required()
    {
        var job = ValidJob();
        job.Destinations.Clear();
        AssertError(job, "Destinations");
    }

    [Theory]
    [InlineData("")]
    [InlineData("Backups")]
    [InlineData(@"D:Backups")]
    [InlineData(@"\\servidor")]
    [InlineData(@"D:\Back|ups")]
    [InlineData(@"D:\Back?ups")]
    [InlineData(@"D:\a\..\b")]
    [InlineData(@"C:\ok\ruta:alterna")]
    public void Invalid_destination_paths_are_rejected(string path)
    {
        var job = ValidJob();
        job.Destinations[0].Path = path;
        AssertError(job, "Destinations[0].Path");
    }

    [Theory]
    [InlineData(@"D:\Backups")]
    [InlineData(@"D:\")]
    [InlineData("E:/bk")]
    [InlineData(@"\\nas01\backups\sql")]
    public void Valid_destination_paths_are_accepted(string path)
    {
        var job = ValidJob();
        job.Destinations[0].Path = path;
        Assert.DoesNotContain(Errors(job), i => i.Field == "Destinations[0].Path");
    }

    [Fact]
    public void Duplicate_destination_paths_and_names_are_rejected()
    {
        var job = ValidJob();
        job.Destinations[1].Path = @"d:\backups\";
        job.Destinations[1].Name = "LOCAL";

        var fields = Errors(job).Select(e => e.Field).ToList();
        Assert.Contains("Destinations[1].Path", fields);
        Assert.Contains("Destinations[1].Name", fields);
    }

    [Fact]
    public void Single_destination_is_only_a_warning()
    {
        var job = ValidJob();
        job.Destinations.RemoveAt(1);

        var issues = JobValidator.Validate(job);
        Assert.Contains(issues, i => i.Field == "Destinations" && i.Severity == IssueSeverity.Warning);
        Assert.False(JobValidator.HasErrors(issues));
    }

    // ------------------------------------------------------------- Opciones

    [Theory]
    [InlineData("")]
    [InlineData("{Database}")]
    [InlineData("{Timestamp}")]
    [InlineData(@"{Database}\{Nope}_{Timestamp}")]
    [InlineData(@"..\{Database}_{Timestamp}")]
    [InlineData(@"C:\{Database}_{Timestamp}")]
    [InlineData("{Database}|{Timestamp}")]
    public void Invalid_path_templates_are_rejected(string template)
    {
        var job = ValidJob();
        job.Options.PathTemplate = template;
        AssertError(job, "Options.PathTemplate");
    }

    [Fact]
    public void Template_without_type_is_only_a_warning()
    {
        var job = ValidJob();
        job.Options.PathTemplate = "{Database}_{Timestamp}";

        var issues = JobValidator.Validate(job);
        Assert.Contains(issues, i => i.Field == "Options.PathTemplate" && i.Severity == IssueSeverity.Warning);
        Assert.False(JobValidator.HasErrors(issues));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(86401)]
    public void Timeout_out_of_range_is_rejected(int seconds)
    {
        var job = ValidJob();
        job.Options.CommandTimeoutSeconds = seconds;
        AssertError(job, "Options.CommandTimeoutSeconds");
    }

    // ------------------------------------------------------------- Retención y avisos

    [Fact]
    public void Missing_retention_is_a_warning()
    {
        var job = ValidJob();
        job.Retention = new RetentionPolicy { KeepDays = null, KeepLast = null };

        var issues = JobValidator.Validate(job);
        Assert.Contains(issues, i => i.Field == "Retention" && i.Severity == IssueSeverity.Warning);
        Assert.False(JobValidator.HasErrors(issues));
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(3651, null)]
    [InlineData(null, 0)]
    [InlineData(null, 1001)]
    public void Retention_out_of_range_is_rejected(int? days, int? last)
    {
        var job = ValidJob();
        job.Retention = new RetentionPolicy { KeepDays = days, KeepLast = last };
        Assert.NotEmpty(Errors(job));
    }

    [Theory]
    [InlineData("no-es-url")]
    [InlineData("ftp://servidor/hook")]
    [InlineData("/relativa")]
    public void Invalid_webhook_is_rejected(string url)
    {
        var job = ValidJob();
        job.Notifications.WebhookUrl = url;
        AssertError(job, "Notifications.WebhookUrl");
    }

    [Fact]
    public void Http_or_empty_webhook_are_only_warnings()
    {
        var job = ValidJob();
        job.Notifications.WebhookUrl = "http://hooks.example.test/x";
        var http = JobValidator.Validate(job);
        Assert.False(JobValidator.HasErrors(http));
        Assert.Contains(http, i => i.Field == "Notifications.WebhookUrl");

        job.Notifications.WebhookUrl = null;
        var none = JobValidator.Validate(job);
        Assert.False(JobValidator.HasErrors(none));
        Assert.Contains(none, i => i.Field == "Notifications.WebhookUrl");
    }

    [Fact]
    public void Webhook_without_any_event_enabled_warns()
    {
        var job = ValidJob();
        job.Notifications.OnFailure = false;
        job.Notifications.OnWarning = false;
        job.Notifications.OnSuccess = false;

        Assert.Contains(JobValidator.Validate(job), i => i.Field == "Notifications");
    }

    // ------------------------------------------------------------- Soporte

    [Fact]
    public void NextOccurrences_returns_the_following_runs_in_order()
    {
        var from = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

        var next = ScheduleEvaluator.NextOccurrences("0 23 * * *", from, TimeZoneInfo.Utc, 3);

        Assert.Equal(
            [new DateTime(2026, 11, 17, 23, 0, 0), new DateTime(2026, 11, 18, 23, 0, 0), new DateTime(2026, 11, 19, 23, 0, 0)],
            next.Select(d => DateTime.SpecifyKind(d, DateTimeKind.Unspecified)));
    }

    [Fact]
    public void NextOccurrences_of_invalid_cron_is_empty()
    {
        Assert.Empty(ScheduleEvaluator.NextOccurrences("abc", DateTime.UtcNow, TimeZoneInfo.Utc, 3));
    }

    [Fact]
    public void Duplicate_creates_independent_copy_with_new_identifiers()
    {
        var original = ValidJob();
        var copy = JobFactory.Duplicate(original);

        Assert.NotEqual(original.Id, copy.Id);
        Assert.Equal("Ventas (copia)", copy.Name);
        Assert.NotEqual(original.Steps[0].Id, copy.Steps[0].Id);

        copy.Destinations[0].Path = @"Z:\otra";
        Assert.Equal(@"D:\Backups", original.Destinations[0].Path);
    }

    [Fact]
    public void Clone_keeps_identifiers_but_is_independent()
    {
        var original = ValidJob();
        var clone = JobFactory.Clone(original);

        Assert.Equal(original.Id, clone.Id);
        Assert.Equal(original.Steps[0].Id, clone.Steps[0].Id);

        clone.Name = "Otro";
        Assert.Equal("Ventas", original.Name);
    }
}
