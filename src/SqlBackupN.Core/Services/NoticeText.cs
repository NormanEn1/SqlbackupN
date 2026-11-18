using System.Globalization;
using System.Text;
using SqlBackupN.Core.Domain;

namespace SqlBackupN.Core.Services;

/// <summary>Arma el texto de los avisos. Función pura: el nombre del equipo entra como parámetro.</summary>
public static class NoticeText
{
    /// <summary>
    /// Texto del aviso de una ejecución: estado, job, tipo y servidor en la primera línea y, debajo, una línea por base con su
    /// resultado (tamaño, duración y archivo si salió bien; el error o las advertencias si no).
    /// </summary>
    public static string ForRun(JobRunRecord record, string machine)
    {
        var label = record.Status switch
        {
            RunStatus.Succeeded => "OK",
            RunStatus.Warning => "ADVERTENCIA",
            RunStatus.Failed => "FALLO",
            _ => "OMITIDO"
        };

        var sb = new StringBuilder();
        sb.Append($"[{label}] SQL Backup N - {record.JobName} ({record.Type}) en {machine}");
        foreach (var r in record.Results) sb.Append('\n').Append(ForDatabase(r));
        return sb.ToString();
    }

    /// <summary>Resumen por base de datos (una línea por base, sin la cabecera).</summary>
    public static string Summary(JobRunRecord record) =>
        string.Join('\n', record.Results.Select(ForDatabase));

    private static string ForDatabase(DatabaseBackupResult r)
    {
        var sb = new StringBuilder($"- {r.Database}: ");
        if (r.Status == RunStatus.Failed) return sb.Append($"FALLO · {r.Error}").ToString();

        var size = string.Create(CultureInfo.InvariantCulture, $"{r.SizeBytes / 1048576.0:F1} MB");
        sb.Append($"{(r.Status == RunStatus.Warning ? "ADVERTENCIA" : "correcto")} · {size} · {r.Duration:hh\\:mm\\:ss}");
        if (!string.IsNullOrEmpty(r.FilePath)) sb.Append($" · {Path.GetFileName(r.FilePath)}");
        if (r.Warnings.Count > 0) sb.Append($" · {string.Join("; ", r.Warnings)}");
        return sb.ToString();
    }
}
