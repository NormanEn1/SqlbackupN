using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using SqlBackupN.Core.Domain;

namespace SqlBackupN.Core.Services;

/// <summary>
/// Arma el cuerpo JSON que se envía al webhook según el formato elegido (texto, JSON completo o plantilla propia).
/// Funciones puras: no tocan la red ni el reloj.
/// </summary>
public static partial class WebhookPayload
{
    /// <summary>Valores que se pueden usar en una plantilla propia, escritos entre llaves.</summary>
    public static readonly IReadOnlyList<string> Placeholders =
        ["event", "status", "job", "jobId", "type", "server", "machine", "message", "summary", "started", "finished", "duration"];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex PlaceholderRegex();

    /// <summary>Cuerpo del aviso de una ejecución de backup.</summary>
    public static string ForRun(BackupJob job, JobRunRecord record, string machine)
    {
        var message = NoticeText.ForRun(record, machine);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["event"] = "backup.run",
            ["status"] = StatusName(record.Status),
            ["job"] = record.JobName,
            ["jobId"] = record.JobId.ToString(),
            ["type"] = record.Type.ToString(),
            ["server"] = job.Connection.Server,
            ["machine"] = machine,
            ["message"] = message,
            ["summary"] = NoticeText.Summary(record),
            ["started"] = Iso(record.StartedUtc),
            ["finished"] = Iso(record.FinishedUtc),
            ["duration"] = Seconds(record)
        };

        return job.Notifications.WebhookFormat switch
        {
            WebhookFormat.Json => JsonSerializer.Serialize(new
            {
                @event = "backup.run",
                status = values["status"],
                job = record.JobName,
                jobId = record.JobId,
                type = record.Type.ToString(),
                server = job.Connection.Server,
                machine,
                startedUtc = record.StartedUtc,
                finishedUtc = record.FinishedUtc,
                durationSeconds = double.Parse(values["duration"], CultureInfo.InvariantCulture),
                message,
                databases = record.Results.Select(r => new
                {
                    name = r.Database,
                    status = StatusName(r.Status),
                    sizeBytes = r.SizeBytes,
                    durationSeconds = Math.Round(r.Duration.TotalSeconds, 1),
                    file = r.FilePath,
                    error = r.Error,
                    warnings = r.Warnings
                })
            }, Json),
            WebhookFormat.Custom when !string.IsNullOrWhiteSpace(job.Notifications.WebhookBodyTemplate) =>
                Render(job.Notifications.WebhookBodyTemplate, values),
            _ => TextBody(message)
        };
    }

    /// <summary>Cuerpo de un mensaje suelto (aviso de prueba, job que no se ejecuta).</summary>
    public static string ForMessage(NotificationSettings settings, NoticeKind kind, string message, string machine)
    {
        var (eventName, status) = kind switch
        {
            NoticeKind.Test => ("test", "info"),
            NoticeKind.MissedRun => ("backup.missed-run", "warning"),
            _ => ("message", "info")
        };
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["event"] = eventName,
            ["status"] = status,
            ["job"] = string.Empty,
            ["jobId"] = string.Empty,
            ["type"] = string.Empty,
            ["server"] = string.Empty,
            ["machine"] = machine,
            ["message"] = message,
            ["summary"] = string.Empty,
            ["started"] = string.Empty,
            ["finished"] = string.Empty,
            ["duration"] = "0"
        };

        return settings.WebhookFormat switch
        {
            WebhookFormat.Json => JsonSerializer.Serialize(new { @event = eventName, status, machine, message }, Json),
            WebhookFormat.Custom when !string.IsNullOrWhiteSpace(settings.WebhookBodyTemplate) =>
                Render(settings.WebhookBodyTemplate, values),
            _ => TextBody(message)
        };
    }

    /// <summary>
    /// Revisa una plantilla propia: que solo use valores conocidos y que, con valores de ejemplo, produzca un JSON válido.
    /// Devuelve el motivo del error o <c>null</c> si está bien.
    /// </summary>
    public static string? ValidateTemplate(string template)
    {
        var unknown = PlaceholderRegex().Matches(template).Select(m => m.Groups[1].Value)
            .FirstOrDefault(name => !Placeholders.Contains(name, StringComparer.OrdinalIgnoreCase));
        if (unknown is not null)
            return $"La plantilla usa un valor desconocido: {{{unknown}}}. Valores: {string.Join(" ", Placeholders.Select(p => "{" + p + "}"))}.";

        var sample = Placeholders.ToDictionary(p => p, _ => "ejemplo \"con comillas\"\nlínea 2", StringComparer.OrdinalIgnoreCase);
        try
        {
            using var _ = JsonDocument.Parse(Render(template, sample));
            return null;
        }
        catch (JsonException ex)
        {
            return $"La plantilla no produce un JSON válido ({ex.Message.Split('.')[0]}). Escriba los valores entre comillas, p. ej. {{\"text\": \"{{message}}\"}}.";
        }
    }

    /// <summary>Sustituye los valores en la plantilla, ya escapados para ir dentro de un texto JSON.</summary>
    public static string Render(string template, IReadOnlyDictionary<string, string> values) =>
        PlaceholderRegex().Replace(template, m =>
            values.TryGetValue(m.Groups[1].Value, out var v)
                ? JsonEncodedText.Encode(v, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString()
                : m.Value);

    private static string TextBody(string message) => JsonSerializer.Serialize(new { text = message }, Json);

    private static string StatusName(RunStatus status) => status switch
    {
        RunStatus.Succeeded => "succeeded",
        RunStatus.Warning => "warning",
        RunStatus.Failed => "failed",
        _ => "skipped"
    };

    private static string Iso(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);

    private static string Seconds(JobRunRecord r) =>
        Math.Round((r.FinishedUtc - r.StartedUtc).TotalSeconds, 1).ToString(CultureInfo.InvariantCulture);
}
