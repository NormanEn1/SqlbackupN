using SqlBackupN.Core.Abstractions;
using SqlBackupN.Core.Domain;
using SqlBackupN.Core.Services;

namespace SqlBackupN.Tests;

public class PathTemplateTests
{
    private static readonly DateTime Moment = new(2026, 11, 17, 23, 5, 9);

    [Fact]
    public void Render_replaces_tokens_and_sanitizes_values()
    {
        var path = PathTemplate.Render(
            @"{Server}\{Database}\{Type}\{Database}_{Timestamp}",
            new TemplateContext(@"SRV01\SQL2019", "Ventas/2026", BackupType.Differential, Moment));

        var sep = Path.DirectorySeparatorChar;
        Assert.Equal($"SRV01_SQL2019{sep}Ventas_2026{sep}DIFF{sep}Ventas_2026_20261117_230509", path);
    }

    [Fact]
    public void Render_maps_local_server_aliases_to_machine_name()
    {
        var path = PathTemplate.Render("{Server}", new TemplateContext(".", "db", BackupType.Full, Moment));
        Assert.Equal(Environment.MachineName, path);
    }

    [Fact]
    public void Render_rejects_unknown_token()
    {
        Assert.Throws<FormatException>(() =>
            PathTemplate.Render("{Nope}", new TemplateContext("s", "d", BackupType.Full, Moment)));
    }

    [Theory]
    [InlineData(@"..\escape")]
    [InlineData(@"a\..\b")]
    public void Render_rejects_path_traversal(string template)
    {
        Assert.Throws<FormatException>(() =>
            PathTemplate.Render(template, new TemplateContext("s", "d", BackupType.Full, Moment)));
    }
}

public class BackupSqlBuilderTests
{
    private static BackupRequest Request(BackupType type, Action<BackupOptions>? tweak = null)
    {
        var options = new BackupOptions();
        tweak?.Invoke(options);
        return new BackupRequest("Mi]Base", type, @"D:\bk\o'brien.bak", options);
    }

    [Fact]
    public void Full_backup_escapes_identifiers_and_literals()
    {
        var sql = BackupSqlBuilder.BuildBackup(Request(BackupType.Full));

        Assert.StartsWith("BACKUP DATABASE [Mi]]Base] TO DISK = N'D:\\bk\\o''brien.bak'", sql);
        Assert.Contains("COMPRESSION", sql);
        Assert.Contains("CHECKSUM", sql);
    }

    [Fact]
    public void Differential_adds_differential_and_never_copy_only()
    {
        var sql = BackupSqlBuilder.BuildBackup(Request(BackupType.Differential, o => o.CopyOnly = true));

        Assert.Contains("DIFFERENTIAL", sql);
        Assert.DoesNotContain("COPY_ONLY", sql);
    }

    [Fact]
    public void Log_backup_uses_backup_log()
    {
        var sql = BackupSqlBuilder.BuildBackup(Request(BackupType.TransactionLog));
        Assert.StartsWith("BACKUP LOG ", sql);
    }

    [Fact]
    public void Options_can_be_turned_off()
    {
        var sql = BackupSqlBuilder.BuildBackup(Request(BackupType.Full, o => { o.Compression = false; o.Checksum = false; }));
        Assert.Contains("NO_COMPRESSION", sql);
        Assert.Contains("NO_CHECKSUM", sql);
    }

    [Fact]
    public void Verify_only_requests_checksum_when_enabled()
    {
        Assert.EndsWith("WITH CHECKSUM;", BackupSqlBuilder.BuildVerify(@"D:\a.bak", true));
        Assert.EndsWith("N'D:\\a.bak';", BackupSqlBuilder.BuildVerify(@"D:\a.bak", false));
    }
}

public class DatabaseResolverTests
{
    private static readonly string[] Available = ["master", "msdb", "Ventas", "Compras", "Ventas_Test"];

    [Fact]
    public void All_excludes_system_databases_and_exclusions()
    {
        var result = DatabaseResolver.Resolve(
            new DatabaseSelection { Exclude = ["compras"] }, Available);

        Assert.Equal(["Ventas", "Ventas_Test"], result);
    }

    [Fact]
    public void Explicit_allows_system_databases()
    {
        var result = DatabaseResolver.Resolve(
            new DatabaseSelection { Mode = DatabaseSelectionMode.Explicit, Names = ["msdb", "Ventas", "VENTAS"] }, Available);

        Assert.Equal(["msdb", "Ventas"], result);
    }

    [Fact]
    public void Pattern_matches_wildcards()
    {
        var result = DatabaseResolver.Resolve(
            new DatabaseSelection { Mode = DatabaseSelectionMode.Pattern, Pattern = "Ventas*" }, Available);

        Assert.Equal(["Ventas", "Ventas_Test"], result);
    }
}

