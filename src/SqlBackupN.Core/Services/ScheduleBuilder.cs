using System.Globalization;
using System.Text.RegularExpressions;

namespace SqlBackupN.Core.Services;

/// <summary>Tipos de frecuencia que se pueden elegir sin escribir una expresión cron.</summary>
public enum ScheduleMode
{
    /// <summary>Una vez al día, a una hora.</summary>
    Daily = 0,

    /// <summary>Una vez al día, solo ciertos días de la semana.</summary>
    Weekly = 1,

    /// <summary>Cada N horas (1, 2, 3, 4, 6, 8 o 12), en un minuto concreto.</summary>
    EveryHours = 2,

    /// <summary>Cada N minutos (1, 2, 3, 4, 5, 6, 10, 12, 15, 20 o 30).</summary>
    EveryMinutes = 3,

    /// <summary>Un día del mes, a una hora.</summary>
    Monthly = 4,

    /// <summary>Expresión cron escrita a mano (cualquier horario que no encaje en los anteriores).</summary>
    Custom = 5
}

/// <summary>Descripción editable de un horario, equivalente a una expresión cron de 5 campos.</summary>
/// <param name="Mode">Tipo de frecuencia.</param>
/// <param name="Hour">Hora (0-23) para Daily, Weekly y Monthly.</param>
/// <param name="Minute">Minuto (0-59): de la hora, o del minuto de inicio en EveryHours.</param>
/// <param name="Days">Días de la semana en que aplica (Weekly, EveryHours y EveryMinutes); <c>null</c> = todos.</param>
/// <param name="DayOfMonth">Día del mes (1-31) para Monthly.</param>
/// <param name="IntervalHours">Intervalo en horas para EveryHours.</param>
/// <param name="IntervalMinutes">Intervalo en minutos para EveryMinutes.</param>
/// <param name="Cron">Expresión original; es la que se usa en <see cref="ScheduleMode.Custom"/>.</param>
public sealed record ScheduleSpec(
    ScheduleMode Mode,
    int Hour = 23,
    int Minute = 0,
    IReadOnlySet<DayOfWeek>? Days = null,
    int DayOfMonth = 1,
    int IntervalHours = 1,
    int IntervalMinutes = 15,
    string Cron = "");

/// <summary>
/// Convierte entre un horario "amigable" (frecuencia, hora, días) y la expresión cron de 5 campos que guarda
/// el job, y lo describe en palabras. Son funciones puras: la expresión cron sigue siendo el único formato
/// guardado, así los <c>jobs.json</c> existentes siguen funcionando y también se pueden editar a mano.
/// </summary>
public static partial class ScheduleBuilder
{
    /// <summary>Intervalos de horas ofrecidos (los que dividen un día en partes iguales).</summary>
    public static IReadOnlyList<int> HourIntervals { get; } = [1, 2, 3, 4, 6, 8, 12];

    /// <summary>Intervalos de minutos ofrecidos (los que dividen una hora en partes iguales).</summary>
    public static IReadOnlyList<int> MinuteIntervals { get; } = [1, 2, 3, 4, 5, 6, 10, 12, 15, 20, 30];

    /// <summary>Todos los días de la semana.</summary>
    public static IReadOnlySet<DayOfWeek> EveryDay { get; } = new HashSet<DayOfWeek>(Enum.GetValues<DayOfWeek>());

    [GeneratedRegex(@"^\*/(\d{1,2})$")]
    private static partial Regex StepRegex();

    [GeneratedRegex(@"^([0-7])(?:-([0-7]))?$")]
    private static partial Regex DowTokenRegex();

    // ------------------------------------------------------------------ Construir

    /// <summary>
    /// Genera la expresión cron. Devuelve <c>null</c> si el horario está incompleto
    /// (por ejemplo, Weekly sin ningún día marcado).
    /// </summary>
    public static string? ToCron(ScheduleSpec s)
    {
        var minute = Math.Clamp(s.Minute, 0, 59).ToString(CultureInfo.InvariantCulture);
        var hour = Math.Clamp(s.Hour, 0, 23).ToString(CultureInfo.InvariantCulture);
        var days = DowField(s.Days ?? EveryDay);

        switch (s.Mode)
        {
            case ScheduleMode.Daily:
                return $"{minute} {hour} * * *";
            case ScheduleMode.Weekly:
                return days is null ? null : $"{minute} {hour} * * {days}";
            case ScheduleMode.EveryHours:
            {
                if (days is null) return null;
                var field = s.IntervalHours <= 1 ? "*" : $"*/{s.IntervalHours}";
                return $"{minute} {field} * * {days}";
            }
            case ScheduleMode.EveryMinutes:
            {
                if (days is null) return null;
                var field = s.IntervalMinutes <= 1 ? "*" : $"*/{s.IntervalMinutes}";
                return $"{field} * * * {days}";
            }
            case ScheduleMode.Monthly:
                return $"{minute} {hour} {Math.Clamp(s.DayOfMonth, 1, 31)} * *";
            default:
                return s.Cron;
        }
    }

