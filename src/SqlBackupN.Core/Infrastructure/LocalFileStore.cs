using System.IO.Compression;
using SqlBackupN.Core.Abstractions;

namespace SqlBackupN.Core.Infrastructure;

/// <summary>Implementación de <see cref="IFileStore"/> sobre el sistema de archivos local o UNC.</summary>
public sealed class LocalFileStore : IFileStore
{
    /// <inheritdoc />
    public void EnsureDirectory(string path) => Directory.CreateDirectory(path);

    /// <inheritdoc />
    public void VerifyWritable(string directory)
    {
        Directory.CreateDirectory(directory);
        var probe = Path.Combine(directory, $".sqlbackupn-test-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(probe, "prueba");
        File.Delete(probe);
    }

    /// <inheritdoc />
    public void Copy(string source, string destination) => File.Copy(source, destination, overwrite: true);

    /// <inheritdoc />
    public void CreateZip(string sourceFile, string zipPath)
    {
        // Se escribe con un nombre temporal y se renombra al final: un corte a la mitad nunca deja un zip incompleto.
        var temp = zipPath + ".tmp";
        try
        {
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
                zip.CreateEntryFromFile(sourceFile, Path.GetFileName(sourceFile), CompressionLevel.Optimal);
            File.Move(temp, zipPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); }
            catch (Exception) { /* el error principal es el de la compresión */ }
            throw;
        }
    }

    /// <inheritdoc />
    public void Delete(string path) => File.Delete(path);

    /// <inheritdoc />
    public long GetSize(string path) => new FileInfo(path).Length;

    /// <inheritdoc />
    public IReadOnlyList<StoredFile> List(string directory, string searchPattern)
    {
        if (!Directory.Exists(directory)) return [];
        return new DirectoryInfo(directory)
            .EnumerateFiles(searchPattern, SearchOption.TopDirectoryOnly)
            .Select(f => new StoredFile(f.FullName, f.LastWriteTimeUtc, f.Length))
            .ToList();
    }
}