public class RetentionPlannerTests
{
    private static readonly DateTime Now = new(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc);

    private static StoredFile File(string name, int daysOld) => new(name, Now.AddDays(-daysOld), 1);

    [Fact]
    public void Deletes_only_files_older_than_keep_days()
    {
        var files = new[] { File("a", 1), File("b", 10), File("c", 40), File("d", 50) };

        var result = RetentionPlanner.SelectFilesToDelete(files, new RetentionPolicy { KeepDays = 30 }, Now);

        Assert.Equal(["c", "d"], result.Select(f => f.Path).Order());
    }

    [Fact]
    public void Never_deletes_the_newest_file_even_if_expired()
    {
        var files = new[] { File("only", 400) };

        Assert.Empty(RetentionPlanner.SelectFilesToDelete(files, new RetentionPolicy { KeepDays = 30 }, Now));
    }

    [Fact]
    public void Keep_last_protects_recent_files_even_if_old()
    {
        var files = new[] { File("a", 100), File("b", 200), File("c", 300) };

        var result = RetentionPlanner.SelectFilesToDelete(files, new RetentionPolicy { KeepDays = 30, KeepLast = 2 }, Now);

        Assert.Equal(["c"], result.Select(f => f.Path));
    }

    [Fact]
    public void Keep_last_alone_trims_to_count()
    {
        var files = new[] { File("a", 1), File("b", 2), File("c", 3) };

        var result = RetentionPlanner.SelectFilesToDelete(files, new RetentionPolicy { KeepDays = null, KeepLast = 1 }, Now);

        Assert.Equal(["b", "c"], result.Select(f => f.Path).Order());
    }

    [Fact]
    public void No_policy_deletes_nothing()
    {
        var files = new[] { File("a", 1000), File("b", 2000) };

        Assert.Empty(RetentionPlanner.SelectFilesToDelete(files, new RetentionPolicy { KeepDays = null }, Now));
    }
}

public class ScheduleEvaluatorTests
{
    private static BackupJob Job(params BackupStep[] steps) => new()
    {
        CreatedUtc = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
        Steps = [.. steps]
    };

    private static readonly DateTime Now = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Step_is_due_when_next_occurrence_after_last_run_has_passed()
    {
        var step = new BackupStep { Type = BackupType.Full, Cron = "0 3 * * *" };
        var last = new DateTime(2026, 11, 16, 3, 0, 0, DateTimeKind.Utc);

        var due = ScheduleEvaluator.GetDueSteps(Job(step), _ => last, Now, TimeZoneInfo.Utc);

        Assert.Single(due);
    }

    [Fact]
    public void Step_is_not_due_when_already_ran_after_last_occurrence()
    {
        var step = new BackupStep { Type = BackupType.Full, Cron = "0 3 * * *" };
        var last = new DateTime(2026, 11, 17, 3, 0, 5, DateTimeKind.Utc);

        Assert.Empty(ScheduleEvaluator.GetDueSteps(Job(step), _ => last, Now, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Missed_runs_collapse_into_a_single_catch_up()
    {
        // Equipo apagado 10 días: solo debe disparar una vez, no diez.
        var step = new BackupStep { Type = BackupType.Full, Cron = "0 3 * * *" };
        var last = new DateTime(2026, 11, 7, 3, 0, 0, DateTimeKind.Utc);

        Assert.Single(ScheduleEvaluator.GetDueSteps(Job(step), _ => last, Now, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Full_supersedes_differential_but_not_log()
    {
        var full = new BackupStep { Type = BackupType.Full, Cron = "0 3 * * *" };
        var diff = new BackupStep { Type = BackupType.Differential, Cron = "0 3 * * *" };
        var log = new BackupStep { Type = BackupType.TransactionLog, Cron = "*/15 * * * *" };

        var due = ScheduleEvaluator.GetDueSteps(Job(log, diff, full), _ => null, Now, TimeZoneInfo.Utc);

        Assert.Equal([BackupType.Full, BackupType.Differential, BackupType.TransactionLog], due.Select(d => d.Step.Type));
        Assert.False(due[0].Superseded);
        Assert.True(due[1].Superseded);
        Assert.False(due[2].Superseded);
    }

    [Fact]
    public void Disabled_job_or_step_is_ignored()
    {
        var step = new BackupStep { Type = BackupType.Full, Cron = "* * * * *", Enabled = false };
        var job = Job(step);
        Assert.Empty(ScheduleEvaluator.GetDueSteps(job, _ => null, Now, TimeZoneInfo.Utc));

        step.Enabled = true;
        job.Enabled = false;
        Assert.Empty(ScheduleEvaluator.GetDueSteps(job, _ => null, Now, TimeZoneInfo.Utc));
    }
}
