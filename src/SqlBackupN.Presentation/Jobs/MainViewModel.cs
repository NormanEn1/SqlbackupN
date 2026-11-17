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
public sealed record HistoryItem(string When, string Glyph, string Summary, string? Detail, RunStatus Status)
{
    /// <summary>Clave de color del estado (ver <see cref="StatusKinds"/>).</summary>
    public string Kind => StatusKinds.Of(Status);

    /// <summary>Texto de la insignia ("Correcto", "Fallo"…).</summary>
    public string StatusText => StatusKinds.LabelOf(Status);
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
        TimeProvider clock)
    {
        _jobStore = jobStore;
        _history = history;
        _runner = runner;
        _dialogs = dialogs;
        _editorServices = new EditorServices(protector, executor, files, remote, authorizer, dialogs, clock);

        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(), onError: ShowError);
        NewJobCommand = new AsyncRelayCommand(() => EditAsync(JobFactory.CreateNew(), isNew: true), onError: ShowError);
        EditJobCommand = new AsyncRelayCommand(
            () => EditAsync(Selected!.Job, isNew: false), () => Selected is not null, ShowError);
        DuplicateJobCommand = new AsyncRelayCommand(
            () => EditAsync(JobFactory.Duplicate(Selected!.Job), isNew: true), () => Selected is not null, ShowError);
        DeleteJobCommand = new AsyncRelayCommand(DeleteAsync, () => Selected is not null, ShowError);
        RunNowCommand = new AsyncRelayCommand(RunNowAsync, () => Selected is not null, ShowError);

        _ = RefreshCommandSafeAsync();
    }

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
            s.Job.Steps.Select(x => $"{TypeOption.LabelOf(x.Type)}: {x.Cron}{(x.Enabled ? "" : " (desactivado)")}"))
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
            History.Add(new HistoryItem(
                r.StartedUtc.ToLocalTime().ToString("dd MMM HH:mm"), Glyph(r.Status),
                $"{TypeOption.LabelOf(r.Type)} · {size:F1} MB", detail, r.Status));
        }

        Raise(nameof(HasHistory));
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
        var record = await Task.Run(() => _runner.RunStepAsync(item.Job, step, superseded: false, CancellationToken.None));
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
