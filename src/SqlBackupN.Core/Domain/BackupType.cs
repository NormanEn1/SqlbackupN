namespace SqlBackupN.Core.Domain;

/// <summary>Tipo de backup soportado por SQL Server.</summary>
public enum BackupType
{
    /// <summary>Copia completa de la base de datos.</summary>
    Full = 0,

    /// <summary>
    /// Solo los cambios desde el último backup completo. Es el equivalente a un
    /// "incremental" en SQL Server (que no tiene un incremental puro).
    /// </summary>
    Differential = 1,

    /// <summary>Log de transacciones; permite recuperar a un punto en el tiempo.</summary>
    TransactionLog = 2
}

/// <summary>Utilidades asociadas a <see cref="BackupType"/>.</summary>
public static class BackupTypeExtensions
{
    /// <summary>Extensión de archivo habitual para el tipo de backup.</summary>
    public static string FileExtension(this BackupType type) =>
        type == BackupType.TransactionLog ? ".trn" : ".bak";

    /// <summary>Nombre corto usado en rutas y nombres de archivo.</summary>
    public static string FolderName(this BackupType type) => type switch
    {
        BackupType.Full => "FULL",
        BackupType.Differential => "DIFF",
        BackupType.TransactionLog => "LOG",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };
}
