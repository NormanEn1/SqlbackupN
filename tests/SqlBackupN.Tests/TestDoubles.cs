using SqlBackupN.Core.Abstractions;
using SqlBackupN.Core.Domain;
using SqlBackupN.Presentation.Infrastructure;
using SqlBackupN.Presentation.Jobs;

namespace SqlBackupN.Tests;

/// <summary>Dobles de prueba compartidos por las pruebas de los view-models.</summary>
internal sealed class StubProtector : ISecretProtector
{
    public string Protect(string plainText) => "enc:" + plainText;
    public string Unprotect(string protectedText) => protectedText["enc:".Length..];
}

internal sealed class StubExecutor : IBackupExecutor
{
    public List<string> Databases { get; } = ["Ventas", "Compras"];
    public Exception? Failure { get; set; }

    public Task<IReadOnlyList<string>> ListDatabasesAsync(SqlServerConnection connection, CancellationToken ct) =>
        Failure is null ? Task.FromResult<IReadOnlyList<string>>(Databases) : Task.FromException<IReadOnlyList<string>>(Failure);

    public Task BackupAsync(SqlServerConnection connection, BackupRequest request, CancellationToken ct) => Task.CompletedTask;

    public Task VerifyAsync(SqlServerConnection connection, string filePath, bool checksum, int timeoutSeconds, CancellationToken ct) =>
        Task.CompletedTask;
}

/// <summary>Almacenamiento remoto simulado: registra subidas y borrados y permite fabricar fallos.</summary>
internal sealed class StubRemote : IRemoteStorage
{
    public List<(string Destination, string Local, string Relative)> Uploads { get; } = [];
    public List<string> Deleted { get; } = [];
    public List<RemoteFile> ExistingFiles { get; } = [];
    public List<string> ListedDirectories { get; } = [];
    public Exception? UploadFailure { get; set; }
    public Exception? TestFailure { get; set; }
    public List<DestinationConfig> Tested { get; } = [];

    public Task UploadAsync(DestinationConfig destination, string localFilePath, string relativePath, CancellationToken ct)
    {
        if (UploadFailure is not null) return Task.FromException(UploadFailure);
        Uploads.Add((destination.Name, localFilePath, relativePath));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RemoteFile>> ListAsync(
        DestinationConfig destination, string relativeDirectory, string extension, CancellationToken ct)
    {
        ListedDirectories.Add(relativeDirectory);
        return Task.FromResult<IReadOnlyList<RemoteFile>>(
            ExistingFiles.Where(f => f.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)).ToList());
    }

    public Task DeleteAsync(DestinationConfig destination, RemoteFile file, CancellationToken ct)
    {
        Deleted.Add(file.Id);
        return Task.CompletedTask;
    }

    public Task<string> TestAsync(DestinationConfig destination, CancellationToken ct)
    {
        Tested.Add(destination);
        return TestFailure is null
            ? Task.FromResult("Conectado como demo@example.test · la carpeta está lista.")
            : Task.FromException<string>(TestFailure);
    }
}

/// <summary>Conexión con Google simulada: no abre ningún navegador.</summary>
internal sealed class StubAuthorizer : IGoogleAuthorizer
{
    public GoogleAuthResult Result { get; set; } = new("refresh-123", "demo@example.test");
    public Exception? Failure { get; set; }
    public string? LastClientId { get; private set; }
    public string? LastClientSecret { get; private set; }

    public Task<GoogleAuthResult> AuthorizeAsync(string clientId, string clientSecret, CancellationToken ct)
    {
        LastClientId = clientId;
        LastClientSecret = clientSecret;
        return Failure is null ? Task.FromResult(Result) : Task.FromException<GoogleAuthResult>(Failure);
    }
}

internal sealed class StubFiles : IFileStore
{
    public List<string> Verified { get; } = [];
    public string? FailFor { get; set; }

    public void VerifyWritable(string directory)
    {
        if (directory == FailFor) throw new UnauthorizedAccessException("Acceso denegado");
        Verified.Add(directory);
    }

    public void EnsureDirectory(string path) { }
    public void Copy(string source, string destination) { }
    public void Delete(string path) { }
    public long GetSize(string path) => 0;
    public IReadOnlyList<StoredFile> List(string directory, string searchPattern) => [];
}

internal sealed class StubDialogs : IDialogService
{
    /// <summary>Acción que simula lo que hace el usuario dentro del editor.</summary>
    public Action<JobEditorViewModel>? OnEdit { get; set; }
    public bool ConfirmAnswer { get; set; }
    public string? FolderToPick { get; set; }
    public List<string> Messages { get; } = [];
    public int EditorsShown { get; private set; }

    public bool ShowJobEditor(JobEditorViewModel editor)
    {
        EditorsShown++;
        OnEdit?.Invoke(editor);
        return editor.Result is not null;
    }

    public bool Confirm(string title, string message) => ConfirmAnswer;
    public void ShowMessage(string title, string message) => Messages.Add(message);
    public string? PickFolder(string? initialDirectory) => FolderToPick;
}

internal sealed class MemoryJobStore : IJobStore
{
    public List<BackupJob> Jobs { get; set; } = [];

    public Task<IReadOnlyList<BackupJob>> LoadAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<BackupJob>>(Jobs.ToList());

    public Task SaveAsync(IEnumerable<BackupJob> jobs, CancellationToken ct = default)
    {
        Jobs = jobs.ToList();
        return Task.CompletedTask;
    }
}

internal sealed class MemoryHistoryStore : IHistoryStore
{
    public Task AppendAsync(JobRunRecord record, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<JobRunRecord>> GetRecentAsync(Guid? jobId, int max, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<JobRunRecord>>([]);

    public Task<DateTime?> GetLastRunUtcAsync(Guid jobId, Guid stepId, CancellationToken ct = default) =>
        Task.FromResult<DateTime?>(null);
}

internal sealed class StubNotifier : INotifier
{
    public Task NotifyAsync(BackupJob job, JobRunRecord record, CancellationToken ct) => Task.CompletedTask;
}
