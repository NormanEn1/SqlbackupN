using System.Text.RegularExpressions;
using Cronos;
using SqlBackupN.Core.Domain;

namespace SqlBackupN.Core.Services;

/// <summary>Gravedad de un problema de validación.</summary>
public enum IssueSeverity
{
    /// <summary>Impide guardar el job.</summary>
    Error = 0,

    /// <summary>Se puede guardar, pero conviene revisarlo.</summary>
    Warning = 1
}

/// <summary>Un problema encontrado al validar un job.</summary>
/// <param name="Severity">Error o advertencia.</param>
/// <param name="Field">
/// Campo afectado, p. ej. <c>Name</c>, <c>Connection.Server</c>, <c>Steps[0].Cron</c>,
/// <c>Destinations[1].Path</c>. Las pantallas lo usan para mostrar el mensaje junto al campo.
/// </param>
/// <param name="Message">Mensaje para el usuario, en español.</param>
public sealed record ValidationIssue(IssueSeverity Severity, string Field, string Message);

/// <summary>
/// Valida un <see cref="BackupJob"/> completo. Es una función pura (sin disco ni red) que usan
/// las pantallas de edición y que también sirve para revisar un <c>jobs.json</c> escrito a mano.
/// Todo lo que impediría que un backup funcione es <see cref="IssueSeverity.Error"/>; lo que solo
/// es una mala práctica es <see cref="IssueSeverity.Warning"/>.
/// </summary>
public static partial class JobValidator
{
    private const string InvalidPathChars = "<>\"|?*";

    [GeneratedRegex(@"^[\w.\-\\,:()\[\]]+$")]
    private static partial Regex ServerRegex();

    [GeneratedRegex(@"^([A-Za-z]:[\\/]|\\\\[^\\/]+[\\/][^\\/]+)")]
    private static partial Regex AbsolutePathRegex();

    [GeneratedRegex(@"\{\w+\}")]
    private static partial Regex TokenRegex();

    /// <summary>Valida el job y devuelve todos los problemas (vacío = correcto).</summary>
    /// <param name="job">Job a validar.</param>
    /// <param name="otherJobNames">Nombres de los demás jobs, para exigir nombres únicos.</param>
    public static IReadOnlyList<ValidationIssue> Validate(BackupJob job, IEnumerable<string>? otherJobNames = null)
    {
        var issues = new List<ValidationIssue>();
        ValidateGeneral(job, otherJobNames, issues);
        ValidateConnection(job.Connection, issues);
        ValidateDatabases(job.Databases, issues);
        ValidateSteps(job.Steps, issues);
        ValidateDestinations(job.Destinations, issues);
        ValidateOptions(job.Options, issues);
        ValidateRetention(job.Retention, issues);
        ValidateNotifications(job.Notifications, issues);
        return issues;
    }

    /// <summary>Indica si la lista contiene algún error (las advertencias no cuentan).</summary>
    public static bool HasErrors(IEnumerable<ValidationIssue> issues) =>
        issues.Any(i => i.Severity == IssueSeverity.Error);

    private static void ValidateGeneral(BackupJob job, IEnumerable<string>? others, List<ValidationIssue> issues)
    {
        var name = job.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
            Error(issues, "Name", "El nombre es obligatorio.");
        else if (name.Length > 100)
            Error(issues, "Name", "El nombre no puede superar 100 caracteres.");
        else if (others?.Any(n => string.Equals(n?.Trim(), name, StringComparison.OrdinalIgnoreCase)) == true)
            Error(issues, "Name", "Ya existe otro job con ese nombre.");
    }

    private static void ValidateConnection(SqlServerConnection c, List<ValidationIssue> issues)
    {
        var server = c.Server?.Trim() ?? string.Empty;
        if (server.Length == 0)
        {
            Error(issues, "Connection.Server", "El servidor es obligatorio (ej.: .\\SQL2019 o srv01,1433).");
        }
        else if (!ServerRegex().IsMatch(server))
        {
            Error(issues, "Connection.Server", "Contiene caracteres no permitidos. Use solo letras, números y . - \\ , : ( )");
        }
        else if (server.EndsWith('\\') || server.EndsWith(','))
        {
            Error(issues, "Connection.Server", "El nombre de instancia o el puerto está incompleto.");
        }
        else if (server.Contains(',') &&
                 !(int.TryParse(server[(server.LastIndexOf(',') + 1)..], out var port) && port is >= 1 and <= 65535))
        {
            Error(issues, "Connection.Server", "El puerto debe ser un número entre 1 y 65535.");
        }

        if (c.UseWindowsAuthentication) return;

        if (string.IsNullOrWhiteSpace(c.UserName))
            Error(issues, "Connection.UserName", "El usuario es obligatorio con autenticación SQL.");
        if (string.IsNullOrEmpty(c.ProtectedPassword))
            Error(issues, "Connection.Password", "La contraseña es obligatoria con autenticación SQL.");
    }

