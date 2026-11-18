using System.Collections.ObjectModel;
using SqlBackupN.Core.Abstractions;
using SqlBackupN.Core.Domain;
using SqlBackupN.Core.Infrastructure;
using SqlBackupN.Core.Services;
using SqlBackupN.Presentation.Infrastructure;

namespace SqlBackupN.Presentation.Jobs;

/// <summary>Fila de la lista de jobs.</summary>
/// <param name="Job">Job representado.</param>
/// <param name="StatusGlyph">Símbolo del último estado.</param>
/// <param name="NextInfo">Texto secundario (última ejecución).</param>
/// <param name="LastStatus">Estado de la última ejecución (<c>null</c> si nunca se ejecutó).</param>
public sealed record JobItem(BackupJob Job, string StatusGlyph, string NextInfo, RunStatus? LastStatus = null)
{
    /// <summary>Nombre visible; indica si el job está desactivado.</summary>
    public string Name => Job.Enabled ? Job.Name : $"{Job.Name} (desactivado)";

    /// <summary>Clave de color del último estado (ver <see cref="StatusKinds"/>).</summary>
    public string Kind => StatusKinds.Of(LastStatus);
}

/// <summary>Fila del historial.</summary>
/// <param name="When">Fecha y hora de inicio.</param>
/// <param name="Glyph">Símbolo del estado.</param>
/// <param name="Summary">Tipo y tamaño.</param>
/// <param name="Detail">Primer error o advertencia, si lo hay.</param>
/// <param name="Status">Estado de la ejecución.</param>
/// <param name="Log">Registro paso a paso de esa ejecución.</param>
/// <param name="Reconstructed"><c>true</c> si la ejecución es anterior al registro y este se reconstruyó de su resumen.</param>
/// <param name="Id">Identificador de la ejecución (para borrarla del historial).</param>
public sealed record HistoryItem(
    string When, string Glyph, string Summary, string? Detail, RunStatus Status,
    IReadOnlyList<RunLogEntry>? Log = null, bool Reconstructed = false, Guid Id = default)
{
    /// <summary><c>true</c> si hay un registro que mostrar.</summary>
    public bool HasLog => Log is { Count: > 0 };

    /// <summary>Clave de color del estado (ver <see cref="StatusKinds"/>).</summary>
    public string Kind => StatusKinds.Of(Status);

    /// <summary>Texto de la insignia ("Correcto", "Fallo"…).</summary>
    public string StatusText => StatusKinds.LabelOf(Status);
}

/// <summary>Línea del registro de ejecución tal como se muestra.</summary>
/// <param name="Time">Hora local (HH:mm:ss).</param>
/// <param name="Text">Texto, con la base de datos entre corchetes si la línea es de una base concreta.</param>
/// <param name="Level">Gravedad.</param>
public sealed record RunLogLine(string Time, string Text, RunLogLevel Level)
{
    /// <summary>Clave de color de la línea (<c>ok</c>, <c>warn</c> o <c>fail</c>).</summary>
    public string Kind => Level switch
    {
        RunLogLevel.Error => "fail",
        RunLogLevel.Warning => "warn",
        _ => "ok"
    };

    /// <summary>Convierte una línea del registro del núcleo en una línea para mostrar.</summary>
    public static RunLogLine From(RunLogEntry entry) => new(
        entry.TimeUtc.ToLocalTime().ToString("HH:mm:ss"),
        entry.Database is null ? entry.Message : $"[{entry.Database}] {entry.Message}",
        entry.Level);
}

/// <summary>
/// Ventana principal: lista de jobs, detalle en tarjetas e historial, con las acciones de
/// crear, editar, duplicar, eliminar y ejecutar. La edición se delega en
/// <see cref="JobEditorViewModel"/>; aquí solo se guarda el resultado en <see cref="IJobStore"/>.
/// </summary>
public sealed class MainViewModel : ViewModelBase
{
    private readonly IJobStore _jobStore;
    private readonly IHistoryStore _history;
    private readonly JobRunner _runner;
    private readonly IDialogService _dialogs;
    private readonly EditorServices _editorServices;
    private JobItem? _selected;
    private string _status = "Listo";

