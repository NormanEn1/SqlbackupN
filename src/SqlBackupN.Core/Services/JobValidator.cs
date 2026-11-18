using System.Text.RegularExpressions;
using Cronos;
using SqlBackupN.Core.Domain;

namespace SqlBackupN.Core.Services;

/// <summary>Gravedad de un problema de validación.</summary>
public enum IssueSeverity
{
    /// <summary>Un dato escrito es incorrecto. Impide guardar el job.</summary>
    Error = 0,

    /// <summary>
    /// Falta un dato obligatorio que todavía no se ha rellenado (no es una equivocación del usuario).
    /// Impide guardar, pero la interfaz lo muestra como algo pendiente, no como un error.
    /// </summary>
    Missing = 1,

    /// <summary>Se puede guardar, pero conviene revisarlo.</summary>
    Warning = 2
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
    /// <param name="googleClientAvailable">
    /// Si ya hay credenciales de Google compartidas (guardadas por el usuario o incluidas con la aplicación);
    /// en ese caso un destino de Drive no necesita traer las suyas.
    /// </param>
    public static IReadOnlyList<ValidationIssue> Validate(
        BackupJob job, IEnumerable<string>? otherJobNames = null, bool googleClientAvailable = false)
    {
        var issues = new List<ValidationIssue>();
        ValidateGeneral(job, otherJobNames, issues);
        ValidateConnection(job.Connection, issues);
        ValidateDatabases(job.Databases, issues);
        ValidateSteps(job.Steps, issues);
        ValidateDestinations(job.Destinations, googleClientAvailable, issues);
        ValidateOptions(job.Options, job.Databases, issues);
        ValidateTemporaryPrimary(job, issues);
        ValidateRetention(job.Retention, issues);
        ValidateNotifications(job.Notifications, issues);
        return issues;
    }

    /// <summary>Indica si la lista contiene algún error (datos incorrectos; no cuentan los datos pendientes ni las advertencias).</summary>
    public static bool HasErrors(IEnumerable<ValidationIssue> issues) =>
        issues.Any(i => i.Severity == IssueSeverity.Error);

    /// <summary>Indica si el job no se puede guardar todavía: hay errores o faltan datos obligatorios.</summary>
    public static bool BlocksSaving(IEnumerable<ValidationIssue> issues) =>
        issues.Any(i => i.Severity is IssueSeverity.Error or IssueSeverity.Missing);

    private static void ValidateGeneral(BackupJob job, IEnumerable<string>? others, List<ValidationIssue> issues)
    {
        var name = job.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
            Missing(issues, "Name", "Falta indicar el nombre del job.");
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
            Missing(issues, "Connection.Server", "Falta indicar el servidor (ej.: .\\SQL2019 o srv01,1433).");
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
            Missing(issues, "Connection.UserName", "Falta indicar el usuario de SQL Server.");
        if (string.IsNullOrEmpty(c.ProtectedPassword))
            Missing(issues, "Connection.Password", "Falta indicar la contraseña de SQL Server.");
    }

    private static void ValidateDatabases(DatabaseSelection d, List<ValidationIssue> issues)
    {
        switch (d.Mode)
        {
            case DatabaseSelectionMode.Explicit:
                if (d.Names.Count(n => !string.IsNullOrWhiteSpace(n)) == 0)
                    Missing(issues, "Databases.Names", "Falta indicar al menos una base de datos (una por línea).");
                else if (d.Names.Any(n => n.Length > 128 || n.Any(char.IsControl)))
                    Error(issues, "Databases.Names", "Hay un nombre no válido (máximo 128 caracteres, sin caracteres de control).");
                break;
            case DatabaseSelectionMode.Pattern:
                if (string.IsNullOrWhiteSpace(d.Pattern))
                    Missing(issues, "Databases.Pattern", "Falta indicar el patrón, p. ej. Ventas* (* = cualquier texto, ? = un carácter).");
                else if (d.Pattern.Length > 128)
                    Error(issues, "Databases.Pattern", "El patrón no puede superar 128 caracteres.");
                break;
        }
    }

