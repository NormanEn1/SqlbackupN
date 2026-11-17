using Microsoft.Extensions.Logging.Abstractions;
using SqlBackupN.Core.Domain;
using SqlBackupN.Core.Services;
using SqlBackupN.Presentation.Jobs;

namespace SqlBackupN.Tests;

/// <summary>Pruebas del flujo de administración (crear, editar, duplicar, eliminar) sin interfaz ni disco.</summary>
public class MainViewModelTests
{
    private sealed record Rig(MainViewModel Main, MemoryJobStore Store, StubDialogs Dialogs);

    private static BackupJob Job(string name) => new()
    {
        Name = name,
        Steps = [new BackupStep { Type = BackupType.Full, Cron = "0 23 * * *" }],
        Destinations = [new DestinationConfig { Name = "Local", Path = @"D:\Backups" }]
    };

    private static Rig Create(params BackupJob[] existing)
    {
        var store = new MemoryJobStore { Jobs = existing.ToList() };
        var dialogs = new StubDialogs();
        var executor = new StubExecutor();
        var files = new StubFiles();
        var history = new MemoryHistoryStore();
        var runner = new JobRunner(executor, files, new StubRemote(), history, new StubNotifier(), TimeProvider.System,
            NullLogger<JobRunner>.Instance);
        var main = new MainViewModel(store, history, runner, dialogs, new StubProtector(), executor, files, new StubRemote(), new StubAuthorizer(), TimeProvider.System);
        return new Rig(main, store, dialogs);
    }

    [Fact]
    public void Without_jobs_the_welcome_screen_is_shown()
    {
        var rig = Create();

        Assert.True(rig.Main.IsEmpty);
        Assert.False(rig.Main.HasSelection);
        Assert.False(rig.Main.EditJobCommand.CanExecute(null));
        Assert.False(rig.Main.DeleteJobCommand.CanExecute(null));
        Assert.True(rig.Main.NewJobCommand.CanExecute(null));
    }

    [Fact]
    public void Creating_a_job_adds_it_selects_it_and_persists_it()
    {
        var rig = Create();
        rig.Dialogs.OnEdit = editor =>
        {
            editor.Name = "Ventas";
            editor.AddDestinationCommand.Execute(null);
            editor.Destinations[0].Path = @"D:\Backups";
            editor.SaveCommand.Execute(null);
        };

        rig.Main.NewJobCommand.Execute(null);

        Assert.Single(rig.Store.Jobs);
        Assert.Equal("Ventas", rig.Store.Jobs[0].Name);
        Assert.False(rig.Main.IsEmpty);
        Assert.Equal("Ventas", rig.Main.Selected!.Job.Name);
        Assert.True(rig.Main.EditJobCommand.CanExecute(null));
    }

    [Fact]
    public void Cancelling_the_editor_changes_nothing()
    {
        var rig = Create(Job("Ventas"));
        rig.Dialogs.OnEdit = editor =>
        {
            editor.Name = "Cambiado";
            editor.CancelCommand.Execute(null);
        };

        rig.Main.EditJobCommand.Execute(null);

        Assert.Equal("Ventas", rig.Store.Jobs[0].Name);
    }

    [Fact]
    public void Editing_replaces_the_job_in_place_keeping_its_id()
    {
        var original = Job("Ventas");
        var rig = Create(original, Job("Compras"));
        rig.Dialogs.OnEdit = editor =>
        {
            editor.Name = "Ventas 2026";
            editor.SaveCommand.Execute(null);
        };

        rig.Main.EditJobCommand.Execute(null);

        Assert.Equal(2, rig.Store.Jobs.Count);
        Assert.Equal("Ventas 2026", rig.Store.Jobs[0].Name);
        Assert.Equal(original.Id, rig.Store.Jobs[0].Id);
        Assert.Equal("Compras", rig.Store.Jobs[1].Name);
    }

    [Fact]
    public void Editor_rejects_a_name_used_by_another_job()
    {
        var rig = Create(Job("Ventas"), Job("Compras"));
        rig.Dialogs.OnEdit = editor =>
        {
            editor.Name = "compras";
            Assert.False(editor.CanSave);
            editor.CancelCommand.Execute(null);
        };

        rig.Main.EditJobCommand.Execute(null);

        Assert.Equal(1, rig.Dialogs.EditorsShown);
    }

    [Fact]
    public void Duplicating_adds_a_copy_with_a_new_id()
    {
        var original = Job("Ventas");
        var rig = Create(original);
        rig.Dialogs.OnEdit = editor => editor.SaveCommand.Execute(null);

        rig.Main.DuplicateJobCommand.Execute(null);

        Assert.Equal(2, rig.Store.Jobs.Count);
        var copy = rig.Store.Jobs.Single(j => j.Id != original.Id);
        Assert.Equal("Ventas (copia)", copy.Name);
    }

    [Fact]
    public void Deleting_requires_confirmation()
    {
        var rig = Create(Job("Ventas"));
        rig.Dialogs.ConfirmAnswer = false;

        rig.Main.DeleteJobCommand.Execute(null);
        Assert.Single(rig.Store.Jobs);

        rig.Dialogs.ConfirmAnswer = true;
        rig.Main.DeleteJobCommand.Execute(null);
        Assert.Empty(rig.Store.Jobs);
        Assert.True(rig.Main.IsEmpty);
    }

    [Fact]
    public void Disabled_jobs_are_labelled_in_the_list()
    {
        var disabled = Job("Antiguo");
        disabled.Enabled = false;
        var rig = Create(disabled);

        Assert.Equal("Antiguo (desactivado)", rig.Main.Jobs[0].Name);
    }

    [Fact]
    public void Detail_cards_describe_the_selected_job()
    {
        var job = Job("Ventas");
        job.Databases = new DatabaseSelection
        {
            Mode = DatabaseSelectionMode.Explicit, Names = ["Ventas", "Compras"], Exclude = ["Temp"]
        };
        job.Retention = new RetentionPolicy { KeepDays = 14, KeepLast = 2 };
        var rig = Create(job);

        Assert.Equal("Ventas", rig.Main.Title);
        Assert.Contains("Ventas, Compras", rig.Main.DatabasesText);
        Assert.Contains("Excluye: Temp", rig.Main.DatabasesText);
        Assert.Equal("14 días, mínimo 2 archivos", rig.Main.RetentionText);
        Assert.Contains("Completo: 0 23 * * *", rig.Main.ScheduleText);
        Assert.Contains("Principal: Local → D:\\Backups", rig.Main.DestinationsText);
    }
}
