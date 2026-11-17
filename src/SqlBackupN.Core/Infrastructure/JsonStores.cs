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
            await File.AppendAllTextAsync(_path, line, ct);
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
