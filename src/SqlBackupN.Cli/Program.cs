using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SqlBackupN.Core;
using SqlBackupN.Core.Abstractions;
using SqlBackupN.Core.Domain;
using SqlBackupN.Core.Infrastructure;
using SqlBackupN.Core.Services;

// CLI de SQL Backup N. Pensado para scripts: salida estable, --json opcional y códigos de salida definidos.
//   0 = correcto   1 = advertencias   2 = fallo   64 = uso incorrecto
const int ExitOk = 0, ExitWarning = 1, ExitFailed = 2, ExitUsage = 64;

var services = new ServiceCollection().AddLogging().AddSqlBackupNCore().BuildServiceProvider();
var options = new CliOptions(args);

try
{
    return options.Command switch
    {
        "jobs" => await ListJobsAsync(),
        "history" => await ShowHistoryAsync(),
        "databases" => await ListDatabasesAsync(),
        "run" => await RunAsync(),
        _ => Usage()
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return ExitFailed;
}

int Usage()
{
    Console.WriteLine("""
        SQL Backup N - Centinela Appliance

        Uso: sqlbackupn <comando> [opciones]

        Comandos:
          jobs                                   Lista los jobs configurados.
          run <job> [--type full|diff|log]       Ejecuta un job ahora (por defecto: full).
          history [--limit N]                    Muestra las últimas ejecuciones.
          databases --server <servidor>          Lista las bases del servidor (autenticación Windows).

        Opciones comunes:
          --json                                 Salida en JSON.

        Códigos de salida: 0 correcto, 1 advertencias, 2 fallo, 64 uso incorrecto.
        """);
    return options.Command is null or "help" ? ExitOk : ExitUsage;
}

async Task<int> ListJobsAsync()
{
    var jobs = await services.GetRequiredService<IJobStore>().LoadAsync();
    if (options.Json)
    {
        Console.WriteLine(JsonSerializer.Serialize(jobs, JsonDefaults.Indented));
        return ExitOk;
    }

    if (jobs.Count == 0) Console.WriteLine($"No hay jobs. Edite {AppPaths.JobsFile}");
    foreach (var j in jobs)
    {
        var steps = string.Join(", ", j.Steps.Select(s => $"{s.Type} [{s.Cron}]"));
        Console.WriteLine($"{(j.Enabled ? "●" : "○")} {j.Name}  ({j.Id})  {j.Connection.Server}  {steps}");
    }
    return ExitOk;
}

async Task<int> ShowHistoryAsync()
{
    var limit = int.TryParse(options.Get("limit"), out var n) ? n : 20;
    var records = await services.GetRequiredService<IHistoryStore>().GetRecentAsync(null, limit);
    if (options.Json)
    {
        Console.WriteLine(JsonSerializer.Serialize(records, JsonDefaults.Indented));
        return ExitOk;
    }

    foreach (var r in records)
    {
        Console.WriteLine($"{r.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm}  {r.Status,-9} {r.Type,-14} {r.JobName}  " +
                          $"({r.Results.Count(x => x.Status != RunStatus.Failed)}/{r.Results.Count} bases)");
        foreach (var f in r.Results.Where(x => x.Error is not null))
            Console.WriteLine($"    {f.Database}: {f.Error}");
    }
    return ExitOk;
}

async Task<int> ListDatabasesAsync()
{
    var server = options.Get("server");
    if (server is null) return Usage();

    var dbs = await services.GetRequiredService<IBackupExecutor>()
        .ListDatabasesAsync(new SqlServerConnection { Server = server }, CancellationToken.None);
    Console.WriteLine(options.Json ? JsonSerializer.Serialize(dbs) : string.Join(Environment.NewLine, dbs));
    return ExitOk;
}

async Task<int> RunAsync()
{
    var name = options.Positional.FirstOrDefault();
    if (name is null) return Usage();

    var type = (options.Get("type")?.ToLowerInvariant()) switch
    {
        null or "full" => BackupType.Full,
        "diff" or "differential" => BackupType.Differential,
        "log" => BackupType.TransactionLog,
        _ => (BackupType?)null
    };
    if (type is null) return Usage();

    var jobs = await services.GetRequiredService<IJobStore>().LoadAsync();
    var job = jobs.FirstOrDefault(j =>
        j.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || j.Id.ToString() == name);
    if (job is null)
    {
        Console.Error.WriteLine($"No existe el job '{name}'.");
        return ExitUsage;
    }

    var step = job.Steps.FirstOrDefault(s => s.Type == type) ?? new BackupStep { Type = type.Value };

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    // Sin --json se escribe en vivo cada paso (en el error estándar, para no mezclarlo con el resultado final).
    IProgress<RunLogEntry>? live = options.Json ? null : new LiveLog();
    var record = await services.GetRequiredService<JobRunner>().RunStepAsync(job, step, superseded: false, cts.Token, live);

    if (options.Json)
    {
        Console.WriteLine(JsonSerializer.Serialize(record, JsonDefaults.Indented));
    }
    else
    {
        Console.WriteLine($"{record.Status}: {record.JobName} / {record.Type}");
        foreach (var r in record.Results)
        {
            Console.WriteLine($"  {r.Status,-9} {r.Database}  {r.SizeBytes / 1048576.0:F1} MB  {r.Duration:hh\\:mm\\:ss}  {r.FilePath}");
            if (r.Error is not null) Console.WriteLine($"            error: {r.Error}");
            foreach (var w in r.Warnings) Console.WriteLine($"            aviso: {w}");
        }
    }

    return record.Status switch
    {
        RunStatus.Succeeded => ExitOk,
        RunStatus.Warning => ExitWarning,
        _ => ExitFailed
    };
}

/// <summary>Análisis mínimo de argumentos: comando, posicionales y <c>--clave valor</c> / <c>--bandera</c>.</summary>
internal sealed class CliOptions
{
    private readonly Dictionary<string, string?> _named = new(StringComparer.OrdinalIgnoreCase);

    public CliOptions(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                var key = args[i][2..];
                var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
                _named[key] = hasValue ? args[++i] : null;
            }
            else if (Command is null) Command = args[i].ToLowerInvariant();
            else Positional.Add(args[i]);
        }
    }

    public string? Command { get; }
    public List<string> Positional { get; } = [];
    public bool Json => _named.ContainsKey("json");
    public string? Get(string key) => _named.GetValueOrDefault(key);
}

/// <summary>Escribe cada línea del registro de ejecución en el error estándar apenas ocurre.</summary>
internal sealed class LiveLog : IProgress<RunLogEntry>
{
    public void Report(RunLogEntry entry)
    {
        var mark = entry.Level switch { RunLogLevel.Error => "ERROR", RunLogLevel.Warning => "AVISO", _ => "     " };
        var scope = entry.Database is null ? string.Empty : $"[{entry.Database}] ";
        Console.Error.WriteLine($"{entry.TimeUtc.ToLocalTime():HH:mm:ss} {mark} {scope}{entry.Message}");
    }
}