    /// <summary>Crea el view-model con sus dependencias.</summary>
    public MainViewModel(
        IJobStore jobStore,
        IHistoryStore history,
        JobRunner runner,
        IDialogService dialogs,
        ISecretProtector protector,
        IBackupExecutor executor,
        IFileStore files,
        IRemoteStorage remote,
        IGoogleAuthorizer authorizer,
        IGoogleClientProvider googleClients,
        TimeProvider clock,
        INotifier? notifier = null,
        ThemeController? theme = null)
    {
        _jobStore = jobStore;
        _history = history;
        _runner = runner;
        _dialogs = dialogs;
        Theme = theme;
        _editorServices = new EditorServices(protector, executor, files, remote, authorizer, googleClients, dialogs, clock, notifier);

        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(), onError: ShowError);
        NewJobCommand = new AsyncRelayCommand(() => EditAsync(JobFactory.CreateNew(), isNew: true), onError: ShowError);
        EditJobCommand = new AsyncRelayCommand(
            () => EditAsync(Selected!.Job, isNew: false), () => Selected is not null, ShowError);
        DuplicateJobCommand = new AsyncRelayCommand(
            () => EditAsync(JobFactory.Duplicate(Selected!.Job), isNew: true), () => Selected is not null, ShowError);
        DeleteJobCommand = new AsyncRelayCommand(DeleteAsync, () => Selected is not null, ShowError);
        RunNowCommand = new AsyncRelayCommand(RunNowAsync, () => Selected is not null, ShowError);
        ShowLogCommand = new RelayCommand(p =>
        {
            if (p is HistoryItem h) _dialogs.ShowRunLog(RunLogViewModel.ForHistory(h));
        });

        DeleteHistoryItemCommand = new RelayCommand(p =>
        {
            if (p is HistoryItem h) _ = DeleteHistoryItemAsync(h);
        });
        ClearHistoryCommand = new AsyncRelayCommand(() => ClearHistoryAsync(allJobs: false), () => Selected is not null && HasHistory, ShowError);
        ClearAllHistoryCommand = new AsyncRelayCommand(() => ClearHistoryAsync(allJobs: true), () => Jobs.Count > 0, ShowError);