    private static void ValidateDatabases(DatabaseSelection d, List<ValidationIssue> issues)
    {
        switch (d.Mode)
        {
            case DatabaseSelectionMode.Explicit:
                if (d.Names.Count(n => !string.IsNullOrWhiteSpace(n)) == 0)
                    Error(issues, "Databases.Names", "Indique al menos una base de datos (una por línea).");
                else if (d.Names.Any(n => n.Length > 128 || n.Any(char.IsControl)))
                    Error(issues, "Databases.Names", "Hay un nombre no válido (máximo 128 caracteres, sin caracteres de control).");
                break;
            case DatabaseSelectionMode.Pattern:
                if (string.IsNullOrWhiteSpace(d.Pattern))
                    Error(issues, "Databases.Pattern", "Indique un patrón, p. ej. Ventas* (* = cualquier texto, ? = un carácter).");
                else if (d.Pattern.Length > 128)
                    Error(issues, "Databases.Pattern", "El patrón no puede superar 128 caracteres.");
                break;
        }
    }

    private static void ValidateSteps(List<BackupStep> steps, List<ValidationIssue> issues)
    {
        if (steps.Count == 0)
        {
            Error(issues, "Steps", "Agregue al menos un paso de programación.");
            return;
        }

        for (var i = 0; i < steps.Count; i++)
        {
            var cron = steps[i].Cron?.Trim() ?? string.Empty;
            if (cron.Length == 0)
            {
                Error(issues, $"Steps[{i}].Cron", "La programación es obligatoria. Ejemplo: 0 23 * * * (todos los días a las 23:00).");
                continue;
            }

            try
            {
                CronExpression.Parse(cron, CronFormat.Standard);
            }
            catch (CronFormatException)
            {
                Error(issues, $"Steps[{i}].Cron",
                    "Expresión cron no válida. Use 5 campos: minuto (0-59) hora (0-23) día (1-31) mes (1-12) díaSemana (0-6). Ej.: 0 23 * * *");
            }
        }

        var enabled = steps.Where(s => s.Enabled).ToList();
        if (enabled.Count == 0)
        {
            Warn(issues, "Steps", "Ningún paso está habilitado: el job no hará backups.");
        }
        else if (!enabled.Any(s => s.Type == BackupType.Full))
        {
            Error(issues, "Steps", "Los backups diferenciales y de log necesitan un backup completo habilitado en el mismo job.");
        }
    }

    private static void ValidateDestinations(List<DestinationConfig> destinations, List<ValidationIssue> issues)
    {
        if (destinations.Count == 0)
        {
            Error(issues, "Destinations", "Agregue al menos un destino.");
            return;
        }

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < destinations.Count; i++)
        {
            var d = destinations[i];
            var name = d.Name?.Trim() ?? string.Empty;
            if (name.Length == 0) Error(issues, $"Destinations[{i}].Name", "El nombre es obligatorio.");
            else if (name.Length > 60) Error(issues, $"Destinations[{i}].Name", "El nombre no puede superar 60 caracteres.");
            else if (!seenNames.Add(name)) Error(issues, $"Destinations[{i}].Name", "Ya hay otro destino con ese nombre.");

            if (d.Kind == DestinationKind.GoogleDrive)
            {
                ValidateDrive(d, i, seenPaths, issues);
                continue;
            }

            var path = d.Path?.Trim() ?? string.Empty;
            var problem = PathProblem(path);
            if (problem is not null) Error(issues, $"Destinations[{i}].Path", problem);
            else if (!seenPaths.Add(path.TrimEnd('\\', '/')))
                Error(issues, $"Destinations[{i}].Path", "Ya hay otro destino con esa carpeta.");
        }

