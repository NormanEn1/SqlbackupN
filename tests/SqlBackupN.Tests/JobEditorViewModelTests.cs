using SqlBackupN.Core.Domain;
using SqlBackupN.Core.Services;
using SqlBackupN.Presentation.Jobs;

namespace SqlBackupN.Tests;

public class JobEditorViewModelTests
{
    private static BackupJob ExistingJob() => new()
    {
        Name = "Ventas",
        CreatedUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        Connection = new SqlServerConnection { Server = @".\SQL2019" },
        Steps = [new BackupStep { Type = BackupType.Full, Cron = "0 23 * * *" }],
        Destinations =
        [
            new DestinationConfig { Name = "Local", Path = @"D:\Backups" },
            new DestinationConfig { Name = "NAS", Path = @"\\nas01\backups" }
        ],
        Notifications = new NotificationSettings { WebhookUrl = "https://hooks.example.test/x" }
    };

    private sealed record Rig(
        JobEditorViewModel Editor, StubExecutor Executor, StubFiles Files, StubDialogs Dialogs, BackupJob Source,
        StubRemote Remote, StubAuthorizer Authorizer);

    private static Rig Create(BackupJob? job = null, bool isNew = false, params string[] otherNames)
    {
        var source = job ?? ExistingJob();
        var executor = new StubExecutor();
        var files = new StubFiles();
        var remote = new StubRemote();
        var authorizer = new StubAuthorizer();
        var dialogs = new StubDialogs();
        var services = new EditorServices(new StubProtector(), executor, files, remote, authorizer, new StubGoogleClients(), dialogs, TimeProvider.System);
        // El editor trabaja sobre una copia, igual que la ventana principal.
        var editor = new JobEditorViewModel(JobFactory.Clone(source), isNew, otherNames, services);
        return new Rig(editor, executor, files, dialogs, source, remote, authorizer);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 150 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition(), "La condición no se cumplió a tiempo.");
    }

    // ------------------------------------------------------------- Validación en vivo

    [Fact]
    public void Existing_valid_job_can_be_saved()
    {
        var editor = Create().Editor;

        Assert.True(editor.CanSave);
        Assert.Equal(0, editor.ErrorCount);
        Assert.Equal("Todo correcto", editor.SummaryText);
    }

    [Fact]
    public void New_job_starts_with_errors_and_cannot_be_saved()
    {
        var editor = Create(JobFactory.CreateNew(), isNew: true).Editor;

        Assert.False(editor.CanSave);
        Assert.True(editor.Errors.ContainsKey("Name"));
        Assert.True(editor.Errors.ContainsKey("Destinations"));
        Assert.False(editor.SaveCommand.CanExecute(null));
        Assert.Equal("Nuevo job", editor.Title);
    }

    [Fact]
    public void Fixing_a_field_clears_its_error_immediately()
    {
        var editor = Create(JobFactory.CreateNew(), isNew: true).Editor;

        editor.Name = "Compras";

        Assert.False(editor.Errors.ContainsKey("Name"));
        Assert.True(editor.Errors.ContainsKey("Destinations"));
    }

    [Fact]
    public void Save_becomes_available_once_everything_is_valid()
    {
        var editor = Create(JobFactory.CreateNew(), isNew: true).Editor;

        editor.Name = "Compras";
        editor.AddDestinationCommand.Execute(null);
        editor.Destinations[0].Path = @"D:\Backups";

        Assert.True(editor.CanSave);
        Assert.True(editor.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void Invalid_cron_shows_error_on_its_step()
    {
        var editor = Create().Editor;

        editor.Steps[0].Cron = "abc";

        Assert.NotEmpty(editor.Steps[0].Error);
        Assert.True(editor.Errors.ContainsKey("Steps[0].Cron"));
        Assert.False(editor.CanSave);
    }

    [Fact]
    public void Summary_rows_say_which_area_each_problem_belongs_to()
    {
        var editor = Create().Editor;

        editor.Steps[0].Cron = "abc";
        editor.Destinations[1].Path = "relativa";

        var texts = editor.Issues.Where(i => i.IsError).Select(i => i.Text).ToList();
        Assert.Contains(texts, t => t.StartsWith("Programación (paso 1): ", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.StartsWith("Destinos (destino 2): ", StringComparison.Ordinal));
        Assert.DoesNotContain(texts, t => t.Contains("The given cron", StringComparison.Ordinal));
    }

    [Fact]
    public void Summary_kind_follows_the_worst_problem()
    {
        var editor = Create().Editor;
        Assert.Equal("ok", editor.SummaryKind);

        editor.WebhookUrl = "";
        Assert.Equal("warning", editor.SummaryKind);

        editor.Name = "";
        Assert.Equal("error", editor.SummaryKind);
        Assert.Equal("✖", editor.SummaryIcon);
    }

    [Theory]
    [InlineData(RunStatus.Succeeded, "ok")]
    [InlineData(RunStatus.Warning, "warn")]
    [InlineData(RunStatus.Failed, "fail")]
    [InlineData(RunStatus.Skipped, "skip")]
    [InlineData(null, "none")]
    public void Status_kinds_map_to_color_keys(RunStatus? status, string expected)
    {
        Assert.Equal(expected, StatusKinds.Of(status));
    }

    [Fact]
    public void Valid_cron_shows_next_runs()
    {
        var editor = Create().Editor;

        Assert.StartsWith("Próximas:", editor.Steps[0].NextRuns);

        editor.Steps[0].Enabled = false;
        Assert.Equal("Paso desactivado", editor.Steps[0].NextRuns);
    }

    [Fact]
    public void Non_numeric_text_reports_a_single_clear_error()
    {
        var editor = Create().Editor;

        editor.UseKeepDays = true;
        editor.KeepDaysText = "abc";

        var message = editor.Errors["Retention.KeepDays"];
        Assert.Contains("número entero", message);
        Assert.DoesNotContain("entre 1 y 3650", message);
        Assert.False(editor.CanSave);

        editor.KeepDaysText = "45";
        Assert.False(editor.Errors.ContainsKey("Retention.KeepDays"));
    }

    [Fact]
    public void Out_of_range_numbers_are_rejected()
    {
        var editor = Create().Editor;

        editor.UseKeepLast = true;
        editor.KeepLastText = "5000";
        editor.TimeoutText = "-5";

        Assert.True(editor.Errors.ContainsKey("Retention.KeepLast"));
        Assert.True(editor.Errors.ContainsKey("Options.CommandTimeoutSeconds"));
    }

    [Fact]
    public void Warnings_do_not_block_saving()
    {
        var editor = Create().Editor;

        editor.UseKeepDays = false;
        editor.UseKeepLast = false;
        editor.WebhookUrl = "";

        Assert.True(editor.CanSave);
        Assert.True(editor.Warnings.ContainsKey("Retention"));
        Assert.True(editor.WarningCount >= 2);
        Assert.Contains("advertencia", editor.SummaryText);
    }

    [Fact]
    public void Duplicate_job_name_is_reported_using_other_job_names()
    {
        var editor = Create(ExistingJob(), false, "Compras").Editor;

        editor.Name = "compras";

        Assert.Contains("Ya existe", editor.Errors["Name"]);
    }

    [Fact]
    public void Changing_database_mode_updates_radio_flags_and_validation()
    {
        var editor = Create().Editor;
        Assert.True(editor.IsAllDatabases);

        editor.IsExplicitDatabases = true;

        Assert.False(editor.IsAllDatabases);
        Assert.True(editor.IsExplicitDatabases);
        Assert.True(editor.Errors.ContainsKey("Databases.Names"));

        editor.NamesText = "Ventas\r\nCompras";
        Assert.False(editor.Errors.ContainsKey("Databases.Names"));
    }

    [Fact]
    public void Path_preview_reflects_the_template_and_first_destination()
    {
        var editor = Create().Editor;

        Assert.StartsWith(@"Ejemplo: D:\Backups\", editor.PathPreview);
        Assert.EndsWith(".bak", editor.PathPreview);
        Assert.Contains("FULL", editor.PathPreview);

        editor.NameTemplate = "{Nope}";
        Assert.Equal(string.Empty, editor.PathPreview);
        Assert.True(editor.Errors.ContainsKey("Options.PathTemplate"));
    }

    // ------------------------------------------------------------- Pasos y destinos

    [Fact]
    public void Applying_a_plan_replaces_the_steps_and_stays_valid()
    {
        var editor = Create().Editor;

        editor.ApplyPlanCommand.Execute("WeeklyFullDailyDifferentialAndLog");

        Assert.Equal(
            [BackupType.Full, BackupType.Differential, BackupType.TransactionLog],
            editor.Steps.Select(s => s.Type));
        Assert.True(editor.CanSave);
    }

    [Fact]
    public void Removing_the_full_step_while_a_differential_remains_is_an_error()
    {
        var editor = Create().Editor;
        editor.ApplyPlanCommand.Execute("WeeklyFullDailyDifferential");

        editor.Steps[0].RemoveCommand.Execute(null);

        Assert.True(editor.Errors.ContainsKey("Steps"));
        Assert.False(editor.CanSave);
    }

    [Fact]
    public void Add_step_proposes_a_differential_when_a_full_exists()
    {
        var editor = Create().Editor;

        editor.AddStepCommand.Execute(null);

        Assert.Equal(2, editor.Steps.Count);
        Assert.Equal(BackupType.Differential, editor.Steps[1].Type);
    }

    [Fact]
    public void Removing_every_destination_is_an_error_and_adding_one_asks_for_a_path()
    {
        var editor = Create().Editor;

        while (editor.Destinations.Count > 0) editor.Destinations[0].RemoveCommand.Execute(null);
        Assert.True(editor.Errors.ContainsKey("Destinations"));

        editor.AddDestinationCommand.Execute(null);
        Assert.Single(editor.Destinations);
        Assert.NotEmpty(editor.Destinations[0].PathError);
    }

    [Fact]
    public void Moving_a_destination_changes_which_one_is_primary()
    {
        var editor = Create().Editor;
        Assert.StartsWith("Principal", editor.Destinations[0].Role);

        editor.Destinations[1].MoveUpCommand.Execute(null);

        Assert.Equal("NAS", editor.Destinations[0].Name);
        Assert.StartsWith("Principal", editor.Destinations[0].Role);
        Assert.Equal("Copia", editor.Destinations[1].Role);
    }

    [Fact]
    public void Browse_fills_the_path_with_the_chosen_folder()
    {
        var rig = Create();
        rig.Dialogs.FolderToPick = @"E:\Respaldos";

        rig.Editor.Destinations[0].BrowseCommand.Execute(null);

        Assert.Equal(@"E:\Respaldos", rig.Editor.Destinations[0].Path);
    }

    [Fact]
    public async Task Destination_test_reports_success_and_failure()
    {
        var rig = Create();
        rig.Files.FailFor = @"\\nas01\backups";

        rig.Editor.Destinations[0].TestCommand.Execute(null);
        await WaitUntil(() => rig.Editor.Destinations[0].TestResult.StartsWith('✔'));
        Assert.Contains(@"D:\Backups", rig.Files.Verified);

        rig.Editor.Destinations[1].TestCommand.Execute(null);
        await WaitUntil(() => rig.Editor.Destinations[1].TestResult.StartsWith('✖'));
        Assert.Contains("Acceso denegado", rig.Editor.Destinations[1].TestResult);
    }

    [Fact]
    public void Destination_test_refuses_an_invalid_path()
    {
        var rig = Create();
        rig.Editor.Destinations[0].Path = "relativa";

        rig.Editor.Destinations[0].TestCommand.Execute(null);

        Assert.StartsWith("✖", rig.Editor.Destinations[0].TestResult);
        Assert.Empty(rig.Files.Verified);
    }

    // ------------------------------------------------------------- Conexión

    [Fact]
    public async Task Connection_test_lists_the_databases()
    {
        var rig = Create();

        rig.Editor.TestConnectionCommand.Execute(null);

        await WaitUntil(() => rig.Editor.ConnectionTestResult.StartsWith('✔'));
        Assert.Contains("2 bases", rig.Editor.ConnectionTestResult);
        Assert.Contains("Ventas", rig.Editor.ConnectionTestResult);
    }

    [Fact]
    public async Task Connection_test_shows_the_first_line_of_the_error()
    {
        var rig = Create();
        rig.Executor.Failure = new InvalidOperationException("No se encontró el servidor\nDetalle técnico largo");

        rig.Editor.TestConnectionCommand.Execute(null);

        await WaitUntil(() => rig.Editor.ConnectionTestResult.StartsWith('✖'));
        Assert.Equal("✖ No se encontró el servidor", rig.Editor.ConnectionTestResult);
    }

    [Fact]
    public void Connection_test_needs_a_server()
    {
        var rig = Create();
        rig.Editor.Server = "";

        rig.Editor.TestConnectionCommand.Execute(null);

        Assert.Contains("servidor", rig.Editor.ConnectionTestResult);
    }

    // ------------------------------------------------------------- Guardado

    [Fact]
    public void Save_produces_the_edited_job_keeping_identifiers_and_closes_with_true()
    {
        var rig = Create();
        bool? closed = null;
        rig.Editor.CloseRequested += saved => closed = saved;

        rig.Editor.Name = "  Ventas renombrado  ";
        rig.Editor.Server = "srv01";
        rig.Editor.SaveCommand.Execute(null);

        var result = rig.Editor.Result!;
        Assert.True(closed);
        Assert.Equal(rig.Source.Id, result.Id);
        Assert.Equal(rig.Source.CreatedUtc, result.CreatedUtc);
        Assert.Equal(rig.Source.Steps[0].Id, result.Steps[0].Id);
        Assert.Equal("Ventas renombrado", result.Name);
        Assert.Equal("srv01", result.Connection.Server);
        Assert.Equal(["Local", "NAS"], result.Destinations.Select(d => d.Name));
        // El original no se modifica hasta que la ventana principal lo reemplaza.
        Assert.Equal("Ventas", rig.Source.Name);
    }

    [Fact]
    public void Save_is_ignored_while_there_are_errors()
    {
        var rig = Create();
        bool? closed = null;
        rig.Editor.CloseRequested += saved => closed = saved;
        rig.Editor.Name = "";

        rig.Editor.SaveCommand.Execute(null);

        Assert.Null(closed);
        Assert.Null(rig.Editor.Result);
    }

    [Fact]
    public void Cancel_closes_with_false_and_produces_no_result()
    {
        var rig = Create();
        bool? closed = null;
        rig.Editor.CloseRequested += saved => closed = saved;
        rig.Editor.Name = "Cambiado";

        rig.Editor.CancelCommand.Execute(null);

        Assert.False(closed);
        Assert.Null(rig.Editor.Result);
    }

    [Fact]
    public void Retention_and_options_round_trip_into_the_result()
    {
        var rig = Create();
        rig.Editor.UseKeepDays = true;
        rig.Editor.KeepDaysText = "14";
        rig.Editor.UseKeepLast = false;
        rig.Editor.Compression = false;
        rig.Editor.TimeoutText = "600";
        rig.Editor.OnSuccess = true;

        rig.Editor.SaveCommand.Execute(null);

        var result = rig.Editor.Result!;
        Assert.Equal(14, result.Retention.KeepDays);
        Assert.Null(result.Retention.KeepLast);
        Assert.False(result.Options.Compression);
        Assert.Equal(600, result.Options.CommandTimeoutSeconds);
        Assert.True(result.Notifications.OnSuccess);
    }

    // ------------------------------------------------------------- Contraseñas

    [Fact]
    public void Typed_sql_password_is_protected_and_never_stored_in_plain_text()
    {
        var rig = Create();
        rig.Editor.UseWindowsAuthentication = false;
        rig.Editor.UserName = "backup_user";
        Assert.True(rig.Editor.Errors.ContainsKey("Connection.Password"));

        rig.Editor.Password = "S3creta!";
        rig.Editor.SaveCommand.Execute(null);

        var connection = rig.Editor.Result!.Connection;
        Assert.False(connection.UseWindowsAuthentication);
        Assert.Equal("backup_user", connection.UserName);
        Assert.Equal("enc:S3creta!", connection.ProtectedPassword);
        Assert.DoesNotContain("pendiente", connection.ProtectedPassword);
    }

    [Fact]
    public void Blank_password_keeps_the_stored_one()
    {
        var job = ExistingJob();
        job.Connection.UseWindowsAuthentication = false;
        job.Connection.UserName = "backup_user";
        job.Connection.ProtectedPassword = "enc:guardada";
        var editor = Create(job).Editor;

        Assert.True(editor.CanSave);
        Assert.True(editor.HasStoredPassword);
        Assert.Contains("Déjelo en blanco", editor.PasswordHint);

        editor.SaveCommand.Execute(null);

        Assert.Equal("enc:guardada", editor.Result!.Connection.ProtectedPassword);
    }

    [Fact]
    public void Windows_authentication_discards_user_and_password()
    {
        var job = ExistingJob();
        job.Connection.UseWindowsAuthentication = false;
        job.Connection.UserName = "x";
        job.Connection.ProtectedPassword = "enc:y";
        var editor = Create(job).Editor;

        editor.UseWindowsAuthentication = true;
        editor.SaveCommand.Execute(null);

        var connection = editor.Result!.Connection;
        Assert.Null(connection.UserName);
        Assert.Null(connection.ProtectedPassword);
    }
}
