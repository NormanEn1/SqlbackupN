using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SqlBackupN.Core.Abstractions;
using SqlBackupN.Core.Domain;

namespace SqlBackupN.Core.Services;

/// <summary>
/// Orquesta la ejecución de un paso de un job: resuelve las bases, hace el backup en el
/// destino primario, lo verifica, lo copia a los demás destinos, aplica retención,
/// guarda el historial y avisa. Una falla en una base no detiene a las demás.
/// </summary>
public sealed class JobRunner(
    IBackupExecutor executor,
    IFileStore files,
    IHistoryStore history,
    INotifier notifier,
    TimeProvider clock,
    ILogger<JobRunner> logger)
{
    /// <summary>Ejecuta un paso y devuelve (y persiste) su registro.</summary>
    /// <param name="job">Job al que pertenece.</param>
    /// <param name="step">Paso a ejecutar.</param>
    /// <param name="superseded">Si es <c>true</c> no ejecuta nada y registra el paso como omitido.</param>
    /// <param name="ct">Cancelación.</param>
    public async Task<JobRunRecord> RunStepAsync(
        BackupJob job, BackupStep step, bool superseded, CancellationToken ct)
    {
        var record = new JobRunRecord
        {
            JobId = job.Id,
            JobName = job.Name,
            StepId = step.Id,
            Type = step.Type,
            StartedUtc = clock.GetUtcNow().UtcDateTime
        };

        if (superseded)
        {
            record.Status = RunStatus.Skipped;
        }
        else
        {
            try
            {
                var available = await executor.ListDatabasesAsync(job.Connection, ct);
                foreach (var db in DatabaseResolver.Resolve(job.Databases, available))
                {
                    ct.ThrowIfCancellationRequested();
                    record.Results.Add(await BackupOneAsync(job, step.Type, db, ct));
                }
                record.Status = JobRunRecord.Aggregate(record.Results);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falló el job {Job} antes de respaldar las bases.", job.Name);
                record.Results.Add(new DatabaseBackupResult
                {
                    Database = "(job)",
                    Status = RunStatus.Failed,
                    Error = ex.Message
                });
                record.Status = RunStatus.Failed;
            }
        }

        record.FinishedUtc = clock.GetUtcNow().UtcDateTime;
        await history.AppendAsync(record, ct);
        await TryNotifyAsync(job, record, ct);
        return record;
    }

    private async Task<DatabaseBackupResult> BackupOneAsync(
        BackupJob job, BackupType type, string database, CancellationToken ct)
    {
        var result = new DatabaseBackupResult { Database = database };
        var watch = Stopwatch.StartNew();
        try
        {
            if (job.Destinations.Count == 0)
                throw new InvalidOperationException("El job no tiene destinos configurados.");

            var localNow = clock.GetLocalNow().DateTime;
            var relative = PathTemplate.Render(
                job.Options.PathTemplate,
                new TemplateContext(job.Connection.Server, database, type, localNow)) + type.FileExtension();

            // El destino primario lo escribe SQL Server directamente.
            var primaryPath = Path.Combine(job.Destinations[0].Path, relative);
            files.EnsureDirectory(Path.GetDirectoryName(primaryPath)!);

            await executor.BackupAsync(
                job.Connection, new BackupRequest(database, type, primaryPath, job.Options), ct);

            if (job.Options.VerifyAfterBackup)
            {
                await executor.VerifyAsync(
                    job.Connection, primaryPath, job.Options.Checksum, job.Options.CommandTimeoutSeconds, ct);
            }

            result.FilePath = primaryPath;
            result.SizeBytes = files.GetSize(primaryPath);
            result.Status = RunStatus.Succeeded;

            // A partir de aquí el backup ya existe: los fallos son advertencias, no errores.
            foreach (var (destination, index) in job.Destinations.Select((d, i) => (d, i)))
            {
                var target = Path.Combine(destination.Path, relative);
                if (index > 0) TryCopy(primaryPath, target, destination, result);
                ApplyRetention(job, type, destination, target, result);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falló el backup {Type} de {Database}.", type, database);
            result.Status = RunStatus.Failed;
            result.Error = ex.Message;
        }

        result.Duration = watch.Elapsed;
        return result;
    }

    private void TryCopy(string source, string target, DestinationConfig destination, DatabaseBackupResult result)
    {
        try
        {
            files.EnsureDirectory(Path.GetDirectoryName(target)!);
            files.Copy(source, target);
        }
        catch (Exception ex)
        {
            AddWarning(result, $"No se pudo copiar a '{destination.Name}': {ex.Message}");
        }
    }

    private void ApplyRetention(
        BackupJob job, BackupType type, DestinationConfig destination, string target, DatabaseBackupResult result)
    {
        try
        {
            var directory = Path.GetDirectoryName(target)!;
            var existing = files.List(directory, "*" + type.FileExtension());
            var toDelete = RetentionPlanner.SelectFilesToDelete(existing, job.Retention, clock.GetUtcNow().UtcDateTime);
            foreach (var file in toDelete) files.Delete(file.Path);
        }
        catch (Exception ex)
        {
            AddWarning(result, $"No se pudo aplicar la retención en '{destination.Name}': {ex.Message}");
        }
    }

    private void AddWarning(DatabaseBackupResult result, string message)
    {
        logger.LogWarning("{Message}", message);
        result.Warnings.Add(message);
        if (result.Status == RunStatus.Succeeded) result.Status = RunStatus.Warning;
    }

    private async Task TryNotifyAsync(BackupJob job, JobRunRecord record, CancellationToken ct)
    {
        if (!job.Notifications.ShouldNotify(record.Status)) return;
        try
        {
            await notifier.NotifyAsync(job, record, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "No se pudo enviar la notificación del job {Job}.", job.Name);
        }
    }
}
