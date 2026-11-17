using Cronos;
using SqlBackupN.Core.Domain;

namespace SqlBackupN.Core.Services;

/// <summary>Un paso que ya le toca ejecutarse.</summary>
/// <param name="Step">El paso.</param>
/// <param name="Superseded">
/// <c>true</c> si otro paso más completo se ejecuta ahora (un diferencial queda reemplazado
/// por un completo); se registra como omitido para no repetirlo enseguida.
/// </param>
public sealed record DueStep(BackupStep Step, bool Superseded);

/// <summary>
/// Calcula qué pasos de un job están vencidos. Es una función pura: no usa reloj ni disco,
/// recibe "ahora" y la última ejecución de cada paso. Así también recupera ejecuciones
/// perdidas (equipo apagado): si el próximo disparo posterior a la última ejecución ya pasó, está vencido.
/// </summary>
public static class ScheduleEvaluator
{
    /// <summary>Devuelve los pasos vencidos ordenados: Full, Differential, TransactionLog.</summary>
    /// <param name="job">Job a evaluar.</param>
    /// <param name="lastRunUtc">Fin (UTC) de la última ejecución de un paso, o null si nunca corrió.</param>
    /// <param name="nowUtc">Hora actual en UTC.</param>
    /// <param name="zone">Zona horaria en la que se interpreta el cron.</param>
    public static IReadOnlyList<DueStep> GetDueSteps(
        BackupJob job, Func<Guid, DateTime?> lastRunUtc, DateTime nowUtc, TimeZoneInfo zone)
    {
        if (!job.Enabled) return [];

        var due = new List<BackupStep>();
        foreach (var step in job.Steps.Where(s => s.Enabled))
        {
            var cron = CronExpression.Parse(step.Cron, CronFormat.Standard);
            var baseline = DateTime.SpecifyKind(lastRunUtc(step.Id) ?? job.CreatedUtc, DateTimeKind.Utc);
            var next = cron.GetNextOccurrence(baseline, zone);
            if (next is not null && next.Value <= nowUtc) due.Add(step);
        }

        var fullDue = due.Any(s => s.Type == BackupType.Full);
        return due
            .OrderBy(s => (int)s.Type)
            .Select(s => new DueStep(s, Superseded: fullDue && s.Type == BackupType.Differential))
            .ToList();
    }
}
