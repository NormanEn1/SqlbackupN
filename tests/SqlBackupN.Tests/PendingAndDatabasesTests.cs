using SqlBackupN.Core.Domain;
using SqlBackupN.Core.Services;
using SqlBackupN.Presentation.Jobs;

namespace SqlBackupN.Tests;

/// <summary>Lo que falta por rellenar es "pendiente" (neutro); solo lo mal escrito es un error.</summary>
public class MissingVersusErrorTests
{
    private static BackupJob ValidJob() => new()
    {
        Name = "Ventas",
        Connection = new SqlServerConnection { Server = "srv01" },
        Steps = [new BackupStep { Type = BackupType.Full, Cron = "0 23 * * *" }],
        Destinations =
        [
            new DestinationConfig { Name = "Local", Path = @"D:\Backups" },
            new DestinationConfig { Name = "NAS", Path = @"\\nas01\backups" }
        ],
        Retention = new RetentionPolicy { KeepDays = 30 },
        Notifications = new NotificationSettings { WebhookUrl = "https://hooks.example.test/x" }
    };

    private static IssueSeverity? SeverityOf(BackupJob job, string field) =>
        JobValidator.Validate(job).FirstOrDefault(i => i.Field == field)?.Severity;

    [Fact]
    public void An_untouched_new_job_has_no_errors_only_pending_data()
    {
        var issues = JobValidator.Validate(JobFactory.CreateNew());

        Assert.DoesNotContain(issues, i => i.Severity == IssueSeverity.Error);
        Assert.Contains(issues, i => i.Field == "Name" && i.Severity == IssueSeverity.Missing);
        Assert.Contains(issues, i => i.Field == "Destinations" && i.Severity == IssueSeverity.Missing);
        Assert.False(JobValidator.HasErrors(issues));
        Assert.True(JobValidator.BlocksSaving(issues));
    }

    [Fact]
    public void Empty_required_values_are_missing_not_errors()
    {
        var name = ValidJob(); name.Name = "";
        var server = ValidJob(); server.Connection.Server = "";
        var user = ValidJob(); user.Connection.UseWindowsAuthentication = false;
        var names = ValidJob(); names.Databases = new DatabaseSelection { Mode = DatabaseSelectionMode.Explicit };
        var pattern = ValidJob(); pattern.Databases = new DatabaseSelection { Mode = DatabaseSelectionMode.Pattern };
        var steps = ValidJob(); steps.Steps.Clear();
        var cron = ValidJob(); cron.Steps[0].Cron = "";
        var dests = ValidJob(); dests.Destinations.Clear();
        var destName = ValidJob(); destName.Destinations[0].Name = "";
        var path = ValidJob(); path.Destinations[0].Path = "";
        var template = ValidJob(); template.Options.PathTemplate = "";

        Assert.Equal(IssueSeverity.Missing, SeverityOf(name, "Name"));
        Assert.Equal(IssueSeverity.Missing, SeverityOf(server, "Connection.Server"));
        Assert.Equal(IssueSeverity.Missing, SeverityOf(user, "Connection.UserName"));
        Assert.Equal(IssueSeverity.Missing, SeverityOf(user, "Connection.Password"));
        Assert.Equal(IssueSeverity.Missing, SeverityOf(names, "Databases.Names"));
        Assert.Equal(IssueSeverity.Missing, SeverityOf(pattern, "Databases.Pattern"));
        Assert.Equal(IssueSeverity.Missing, SeverityOf(steps, "Steps"));
        Assert.Equal(IssueSeverity.Missing, SeverityOf(cron, "Steps[0].Cron"));
        Assert.Equal(IssueSeverity.Missing, SeverityOf(dests, "Destinations"));
        Assert.Equal(IssueSeverity.Missing, SeverityOf(destName, "Destinations[0].Name"));
        Assert.Equal(IssueSeverity.Missing, SeverityOf(path, "Destinations[0].Path"));
        Assert.Equal(IssueSeverity.Missing, SeverityOf(template, "Options.PathTemplate"));
    }

