using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SqlBackupN.Core.Abstractions;
using SqlBackupN.Core.Domain;

namespace SqlBackupN.Core.Services;

/// <summary>
/// Orquesta la ejecución de un paso de un job: resuelve las bases, hace el backup en el
/// destino primario (siempre una carpeta), lo verifica, lo copia a los demás destinos (carpetas o
/// Google Drive), aplica retención, guarda el historial y avisa. Una falla en una base no detiene a las demás.
/// </summary>
public sealed class JobRunner(
    IBackupExecutor executor,
    IFileStore files,
    IRemoteStorage remote,
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
            if (job.Destinations[0].Kind != DestinationKind.Folder)
                throw new InvalidOperationException(
                    "El destino principal debe ser una carpeta (SQL Server escribe allí el backup).");

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
                ct.ThrowIfCancellationRequested();
                if (index > 0) await CopyAsync(destination, primaryPath, relative, result, ct);
                await ApplyRetentionAsync(job, type, destination, relative, result, ct);
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

    private async Task CopyAsync(
        DestinationConfig destination, string source, string relative, DatabaseBackupResult result, CancellationToken ct)
    {
        try
        {
            if (destination.Kind == DestinationKind.GoogleDrive)
            {
                await remote.UploadAsync(destination, source, relative, ct);
            }
            else
            {
                var target = Path.Combine(destination.Path, relative);
                files.EnsureDirectory(Path.GetDirectoryName(target)!);
                files.Copy(source, target);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AddWarning(result, $"No se pudo copiar a '{destination.Name}': {ex.Message}");
        }
    }

    private async Task ApplyRetentionAsync(
        BackupJob job, BackupType type, DestinationConfig destination, string relative,
        DatabaseBackupResult result, CancellationToken ct)
    {
        try
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var extension = type.FileExtension();

            if (destination.Kind == DestinationKind.GoogleDrive)
            {
                var directory = Path.GetDirectoryName(relative) ?? string.Empty;
                var remoteFiles = await remote.ListAsync(destination, directory, extension, ct);
                // El planificador solo necesita fecha y tamaño; la "ruta" lleva el identificador remoto.
                var known = remoteFiles.ToDictionary(f => f.Id);
                var candidates = remoteFiles.Select(f => new StoredFile(f.Id, f.CreatedUtc, f.SizeBytes)).ToList();
                foreach (var expired in RetentionPlanner.SelectFilesToDelete(candidates, job.Retention, now))
                    await remote.DeleteAsync(destination, known[expired.Path], ct);
            }
            else
            {
                var directory = Path.GetDirectoryName(Path.Combine(destination.Path, relative))!;
                var existing = files.List(directory, "*" + extension);
                foreach (var file in RetentionPlanner.SelectFilesToDelete(existing, job.Retention, now))
                    files.Delete(file.Path);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
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