    // ------------------------------------------------------------------ Interpretar

    /// <summary>
    /// Interpreta una expresión cron. Si encaja en una de las frecuencias amigables devuelve su descripción;
    /// si no, devuelve <see cref="ScheduleMode.Custom"/> con la expresión original. Nunca lanza excepciones.
    /// </summary>
    public static ScheduleSpec Parse(string? cron)
    {
        var original = cron?.Trim() ?? string.Empty;
        var custom = new ScheduleSpec(ScheduleMode.Custom, Cron: original);

        var f = original.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (f.Length != 5 || f[3] != "*") return custom;

        var days = ParseDow(f[4]);
        if (days is null) return custom;
        var allDays = days.Count == 7;

        // Una vez al mes: "m h d * *"
        if (IsInt(f[2], 1, 31, out var dom))
        {
            return IsInt(f[0], 0, 59, out var mm) && IsInt(f[1], 0, 23, out var hh) && f[4] == "*"
                ? new ScheduleSpec(ScheduleMode.Monthly, hh, mm, DayOfMonth: dom, Cron: original)
                : custom;
        }
        if (f[2] != "*") return custom;

        // Una vez al día (con o sin días concretos): "m h * * dow"
        if (IsInt(f[0], 0, 59, out var minute) && IsInt(f[1], 0, 23, out var hour))
        {
            return new ScheduleSpec(allDays ? ScheduleMode.Daily : ScheduleMode.Weekly, hour, minute, days, Cron: original);
        }

        // Cada N minutos: "*/N * * * dow" (o "* * * * dow")
        if (f[1] == "*")
        {
            if (f[0] == "*")
                return new ScheduleSpec(ScheduleMode.EveryMinutes, Days: days, IntervalMinutes: 1, Cron: original);
            if (StepRegex().Match(f[0]) is { Success: true } m1 &&
                int.Parse(m1.Groups[1].Value, CultureInfo.InvariantCulture) is var n1 && MinuteIntervals.Contains(n1))
                return new ScheduleSpec(ScheduleMode.EveryMinutes, Days: days, IntervalMinutes: n1, Cron: original);

            // Cada hora: "m * * * dow"
            if (IsInt(f[0], 0, 59, out var m2))
                return new ScheduleSpec(ScheduleMode.EveryHours, Minute: m2, Days: days, IntervalHours: 1, Cron: original);
            return custom;
        }

        // Cada N horas: "m */N * * dow"
        if (IsInt(f[0], 0, 59, out var m3) &&
            StepRegex().Match(f[1]) is { Success: true } m4 &&
            int.Parse(m4.Groups[1].Value, CultureInfo.InvariantCulture) is var n2 && HourIntervals.Contains(n2))
            return new ScheduleSpec(ScheduleMode.EveryHours, Minute: m3, Days: days, IntervalHours: n2, Cron: original);

        return custom;
    }

    // ------------------------------------------------------------------ Describir

    /// <summary>Describe una expresión cron en palabras, o <c>null</c> si es personalizada.</summary>
    public static string? Describe(string? cron)
    {
        var spec = Parse(cron);
        return spec.Mode == ScheduleMode.Custom ? null : Describe(spec);
    }

