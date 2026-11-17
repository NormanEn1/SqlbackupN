using SqlBackupN.Core.Abstractions;
using SqlBackupN.Core.Domain;

namespace SqlBackupN.Core.Services;

/// <summary>
/// Decide qué archivos de un directorio se pueden borrar. No toca el disco:
/// recibe la lista y devuelve la selección, de modo que la regla sea fácil de probar.
/// </summary>
public static class RetentionPlanner
{
    /// <summary>
    /// Regla: se borra un archivo solo si (a) no está entre los <c>KeepLast</c> más recientes
    /// y (b) es más viejo que <c>KeepDays</c>. Con una sola de las dos opciones, aplica solo esa.
    /// El archivo más reciente nunca se borra. Sin ninguna opción no se borra nada.
    /// </summary>
    public static IReadOnlyList<StoredFile> SelectFilesToDelete(
        IReadOnlyCollection<StoredFile> files, RetentionPolicy policy, DateTime nowUtc)
    {
        if (policy.KeepDays is null && policy.KeepLast is null) return [];

        var protectedCount = Math.Max(1, policy.KeepLast ?? 1);
        DateTime? cutoff = policy.KeepDays is { } days ? nowUtc.AddDays(-days) : null;

        return files
            .OrderByDescending(f => f.LastWriteUtc)
            .Skip(protectedCount)
            .Where(f => cutoff is null || f.LastWriteUtc < cutoff)
            .ToList();
    }
}
