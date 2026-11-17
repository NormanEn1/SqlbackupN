using SqlBackupN.Core.Domain;
using SqlBackupN.Core.Services;
using SqlBackupN.Presentation.Infrastructure;

namespace SqlBackupN.Presentation.Jobs;

/// <summary>Opción de la lista de frecuencias.</summary>
/// <param name="Value">Tipo de frecuencia.</param>
/// <param name="Label">Texto visible.</param>
public sealed record ScheduleModeOption(ScheduleMode Value, string Label)
{
    /// <summary>Todas las opciones, en el orden en que se muestran.</summary>
    public static IReadOnlyList<ScheduleModeOption> All { get; } =
    [
        new(ScheduleMode.Daily, "Todos los días"),
        new(ScheduleMode.Weekly, "Días de la semana"),
        new(ScheduleMode.EveryHours, "Cada pocas horas"),
        new(ScheduleMode.EveryMinutes, "Cada pocos minutos"),
        new(ScheduleMode.Monthly, "Una vez al mes"),
        new(ScheduleMode.Custom, "Avanzado (expresión cron)")
    ];
}

/// <summary>
/// Un paso de programación dentro del editor. El usuario elige tipo de backup, frecuencia, hora y días con
/// controles sencillos; por debajo se mantiene la expresión cron (<see cref="Cron"/>), que es lo único que se guarda.
/// Si el job trae un cron que no encaja en ninguna frecuencia amigable, el paso se muestra en modo
/// <see cref="ScheduleMode.Custom"/> con el cron editable.
/// </summary>
public sealed class StepEditorViewModel : ViewModelBase
{
    private readonly Action _changed;
    private readonly bool[] _days = new bool[7];   // índice = (int)DayOfWeek (domingo = 0)
    private BackupType _type;
    private ScheduleMode _mode;
    private string _cron;
    private bool _enabled;
    private int _hour = 23;
    private int _minute;
    private int _dayOfMonth = 1;
    private int _intervalHours = 1;
    private int _intervalMinutes = 15;
    private string _nextRuns = string.Empty;
    private string _error = string.Empty;
    private bool _loading = true;

    /// <summary>Crea el view-model a partir de un paso existente.</summary>
    /// <param name="step">Paso de origen (se conserva su identificador).</param>
    /// <param name="changed">Se invoca al cambiar cualquier campo editable.</param>
    /// <param name="remove">Se invoca al pulsar "Quitar".</param>
    public StepEditorViewModel(BackupStep step, Action changed, Action<StepEditorViewModel> remove)
    {
        Id = step.Id;
        _type = step.Type;
        _enabled = step.Enabled;
        _cron = step.Cron;
        _changed = changed;
        RemoveCommand = new RelayCommand(() => remove(this));

        var spec = ScheduleBuilder.Parse(step.Cron);
        Apply(spec);
        _mode = spec.Mode;
        _loading = false;
    }

    // ---------------------------------------------------------------- Datos básicos

    /// <summary>Identificador del paso (se conserva para no perder el historial de última ejecución).</summary>
    public Guid Id { get; }

    /// <summary>Tipo de backup.</summary>
    public BackupType Type { get => _type; set { if (Set(ref _type, value)) _changed(); } }

    /// <summary>Si el paso está habilitado.</summary>
    public bool Enabled { get => _enabled; set { if (Set(ref _enabled, value)) _changed(); } }

    /// <summary>Texto con las próximas ejecuciones (lo calcula el editor).</summary>
    public string NextRuns { get => _nextRuns; set => Set(ref _nextRuns, value); }

    /// <summary>Error de validación de este paso (vacío si es correcto).</summary>
    public string Error { get => _error; set => Set(ref _error, value); }

    /// <summary>Quita el paso del job.</summary>
    public RelayCommand RemoveCommand { get; }

    /// <summary>Convierte el estado editado en un paso del dominio.</summary>
    public BackupStep ToStep() => new() { Id = Id, Type = Type, Cron = Cron.Trim(), Enabled = Enabled };

