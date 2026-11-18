using System.Globalization;
using Microsoft.Extensions.Logging;
using SqlBackupN.Core.Abstractions;
using SqlBackupN.Core.Domain;

namespace SqlBackupN.Core.Services;

/// <summary>Reglas puras para detectar que un job lleva demasiado tiempo sin ejecutarse.</summary>
public static class MissedRunChecker
{
    /// <summary>
    /// <c>true</c> si el job tiene activado el aviso, hay webhook y han pasado al menos las horas indicadas desde su última
    /// ejecución (o desde que se creó, si nunca se ejecutó).
    /// </summary>
    public static bool IsOverdue(BackupJob job, DateTime? lastRunUtc, DateTime nowUtc)
    {
        var n = job.Notifications;
        if (n.MissedRunHours <= 0 || !job.Enabled || string.IsNullOrWhiteSpace(n.WebhookUrl)) return false;
        if (!job.Steps.Any(s => s.Enabled)) return false;
        return nowUtc - (lastRunUtc ?? job.CreatedUtc) >= TimeSpan.FromHours(n.MissedRunHours);
    }

    /// <summary>Texto del aviso de un job que no se ha ejecutado a tiempo.</summary>
    public static string BuildMessage(BackupJob job, DateTime? lastRunUtc, DateTime nowUtc, string machine)
    {
        var since = lastRunUtc ?? job.CreatedUtc;
        var hours = (int)Math.Floor((nowUtc - since).TotalHours);
        var last = lastRunUtc is { } l
            ? $"última ejecución: {l.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)}"
            : "nunca se ha ejecutado";
        return $"[AVISO] SQL Backup N - {job.Name} lleva {hours} h sin ejecutarse ({last}; límite: {job.Notifications.MissedRunHours} h) en {machine}";
    }
}

/// <summary>
/// Revisa los jobs que piden aviso por falta de ejecución y avisa una sola vez por cada «última ejecución»: si el
/// job vuelve a ejecutarse y después vuelve a quedarse atrás, avisa de nuevo. Si el envío falla, reintenta al cabo de una hora.
/// Solo detecta el problema mientras el servicio está en marcha.
/// </summary>
public sealed class MissedRunMonitor(IHistoryStore history, INotifier notifier, ILogger<MissedRunMonitor> logger)
{
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);
    private readonly Dictionary<Guid, DateTime> _alertedFor = [];
    private readonly Dictionary<Guid, DateTime> _retryAfter = [];

    /// <summary>Revisa un job y, si corresponde, envía el aviso.</summary>
    public async Task CheckAsync(BackupJob job, DateTime nowUtc, CancellationToken ct)
    {
        if (job.Notifications.MissedRunHours <= 0) return;

        DateTime? last = null;
        foreach (var step in job.Steps)
        {
            var run = await history.GetLastRunUtcAsync(job.Id, step.Id, ct);
            if (run is { } r && (last is null || r > last)) last = r;
        }

        if (!MissedRunChecker.IsOverdue(job, last, nowUtc)) return;

        var baseline = last ?? job.CreatedUtc;
        if (_alertedFor.TryGetValue(job.Id, out var alerted) && alerted == baseline) return;
        if (_retryAfter.TryGetValue(job.Id, out var retry) && nowUtc < retry) return;

        try
        {
            await notifier.SendMessageAsync(
                job.Notifications, NoticeKind.MissedRun, MissedRunChecker.BuildMessage(job, last, nowUtc, Environment.MachineName), ct);
            _alertedFor[job.Id] = baseline;
            _retryAfter.Remove(job.Id);
            logger.LogWarning("El job {Job} lleva más de {Hours} h sin ejecutarse: se envió el aviso.", job.Name, job.Notifications.MissedRunHours);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _retryAfter[job.Id] = nowUtc + RetryAfterFailure;
            logger.LogWarning(ex, "No se pudo enviar el aviso de que {Job} no se ejecuta.", job.Name);
        }
    }
}
