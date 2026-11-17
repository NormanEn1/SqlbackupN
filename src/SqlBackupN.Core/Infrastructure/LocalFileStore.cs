using SqlBackupN.Core.Abstractions;

namespace SqlBackupN.Core.Infrastructure;

/// <summary>Implementación de <see cref="IFileStore"/> sobre el sistema de archivos local o UNC.</summary>
public sealed class LocalFileStore : IFileStore
{
    /// <inheritdoc />
    public void EnsureDirectory(string path) => Directory.CreateDirectory(path);

    /// <inheritdoc />
    public void Copy(string source, string destination) => File.Copy(source, destination, overwrite: true);

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
