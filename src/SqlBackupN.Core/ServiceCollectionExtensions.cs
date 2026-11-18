using Microsoft.Extensions.DependencyInjection;
using SqlBackupN.Core.Abstractions;
using SqlBackupN.Core.Infrastructure;
using SqlBackupN.Core.Services;

namespace SqlBackupN.Core;

/// <summary>Registro de dependencias compartido por el servicio, el CLI y la GUI.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registra todo el núcleo. Los ejecutables solo añaden lo suyo (host, logging, UI).
    /// El notificador es <see cref="WebhookNotifier"/>; el llamador puede reemplazarlo.
    /// </summary>
    public static IServiceCollection AddSqlBackupNCore(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
        services.AddSingleton<IBackupExecutor, SqlBackupExecutor>();
        services.AddSingleton<IFileStore, LocalFileStore>();
        services.AddSingleton<IGoogleClientProvider, FileGoogleClientProvider>();
        services.AddSingleton<IRemoteStorage, GoogleDriveStorage>();
        services.AddSingleton<IGoogleAuthorizer, GoogleAuthorizer>();
        services.AddSingleton<IJobStore>(_ => new JsonJobStore());
        services.AddSingleton<IHistoryStore>(_ => new JsonHistoryStore());
        services.AddSingleton<IUserSettingsStore>(_ => new JsonUserSettingsStore());
        services.AddSingleton<INotifier>(sp => new WebhookNotifier(sp.GetRequiredService<ISecretProtector>()));
        services.AddSingleton<MissedRunMonitor>();
        services.AddSingleton<JobRunner>();
        return services;
    }
}
