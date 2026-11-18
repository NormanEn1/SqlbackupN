using System.Net;
using System.Security.Cryptography;
using System.Text;
using SqlBackupN.Core.Abstractions;
using SqlBackupN.Core.Domain;
using SqlBackupN.Core.Services;

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

    /// <inheritdoc />
    public Task SendMessageAsync(NotificationSettings settings, NoticeKind kind, string message, CancellationToken ct) =>
        Task.CompletedTask;
}

/// <summary>
/// Envía los avisos por webhook: un POST JSON con el formato elegido (texto, JSON completo o plantilla propia), con la
/// autenticación y las cabeceras configuradas. Reintenta solo los fallos que pueden pasar solos (red, tiempo de espera,
/// errores 5xx, 408 y 429): un 401 o un 404 no mejoran reintentando.
/// </summary>
public sealed class WebhookNotifier : INotifier
{
    private static readonly TimeSpan[] DefaultRetryDelays =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];

    private readonly ISecretProtector? _protector;
    private readonly HttpClient _http;
    private readonly IReadOnlyList<TimeSpan> _retryDelays;

    /// <summary>Crea el notificador. Los parámetros opcionales existen para poder probarlo sin red ni esperas.</summary>
    /// <param name="protector">Descifra el token guardado (sin él, solo funcionan los webhooks sin autenticación).</param>
    /// <param name="handler">Manejador HTTP (por defecto el normal).</param>
    /// <param name="retryDelays">Esperas antes de cada reintento (por defecto 2, 6, 15, 30 y 60 s).</param>
    public WebhookNotifier(
        ISecretProtector? protector = null, HttpMessageHandler? handler = null, IReadOnlyList<TimeSpan>? retryDelays = null)
    {
        _protector = protector;
        // El tiempo de espera lo fija cada envío (WebhookTimeoutSeconds), no el cliente.
        _http = new HttpClient(handler ?? new HttpClientHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        _retryDelays = retryDelays ?? DefaultRetryDelays;
    }

    /// <inheritdoc />
    public Task NotifyAsync(BackupJob job, JobRunRecord record, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(job.Notifications.WebhookUrl)
            ? Task.CompletedTask
            : SendAsync(job.Notifications, WebhookPayload.ForRun(job, record, Environment.MachineName), ct);

    /// <inheritdoc />
    public Task SendMessageAsync(NotificationSettings settings, NoticeKind kind, string message, CancellationToken ct) =>
        SendAsync(settings, WebhookPayload.ForMessage(settings, kind, message, Environment.MachineName), ct);

    /// <summary>Texto de un aviso de ejecución (se conserva para quien solo necesita el texto).</summary>
    public static string BuildMessage(JobRunRecord record) => NoticeText.ForRun(record, Environment.MachineName);

    private async Task SendAsync(NotificationSettings settings, string body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.WebhookUrl))
            throw new InvalidOperationException("Falta la dirección del webhook.");

        var secret = ResolveSecret(settings);
        var retries = Math.Clamp(settings.WebhookRetries, 0, 5);
        var timeout = TimeSpan.FromSeconds(settings.WebhookTimeoutSeconds is >= 1 and <= 300 ? settings.WebhookTimeoutSeconds : 15);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await PostOnceAsync(settings, body, secret, timeout, ct);
                return;
            }
            catch (Exception ex) when (IsTransient(ex) && !ct.IsCancellationRequested && attempt < retries)
            {
                await Task.Delay(_retryDelays[Math.Min(attempt, _retryDelays.Count - 1)], ct);
            }
            catch (Exception ex) when (ex is TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new HttpRequestException($"El servicio no respondió en {timeout.TotalSeconds:0} s.", ex);
            }
        }
    }

    private async Task PostOnceAsync(
        NotificationSettings settings, string body, string? secret, TimeSpan timeout, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.WebhookUrl!.Trim())
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };

        foreach (var line in settings.WebhookHeaders)
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            request.Headers.TryAddWithoutValidation(line[..colon].Trim(), line[(colon + 1)..].Trim());
        }
        switch (settings.WebhookAuth)
        {
            case WebhookAuth.Bearer:
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + secret);
                break;
            case WebhookAuth.Header:
                request.Headers.TryAddWithoutValidation(settings.WebhookAuthHeader!.Trim(), secret);
                break;
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(timeout);
        using var response = await _http.SendAsync(request, timeoutSource.Token);
        if (response.IsSuccessStatusCode) return;

        var snippet = (await response.Content.ReadAsStringAsync(timeoutSource.Token)).Trim();
        if (snippet.Length > 200) snippet = snippet[..200] + "…";
        throw new HttpRequestException(
            $"El servicio respondió {(int)response.StatusCode} {response.ReasonPhrase}" + (snippet.Length > 0 ? $": {snippet}" : string.Empty),
            null, response.StatusCode);
    }

    /// <summary>Descifra el token; falla con un motivo claro si falta o si se guardó en otro equipo.</summary>
    private string? ResolveSecret(NotificationSettings settings)
    {
        if (settings.WebhookAuth == WebhookAuth.None) return null;
        if (string.IsNullOrEmpty(settings.ProtectedWebhookSecret))
            throw new InvalidOperationException("Falta el token de autorización del webhook.");
        if (settings.WebhookAuth == WebhookAuth.Header && string.IsNullOrWhiteSpace(settings.WebhookAuthHeader))
            throw new InvalidOperationException("Falta el nombre de la cabecera de autorización.");
        if (_protector is null)
            throw new InvalidOperationException("No hay forma de descifrar el token de autorización del webhook.");
        try
        {
            return _protector.Unprotect(settings.ProtectedWebhookSecret);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "No se pudo descifrar el token del webhook (se guardó en otro equipo o con otra cuenta). Escríbalo de nuevo.", ex);
        }
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: { } code } => (int)code >= 500 || code is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests,
        HttpRequestException => true,
        TaskCanceledException => true,
        _ => false
    };
}