    /// <summary>Describe un horario en palabras (ej.: "De lunes a viernes a las 22:00").</summary>
    public static string Describe(ScheduleSpec s)
    {
        var time = $"{Math.Clamp(s.Hour, 0, 23):00}:{Math.Clamp(s.Minute, 0, 59):00}";
        var days = s.Days ?? EveryDay;
        var daySuffix = days.Count is 7 or 0 ? string.Empty : " · " + LowerFirst(DaysText(days));

        return s.Mode switch
        {
            ScheduleMode.Daily => $"Todos los días a las {time}",
            ScheduleMode.Weekly => $"{DaysText(days)} a las {time}",
            ScheduleMode.EveryHours => (s.IntervalHours <= 1
                ? $"Cada hora, en el minuto {s.Minute:00}"
                : $"Cada {s.IntervalHours} horas, en el minuto {s.Minute:00}") + daySuffix,
            ScheduleMode.EveryMinutes => (s.IntervalMinutes <= 1
                ? "Cada minuto"
                : $"Cada {s.IntervalMinutes} minutos") + daySuffix,
            ScheduleMode.Monthly => $"El día {s.DayOfMonth} de cada mes a las {time}",
            _ => s.Cron
        };
    }

    private static readonly string[] Singular =
        ["domingo", "lunes", "martes", "miércoles", "jueves", "viernes", "sábado"];

    /// <summary>Texto de un conjunto de días: "De lunes a viernes", "Los domingos", "Los lunes, miércoles y viernes"…</summary>
    private static string DaysText(IReadOnlySet<DayOfWeek> days)
    {
        if (days.Count == 7) return "Todos los días";

        // Orden de lectura: lunes … domingo.
        var ordered = Enumerable.Range(0, 7)
            .Select(i => (DayOfWeek)((i + 1) % 7))
            .Where(days.Contains)
            .ToList();
        if (ordered.Count == 0) return "Ningún día";
        if (ordered.Count == 1) return "Los " + Plural(ordered[0]);
        if (ordered.Count == 2 && days.Contains(DayOfWeek.Saturday) && days.Contains(DayOfWeek.Sunday))
            return "Sábados y domingos";

        var order = ordered.Select(d => ((int)d + 6) % 7).ToList();   // lunes = 0 … domingo = 6
        var consecutive = order.Zip(order.Skip(1), (a, b) => b - a).All(x => x == 1);
        if (consecutive && ordered.Count >= 3)
            return $"De {Singular[(int)ordered[0]]} a {Singular[(int)ordered[^1]]}";

        var names = ordered.Select(d => Singular[(int)d]).ToList();
        return "Los " + string.Join(", ", names.Take(names.Count - 1).Select((n, i) => i == 0 ? Pluralize(n) : n))
                      + " y " + names[^1];
    }

    private static string Plural(DayOfWeek d) => Pluralize(Singular[(int)d]);

    private static string Pluralize(string day) => day.EndsWith('s') ? day : day + "s";

    private static string LowerFirst(string text) =>
        text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];

    // ------------------------------------------------------------------ Auxiliares

    private static bool IsInt(string text, int min, int max, out int value)
    {
        value = 0;
        return text.All(char.IsAsciiDigit) && text.Length is > 0 and <= 2 &&
               int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) &&
               value >= min && value <= max;
    }

    /// <summary>Interpreta el campo de días de la semana de cron (0-7, listas y rangos); <c>null</c> si no es soportado.</summary>
    private static HashSet<DayOfWeek>? ParseDow(string field)
    {
        if (field == "*") return [.. EveryDay];

        var result = new HashSet<DayOfWeek>();
        foreach (var token in field.Split(','))
        {
            var m = DowTokenRegex().Match(token);
            if (!m.Success) return null;

            var a = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var b = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : a;
            if (a > b) return null;
            for (var d = a; d <= b; d++) result.Add((DayOfWeek)(d % 7));   // 7 también es domingo
        }
        return result.Count == 0 ? null : result;
    }

    /// <summary>Compacta un conjunto de días al campo cron ("*", "1-5", "0,6"); <c>null</c> si está vacío.</summary>
    private static string? DowField(IReadOnlySet<DayOfWeek> days)
    {
        if (days.Count == 0) return null;
        if (days.Count == 7) return "*";

        var values = days.Select(d => (int)d).Order().ToList();
        var parts = new List<string>();
        for (var i = 0; i < values.Count;)
        {
            var j = i;
            while (j + 1 < values.Count && values[j + 1] == values[j] + 1) j++;
            var length = j - i + 1;
            if (length >= 3) parts.Add($"{values[i]}-{values[j]}");
            else parts.AddRange(values.Skip(i).Take(length).Select(v => v.ToString(CultureInfo.InvariantCulture)));
            i = j + 1;
        }
        return string.Join(',', parts);
    }
}
