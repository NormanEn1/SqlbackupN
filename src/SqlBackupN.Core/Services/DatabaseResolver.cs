using System.Text.RegularExpressions;
using SqlBackupN.Core.Domain;

namespace SqlBackupN.Core.Services;

/// <summary>Decide qué bases de datos entran en un job a partir de las existentes en el servidor.</summary>
public static class DatabaseResolver
{
    private static readonly HashSet<string> SystemDatabases =
        new(["master", "model", "msdb", "tempdb"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Aplica el modo de selección y las exclusiones.</summary>
    /// <param name="selection">Configuración del job.</param>
    /// <param name="available">Bases en línea del servidor.</param>
    /// <returns>Nombres a respaldar, sin duplicados y en orden alfabético.</returns>
    public static IReadOnlyList<string> Resolve(DatabaseSelection selection, IEnumerable<string> available)
    {
        var userDbs = available.Where(n => !SystemDatabases.Contains(n));

        IEnumerable<string> picked = selection.Mode switch
        {
            DatabaseSelectionMode.AllUserDatabases => userDbs,
            // En modo explícito se respetan los nombres tal cual; si no existe, el backup fallará visiblemente.
            DatabaseSelectionMode.Explicit => selection.Names,
            DatabaseSelectionMode.Pattern => string.IsNullOrWhiteSpace(selection.Pattern)
                ? []
                : userDbs.Where(n => WildcardToRegex(selection.Pattern).IsMatch(n)),
            _ => []
        };

        var exclude = new HashSet<string>(selection.Exclude, StringComparer.OrdinalIgnoreCase);
        return picked
            .Where(n => !string.IsNullOrWhiteSpace(n) && !exclude.Contains(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Regex WildcardToRegex(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