        if (destinations.Count == 1)
        {
            Warn(issues, "Destinations",
                "Solo hay un destino. Se recomienda una copia en otra unidad o equipo (regla 3-2-1).");
        }
    }

    private static void ValidateDrive(DestinationConfig d, int index, HashSet<string> seen, List<ValidationIssue> issues)
    {
        var prefix = $"Destinations[{index}]";
        var drive = d.Drive ?? new DriveSettings();

        if (index == 0)
        {
            Error(issues, $"{prefix}.Kind",
                "El destino principal debe ser una carpeta: SQL Server escribe allí el backup y desde ahí se sube la copia a Google Drive. Ponga Google Drive en una posición posterior.");
        }

        if (string.IsNullOrWhiteSpace(drive.ClientId))
            Error(issues, $"{prefix}.Drive.ClientId", "El ID de cliente de Google es obligatorio (ver docs/google-drive.md).");
        if (string.IsNullOrEmpty(drive.ProtectedClientSecret))
            Error(issues, $"{prefix}.Drive.ClientSecret", "El secreto de cliente de Google es obligatorio.");
        if (string.IsNullOrEmpty(drive.ProtectedRefreshToken))
            Error(issues, $"{prefix}.Drive.Account", "La cuenta de Google no está conectada. Pulse «Conectar con Google».");

        var folder = drive.FolderName?.Trim() ?? string.Empty;
        var problem = DrivePath.FolderNameProblem(folder);
        if (problem is not null)
            Error(issues, $"{prefix}.Drive.Folder", problem);
        else if (!seen.Add("drive:" + string.Join('/', DrivePath.Split(folder)).ToLowerInvariant()))
            Error(issues, $"{prefix}.Drive.Folder", "Ya hay otro destino de Google Drive con esa carpeta.");
    }

    /// <summary>Devuelve el problema de una ruta de carpeta, o null si es válida.</summary>
    private static string? PathProblem(string path)
    {
        if (path.Length == 0) return "La carpeta es obligatoria.";
        if (path.Length > 240) return "La ruta es demasiado larga (máximo 240 caracteres).";
        if (path.IndexOfAny(InvalidPathChars.ToCharArray()) >= 0 || path.Any(char.IsControl))
            return "Contiene caracteres no permitidos (< > \" | ? *).";
        // Solo se admite ':' en la letra de unidad (posición 1); cualquier otro sería un flujo alterno NTFS.
        if (path.Length >= 2 && path.IndexOf(':', 2) >= 0)
            return "Contiene ':' en una posición no válida.";
        if (!AbsolutePathRegex().IsMatch(path))
            return "Debe ser una ruta completa: D:\\Backups o de red \\\\servidor\\recurso.";
        if (path.Split('\\', '/').Any(s => s == ".."))
            return "No se permite '..' en la ruta.";
        return null;
    }

    private static void ValidateOptions(BackupOptions o, List<ValidationIssue> issues)
    {
        if (o.CommandTimeoutSeconds is < 0 or > 86400)
            Error(issues, "Options.CommandTimeoutSeconds", "El tiempo máximo debe estar entre 0 (sin límite) y 86400 segundos.");

        var template = o.PathTemplate?.Trim() ?? string.Empty;
        if (template.Length == 0)
        {
            Error(issues, "Options.PathTemplate", "La plantilla de nombre es obligatoria.");
            return;
        }

        var literal = TokenRegex().Replace(template, string.Empty);
        if (literal.IndexOfAny((InvalidPathChars + ":").ToCharArray()) >= 0 || literal.Any(char.IsControl))
        {
            Error(issues, "Options.PathTemplate", "Contiene caracteres no permitidos (< > \" | ? * :).");
            return;
        }

        try
        {
            _ = PathTemplate.Render(template,
                new TemplateContext("SERVIDOR", "BaseDemo", BackupType.Full, new DateTime(2026, 1, 1, 0, 0, 0)));
        }
        catch (FormatException ex)
        {
            Error(issues, "Options.PathTemplate", ex.Message);
            return;
        }

        if (!Regex.IsMatch(template, @"\{Database\}", RegexOptions.IgnoreCase))
            Error(issues, "Options.PathTemplate", "Debe incluir {Database}; si no, las bases se pisarían entre sí.");
        if (!Regex.IsMatch(template, @"\{Timestamp\}", RegexOptions.IgnoreCase))
            Error(issues, "Options.PathTemplate", "Debe incluir {Timestamp}; si no, cada backup sobrescribiría al anterior.");
        if (!Regex.IsMatch(template, @"\{Type\}", RegexOptions.IgnoreCase))
            Warn(issues, "Options.PathTemplate", "Se recomienda incluir {Type}: la retención se aplica por carpeta y no debe mezclar completos, diferenciales y logs.");
    }

    private static void ValidateRetention(RetentionPolicy r, List<ValidationIssue> issues)
    {
        if (r.KeepDays is null && r.KeepLast is null)
            Warn(issues, "Retention", "Sin retención los backups nunca se borran y el disco terminará llenándose.");
        if (r.KeepDays is { } days and (< 1 or > 3650))
            Error(issues, "Retention.KeepDays", "Los días a conservar deben estar entre 1 y 3650.");
        if (r.KeepLast is { } last and (< 1 or > 1000))
            Error(issues, "Retention.KeepLast", "La cantidad mínima debe estar entre 1 y 1000.");
    }

    private static void ValidateNotifications(NotificationSettings n, List<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(n.WebhookUrl))
        {
            Warn(issues, "Notifications.WebhookUrl", "Sin aviso configurado: nadie se enterará si un backup falla.");
            return;
        }

        if (!Uri.TryCreate(n.WebhookUrl.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            Error(issues, "Notifications.WebhookUrl", "Debe ser una URL completa que empiece por https:// (o http://).");
            return;
        }

        if (uri.Scheme == "http")
            Warn(issues, "Notifications.WebhookUrl", "La URL usa http sin cifrado; se recomienda https.");
        if (!n.OnSuccess && !n.OnWarning && !n.OnFailure)
            Warn(issues, "Notifications", "Hay webhook pero ningún evento activado: no se enviará ningún aviso.");
    }

    private static void Error(List<ValidationIssue> issues, string field, string message) =>
        issues.Add(new ValidationIssue(IssueSeverity.Error, field, message));

    private static void Warn(List<ValidationIssue> issues, string field, string message) =>
        issues.Add(new ValidationIssue(IssueSeverity.Warning, field, message));
}
