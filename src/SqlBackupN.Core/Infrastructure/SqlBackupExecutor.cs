using Microsoft.Data.SqlClient;
using SqlBackupN.Core.Abstractions;
using SqlBackupN.Core.Domain;
using SqlBackupN.Core.Services;

namespace SqlBackupN.Core.Infrastructure;

/// <summary>Implementación de <see cref="IBackupExecutor"/> con Microsoft.Data.SqlClient.</summary>
public sealed class SqlBackupExecutor(ISecretProtector protector) : IBackupExecutor
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListDatabasesAsync(SqlServerConnection connection, CancellationToken ct)
    {
        const string sql =
            "SELECT name FROM sys.databases WHERE state = 0 AND source_database_id IS NULL " +
            "AND name <> N'tempdb' ORDER BY name;";

        await using var conn = await OpenAsync(connection, ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var names = new List<string>();
        while (await reader.ReadAsync(ct)) names.Add(reader.GetString(0));
        return names;
    }

    /// <inheritdoc />
    public async Task BackupAsync(SqlServerConnection connection, BackupRequest request, CancellationToken ct)
    {
        await ExecuteAsync(connection, BackupSqlBuilder.BuildBackup(request), request.Options.CommandTimeoutSeconds, ct);
    }

    /// <inheritdoc />
    public async Task VerifyAsync(
        SqlServerConnection connection, string filePath, bool checksum, int timeoutSeconds, CancellationToken ct)
    {
        await ExecuteAsync(connection, BackupSqlBuilder.BuildVerify(filePath, checksum), timeoutSeconds, ct);
    }

    private async Task ExecuteAsync(SqlServerConnection connection, string sql, int timeoutSeconds, CancellationToken ct)
    {
        await using var conn = await OpenAsync(connection, ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = timeoutSeconds };
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<SqlConnection> OpenAsync(SqlServerConnection connection, CancellationToken ct)
    {
        var conn = new SqlConnection(SqlConnectionStrings.Build(connection, protector));
        await conn.OpenAsync(ct);
        return conn;
    }
}

/// <summary>Construye cadenas de conexión sin exponer contraseñas en logs ni en disco.</summary>
public static class SqlConnectionStrings
{
    /// <summary>Genera la cadena de conexión (base <c>master</c>) para el servidor indicado.</summary>
    public static string Build(SqlServerConnection c, ISecretProtector protector)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = c.Server,
            InitialCatalog = "master",
            ApplicationName = "SQL Backup N",
            TrustServerCertificate = c.TrustServerCertificate,
            ConnectTimeout = 15
        };

        if (c.UseWindowsAuthentication)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.UserID = c.UserName ?? string.Empty;
            builder.Password = string.IsNullOrEmpty(c.ProtectedPassword)
                ? string.Empty
                : protector.Unprotect(c.ProtectedPassword);
        }

        return builder.ConnectionString;
    }
}
