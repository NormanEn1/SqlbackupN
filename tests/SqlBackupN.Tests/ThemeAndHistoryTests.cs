using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using SqlBackupN.Core.Abstractions;
using SqlBackupN.Core.Domain;
using SqlBackupN.Core.Infrastructure;
using SqlBackupN.Core.Services;
using SqlBackupN.Presentation.Infrastructure;
using SqlBackupN.Presentation.Jobs;

namespace SqlBackupN.Tests;

/// <summary>Modo claro/oscuro (preferencia, controlador y coherencia de la paleta) y borrado del historial.</summary>
public class ThemeAndHistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlbackupn-test-" + Guid.NewGuid().ToString("N"));

    public ThemeAndHistoryTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string File_(string name) => Path.Combine(_dir, name);

    // ------------------------------------------------------------- Preferencias del usuario

    [Fact]
    public void The_theme_preference_is_saved_and_read_back()
    {
        var store = new JsonUserSettingsStore(File_("sub/settings.json"));

        store.Save(new UserSettings { Theme = AppTheme.Dark });

        Assert.Equal(AppTheme.Dark, new JsonUserSettingsStore(File_("sub/settings.json")).Load().Theme);
    }

    [Fact]
    public void A_missing_or_damaged_settings_file_gives_the_defaults_instead_of_failing()
    {
        Assert.Equal(AppTheme.System, new JsonUserSettingsStore(File_("no-existe.json")).Load().Theme);

        File.WriteAllText(File_("roto.json"), "{ esto no es json");
        Assert.Equal(AppTheme.System, new JsonUserSettingsStore(File_("roto.json")).Load().Theme);
    }

    // ------------------------------------------------------------- Controlador del tema

    private sealed class MemorySettings(AppTheme initial) : IUserSettingsStore
    {
        public List<AppTheme> Saved { get; } = [];
        public bool Fail { get; set; }
        public UserSettings Load() => new() { Theme = initial };
        public void Save(UserSettings settings)
        {
            if (Fail) throw new IOException("disco de solo lectura");
            Saved.Add(settings.Theme);
        }
    }

    private sealed class RecordingTheme : IThemeService
    {
        public List<AppTheme> Applied { get; } = [];
        public void Apply(AppTheme mode) => Applied.Add(mode);
    }

    [Fact]
    public void The_saved_mode_is_applied_when_the_controller_starts()
    {
        var service = new RecordingTheme();

        var theme = new ThemeController(new MemorySettings(AppTheme.Dark), service);

        Assert.Equal(AppTheme.Dark, theme.Mode);
        Assert.Equal([AppTheme.Dark], service.Applied);
        Assert.Equal("Tema: oscuro", theme.Label);
    }

    [Fact]
    public void The_button_cycles_automatic_light_dark_and_remembers_each_choice()
    {
        var store = new MemorySettings(AppTheme.System);
        var service = new RecordingTheme();
        var theme = new ThemeController(store, service);

        theme.CycleCommand.Execute(null);
        theme.CycleCommand.Execute(null);
        theme.CycleCommand.Execute(null);

        Assert.Equal([AppTheme.Light, AppTheme.Dark, AppTheme.System], store.Saved);
        Assert.Equal([AppTheme.System, AppTheme.Light, AppTheme.Dark, AppTheme.System], service.Applied);
        Assert.Equal("Tema: automático", theme.Label);
    }

    [Fact]
    public void Not_being_able_to_save_the_preference_does_not_stop_the_change()
    {
        var service = new RecordingTheme();
        var theme = new ThemeController(new MemorySettings(AppTheme.System) { Fail = true }, service);

        theme.CycleCommand.Execute(null);

        Assert.Equal(AppTheme.Light, theme.Mode);
        Assert.Equal(AppTheme.Light, service.Applied[^1]);
    }

    // ------------------------------------------------------------- Coherencia de la paleta (los archivos XAML)

    private static string StylesDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SqlBackupN.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "SqlBackupN.App");
    }

    private static HashSet<string> BrushKeys(string file) =>
        Regex.Matches(File.ReadAllText(file), "<SolidColorBrush x:Key=\"(\\w+)\"").Select(m => m.Groups[1].Value).ToHashSet();

    [Fact]
    public void The_dark_palette_defines_exactly_the_same_brushes_as_the_light_one()
    {
        var styles = Path.Combine(StylesDirectory(), "Styles");

        var light = BrushKeys(Path.Combine(styles, "Theme.xaml"));
        var dark = BrushKeys(Path.Combine(styles, "Theme.Dark.xaml"));

        Assert.NotEmpty(light);
        Assert.Empty(light.Except(dark));     // un pincel sin versión oscura se vería claro sobre fondo oscuro
        Assert.Empty(dark.Except(light));     // y uno solo oscuro no tendría efecto en el modo claro
    }

    [Fact]
    public void Screens_use_dynamic_theme_brushes_and_no_hand_written_colors()
    {
        var app = StylesDirectory();
        var brushes = BrushKeys(Path.Combine(app, "Styles", "Theme.xaml"));
        var files = Directory.GetFiles(app, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !Path.GetFileName(f).StartsWith("Theme", StringComparison.Ordinal))
            .ToList();

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            // StaticResource de un pincel del tema: no cambiaría al pasar a oscuro.
            foreach (Match m in Regex.Matches(text, @"\{StaticResource (\w+)\}"))
                Assert.False(brushes.Contains(m.Groups[1].Value), $"{Path.GetFileName(file)} usa StaticResource {m.Groups[1].Value}: use DynamicResource.");
            // Colores escritos a mano: no cambiarían con el tema.
            Assert.False(Regex.IsMatch(text, "\"#[0-9A-Fa-f]{6,8}\""), $"{Path.GetFileName(file)} tiene un color escrito a mano: defínalo en Theme.xaml y Theme.Dark.xaml.");
        }
    }

    // ------------------------------------------------------------- Historial: borrar

    private static JobRunRecord Run(Guid jobId, string status = "x", int minutesAgo = 0) => new()
    {
        JobId = jobId,
        JobName = status,
        StartedUtc = DateTime.UtcNow.AddMinutes(-minutesAgo),
        FinishedUtc = DateTime.UtcNow.AddMinutes(-minutesAgo),
        Results = [new DatabaseBackupResult { Database = "Ventas", FilePath = @"D:\bk\a.bak" }]
    };

    [Fact]
    public async Task Deleting_one_run_removes_only_that_one_and_leaves_no_temp_file()
    {
        var store = new JsonHistoryStore(File_("history.jsonl"));
        var job = Guid.NewGuid();
        var a = Run(job, "a", 3);
        var b = Run(job, "b", 2);
        var c = Run(job, "c", 1);
        foreach (var r in new[] { a, b, c }) await store.AppendAsync(r);

        var removed = await store.DeleteAsync([b.Id]);

        Assert.Equal(1, removed);
        Assert.Equal(["c", "a"], (await store.GetRecentAsync(null, 10)).Select(r => r.JobName));
        Assert.False(File.Exists(File_("history.jsonl.tmp")));
    }

    [Fact]
    public async Task Clearing_a_job_keeps_the_others_and_clearing_all_empties_the_history()
    {
        var store = new JsonHistoryStore(File_("history.jsonl"));
        var one = Guid.NewGuid();
        var two = Guid.NewGuid();
        await store.AppendAsync(Run(one, "1a", 3));
        await store.AppendAsync(Run(one, "1b", 2));
        await store.AppendAsync(Run(two, "2a", 1));

        Assert.Equal(2, await store.ClearAsync(one));
        Assert.Equal(["2a"], (await store.GetRecentAsync(null, 10)).Select(r => r.JobName));

        Assert.Equal(1, await store.ClearAsync(null));
        Assert.Empty(await store.GetRecentAsync(null, 10));
        Assert.Equal(0, await store.ClearAsync(null));
    }

    [Fact]
    public async Task Deleting_when_there_is_no_history_file_or_no_match_does_nothing()
    {
        var store = new JsonHistoryStore(File_("history.jsonl"));

        Assert.Equal(0, await store.DeleteAsync([Guid.NewGuid()]));
        Assert.Equal(0, await store.ClearAsync(null));

        await store.AppendAsync(Run(Guid.NewGuid()));
        Assert.Equal(0, await store.DeleteAsync([Guid.NewGuid()]));
        Assert.Single(await store.GetRecentAsync(null, 10));
    }

    [Fact]
    public async Task Unreadable_lines_are_kept_when_rewriting()
    {
        var path = File_("history.jsonl");
        var store = new JsonHistoryStore(path);
        var run = Run(Guid.NewGuid());
        await store.AppendAsync(run);
        File.AppendAllText(path, "línea dañada\n");

        await store.DeleteAsync([run.Id]);

        Assert.Contains("línea dañada", File.ReadAllText(path));
    }

    [Fact]
    public async Task A_run_finishing_while_the_file_is_being_rewritten_is_not_lost()
    {
        var path = File_("history.jsonl");
        var store = new JsonHistoryStore(path);
        var old = Run(Guid.NewGuid(), "vieja");
        await store.AppendAsync(old);

        // Otro proceso (la interfaz) tiene el archivo abierto en exclusiva un momento: el servicio espera y agrega.
        var locker = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var appending = new JsonHistoryStore(path).AppendAsync(Run(Guid.NewGuid(), "nueva"));
        await Task.Delay(300);
        Assert.False(appending.IsCompleted);
        locker.Dispose();
        await appending;

        Assert.Equal(["nueva", "vieja"], (await store.GetRecentAsync(null, 10)).Select(r => r.JobName).Order());
    }

    // ------------------------------------------------------------- Historial: pantalla principal

    private sealed class EditableHistory : IHistoryStore
    {
        public List<JobRunRecord> Records { get; } = [];
        public Task AppendAsync(JobRunRecord record, CancellationToken ct = default) { Records.Add(record); return Task.CompletedTask; }
        public Task<IReadOnlyList<JobRunRecord>> GetRecentAsync(Guid? jobId, int max, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<JobRunRecord>>(Records.Where(r => jobId is null || r.JobId == jobId).Take(max).ToList());
        public Task<DateTime?> GetLastRunUtcAsync(Guid jobId, Guid stepId, CancellationToken ct = default) => Task.FromResult<DateTime?>(null);
        public Task<int> DeleteAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
            Task.FromResult(Records.RemoveAll(r => ids.Contains(r.Id)));
        public Task<int> ClearAsync(Guid? jobId, CancellationToken ct = default) =>
            Task.FromResult(Records.RemoveAll(r => jobId is null || r.JobId == jobId));
    }

    private static (MainViewModel Main, EditableHistory History, StubDialogs Dialogs, BackupJob Job) CreateMain(int runsOfSelected = 3)
    {
        var job = new BackupJob
        {
            Name = "Ventas",
            Connection = new SqlServerConnection { Server = "srv01" },
            Steps = [new BackupStep { Type = BackupType.Full, Cron = "0 23 * * *" }],
            Destinations = [new DestinationConfig { Name = "Local", Path = @"D:\Backups" }]
        };
        var other = new BackupJob { Name = "Compras" };
        var history = new EditableHistory();
        for (var i = 0; i < runsOfSelected; i++) history.Records.Add(Run(job.Id, $"v{i}", i));
        history.Records.Add(Run(other.Id, "otro"));
        var dialogs = new StubDialogs { ConfirmAnswer = true };
        var executor = new StubExecutor();
        var files = new StubFiles();
        var runner = new JobRunner(executor, files, new StubRemote(), history, new StubNotifier(), TimeProvider.System, NullLogger<JobRunner>.Instance);
        var main = new MainViewModel(new MemoryJobStore { Jobs = [job, other] }, history, runner, dialogs, new StubProtector(), executor, files,
            new StubRemote(), new StubAuthorizer(), new StubGoogleClients(), TimeProvider.System);
        main.Selected = main.Jobs.First(j => j.Job.Id == job.Id);
        return (main, history, dialogs, job);
    }

    private static async Task SettleAsync(MainViewModel main)
    {
        // las lecturas del historial son asíncronas pero terminan al instante con los dobles de prueba
        for (var i = 0; i < 100 && main.ClearHistoryCommand.IsRunning; i++) await Task.Delay(10);
        await Task.Delay(50);
    }

    [Fact]
    public async Task Each_history_item_carries_the_id_needed_to_delete_it()
    {
        var (main, history, _, _) = CreateMain();
        await SettleAsync(main);

        Assert.Equal(3, main.History.Count);
        Assert.All(main.History, h => Assert.NotEqual(Guid.Empty, h.Id));
        Assert.Equal(history.Records.Where(r => r.JobName.StartsWith('v')).Select(r => r.Id).Order(),
                     main.History.Select(h => h.Id).Order());
    }

    [Fact]
    public async Task Deleting_one_run_asks_first_and_then_removes_only_that_one()
    {
        var (main, history, dialogs, _) = CreateMain();
        await SettleAsync(main);
        var item = main.History[1];

        dialogs.ConfirmAnswer = false;
        main.DeleteHistoryItemCommand.Execute(item);
        await SettleAsync(main);
        Assert.Equal(3, main.History.Count);                       // dijo que no: no se borra nada

        dialogs.ConfirmAnswer = true;
        main.DeleteHistoryItemCommand.Execute(item);
        await SettleAsync(main);

        Assert.Equal(2, main.History.Count);
        Assert.DoesNotContain(main.History, h => h.Id == item.Id);
        Assert.Equal(3, history.Records.Count);                    // quedan 2 de Ventas y 1 de Compras
        Assert.Equal("Se borró 1 ejecución del historial.", main.Status);
    }

    [Fact]
    public async Task Clearing_the_selected_job_keeps_the_history_of_the_other_jobs()
    {
        var (main, history, _, _) = CreateMain();
        await SettleAsync(main);

        main.ClearHistoryCommand.Execute(null);
        await SettleAsync(main);

        Assert.Empty(main.History);
        Assert.False(main.HasHistory);
        Assert.Equal(["otro"], history.Records.Select(r => r.JobName));
        Assert.Equal("Se borraron 3 ejecución(es) del historial.", main.Status);
        Assert.False(main.ClearHistoryCommand.CanExecute(null));   // ya no hay nada que vaciar
    }

    [Fact]
    public async Task Clearing_everything_empties_the_history_of_all_jobs_after_confirming()
    {
        var (main, history, dialogs, _) = CreateMain();
        await SettleAsync(main);

        dialogs.ConfirmAnswer = false;
        main.ClearAllHistoryCommand.Execute(null);
        await SettleAsync(main);
        Assert.Equal(4, history.Records.Count);

        dialogs.ConfirmAnswer = true;
        main.ClearAllHistoryCommand.Execute(null);
        await SettleAsync(main);

        Assert.Empty(history.Records);
        Assert.Empty(main.History);
    }
}
