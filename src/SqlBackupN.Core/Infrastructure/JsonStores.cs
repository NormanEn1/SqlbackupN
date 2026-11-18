using System.Text.Json;
using System.Text.Json.Serialization;
using SqlBackupN.Core.Abstractions;
using SqlBackupN.Core.Domain;

namespace SqlBackupN.Core.Infrastructure;

/// <summary>Rutas de datos de la aplicación.</summary>
public static class AppPaths
{
    /// <summary>
    /// Carpeta de datos compartida por servicio, CLI y GUI. Por defecto
    /// <c>%ProgramData%\Centinela\SqlBackupN</c>; se puede cambiar con la
    /// variable de entorno <c>SQLBACKUPN_DATA</c>.
    /// </summary>
    public static string DataDirectory { get; } =
        Environment.GetEnvironmentVariable("SQLBACKUPN_DATA") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Centinela", "SqlBackupN");

    /// <summary>Archivo con la configuración de jobs.</summary>
    public static string JobsFile => Path.Combine(DataDirectory, "jobs.json");

    /// <summary>Archivo de historial (un registro JSON por línea).</summary>
    public static string HistoryFile => Path.Combine(DataDirectory, "history.jsonl");

    /// <summary>
    /// Carpeta de las preferencias de la persona que usa la interfaz (<c>%LocalAppData%\Centinela\SqlBackupN</c>); a diferencia
    /// de <see cref="DataDirectory"/> no se comparte con el servicio. Se puede cambiar con <c>SQLBACKUPN_USER</c>.
    /// </summary>
    public static string UserDirectory { get; } =
        Environment.GetEnvironmentVariable("SQLBACKUPN_USER") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Centinela", "SqlBackupN");

    /// <summary>Archivo de preferencias del usuario.</summary>
    public static string UserSettingsFile => Path.Combine(UserDirectory, "settings.json");
}

/// <summary>Preferencias del usuario en un archivo JSON dentro de <see cref="AppPaths.UserDirectory"/>.</summary>
public sealed class JsonUserSettingsStore(string? path = null) : IUserSettingsStore
{
    private readonly string _path = path ?? AppPaths.UserSettingsFile;

    /// <inheritdoc />
    public UserSettings Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(_path), JsonDefaults.Indented) ?? new UserSettings()
                : new UserSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new UserSettings();   // un archivo dañado no debe impedir abrir la aplicación
        }
    }

    /// <inheritdoc />
    public void Save(UserSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(settings, JsonDefaults.Indented));
    }
}

/// <summary>Opciones JSON compartidas: legibles, con enums como texto.</summary>
public static class JsonDefaults
{
    /// <summary>Opciones para jobs (indentadas, editables a mano).</summary>
    public static JsonSerializerOptions Indented { get; } = Create(indented: true);

    /// <summary>Opciones para historial (una línea por registro).</summary>
    public static JsonSerializerOptions Compact { get; } = Create(indented: false);

    private static JsonSerializerOptions Create(bool indented) => new()
    {
        WriteIndented = indented,
        // Se escribe en camelCase (como en la documentación); al leer se aceptan ambas formas.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
}

/// <summary>Guarda los jobs en <c>jobs.json</c> con escritura atómica (archivo temporal + reemplazo).</summary>
public sealed class JsonJobStore(string? path = null) : IJobStore
{
    private readonly string _path = path ?? AppPaths.JobsFile;

    /// <inheritdoc />
    public async Task<IReadOnlyList<BackupJob>> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return [];
        await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return await JsonSerializer.DeserializeAsync<List<BackupJob>>(stream, JsonDefaults.Indented, ct) ?? [];
    }

    /// <inheritdoc />
    public async Task SaveAsync(IEnumerable<BackupJob> jobs, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, jobs.ToList(), JsonDefaults.Indented, ct);
        }
        File.Move(temp, _path, overwrite: true);
    }
}