    private static void ValidateSteps(List<BackupStep> steps, List<ValidationIssue> issues)
    {
        if (steps.Count == 0)
        {
            Missing(issues, "Steps", "Falta agregar al menos un paso de programación.");
            return;
        }

        for (var i = 0; i < steps.Count; i++)
        {
            var cron = steps[i].Cron?.Trim() ?? string.Empty;
            if (cron.Length == 0)
            {
                Missing(issues, $"Steps[{i}].Cron", "Falta indicar la programación. Ejemplo: 0 23 * * * (todos los días a las 23:00).");
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

    private static void ValidateDestinations(
        List<DestinationConfig> destinations, bool googleClientAvailable, List<ValidationIssue> issues)
    {
        if (destinations.Count == 0)
        {
            Missing(issues, "Destinations", "Falta agregar al menos un destino.");
            return;
        }

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < destinations.Count; i++)
        {
            var d = destinations[i];
            var name = d.Name?.Trim() ?? string.Empty;
            if (name.Length == 0) Missing(issues, $"Destinations[{i}].Name", "Falta indicar el nombre del destino.");
            else if (name.Length > 60) Error(issues, $"Destinations[{i}].Name", "El nombre no puede superar 60 caracteres.");
            else if (!seenNames.Add(name)) Error(issues, $"Destinations[{i}].Name", "Ya hay otro destino con ese nombre.");

            if (d.Kind == DestinationKind.GoogleDrive)
            {
                ValidateDrive(d, i, seenPaths, googleClientAvailable, issues);
                continue;
            }

            var path = d.Path?.Trim() ?? string.Empty;
            var problem = PathProblem(path);
            if (path.Length == 0) Missing(issues, $"Destinations[{i}].Path", "Falta indicar la carpeta (ej.: D:\\Backups o \\\\servidor\\recurso).");
            else if (problem is not null) Error(issues, $"Destinations[{i}].Path", problem);
            else if (!seenPaths.Add(path.TrimEnd('\\', '/')))
                Error(issues, $"Destinations[{i}].Path", "Ya hay otro destino con esa carpeta.");
        }

        if (destinations.Count == 1)
        {
            Warn(issues, "Destinations",
                "Solo hay un destino. Se recomienda una copia en otra unidad o equipo (regla 3-2-1).");
        }
    }

    private static void ValidateDrive(
        DestinationConfig d, int index, HashSet<string> seen, bool googleClientAvailable, List<ValidationIssue> issues)
    {
        var prefix = $"Destinations[{index}]";
        var drive = d.Drive ?? new DriveSettings();

        if (index == 0)
        {
            Error(issues, $"{prefix}.Kind",
                "El destino principal debe ser una carpeta: SQL Server escribe allí el backup y desde ahí se sube la copia a Google Drive. Ponga Google Drive en una posición posterior.");
        }

        // Las credenciales pueden ser propias del destino (versiones anteriores) o compartidas por todos.
        var ownCredentials = !string.IsNullOrWhiteSpace(drive.ClientId);
        if (ownCredentials && string.IsNullOrEmpty(drive.ProtectedClientSecret))
            Missing(issues, $"{prefix}.Drive.Client", "Falta el secreto de cliente de Google de este destino.");
        else if (!ownCredentials && !googleClientAvailable)
            Missing(issues, $"{prefix}.Drive.Client",
                "Faltan las credenciales de Google. Se configuran una sola vez (ver «Credenciales de Google» y docs/google-drive.md).");

        if (string.IsNullOrEmpty(drive.ProtectedRefreshToken))
            Missing(issues, $"{prefix}.Drive.Account", "Falta conectar la cuenta de Google. Pulse «Conectar con Google».");

        var folder = drive.FolderName?.Trim() ?? string.Empty;
        var problem = DrivePath.FolderNameProblem(folder);
        if (folder.Length == 0)
            Missing(issues, $"{prefix}.Drive.Folder", "Falta indicar la carpeta de Drive.");
        else if (problem is not null)
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

    private static void ValidateTemporaryPrimary(BackupJob job, List<ValidationIssue> issues)
    {
        if (job.Options.TemporaryPrimary && job.Destinations.Count < 2)
            Error(issues, "Options.TemporaryPrimary",
                "Con la carpeta principal temporal agregue al menos un destino final (NAS o Google Drive): si no, el backup se borraría sin tener otra copia.");
    }

    private static void ValidateOptions(BackupOptions o, DatabaseSelection databases, List<ValidationIssue> issues)
    {
        if (o.CommandTimeoutSeconds is < 0 or > 86400)
            Error(issues, "Options.CommandTimeoutSeconds", "El tiempo máximo debe estar entre 0 (sin límite) y 86400 segundos.");

        var template = o.PathTemplate?.Trim() ?? string.Empty;
        if (template.Length == 0)
        {
            Missing(issues, "Options.PathTemplate", "Falta indicar la plantilla de nombre de los archivos.");
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

        // {Database} solo hace falta si el job puede respaldar varias bases: con una sola base explícita no hay a quién pisar.
        var singleDatabase = databases.Mode == DatabaseSelectionMode.Explicit && databases.Names.Count == 1;
        if (!singleDatabase && !Regex.IsMatch(template, @"\{Database\}", RegexOptions.IgnoreCase))
            Error(issues, "Options.PathTemplate", "Debe incluir {Database}; si no, las bases se pisarían entre sí (solo se puede omitir si el job respalda una única base).");
        if (!Regex.IsMatch(template, @"\{Timestamp\}", RegexOptions.IgnoreCase))
        {
            if (Regex.IsMatch(template, @"\{Date\}", RegexOptions.IgnoreCase))
                Warn(issues, "Options.PathTemplate", "Con {Date} hay un archivo por día: una segunda ejecución el mismo día reemplaza a la anterior. Use {Timestamp} para conservarlas todas.");
            else
                Error(issues, "Options.PathTemplate", "Debe incluir {Timestamp} (o {Date} para un archivo por día); si no, cada backup sobrescribiría al anterior.");
        }
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
        if (n.MissedRunHours is < 0 or > 8760)
            Error(issues, "Notifications.MissedRunHours", "Las horas sin ejecutarse deben estar entre 1 y 8760.");

        if (string.IsNullOrWhiteSpace(n.WebhookUrl))
        {
            Warn(issues, "Notifications.WebhookUrl", "Sin aviso configurado: nadie se enterará si un backup falla.");
            if (n.MissedRunHours > 0)
                Warn(issues, "Notifications.MissedRunHours", "El aviso por falta de ejecución necesita un webhook: sin él no se enviará.");
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
        ValidateWebhookOptions(n, issues);
        if (!n.OnSuccess && !n.OnWarning && !n.OnFailure)
            Warn(issues, "Notifications", "Hay webhook pero ningún evento activado: no se enviará ningún aviso.");
        else if (!n.OnFailure)
            Warn(issues, "Notifications", "Los fallos no avisan: si un backup falla, nadie se enterará.");
    }

    private static readonly string[] ReservedHeaders =
        ["host", "content-length", "content-type", "transfer-encoding", "connection"];

    private static void ValidateWebhookOptions(NotificationSettings n, List<ValidationIssue> issues)
    {
        if (n.WebhookFormat == WebhookFormat.Custom)
        {
            if (string.IsNullOrWhiteSpace(n.WebhookBodyTemplate))
                Missing(issues, "Notifications.WebhookBodyTemplate", "Escriba la plantilla del mensaje.");
            else if (WebhookPayload.ValidateTemplate(n.WebhookBodyTemplate) is { } problem)
                Error(issues, "Notifications.WebhookBodyTemplate", problem);
        }

        if (n.WebhookAuth != WebhookAuth.None)
        {
            if (n.WebhookAuth == WebhookAuth.Header && !HeaderNameRegex().IsMatch(n.WebhookAuthHeader?.Trim() ?? string.Empty))
                Error(issues, "Notifications.WebhookAuthHeader", "Indique el nombre de la cabecera (letras, números y guiones; p. ej. x-api-key).");
            if (string.IsNullOrEmpty(n.ProtectedWebhookSecret))
                Missing(issues, "Notifications.WebhookSecret", "Escriba el token o la clave de autorización del webhook.");
        }

        if (n.WebhookHeaders.Count > 10)
            Error(issues, "Notifications.WebhookHeaders", "Se admiten hasta 10 cabeceras adicionales.");
        foreach (var line in n.WebhookHeaders)
        {
            var colon = line.IndexOf(':');
            var name = colon > 0 ? line[..colon].Trim() : string.Empty;
            if (!HeaderNameRegex().IsMatch(name) || line[(colon + 1)..].Trim().Length == 0)
            {
                Error(issues, "Notifications.WebhookHeaders", $"«{line}» no es una cabecera válida: use «Nombre: valor».");
                break;
            }
            if (ReservedHeaders.Contains(name.ToLowerInvariant()))
            {
                Error(issues, "Notifications.WebhookHeaders", $"La cabecera {name} la fija la aplicación y no se puede cambiar.");
                break;
            }
            if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            {
                Warn(issues, "Notifications.WebhookHeaders", "Para un token use la opción «Autenticación»: aquí quedaría sin cifrar en el archivo del job.");
                break;
            }
        }

        if (n.WebhookRetries is < 0 or > 5)
            Error(issues, "Notifications.WebhookRetries", "Los reintentos deben estar entre 0 y 5.");
        if (n.WebhookTimeoutSeconds is < 5 or > 120)
            Error(issues, "Notifications.WebhookTimeoutSeconds", "El tiempo de espera debe estar entre 5 y 120 segundos.");
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9-]{0,63}$")]
    private static partial Regex HeaderNameRegex();

    private static void Error(List<ValidationIssue> issues, string field, string message) =>
        issues.Add(new ValidationIssue(IssueSeverity.Error, field, message));

    private static void Missing(List<ValidationIssue> issues, string field, string message) =>
        issues.Add(new ValidationIssue(IssueSeverity.Missing, field, message));

    private static void Warn(List<ValidationIssue> issues, string field, string message) =>
        issues.Add(new ValidationIssue(IssueSeverity.Warning, field, message));
}
