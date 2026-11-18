using System.Globalization;
using SqlBackupN.Core.Domain;

namespace SqlBackupN.Core.Services;

/// <summary>
/// Reconstruye un registro de ejecución a partir del resumen guardado, para las ejecuciones hechas antes de que
/// existiera el registro paso a paso (<see cref="JobRunRecord.Log"/> vacío). Es una función pura.
/// </summary>
public static class RunLogBuilder
{
    /// <summary>
    /// Devuelve las líneas del registro de una ejecución: el original si lo tiene; si no, uno reconstruido con lo que
    /// sí se guardó (resultado, archivo, tamaño, duración, advertencias y errores de cada base).
    /// </summary>
    /// <param name="record">Ejecución del historial.</param>
    /// <param name="reconstructed"><c>true</c> si el registro devuelto no es el original sino reconstruido.</param>
    public static IReadOnlyList<RunLogEntry> For(JobRunRecord record, out bool reconstructed)
    {
        reconstructed = record.Log.Count == 0;
        return reconstructed ? Reconstruct(record) : record.Log;
    }

    private static List<RunLogEntry> Reconstruct(JobRunRecord r)
    {
        var lines = new List<RunLogEntry>
        {
            Line(r.StartedUtc, RunLogLevel.Info, null,
                "Registro reconstruido del resumen guardado: esta ejecución es anterior al registro paso a paso."),
            Line(r.StartedUtc, RunLogLevel.Info, null,
                $"Inicio del backup {Describe(r.Type)} del job '{r.JobName}'.")
        };

        foreach (var db in r.Results)
        {
            var database = db.Database == "(job)" ? null : db.Database;
            if (db.Status == RunStatus.Failed)
            {
                lines.Add(Line(r.StartedUtc, RunLogLevel.Error, database, $"Falló el backup: {db.Error}"));
                continue;
            }

            var size = string.Create(CultureInfo.InvariantCulture, $"{db.SizeBytes / 1048576.0:F1} MB");
            var where = string.IsNullOrEmpty(db.FilePath) ? string.Empty : $": {db.FilePath}";
            lines.Add(Line(r.StartedUtc, RunLogLevel.Info, database,
                $"Backup creado{where} ({size}, {Format(db.Duration)})."));
            foreach (var warning in db.Warnings)
                lines.Add(Line(r.StartedUtc, RunLogLevel.Warning, database, warning));
        }

        lines.Add(Line(r.FinishedUtc, r.Status switch
        {
            RunStatus.Failed => RunLogLevel.Error,
            RunStatus.Warning => RunLogLevel.Warning,
            _ => RunLogLevel.Info
        }, null, $"Terminado: {Describe(r.Status)} en {Format(r.FinishedUtc - r.StartedUtc)}."));
        return lines;
    }

    private static RunLogEntry Line(DateTime utc, RunLogLevel level, string? database, string message) =>
        new() { TimeUtc = utc, Level = level, Database = database, Message = message };

    private static string Format(TimeSpan t) => t.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

    private static string Describe(BackupType type) => type switch
    {
        BackupType.Full => "completo",
        BackupType.Differential => "diferencial",
        _ => "de log de transacciones"
    };

    private static string Describe(RunStatus status) => status switch
    {
        RunStatus.Succeeded => "correcto",
        RunStatus.Warning => "con advertencias",
        RunStatus.Failed => "con fallos",
        _ => "omitido"
    };
}