/// <summary>
/// Historial en formato JSON Lines: solo se agregan líneas, así varios procesos
/// (servicio, CLI, GUI) pueden escribir sin pisarse.
/// </summary>
public sealed class JsonHistoryStore(string? path = null) : IHistoryStore
{
    private readonly string _path = path ?? AppPaths.HistoryFile;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <inheritdoc />
    public async Task AppendAsync(JobRunRecord record, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var line = JsonSerializer.Serialize(record, JsonDefaults.Compact) + Environment.NewLine;
        await _gate.WaitAsync(ct);
        try
        {
            // Mientras otro proceso (la interfaz) reescribe el archivo para borrar ejecuciones, abrirlo falla un instante: se reintenta.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await File.AppendAllTextAsync(_path, line, ct);
                    return;
                }
                catch (IOException) when (attempt < AppendRetries)
                {
                    await Task.Delay(AppendRetryDelay, ct);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private const int AppendRetries = 40;
    private static readonly TimeSpan AppendRetryDelay = TimeSpan.FromMilliseconds(50);

    /// <inheritdoc />
    public Task<int> DeleteAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
        RewriteAsync(r => !ids.Contains(r.Id), ct);

    /// <inheritdoc />
    public Task<int> ClearAsync(Guid? jobId, CancellationToken ct = default) =>
        RewriteAsync(r => jobId is not null && r.JobId != jobId, ct);

    /// <summary>Abre el historial con acceso exclusivo de escritura; si otro proceso está agregando una línea, reintenta un momento.</summary>
    private async Task<FileStream> OpenForRewriteAsync(CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            }
            catch (IOException) when (attempt < AppendRetries)
            {
                await Task.Delay(AppendRetryDelay, ct);
            }
        }
    }

    /// <summary>
    /// Reescribe el archivo dejando solo las ejecuciones que cumplen <paramref name="keep"/>. Se abre con acceso exclusivo de
    /// escritura: ningún otro proceso puede agregar una línea mientras tanto (el servicio reintenta), así no se pierde ninguna.
    /// Las líneas ilegibles se conservan tal cual. Devuelve cuántas ejecuciones se borraron.
    /// </summary>
    private async Task<int> RewriteAsync(Func<JobRunRecord, bool> keep, CancellationToken ct)
    {
        if (!File.Exists(_path)) return 0;

        await _gate.WaitAsync(ct);
        try
        {
            await using var stream = await OpenForRewriteAsync(ct);
            var kept = new List<string>();
            var removed = 0;
            using (var reader = new StreamReader(stream, leaveOpen: true))
            {
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    JobRunRecord? record = null;
                    try { record = JsonSerializer.Deserialize<JobRunRecord>(line, JsonDefaults.Compact); }
                    catch (JsonException) { /* línea dañada: se conserva */ }

                    if (record is not null && !keep(record)) removed++;
                    else kept.Add(line);
                }
            }
            if (removed == 0) return 0;

            // Primero una copia completa del resultado al lado; solo después se sobrescribe el archivo (si algo se corta a la mitad, la copia sirve).
            var temp = _path + ".tmp";
            var text = string.Concat(kept.Select(l => l + Environment.NewLine));
            await File.WriteAllTextAsync(temp, text, ct);
            stream.SetLength(0);
            stream.Position = 0;
            await using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), leaveOpen: true))
            {
                await writer.WriteAsync(text);
                await writer.FlushAsync(ct);
            }
            File.Delete(temp);
            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobRunRecord>> GetRecentAsync(Guid? jobId, int max, CancellationToken ct = default)
    {
        var all = await ReadAllAsync(ct);
        return all
            .Where(r => jobId is null || r.JobId == jobId)
            .OrderByDescending(r => r.StartedUtc)
            .Take(max)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<DateTime?> GetLastRunUtcAsync(Guid jobId, Guid stepId, CancellationToken ct = default)
    {
        var all = await ReadAllAsync(ct);
        var last = all.Where(r => r.JobId == jobId && r.StepId == stepId)
                      .OrderByDescending(r => r.FinishedUtc)
                      .FirstOrDefault();
        return last?.FinishedUtc;
    }

    private async Task<List<JobRunRecord>> ReadAllAsync(CancellationToken ct)
    {
        var records = new List<JobRunRecord>();
        if (!File.Exists(_path)) return records;

        await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonSerializer.Deserialize<JobRunRecord>(line, JsonDefaults.Compact) is { } r) records.Add(r);
            }
            catch (JsonException)
            {
                // Una línea dañada (p. ej. corte de energía) no debe invalidar todo el historial.
            }
        }
        return records;
    }
}