    [Fact]
    public void Wrong_values_are_errors_not_missing()
    {
        var server = ValidJob(); server.Connection.Server = "srv;x";
        var cron = ValidJob(); cron.Steps[0].Cron = "abc";
        var path = ValidJob(); path.Destinations[0].Path = "relativa";
        var name = ValidJob(); name.Name = new string('x', 101);
        var hook = ValidJob(); hook.Notifications.WebhookUrl = "ftp://x";
        var days = ValidJob(); days.Retention.KeepDays = 0;
        var order = ValidJob(); order.Steps[0].Type = BackupType.Differential;

        Assert.Equal(IssueSeverity.Error, SeverityOf(server, "Connection.Server"));
        Assert.Equal(IssueSeverity.Error, SeverityOf(cron, "Steps[0].Cron"));
        Assert.Equal(IssueSeverity.Error, SeverityOf(path, "Destinations[0].Path"));
        Assert.Equal(IssueSeverity.Error, SeverityOf(name, "Name"));
        Assert.Equal(IssueSeverity.Error, SeverityOf(hook, "Notifications.WebhookUrl"));
        Assert.Equal(IssueSeverity.Error, SeverityOf(days, "Retention.KeepDays"));
        Assert.Equal(IssueSeverity.Error, SeverityOf(order, "Steps"));
    }

    [Fact]
    public void Blocking_is_decided_by_errors_and_missing_data_but_never_by_warnings()
    {
        var warningOnly = new[] { new ValidationIssue(IssueSeverity.Warning, "x", "m") };
        var missingOnly = new[] { new ValidationIssue(IssueSeverity.Missing, "x", "m") };
        var errorOnly = new[] { new ValidationIssue(IssueSeverity.Error, "x", "m") };

        Assert.False(JobValidator.BlocksSaving(warningOnly));
        Assert.True(JobValidator.BlocksSaving(missingOnly));
        Assert.True(JobValidator.BlocksSaving(errorOnly));
        Assert.False(JobValidator.HasErrors(missingOnly));
        Assert.True(JobValidator.HasErrors(errorOnly));
    }

    private static JobEditorViewModel NewJobEditor()
    {
        var services = new EditorServices(new StubProtector(), new StubExecutor(), new StubFiles(), new StubRemote(),
            new StubAuthorizer(), new StubGoogleClients(), new StubDialogs(), TimeProvider.System);
        return new JobEditorViewModel(JobFactory.CreateNew(), true, [], services);
    }

    [Fact]
    public void The_summary_of_a_new_job_reads_as_things_to_complete_not_as_mistakes()
    {
        var editor = NewJobEditor();

        Assert.Equal("pending", editor.SummaryKind);
        Assert.Equal("○", editor.SummaryIcon);
        Assert.Equal("Faltan 3 datos para poder guardar · 1 advertencia", editor.SummaryText);

        editor.Name = "Ventas";
        Assert.Equal("Faltan 2 datos para poder guardar · 1 advertencia", editor.SummaryText);
    }

    [Fact]
    public void Mixed_problems_are_counted_separately()
    {
        var editor = NewJobEditor();

        editor.Server = "srv;x";

        Assert.Equal(1, editor.ErrorCount);
        Assert.Equal(2, editor.MissingCount);
        Assert.Equal("error", editor.SummaryKind);
        Assert.Equal("1 error · 2 por completar · 1 advertencia", editor.SummaryText);
    }

    [Fact]
    public void Issue_rows_are_ordered_errors_then_pending_then_recommendations_with_their_own_icons()
    {
        var editor = NewJobEditor();
        editor.Server = "srv;x";

        var kinds = editor.Issues.Select(i => i.Kind).ToList();
        Assert.Equal(["error", "missing", "missing", "warning"], kinds);
        Assert.Equal(["✖", "○", "○", "⚠"], editor.Issues.Select(i => i.Icon));
        Assert.True(editor.Issues[0].IsBlocking);
        Assert.False(editor.Issues[^1].IsBlocking);
    }

    [Fact]
    public void A_new_folder_destination_shows_a_neutral_note_until_the_path_is_wrong_then_a_red_error()
    {
        var editor = NewJobEditor();
        editor.AddDestinationCommand.Execute(null);
        var destination = editor.Destinations[0];

        Assert.NotEmpty(destination.PathPending);
        Assert.Equal(string.Empty, destination.PathError);

        destination.Path = "relativa";

        Assert.Equal(string.Empty, destination.PathPending);
        Assert.NotEmpty(destination.PathError);

        destination.Path = @"D:\Backups";

        Assert.Equal(string.Empty, destination.PathPending);
        Assert.Equal(string.Empty, destination.PathError);
    }
}

