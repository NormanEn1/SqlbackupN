namespace SqlBackupN.Core.Services;

/// <summary>
/// Utilidades puras para trabajar con rutas y consultas de Google Drive (sin red), de modo que se puedan probar.
/// </summary>
public static class DrivePath
{
    private static readonly char[] Separators = ['\\', '/'];

    /// <summary>Divide una ruta (con <c>\</c> o <c>/</c>) en segmentos no vacíos.</summary>
    public static string[] Split(string path) =>
        path.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Separa una ruta relativa de archivo en sus carpetas y el nombre final.
    /// Ej.: <c>SRV\Ventas\FULL\a.bak</c> → (<c>[SRV, Ventas, FULL]</c>, <c>a.bak</c>).
    /// </summary>
    public static (string[] Folders, string FileName) SplitFile(string relativePath)
    {
        var parts = Split(relativePath);
        if (parts.Length == 0) throw new ArgumentException("La ruta del archivo está vacía.", nameof(relativePath));
        return (parts[..^1], parts[^1]);
    }

    /// <summary>
    /// Escapa un texto para usarlo entre comillas simples en el parámetro <c>q</c> de la API de Drive
    /// (se duplican las barras invertidas y se escapan las comillas simples).
    /// </summary>
    public static string EscapeQuery(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");

    /// <summary>
    /// Devuelve el problema de un nombre de carpeta raíz de Drive (puede contener <c>/</c> para subcarpetas),
    /// o <c>null</c> si es válido.
    /// </summary>
    public static string? FolderNameProblem(string folderName)
    {
        var parts = Split(folderName);
        if (parts.Length == 0) return "La carpeta de Drive es obligatoria.";
        if (folderName.Length > 200) return "El nombre de la carpeta es demasiado largo (máximo 200 caracteres).";
        if (parts.Any(p => p == ".." || p == "."))
            return "No se permiten '.' ni '..' en la carpeta de Drive.";
        if (folderName.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0 || folderName.Any(char.IsControl))
            return "Contiene caracteres no permitidos (< > : \" | ? *).";
        return null;
    }
}
