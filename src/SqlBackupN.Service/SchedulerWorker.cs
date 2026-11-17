using SqlBackupN.Core.Abstractions;
using SqlBackupN.Core.Services;

namespace SqlBackupN.Service;

/// <summary>
/// Bucle del servicio. En cada "tick" lee los jobs, calcula los pasos vencidos con
/// <see cref="ScheduleEvaluator"/> y los ejecuta de forma secuencial con <see cref="JobRunner"/>.
/// Un solo paso a la vez evita saturar disco y red (límite de concurrencia = 1).
/// </summary>
public sealed class SchedulerWorker(
    IJobStore jobs,
    IHistoryStore history,
    JobRunner runner,
    TimeProvider clock,
    ILogger<SchedulerWorker> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("SQL Backup N iniciado. Revisando jobs cada {Seconds} s.", TickInterval.TotalSeconds);
        using var timer = new PeriodicTimer(TickInterval);
        do
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Un error en un ciclo nunca debe tumbar el servicio.
                logger.LogError(ex, "Error durante la revisión de jobs.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var job in await jobs.LoadAsync(ct))
        {
            // Se lee la última ejecución de cada paso antes de evaluar (el evaluador es síncrono y puro).
            var lastRuns = new Dictionary<Guid, DateTime?>();
            foreach (var step in job.Steps)
                lastRuns[step.Id] = await history.GetLastRunUtcAsync(job.Id, step.Id, ct);

            var due = ScheduleEvaluator.GetDueSteps(
                job, id => lastRuns.GetValueOrDefault(id), now, TimeZoneInfo.Local);

            foreach (var item in due)
            {
                logger.LogInformation("Ejecutando {Job} / {Type}{Skip}.",
                    job.Name, item.Step.Type, item.Superseded ? " (omitido)" : "");
                var record = await runner.RunStepAsync(job, item.Step, item.Superseded, ct);
                logger.LogInformation("{Job} / {Type}: {Status}.", job.Name, item.Step.Type, record.Status);
            }
        }
    }
}
