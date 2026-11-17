using System.Globalization;
using System.Text.RegularExpressions;
using SqlBackupN.Core.Domain;

namespace SqlBackupN.Core.Services;

/// <summary>Valores disponibles al renderizar una plantilla de ruta.</summary>
/// <param name="Server">Nombre del servidor tal como lo escribió el usuario.</param>
/// <param name="Database">Nombre de la base de datos.</param>
/// <param name="Type">Tipo de backup.</param>
/// <param name="LocalTime">Hora local del backup.</param>
public sealed record TemplateContext(string Server, string Database, BackupType Type, DateTime LocalTime);

/// <summary>Convierte una plantilla como <c>{Database}\{Type}\{Timestamp}</c> en una ruta relativa segura.</summary>
public static partial class PathTemplate
{
    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex TokenRegex();

    /// <summary>
    /// Renderiza la plantilla. Los valores se sanean (sin separadores ni caracteres
    /// inválidos) y se rechazan rutas absolutas o con <c>..</c>.
    /// </summary>
    /// <exception cref="FormatException">Token desconocido o ruta insegura.</exception>
    public static string Render(string template, TemplateContext ctx)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Server"] = Sanitize(NormalizeServer(ctx.Server)),
            ["Database"] = Sanitize(ctx.Database),
            ["Type"] = ctx.Type.FolderName(),
            ["Date"] = ctx.LocalTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            ["Time"] = ctx.LocalTime.ToString("HHmmss", CultureInfo.InvariantCulture),
            ["Timestamp"] = ctx.LocalTime.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)
        };

        var rendered = TokenRegex().Replace(template, m =>
            values.TryGetValue(m.Groups[1].Value, out var v)
                ? v
                : throw new FormatException($"Token desconocido en la plantilla: {m.Value}"));

        rendered = rendered.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

        if (Path.IsPathRooted(rendered) ||
            rendered.Split(Path.DirectorySeparatorChar).Any(s => s == ".."))
        {
            throw new FormatException("La plantilla debe producir una ruta relativa sin '..'.");
        }

        return rendered;
    }

    /// <summary>Reemplaza ".", "(local)" o "localhost" por el nombre real del equipo.</summary>
    private static string NormalizeServer(string server)
    {
        foreach (var local in new[] { "(local)", "localhost", "." })
        {
            if (server.Equals(local, StringComparison.OrdinalIgnoreCase))
                return Environment.MachineName;
            if (server.StartsWith(local + "\\", StringComparison.OrdinalIgnoreCase))
                return Environment.MachineName + server[local.Length..];
        }
        return server;
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['\\', '/', ':']).ToHashSet();
        var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars).Trim();
    }
}
