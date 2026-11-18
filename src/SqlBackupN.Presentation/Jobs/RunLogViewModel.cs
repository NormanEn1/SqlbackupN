using System.Collections.ObjectModel;
using SqlBackupN.Core.Domain;
using SqlBackupN.Presentation.Infrastructure;

namespace SqlBackupN.Presentation.Jobs;

/// <summary>
/// Contenido de la ventana «Registro de ejecución»: las líneas paso a paso de una ejecución, que pueden irse
/// agregando en vivo mientras corre el backup o venir completas de una ejecución ya guardada.
/// </summary>
public sealed class RunLogViewModel : ViewModelBase
{
    private string _subtitle;
    private string _kind = "none";
    private bool _isRunning;
    private readonly DateTime _startedUtc = DateTime.UtcNow;

    /// <summary>Crea un registro vacío. Con <paramref name="isRunning"/> queda «en curso» hasta llamar a <see cref="Finish"/>.</summary>
    /// <param name="title">Título de la ventana.</param>
    /// <param name="isRunning">Si la ejecución todavía está en marcha.</param>
    public RunLogViewModel(string title, bool isRunning = false)
    {
        Title = title;
        _isRunning = isRunning;
        _subtitle = isRunning ? "En curso…" : string.Empty;
    }

    /// <summary>Título (ejecución y job o fecha).</summary>
    public string Title { get; }

    /// <summary>Líneas del registro, en orden cronológico.</summary>
    public ObservableCollection<RunLogLine> Lines { get; } = [];

    /// <summary>Estado en palabras: «En curso…», el resultado final o una nota (p. ej. registro reconstruido).</summary>
    public string Subtitle { get => _subtitle; private set => Set(ref _subtitle, value); }

    /// <summary>Clave de color del estado (<c>ok</c>, <c>warn</c>, <c>fail</c> o <c>none</c>).</summary>
    public string Kind { get => _kind; private set => Set(ref _kind, value); }

    /// <summary><c>true</c> mientras la ejecución sigue en marcha.</summary>
    public bool IsRunning { get => _isRunning; private set => Set(ref _isRunning, value); }

    /// <summary>Todo el registro como texto, para copiarlo.</summary>
    public string AsText => string.Join(Environment.NewLine, Lines.Select(l => $"{l.Time}  {l.Text}"));

    /// <summary>
    /// Consulta opcional del historial: devuelve el registro guardado de esta ejecución cuando ya terminó. Sirve de red de
    /// seguridad: si el aviso de fin no llegó a la ventana, <see cref="CheckCompletionAsync"/> la pone al día igualmente.
    /// </summary>
    public Func<Task<JobRunRecord?>>? Probe { get; set; }

    /// <summary>Mientras corre, actualiza el tiempo transcurrido junto al estado («En curso… 00:45») para que se vea que sigue viva.</summary>
    public void Tick()
    {
        if (!IsRunning) return;
        var elapsed = DateTime.UtcNow - _startedUtc;
        Subtitle = $"En curso… {(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
    }

    /// <summary>
    /// Si la ejecución ya quedó guardada en el historial pero esta ventana seguía «en curso», copia su registro completo y la da por terminada.
    /// Devuelve <c>true</c> si hizo ese ajuste.
    /// </summary>
    public async Task<bool> CheckCompletionAsync()
    {
        if (!IsRunning || Probe is null) return false;
        JobRunRecord? record;
        try { record = await Probe(); }
        catch (Exception) { return false; }   // el historial no se pudo leer ahora: se reintenta en el próximo ciclo
        if (record is null || !IsRunning) return false;

        Lines.Clear();
        foreach (var entry in record.Log) Add(entry);
        Finish(record.Status);
        return true;
    }

    /// <summary>Agrega una línea (la ventana la muestra de inmediato).</summary>
    public void Add(RunLogEntry entry) => Lines.Add(RunLogLine.From(entry));

    /// <summary>Marca la ejecución como terminada con su estado final.</summary>
    public void Finish(RunStatus status)
    {
        IsRunning = false;
        Kind = StatusKinds.Of(status);
        Subtitle = status switch
        {
            RunStatus.Succeeded => "Terminó correctamente",
            RunStatus.Warning => "Terminó con advertencias",
            RunStatus.Failed => "Terminó con fallos",
            _ => "Omitida"
        };
    }

    /// <summary>Marca la ejecución como interrumpida por un error inesperado.</summary>
    public void Abort(string reason)
    {
        IsRunning = false;
        Kind = "fail";
        Subtitle = "Se interrumpió: " + reason;
    }

    /// <summary>Registro de una ejecución del historial (el guardado o, en las antiguas, uno reconstruido).</summary>
    public static RunLogViewModel ForHistory(HistoryItem item)
    {
        var log = new RunLogViewModel($"Registro de ejecución · {item.When}");
        foreach (var entry in item.Log ?? []) log.Add(entry);
        log.Finish(item.Status);
        if (item.Reconstructed)
            log.Subtitle += " · reconstruido del resumen (es anterior al registro paso a paso)";
        return log;
    }
}
