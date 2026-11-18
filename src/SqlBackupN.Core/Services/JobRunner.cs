using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using SqlBackupN.Core.Abstractions;
using SqlBackupN.Core.Domain;

namespace SqlBackupN.Core.Services;

/// <summary>
/// Orquesta la ejecución de un paso de un job: resuelve las bases, hace el backup en el
/// destino primario (siempre una carpeta), lo verifica, lo copia a los demás destinos (carpetas o
/// Google Drive), aplica retención, guarda el historial y avisa. Una falla en una base no detiene a las demás.
/// Cada paso queda en el registro de la ejecución (<see cref="JobRunRecord.Log"/>) y se informa en vivo
/// a quien lo pida mediante <see cref="IProgress{T}"/>.
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
    /// <param name="progress">
    /// Recibe cada línea del registro en cuanto ocurre (opcional). Un fallo del receptor nunca interrumpe el backup.
    /// </param>
    public async Task<JobRunRecord> RunStepAsync(
        BackupJob job, BackupStep step, bool superseded, CancellationToken ct, IProgress<RunLogEntry>? progress = null)
    {
        var record = new JobRunRecord
        {
            JobId = job.Id,
            JobName = job.Name,
            StepId = step.Id,
            Type = step.Type,
            StartedUtc = clock.GetUtcNow().UtcDateTime
        };
        var log = new RunLog(record, progress, clock);
        log.Info(null, $"Inicio del backup {Describe(step.Type)} del job '{job.Name}'.");

        if (superseded)
        {
            record.Status = RunStatus.Skipped;
            log.Info(null, "Paso omitido: lo reemplaza un backup completo.");
        }
        else
        {
            try
            {
                log.Info(null, $"Conectando con {job.Connection.Server} y listando las bases de datos…");
                var available = await executor.ListDatabasesAsync(job.Connection, ct);
                var databases = DatabaseResolver.Resolve(job.Databases, available).ToList();
                if (databases.Count == 0) log.Warning(null, "Ninguna base de datos coincide con la selección del job.");
                else log.Info(null, $"Bases a respaldar ({databases.Count}): {string.Join(", ", databases)}.");

                foreach (var db in databases)
                {
                    ct.ThrowIfCancellationRequested();
                    record.Results.Add(await BackupOneAsync(job, step.Type, db, log, ct));
                }
                record.Status = JobRunRecord.Aggregate(record.Results);
            }
            catch (OperationCanceledException)
            {
                log.Warning(null, "Ejecución cancelada.");
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falló el job {Job} antes de respaldar las bases.", job.Name);
                log.Error(null, $"Falló antes de respaldar las bases: {ex.Message}");
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
        log.Add(LevelOf(record.Status), null,
            $"Terminado: {Describe(record.Status)} en {FormatDuration(record.FinishedUtc - record.StartedUtc)}.");
        // El aviso va antes de guardar: así el historial también dice si se pudo entregar.
        await TryNotifyAsync(job, record, log, ct);
        await history.AppendAsync(record, ct);
        return record;
    }

    private async Task<DatabaseBackupResult> BackupOneAsync(
        BackupJob job, BackupType type, string database, RunLog log, CancellationToken ct)
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
            var rendered = PathTemplate.Render(
                job.Options.PathTemplate,
                new TemplateContext(job.Connection.Server, database, type, localNow));
            // Con ZipOutput el archivo final (el que se copia, se sube y se somete a retención) es un .zip.
            var extension = job.Options.ZipOutput ? ZipExtension : type.FileExtension();
            var relative = rendered + extension;

            // El destino primario lo escribe SQL Server directamente: primero el .bak y, si se pide, luego el .zip.
            var primaryPath = Path.Combine(job.Destinations[0].Path, relative);
            var backupPath = Path.Combine(job.Destinations[0].Path, rendered + type.FileExtension());
            files.EnsureDirectory(Path.GetDirectoryName(primaryPath)!);

            log.Info(database, $"Haciendo el backup {Describe(type)} en {backupPath}…");
            await executor.BackupAsync(
                job.Connection, new BackupRequest(database, type, backupPath, job.Options), ct);
            log.Info(database, "Backup creado.");

            if (job.Options.VerifyAfterBackup)
            {
                log.Info(database, "Verificando el archivo (RESTORE VERIFYONLY)…");
                await executor.VerifyAsync(
                    job.Connection, backupPath, job.Options.Checksum, job.Options.CommandTimeoutSeconds, ct);
                log.Info(database, "Verificación correcta.");
            }

            // Con carpeta principal temporal hace falta otro destino final: si no, se borraría el único ejemplar.
            var temporary = job.Options.TemporaryPrimary && job.Destinations.Count > 1;

            if (job.Options.ZipOutput) CompressBackup(backupPath, primaryPath, database, log);

            result.FilePath = primaryPath;
            result.SizeBytes = files.GetSize(primaryPath);
            result.Status = RunStatus.Succeeded;
            log.Info(database, $"Tamaño del archivo: {FormatSize(result.SizeBytes)}.");
            // El .bak se borra ya, salvo en carpeta temporal: ahí se conserva hasta que el zip esté completo en los destinos finales.
            if (job.Options.ZipOutput && !temporary) DeleteOriginal(backupPath, result, log);

            // A partir de aquí el backup ya existe: los fallos son advertencias, no errores.
            var allCopied = true;
            foreach (var (destination, index) in job.Destinations.Select((d, i) => (d, i)))
            {
                ct.ThrowIfCancellationRequested();
                if (index > 0) allCopied &= await CopyAsync(destination, primaryPath, relative, result, log, ct);
                // La carpeta temporal se vacía al terminar: aplicarle retención no tiene sentido.
                if (index > 0 || !temporary)
                    await ApplyRetentionAsync(job, extension, destination, relative, result, log, ct);
            }

            if (temporary) CleanTemporaryPrimary(backupPath, primaryPath, allCopied, result, log);
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
            log.Error(database, $"Falló el backup: {ex.Message}");
        }

        result.Duration = watch.Elapsed;
        log.Add(LevelOf(result.Status), database,
            $"Base terminada: {Describe(result.Status)} en {FormatDuration(result.Duration)}.");
        return result;
    }

    /// <summary>Copia el archivo a un destino. Devuelve <c>true</c> solo si quedó completo (verificado).</summary>
    private async Task<bool> CopyAsync(
        DestinationConfig destination, string source, string relative, DatabaseBackupResult result,
        RunLog log, CancellationToken ct)
    {
        try
        {
            log.Info(result.Database, $"Copiando a '{destination.Name}' ({DescribeKind(destination.Kind)})…");
            if (destination.Kind == DestinationKind.GoogleDrive)
            {
                await remote.UploadAsync(destination, source, relative, ct);
            }
            else
            {
                var target = Path.Combine(destination.Path, relative);
                files.EnsureDirectory(Path.GetDirectoryName(target)!);
                files.Copy(source, target);
                // Drive ya compara la suma MD5 al subir; en una carpeta se comprueba que el tamaño coincida.
                if (files.GetSize(target) != files.GetSize(source))
                    throw new IOException("el archivo copiado no tiene el mismo tamaño que el original");
            }
            log.Info(result.Database, $"Copia completa en '{destination.Name}'.");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AddWarning(result, log, $"No se pudo copiar a '{destination.Name}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Carpeta principal temporal: con todas las copias completas borra de ella el archivo final (y el .bak); si alguna copia
    /// falló no borra nada, para no perder el único ejemplar completo.
    /// </summary>
    private void CleanTemporaryPrimary(
        string backupPath, string primaryPath, bool allCopied, DatabaseBackupResult result, RunLog log)
    {
        if (!allCopied)
        {
            log.Warning(result.Database,
                "La carpeta principal (temporal) no se limpió: alguna copia no se completó, así que el backup sigue allí.");
            return;
        }

        log.Info(result.Database, "Copias completas en los destinos finales: se borra el backup de la carpeta principal (temporal)…");
        foreach (var path in new[] { primaryPath, backupPath }.Distinct())
        {
            try
            {
                files.Delete(path);
                log.Info(result.Database, $"Borrado {path}.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AddWarning(result, log, $"No se pudo borrar {path} de la carpeta principal: {ex.Message}");
            }
        }
    }

    /// <summary>Comprime el .bak ya verificado. Si falla, el .bak se conserva y el error explica dónde quedó.</summary>
    private void CompressBackup(string backupPath, string zipPath, string database, RunLog log)
    {
        log.Info(database, $"Comprimiendo en {zipPath}…");
        try
        {
            files.CreateZip(backupPath, zipPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new IOException(
                $"No se pudo comprimir el backup (el archivo .bak se conservó en {backupPath}): {ex.Message}", ex);
        }
        log.Info(database, "Zip creado.");
    }

    /// <summary>Borra el .bak ya comprimido; si no se puede, es una advertencia (el zip existe).</summary>
    private void DeleteOriginal(string backupPath, DatabaseBackupResult result, RunLog log)
    {
        try
        {
            files.Delete(backupPath);
            log.Info(result.Database, "Se borró el .bak de la carpeta principal (queda solo el zip).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AddWarning(result, log, $"No se pudo borrar el .bak {backupPath}: {ex.Message}");
        }
    }

    private async Task ApplyRetentionAsync(
        BackupJob job, string extension, DestinationConfig destination, string relative,
        DatabaseBackupResult result, RunLog log, CancellationToken ct)
    {
        try
        {
            log.Info(result.Database, $"Aplicando la retención en '{destination.Name}'…");
            var now = clock.GetUtcNow().UtcDateTime;
            var deleted = 0;

            if (destination.Kind == DestinationKind.GoogleDrive)
            {
                var directory = Path.GetDirectoryName(relative) ?? string.Empty;
                var remoteFiles = await remote.ListAsync(destination, directory, extension, ct);
                // El planificador solo necesita fecha y tamaño; la "ruta" lleva el identificador remoto.
                var known = remoteFiles.ToDictionary(f => f.Id);
                var candidates = remoteFiles.Select(f => new StoredFile(f.Id, f.CreatedUtc, f.SizeBytes)).ToList();
                foreach (var expired in RetentionPlanner.SelectFilesToDelete(candidates, job.Retention, now))
                {
                    await remote.DeleteAsync(destination, known[expired.Path], ct);
                    deleted++;
                }
            }
            else
            {
                var directory = Path.GetDirectoryName(Path.Combine(destination.Path, relative))!;
                var existing = files.List(directory, "*" + extension);
                foreach (var file in RetentionPlanner.SelectFilesToDelete(existing, job.Retention, now))
                {
                    files.Delete(file.Path);
                    deleted++;
                }
            }

            log.Info(result.Database, deleted == 0
                ? $"Retención en '{destination.Name}': no hay archivos para borrar."
                : $"Retención en '{destination.Name}': {deleted} archivo(s) borrado(s).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AddWarning(result, log, $"No se pudo aplicar la retención en '{destination.Name}': {ex.Message}");
        }
    }

    private void AddWarning(DatabaseBackupResult result, RunLog log, string message)
    {
        logger.LogWarning("{Message}", message);
        result.Warnings.Add(message);
        log.Warning(result.Database, message);
        if (result.Status == RunStatus.Succeeded) result.Status = RunStatus.Warning;
    }

    private async Task TryNotifyAsync(BackupJob job, JobRunRecord record, RunLog log, CancellationToken ct)
    {
        if (!job.Notifications.ShouldNotify(record.Status)) return;
        try
        {
            await notifier.NotifyAsync(job, record, ct);
            log.Info(null, "Aviso enviado.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "No se pudo enviar la notificación del job {Job}.", job.Name);
            log.Warning(null, $"No se pudo enviar el aviso: {ex.Message}");
        }
    }

    private const string ZipExtension = ".zip";

    private static RunLogLevel LevelOf(RunStatus status) => status switch
    {
        RunStatus.Failed => RunLogLevel.Error,
        RunStatus.Warning => RunLogLevel.Warning,
        _ => RunLogLevel.Info
    };

    private static string Describe(BackupType type) => type switch
    {
        BackupType.Full => "completo",
        BackupType.Differential => "diferencial",
        BackupType.TransactionLog => "de log de transacciones",
        _ => type.ToString()
    };

    private static string Describe(RunStatus status) => status switch
    {
        RunStatus.Succeeded => "correcto",
        RunStatus.Warning => "con advertencias",
        RunStatus.Failed => "con fallos",
        _ => "omitido"
    };

    private static string DescribeKind(DestinationKind kind) =>
        kind == DestinationKind.GoogleDrive ? "Google Drive" : "carpeta";

    private static string FormatSize(long bytes) =>
        string.Create(CultureInfo.InvariantCulture, $"{bytes / 1048576.0:F1} MB");

    private static string FormatDuration(TimeSpan duration) =>
        duration.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

    /// <summary>Agrega líneas al registro de la ejecución y las informa a quien las esté siguiendo.</summary>
    private sealed class RunLog(JobRunRecord record, IProgress<RunLogEntry>? progress, TimeProvider clock)
    {
        public void Info(string? database, string message) => Add(RunLogLevel.Info, database, message);

        public void Warning(string? database, string message) => Add(RunLogLevel.Warning, database, message);

        public void Error(string? database, string message) => Add(RunLogLevel.Error, database, message);

        public void Add(RunLogLevel level, string? database, string message)
        {
            var entry = new RunLogEntry
            {
                TimeUtc = clock.GetUtcNow().UtcDateTime,
                Level = level,
                Database = database,
                Message = message
            };
            record.Log.Add(entry);
            try
            {
                progress?.Report(entry);
            }
            catch (Exception)
            {
                // Quien sigue el registro no puede interrumpir un backup.
            }
        }
    }
}