/// <summary>Elegir las bases de una lista una vez que la conexión con el servidor funciona.</summary>
public class DatabaseSelectionTests
{
    private sealed record Rig(JobEditorViewModel Editor, StubExecutor Executor);

    private static Rig Create(params string[] preselected)
    {
        var executor = new StubExecutor();
        executor.Databases.Clear();
        executor.Databases.AddRange(["Ventas", "Compras", "master", "msdb"]);

        var job = new BackupJob
        {
            Name = "Ventas",
            Connection = new SqlServerConnection { Server = "srv01" },
            Databases = new DatabaseSelection
            {
                Mode = DatabaseSelectionMode.Explicit, Names = [.. preselected]
            },
            Steps = [new BackupStep { Type = BackupType.Full, Cron = "0 23 * * *" }],
            Destinations = [new DestinationConfig { Name = "Local", Path = @"D:\Backups" }],
            Notifications = new NotificationSettings { WebhookUrl = "https://hooks.example.test/x" }
        };
        var services = new EditorServices(new StubProtector(), executor, new StubFiles(), new StubRemote(),
            new StubAuthorizer(), new StubGoogleClients(), new StubDialogs(), TimeProvider.System);
        return new Rig(new JobEditorViewModel(JobFactory.Clone(job), false, [], services), executor);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 150 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition(), "La condición no se cumplió a tiempo.");
    }

    private static async Task Load(Rig rig)
    {
        rig.Editor.TestConnectionCommand.Execute(null);
        await WaitUntil(() => rig.Editor.HasServerDatabases || rig.Editor.ConnectionTestResult.StartsWith('✖'));
    }

    private static readonly string NL = Environment.NewLine;

    [Fact]
    public void Before_testing_the_connection_there_is_no_list_and_the_hint_explains_how_to_get_one()
    {
        var rig = Create();

        Assert.False(rig.Editor.HasServerDatabases);
        Assert.False(rig.Editor.ShowDatabaseList);
        Assert.True(rig.Editor.ShowManualEditor);
        Assert.Contains("Pruebe la conexión", rig.Editor.DatabasesHint);
    }

    [Fact]
    public async Task A_successful_connection_test_makes_the_databases_selectable()
    {
        var rig = Create();

        await Load(rig);

        Assert.Equal(["Compras", "master", "msdb", "Ventas"], rig.Editor.ServerDatabases.Select(d => d.Name));
        Assert.True(rig.Editor.ShowDatabaseList);
        Assert.False(rig.Editor.ShowManualEditor);
        Assert.Contains("Ya puede elegirlas", rig.Editor.ConnectionTestResult);
        Assert.Equal("4 bases en el servidor · 0 elegidas.", rig.Editor.DatabasesHint);
    }

    [Fact]
    public async Task System_databases_are_labelled()
    {
        var rig = Create();
        await Load(rig);

        var master = rig.Editor.ServerDatabases.Single(d => d.Name == "master");
        var ventas = rig.Editor.ServerDatabases.Single(d => d.Name == "Ventas");

        Assert.True(master.IsSystem);
        Assert.Equal("master  (sistema)", master.Label);
        Assert.False(ventas.IsSystem);
        Assert.Equal("Ventas", ventas.Label);
    }

    [Fact]
    public async Task Names_already_in_the_job_start_checked()
    {
        var rig = Create("Ventas");
        await Load(rig);

        Assert.Equal(["Ventas"], rig.Editor.ServerDatabases.Where(d => d.IsSelected).Select(d => d.Name));
        Assert.Equal("4 bases en el servidor · 1 elegidas.", rig.Editor.DatabasesHint);
    }

    [Fact]
    public async Task Checking_a_database_adds_it_to_the_job_and_clears_the_pending_note()
    {
        var rig = Create();
        await Load(rig);
        Assert.True(rig.Editor.Pending.ContainsKey("Databases.Names"));

        rig.Editor.ServerDatabases.Single(d => d.Name == "Compras").IsSelected = true;
        rig.Editor.ServerDatabases.Single(d => d.Name == "Ventas").IsSelected = true;

        Assert.Equal("Compras" + NL + "Ventas", rig.Editor.NamesText);
        Assert.False(rig.Editor.Pending.ContainsKey("Databases.Names"));
        Assert.True(rig.Editor.CanSave);
    }

    [Fact]
    public async Task Unchecking_the_last_database_makes_the_selection_pending_again()
    {
        var rig = Create("Ventas");
        await Load(rig);

        rig.Editor.ServerDatabases.Single(d => d.Name == "Ventas").IsSelected = false;

        Assert.Equal(string.Empty, rig.Editor.NamesText);
        Assert.True(rig.Editor.Pending.ContainsKey("Databases.Names"));
        Assert.False(rig.Editor.CanSave);
    }

    [Fact]
    public async Task Names_typed_by_hand_that_are_not_on_the_server_are_kept()
    {
        var rig = Create("Ventas", "BaseDeOtroServidor");
        await Load(rig);

        rig.Editor.ServerDatabases.Single(d => d.Name == "Compras").IsSelected = true;

        Assert.Equal("Compras" + NL + "Ventas" + NL + "BaseDeOtroServidor", rig.Editor.NamesText);
    }

    [Fact]
    public async Task Typing_names_by_hand_updates_the_checkmarks()
    {
        var rig = Create("Ventas");
        await Load(rig);

        rig.Editor.NamesText = "Compras";

        Assert.Equal(["Compras"], rig.Editor.ServerDatabases.Where(d => d.IsSelected).Select(d => d.Name));
        Assert.Equal("4 bases en el servidor · 1 elegidas.", rig.Editor.DatabasesHint);
    }

    [Fact]
    public async Task Select_all_picks_only_user_databases_and_none_clears_them()
    {
        var rig = Create();
        await Load(rig);

        rig.Editor.SelectAllDatabasesCommand.Execute(null);
        Assert.Equal(["Compras", "Ventas"], rig.Editor.ServerDatabases.Where(d => d.IsSelected).Select(d => d.Name));
        Assert.Equal("Compras" + NL + "Ventas", rig.Editor.NamesText);

        rig.Editor.ClearDatabasesCommand.Execute(null);
        Assert.DoesNotContain(rig.Editor.ServerDatabases, d => d.IsSelected);
        Assert.Equal(string.Empty, rig.Editor.NamesText);
    }

    [Fact]
    public async Task The_chosen_databases_are_saved_in_the_job()
    {
        var rig = Create();
        await Load(rig);
        rig.Editor.ServerDatabases.Single(d => d.Name == "Ventas").IsSelected = true;
        rig.Editor.ServerDatabases.Single(d => d.Name == "msdb").IsSelected = true;

        rig.Editor.SaveCommand.Execute(null);

        var databases = rig.Editor.Result!.Databases;
        Assert.Equal(DatabaseSelectionMode.Explicit, databases.Mode);
        Assert.Equal(["msdb", "Ventas"], databases.Names);
    }

    [Fact]
    public async Task The_list_can_be_loaded_without_the_connection_test_and_a_failure_is_reported_calmly()
    {
        var rig = Create();
        rig.Executor.Failure = new InvalidOperationException("No se encontró el servidor\nDetalle");

        rig.Editor.LoadDatabasesCommand.Execute(null);
        await WaitUntil(() => rig.Editor.DatabasesLoadResult.StartsWith('✖'));

        Assert.Equal("✖ No se encontró el servidor", rig.Editor.DatabasesLoadResult);
        Assert.False(rig.Editor.HasServerDatabases);

        rig.Executor.Failure = null;
        rig.Editor.LoadDatabasesCommand.Execute(null);
        await WaitUntil(() => rig.Editor.HasServerDatabases);

        Assert.Equal(string.Empty, rig.Editor.DatabasesLoadResult);
    }

    [Fact]
    public async Task Changing_the_server_discards_the_list_because_it_belongs_to_another_server()
    {
        var rig = Create("Ventas");
        await Load(rig);

        rig.Editor.Server = "otro-servidor";

        Assert.False(rig.Editor.HasServerDatabases);
        Assert.True(rig.Editor.ShowManualEditor);
        Assert.Equal(string.Empty, rig.Editor.ConnectionTestResult);
        Assert.Equal("Ventas", rig.Editor.NamesText);   // lo elegido no se pierde
    }

    [Fact]
    public async Task Switching_to_manual_names_hides_the_list_and_back()
    {
        var rig = Create();
        await Load(rig);

        rig.Editor.ToggleManualNamesCommand.Execute(null);
        Assert.False(rig.Editor.ShowDatabaseList);
        Assert.True(rig.Editor.ShowManualEditor);

        rig.Editor.ToggleManualNamesCommand.Execute(null);
        Assert.True(rig.Editor.ShowDatabaseList);
    }
}
