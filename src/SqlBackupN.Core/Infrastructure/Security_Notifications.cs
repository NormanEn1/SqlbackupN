using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using SqlBackupN.Core.Abstractions;
using SqlBackupN.Core.Domain;

namespace SqlBackupN.Core.Infrastructure;

/// <summary>
/// Protege contraseñas con DPAPI en el ámbito de la <b>máquina</b>, para que el servicio
/// (que corre con otra cuenta que la GUI) pueda descifrarlas. Solo Windows.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "SqlBackupN.v1"u8.ToArray();

    /// <inheritdoc />
    public string Protect(string plainText)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI solo está disponible en Windows.");
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plainText), Entropy, DataProtectionScope.LocalMachine);
        return Convert.ToBase64String(data);
    }

    /// <inheritdoc />
    public string Unprotect(string protectedText)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI solo está disponible en Windows.");
        var data = ProtectedData.Unprotect(Convert.FromBase64String(protectedText), Entropy, DataProtectionScope.LocalMachine);
        return Encoding.UTF8.GetString(data);
    }
}

/// <summary>Notificador que no hace nada (valor por defecto cuando no hay canal configurado).</summary>
public sealed class NullNotifier : INotifier
{
    /// <inheritdoc />
    public Task NotifyAsync(BackupJob job, JobRunRecord record, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Envía un POST JSON <c>{"text": "..."}</c> al webhook del job. Es compatible con
/// Slack y con los webhooks entrantes de Teams; otros canales pueden usar un puente.
/// </summary>
public sealed class WebhookNotifier : INotifier
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <inheritdoc />
    public async Task NotifyAsync(BackupJob job, JobRunRecord record, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(job.Notifications.WebhookUrl)) return;
        var response = await Http.PostAsJsonAsync(job.Notifications.WebhookUrl, new { text = BuildMessage(record) }, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Arma el texto del aviso: estado, job y detalle de las bases con problemas.</summary>
    public static string BuildMessage(JobRunRecord record)
    {
        var icon = record.Status switch
        {
            RunStatus.Succeeded => "OK",
            RunStatus.Warning => "ADVERTENCIA",
            RunStatus.Failed => "FALLO",
            _ => "OMITIDO"
        };

        var sb = new StringBuilder();
        sb.Append($"[{icon}] SQL Backup N - {record.JobName} ({record.Type}) en {Environment.MachineName}");
        foreach (var r in record.Results.Where(r => r.Status is RunStatus.Failed or RunStatus.Warning))
        {
            sb.Append($"\n- {r.Database}: {r.Error ?? string.Join("; ", r.Warnings)}");
        }
        return sb.ToString();
    }
}