    // ---------------------------------------------------------------- Horario

    /// <summary>Frecuencias disponibles.</summary>
    public IReadOnlyList<ScheduleModeOption> Modes => ScheduleModeOption.All;

    /// <summary>Intervalos de horas disponibles.</summary>
    public IReadOnlyList<int> HourIntervals => ScheduleBuilder.HourIntervals;

    /// <summary>Intervalos de minutos disponibles.</summary>
    public IReadOnlyList<int> MinuteIntervals => ScheduleBuilder.MinuteIntervals;

    /// <summary>Frecuencia elegida.</summary>
    public ScheduleMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            var previous = _mode;
            _mode = value;

            // Al salir del modo avanzado se aprovecha lo que se pueda entender del cron escrito.
            if (previous == ScheduleMode.Custom && value != ScheduleMode.Custom)
            {
                var parsed = ScheduleBuilder.Parse(_cron);
                if (parsed.Mode != ScheduleMode.Custom) Apply(parsed);
            }

            RaiseModeFlags();
            if (value == ScheduleMode.Custom) RaiseSummary();
            else Rebuild();
            _changed();
        }
    }

    /// <summary>Hora (0-23).</summary>
    public int Hour { get => _hour; set { if (Set(ref _hour, Math.Clamp(value, 0, 23))) Rebuild(); } }

    /// <summary>Minuto (0-59).</summary>
    public int Minute { get => _minute; set { if (Set(ref _minute, Math.Clamp(value, 0, 59))) Rebuild(); } }

    /// <summary>Día del mes (1-31).</summary>
    public int DayOfMonth { get => _dayOfMonth; set { if (Set(ref _dayOfMonth, Math.Clamp(value, 1, 31))) Rebuild(); } }

    /// <summary>Cada cuántas horas.</summary>
    public int IntervalHours { get => _intervalHours; set { if (Set(ref _intervalHours, value)) Rebuild(); } }

    /// <summary>Cada cuántos minutos.</summary>
    public int IntervalMinutes { get => _intervalMinutes; set { if (Set(ref _intervalMinutes, value)) Rebuild(); } }

    /// <summary>Lunes.</summary>
    public bool Monday { get => _days[(int)DayOfWeek.Monday]; set => SetDay(DayOfWeek.Monday, value); }

    /// <summary>Martes.</summary>
    public bool Tuesday { get => _days[(int)DayOfWeek.Tuesday]; set => SetDay(DayOfWeek.Tuesday, value); }

    /// <summary>Miércoles.</summary>
    public bool Wednesday { get => _days[(int)DayOfWeek.Wednesday]; set => SetDay(DayOfWeek.Wednesday, value); }

    /// <summary>Jueves.</summary>
    public bool Thursday { get => _days[(int)DayOfWeek.Thursday]; set => SetDay(DayOfWeek.Thursday, value); }

    /// <summary>Viernes.</summary>
    public bool Friday { get => _days[(int)DayOfWeek.Friday]; set => SetDay(DayOfWeek.Friday, value); }

    /// <summary>Sábado.</summary>
    public bool Saturday { get => _days[(int)DayOfWeek.Saturday]; set => SetDay(DayOfWeek.Saturday, value); }

    /// <summary>Domingo.</summary>
    public bool Sunday { get => _days[(int)DayOfWeek.Sunday]; set => SetDay(DayOfWeek.Sunday, value); }

    /// <summary>
    /// Expresión cron. En los modos amigables se genera sola; al escribirla a mano el paso pasa
    /// al modo avanzado.
    /// </summary>
    public string Cron
    {
        get => _cron;
        set
        {
            if (!Set(ref _cron, value ?? string.Empty)) return;
            if (!_loading && _mode != ScheduleMode.Custom)
            {
                _mode = ScheduleMode.Custom;
                RaiseModeFlags();
            }
            RaiseSummary();
            _changed();
        }
    }

    /// <summary>Resumen en palabras ("De lunes a viernes a las 22:00").</summary>
    public string Summary => NoDays
        ? "Marque al menos un día de la semana."
        : ScheduleBuilder.Describe(_cron) ?? "Expresión cron personalizada";

    /// <summary>La expresión cron equivalente, para quien quiera verla.</summary>
    public string CronText => $"cron: {_cron}";

    /// <summary><c>true</c> si el modo necesita días y no hay ninguno marcado.</summary>
    public bool NoDays => ShowDays && !_days.Any(d => d);

    /// <summary>Se muestra hora y minuto.</summary>
    public bool ShowTime => _mode is ScheduleMode.Daily or ScheduleMode.Weekly or ScheduleMode.Monthly;

    /// <summary>Se muestran los botones de días de la semana.</summary>
    public bool ShowDays => _mode is ScheduleMode.Weekly or ScheduleMode.EveryHours or ScheduleMode.EveryMinutes;

    /// <summary>Se muestra "cada N horas, en el minuto M".</summary>
    public bool ShowHourInterval => _mode == ScheduleMode.EveryHours;

    /// <summary>Se muestra "cada N minutos".</summary>
    public bool ShowMinuteInterval => _mode == ScheduleMode.EveryMinutes;

    /// <summary>Se muestra el día del mes.</summary>
    public bool ShowMonthDay => _mode == ScheduleMode.Monthly;

    /// <summary>Se muestra el cron editable.</summary>
    public bool ShowCron => _mode == ScheduleMode.Custom;

    /// <summary>Se muestra el cron generado (solo lectura).</summary>
    public bool ShowCronText => _mode != ScheduleMode.Custom;

    // ---------------------------------------------------------------- Lógica

    private void SetDay(DayOfWeek day, bool value)
    {
        if (_days[(int)day] == value) return;
        _days[(int)day] = value;
        Raise(day.ToString());
        Rebuild();
    }

    private void Apply(ScheduleSpec spec)
    {
        _hour = spec.Hour;
        _minute = spec.Minute;
        _dayOfMonth = spec.DayOfMonth;
        _intervalHours = spec.IntervalHours;
        _intervalMinutes = spec.IntervalMinutes;

        var days = spec.Days ?? ScheduleBuilder.EveryDay;
        foreach (var d in Enum.GetValues<DayOfWeek>()) _days[(int)d] = days.Contains(d);

        if (_loading) return;
        foreach (var name in new[]
                 {
                     nameof(Hour), nameof(Minute), nameof(DayOfMonth), nameof(IntervalHours), nameof(IntervalMinutes),
                     nameof(Monday), nameof(Tuesday), nameof(Wednesday), nameof(Thursday),
                     nameof(Friday), nameof(Saturday), nameof(Sunday)
                 })
            Raise(name);
    }

    private ScheduleSpec CurrentSpec() => new(
        _mode, _hour, _minute,
        Enum.GetValues<DayOfWeek>().Where(d => _days[(int)d]).ToHashSet(),
        _dayOfMonth, _intervalHours, _intervalMinutes, _cron);

    /// <summary>Regenera el cron a partir de los controles. Con un horario incompleto conserva el anterior.</summary>
    private void Rebuild()
    {
        if (_loading || _mode == ScheduleMode.Custom) return;

        var cron = ScheduleBuilder.ToCron(CurrentSpec());
        if (cron is not null && cron != _cron)
        {
            _cron = cron;
            Raise(nameof(Cron));
        }

        RaiseSummary();
        _changed();
    }

    private void RaiseSummary()
    {
        Raise(nameof(Summary));
        Raise(nameof(CronText));
        Raise(nameof(NoDays));
    }

    private void RaiseModeFlags()
    {
        foreach (var name in new[]
                 {
                     nameof(Mode), nameof(ShowTime), nameof(ShowDays), nameof(ShowHourInterval),
                     nameof(ShowMinuteInterval), nameof(ShowMonthDay), nameof(ShowCron), nameof(ShowCronText)
                 })
            Raise(name);
    }
}
