using System.Text.Json;
using SqlBackupN.Core.Domain;
using SqlBackupN.Core.Infrastructure;

namespace SqlBackupN.Core.Services;

/// <summary>Planes de programación predefinidos para crear pasos rápidamente.</summary>
public enum PlanPreset
{
    /// <summary>Un completo cada día a las 23:00.</summary>
    DailyFull = 0,

    /// <summary>Completo el domingo y diferencial de lunes a sábado, a las 22:00.</summary>
    WeeklyFullDailyDifferential = 1,

    /// <summary>Como el anterior, más log de transacciones cada 15 minutos.</summary>
    WeeklyFullDailyDifferentialAndLog = 2
}

/// <summary>Crea, copia y duplica jobs con valores iniciales sensatos.</summary>
public static class JobFactory
{
    /// <summary>Job nuevo y vacío, con un completo diario y retención de 30 días.</summary>
    public static BackupJob CreateNew() => new()
    {
        Name = string.Empty,
        CreatedUtc = DateTime.UtcNow,
        Steps = CreateSteps(PlanPreset.DailyFull),
        Retention = new RetentionPolicy { KeepDays = 30, KeepLast = 3 }
    };

    /// <summary>Crea los pasos de un plan predefinido (con identificadores nuevos).</summary>
    public static List<BackupStep> CreateSteps(PlanPreset preset) => preset switch
    {
        PlanPreset.DailyFull =>
        [
            new BackupStep { Type = BackupType.Full, Cron = "0 23 * * *" }
        ],
        PlanPreset.WeeklyFullDailyDifferential =>
        [
            new BackupStep { Type = BackupType.Full, Cron = "0 22 * * 0" },
            new BackupStep { Type = BackupType.Differential, Cron = "0 22 * * 1-6" }
        ],
        PlanPreset.WeeklyFullDailyDifferentialAndLog =>
        [
            new BackupStep { Type = BackupType.Full, Cron = "0 22 * * 0" },
            new BackupStep { Type = BackupType.Differential, Cron = "0 22 * * 1-6" },
            new BackupStep { Type = BackupType.TransactionLog, Cron = "*/15 * * * *" }
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(preset))
    };

    /// <summary>Copia profunda (mantiene los identificadores). Sirve para editar sin tocar el original.</summary>
    public static BackupJob Clone(BackupJob job) =>
        JsonSerializer.Deserialize<BackupJob>(JsonSerializer.Serialize(job, JsonDefaults.Compact), JsonDefaults.Compact)!;

    /// <summary>Copia como job nuevo: identificadores nuevos, nombre "(copia)" y fecha de creación actual.</summary>
    public static BackupJob Duplicate(BackupJob job)
    {
        var copy = Clone(job);
        copy.Id = Guid.NewGuid();
        copy.Name = $"{job.Name} (copia)";
        copy.CreatedUtc = DateTime.UtcNow;
        foreach (var step in copy.Steps) step.Id = Guid.NewGuid();
        return copy;
    }
}
