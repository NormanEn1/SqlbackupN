using SqlBackupN.Core.Abstractions;
using SqlBackupN.Core.Domain;

namespace SqlBackupN.Core.Services;

/// <summary>
/// Construye las sentencias T-SQL. Es una clase pura (sin I/O) para poder probarla,
/// y es el único lugar donde se escapan identificadores y cadenas.
/// </summary>
public static class BackupSqlBuilder
{
    /// <summary>Escapa un identificador: <c>[nombre]</c>, duplicando <c>]</c>.</summary>
    public static string QuoteName(string identifier) => "[" + identifier.Replace("]", "]]") + "]";

    /// <summary>Escapa un literal Unicode: <c>N'texto'</c>, duplicando <c>'</c>.</summary>
    public static string QuoteString(string value) => "N'" + value.Replace("'", "''") + "'";

    /// <summary>Genera <c>BACKUP DATABASE</c> o <c>BACKUP LOG</c> según el tipo.</summary>
    public static string BuildBackup(BackupRequest request)
    {
        var kind = request.Type == BackupType.TransactionLog ? "LOG" : "DATABASE";
        var options = new List<string> { "INIT", $"NAME = {QuoteString($"SQL Backup N {request.Type}")}" };

        if (request.Type == BackupType.Differential) options.Add("DIFFERENTIAL");
        // COPY_ONLY no es válido junto a DIFFERENTIAL.
        if (request.Options.CopyOnly && request.Type != BackupType.Differential) options.Add("COPY_ONLY");
        options.Add(request.Options.Compression ? "COMPRESSION" : "NO_COMPRESSION");
        options.Add(request.Options.Checksum ? "CHECKSUM" : "NO_CHECKSUM");

        return $"BACKUP {kind} {QuoteName(request.Database)} TO DISK = {QuoteString(request.FilePath)} " +
               $"WITH {string.Join(", ", options)};";
    }

    /// <summary>Genera <c>RESTORE VERIFYONLY</c>. Solo pide CHECKSUM si el backup lo incluye.</summary>
    public static string BuildVerify(string filePath, bool checksum) =>
        $"RESTORE VERIFYONLY FROM DISK = {QuoteString(filePath)}" + (checksum ? " WITH CHECKSUM;" : ";");
}