        _ = RefreshCommandSafeAsync();
    }

    /// <summary>Modo claro/oscuro (botón de la barra lateral); <c>null</c> si la ventana no lo ofrece.</summary>
    public ThemeController? Theme { get; }

    /// <summary>Borra una ejecución del historial (pide confirmación).</summary>
    public RelayCommand DeleteHistoryItemCommand { get; }

    /// <summary>Borra todo el historial del job seleccionado (pide confirmación).</summary>
    public AsyncRelayCommand ClearHistoryCommand { get; }

    /// <summary>Borra el historial de todos los jobs (pide confirmación).</summary>
    public AsyncRelayCommand ClearAllHistoryCommand { get; }

    /// <summary>Jobs configurados.</summary>
    public ObservableCollection<JobItem> Jobs { get; } = [];

    /// <summary>Historial del job seleccionado.</summary>
    public ObservableCollection<HistoryItem> History { get; } = [];

    /// <summary>Recarga jobs e historial.</summary>
    public AsyncRelayCommand RefreshCommand { get; }

    /// <summary>Crea un job nuevo.</summary>
    public AsyncRelayCommand NewJobCommand { get; }

    /// <summary>Edita el job seleccionado.</summary>
    public AsyncRelayCommand EditJobCommand { get; }

    /// <summary>Duplica el job seleccionado.</summary>
    public AsyncRelayCommand DuplicateJobCommand { get; }

    /// <summary>Elimina el job seleccionado (con confirmación).</summary>
    public AsyncRelayCommand DeleteJobCommand { get; }

    /// <summary>Ejecuta ahora el backup completo del job seleccionado.</summary>
    public AsyncRelayCommand RunNowCommand { get; }

    /// <summary>Abre en una ventana aparte el registro paso a paso de una ejecución del historial.</summary>
    public RelayCommand ShowLogCommand { get; }

    /// <summary><c>true</c> si el job seleccionado tiene ejecuciones en el historial.</summary>
    public bool HasHistory => History.Count > 0;

    /// <summary><c>true</c> si todavía no hay jobs (muestra la pantalla de bienvenida).</summary>
    public bool IsEmpty => Jobs.Count == 0;

    /// <summary>Job seleccionado.</summary>
    public JobItem? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            foreach (var p in new[]
                     {
                         nameof(Title), nameof(ServerText), nameof(DatabasesText), nameof(DestinationsText),
                         nameof(ScheduleText), nameof(RetentionText), nameof(NotificationsText), nameof(HasSelection)
                     })
                Raise(p);

            EditJobCommand.NotifyCanExecuteChanged();
            DuplicateJobCommand.NotifyCanExecuteChanged();
            DeleteJobCommand.NotifyCanExecuteChanged();
            RunNowCommand.NotifyCanExecuteChanged();
            _ = LoadHistorySafeAsync();
        }
    }

    /// <summary><c>true</c> si hay un job seleccionado.</summary>
    public bool HasSelection => Selected is not null;

    /// <summary>Mensaje de estado inferior.</summary>
    public string Status { get => _status; private set => Set(ref _status, value); }

    /// <summary>Título del panel central.</summary>
    public string Title => Selected?.Name ?? "Seleccione un job";

    /// <summary>Texto de la tarjeta Servidor.</summary>
    public string ServerText => Selected is { } s
        ? $"{s.Job.Connection.Server} · {(s.Job.Connection.UseWindowsAuthentication ? "Autenticación Windows" : $"Autenticación SQL ({s.Job.Connection.UserName})")}"
        : "";

    /// <summary>Texto de la tarjeta Bases de datos.</summary>
    public string DatabasesText => Selected is { } s
        ? DescribeDatabases(s.Job.Databases)
        : "";

    /// <summary>Texto de la tarjeta Destinos.</summary>
    public string DestinationsText => Selected is { } s
        ? string.Join(Environment.NewLine,
            s.Job.Destinations.Select((d, i) => $"{(i == 0 ? "Principal" : "Copia")}: {d.Name} → {d.Path}"))
        : "";

    /// <summary>Texto de la tarjeta Programación.</summary>
    public string ScheduleText => Selected is { } s
        ? string.Join(Environment.NewLine,
            s.Job.Steps.Select(x => $"{TypeOption.LabelOf(x.Type)}: {ScheduleBuilder.Describe(x.Cron) ?? x.Cron}{(x.Enabled ? "" : " (desactivado)")}"))
        : "";

    /// <summary>Texto de la tarjeta Retención.</summary>
    public string RetentionText => Selected is { } s
        ? DescribeRetention(s.Job.Retention)
        : "";

    /// <summary>Texto de la tarjeta Avisos.</summary>
    public string NotificationsText => Selected is { } s
        ? string.IsNullOrWhiteSpace(s.Job.Notifications.WebhookUrl)
            ? "Sin avisos configurados (se recomienda activarlos)"
            : "Webhook configurado"
        : "";

    private static string DescribeDatabases(DatabaseSelection d)
    {
        var text = d.Mode switch
        {
            DatabaseSelectionMode.AllUserDatabases => "Todas las bases de usuario (incluye las nuevas)",
            DatabaseSelectionMode.Explicit => string.Join(", ", d.Names),
            _ => $"Patrón: {d.Pattern}"
        };
        return d.Exclude.Count > 0 ? $"{text}\nExcluye: {string.Join(", ", d.Exclude)}" : text;
    }

    private static string DescribeRetention(RetentionPolicy r)
    {
        if (r.KeepDays is null && r.KeepLast is null) return "Sin retención (no se borra nada)";
        var parts = new List<string>();
        if (r.KeepDays is { } days) parts.Add($"{days} días");
        if (r.KeepLast is { } last) parts.Add($"mínimo {last} archivos");
        return string.Join(", ", parts);
    }

    private void ShowError(Exception ex)
    {
        Status = "Error: " + ex.Message;
        _dialogs.ShowMessage("Ocurrió un error", ex.Message);
    }

    private async Task RefreshCommandSafeAsync()
    {
        try { await RefreshAsync(); }
        catch (Exception ex) { Status = "Error al leer los jobs: " + ex.Message; }
    }

    private async Task LoadHistorySafeAsync()
    {
        try { await LoadHistoryAsync(); }
        catch (Exception ex) { Status = "Error al leer el historial: " + ex.Message; }
    }

    private async Task RefreshAsync(Guid? selectId = null)
    {
        var jobs = await _jobStore.LoadAsync();
        var keep = selectId ?? Selected?.Job.Id;

        Jobs.Clear();
        foreach (var job in jobs)
        {
            var last = (await _history.GetRecentAsync(job.Id, 1)).FirstOrDefault();
            Jobs.Add(new JobItem(job, Glyph(last?.Status),
                last is null ? "Nunca ejecutado" : $"Último: {last.StartedUtc.ToLocalTime():dd/MM HH:mm}",
                last?.Status));
        }

        Raise(nameof(IsEmpty));
        Selected = Jobs.FirstOrDefault(j => j.Job.Id == keep) ?? Jobs.FirstOrDefault();
        Status = jobs.Count == 0 ? "Aún no hay jobs." : $"{jobs.Count} job(s) · {AppPaths.JobsFile}";
    }

    private async Task LoadHistoryAsync()
    {
        var items = await _history.GetRecentAsync(Selected?.Job.Id, 50);
        History.Clear();
        foreach (var r in items)
        {
            var detail = r.Results.FirstOrDefault(x => x.Error is not null)?.Error
                         ?? r.Results.SelectMany(x => x.Warnings).FirstOrDefault();
            var size = r.Results.Sum(x => x.SizeBytes) / 1048576.0;
            // Las ejecuciones anteriores al registro paso a paso muestran uno reconstruido con lo que sí se guardó.
            var log = RunLogBuilder.For(r, out var reconstructed);
            History.Add(new HistoryItem(
                r.StartedUtc.ToLocalTime().ToString("dd MMM HH:mm"), Glyph(r.Status),
                $"{TypeOption.LabelOf(r.Type)} · {size:F1} MB", detail, r.Status, log, reconstructed, r.Id));
        }

        Raise(nameof(HasHistory));
        ClearHistoryCommand.NotifyCanExecuteChanged();
        ClearAllHistoryCommand.NotifyCanExecuteChanged();
    }

    private async Task DeleteHistoryItemAsync(HistoryItem item)
    {
        if (item.Id == Guid.Empty) return;
        if (!_dialogs.Confirm("Borrar ejecución",
                $"¿Borrar del historial la ejecución del {item.When} ({item.Summary})?\n\nSe pierde también su registro paso a paso. Los archivos de backup no se tocan."))
            return;
        try
        {
            var removed = await _history.DeleteAsync([item.Id]);
            await RefreshAfterHistoryChangeAsync();
            Status = removed == 1 ? "Se borró 1 ejecución del historial." : "Esa ejecución ya no estaba en el historial.";
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async Task ClearHistoryAsync(bool allJobs)
    {
        var job = Selected?.Job;
        if (!allJobs && job is null) return;

        var what = allJobs ? "de TODOS los jobs" : $"del job '{job!.Name}'";
        if (!_dialogs.Confirm("Borrar historial",
                $"¿Borrar todo el historial {what}?\n\nSe pierden las ejecuciones y sus registros paso a paso. Los archivos de backup no se tocan. No se puede deshacer."))
            return;

        var removed = await _history.ClearAsync(allJobs ? null : job!.Id);
        await RefreshAfterHistoryChangeAsync();
        Status = removed == 0 ? "No había nada que borrar." : $"Se borraron {removed} ejecución(es) del historial.";
    }

    /// <summary>Vuelve a leer jobs (su último estado) e historial; el historial se recarga aunque el job seleccionado no cambie.</summary>
    private async Task RefreshAfterHistoryChangeAsync()
    {
        await RefreshAsync();
        await LoadHistoryAsync();
    }

    /// <summary>Abre el editor y, si el usuario guarda, persiste el resultado.</summary>
    private async Task EditAsync(BackupJob job, bool isNew)
    {
        var all = (await _jobStore.LoadAsync()).ToList();
        var others = all.Where(j => j.Id != job.Id).Select(j => j.Name);
        var editor = new JobEditorViewModel(JobFactory.Clone(job), isNew, others, _editorServices);

        if (!_dialogs.ShowJobEditor(editor) || editor.Result is not { } saved) return;

        var index = all.FindIndex(j => j.Id == saved.Id);
        if (index >= 0) all[index] = saved;
        else all.Add(saved);

        await SaveAllAsync(all, saved.Id, $"Job '{saved.Name}' guardado.");
    }

    private async Task DeleteAsync()
    {
        if (Selected is not { } item) return;
        if (!_dialogs.Confirm("Eliminar job",
                $"¿Eliminar el job '{item.Job.Name}'?\n\nEl historial se conserva. Esta acción no se puede deshacer."))
            return;

        var all = (await _jobStore.LoadAsync()).Where(j => j.Id != item.Job.Id).ToList();
        await SaveAllAsync(all, null, $"Job '{item.Job.Name}' eliminado.");
    }

    private async Task SaveAllAsync(List<BackupJob> all, Guid? select, string okMessage)
    {
        try
        {
            await _jobStore.SaveAsync(all);
        }
        catch (UnauthorizedAccessException)
        {
            _dialogs.ShowMessage("No se pudo guardar",
                $"No hay permiso para escribir en:\n{AppPaths.JobsFile}\n\nEjecute la aplicación como administrador o ajuste los permisos de esa carpeta.");
            return;
        }

        await RefreshAsync(select);
        Status = okMessage;
    }

    private async Task RunNowAsync()
    {
        if (Selected is not { } item) return;
        Status = $"Ejecutando {item.Job.Name}…";
        var step = item.Job.Steps.FirstOrDefault(s => s.Type == BackupType.Full)
                   ?? new BackupStep { Type = BackupType.Full };

        // Cada paso del backup aparece apenas ocurre en una ventana aparte, que se puede dejar abierta.
        var log = new RunLogViewModel($"Registro de ejecución · {item.Job.Name}", isRunning: true);
        var startedUtc = DateTime.UtcNow.AddSeconds(-2);
        var jobId = item.Job.Id;
        log.Probe = async () =>
            (await _history.GetRecentAsync(jobId, 5)).FirstOrDefault(r => r.StartedUtc >= startedUtc);
        _dialogs.ShowRunLog(log);
        var progress = new UiProgress<RunLogEntry>(log.Add);
        JobRunRecord record;
        try
        {
            record = await Task.Run(() => _runner.RunStepAsync(
                item.Job, step, superseded: false, CancellationToken.None, progress));
        }
        catch (Exception ex)
        {
            log.Abort(ex.Message);
            throw;
        }
        log.Finish(record.Status);
        await RefreshAsync();
        Status = $"{item.Job.Name}: {record.Status}";
    }

    private static string Glyph(RunStatus? s) => s switch
    {
        RunStatus.Succeeded => "✔",
        RunStatus.Warning => "⚠",
        RunStatus.Failed => "✖",
        RunStatus.Skipped => "↷",
        _ => "·"
    };
}
