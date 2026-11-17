using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SqlBackupN.Core;
using SqlBackupN.Presentation.Infrastructure;
using SqlBackupN.Presentation.Jobs;

namespace SqlBackupN.App;

/// <summary>Punto de entrada de la GUI: construye el contenedor de dependencias y abre la ventana principal.</summary>
public partial class App : Application
{
    private ServiceProvider? _services;

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _services = new ServiceCollection()
            .AddLogging()
            .AddSqlBackupNCore()
            .AddSingleton<IDialogService, WpfDialogService>()
            .AddSingleton<MainViewModel>()
            .BuildServiceProvider();

        var window = new MainWindow { DataContext = _services.GetRequiredService<MainViewModel>() };
        MainWindow = window;
        window.Show();
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }
}
