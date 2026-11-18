using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using SqlBackupN.Core.Domain;
using SqlBackupN.Presentation.Infrastructure;

namespace SqlBackupN.App;

/// <summary>
/// Cambia entre claro y oscuro en caliente: en modo oscuro agrega <c>Styles/Theme.Dark.xaml</c> al final de los recursos de la
/// aplicación (sus pinceles reemplazan a los claros, porque las pantallas usan <c>DynamicResource</c>) y lo quita al volver al claro.
/// También oscurece la barra de título de las ventanas y, en modo automático, sigue los cambios de Windows.
/// </summary>
public sealed class WpfThemeService : IThemeService
{
    private static readonly Uri DarkUri = new("pack://application:,,,/SqlBackupN;component/Styles/Theme.Dark.xaml");
    private ResourceDictionary? _dark;
    private AppTheme _mode = AppTheme.System;

    /// <summary>Se engancha a los cambios de Windows y a la apertura de cada ventana.</summary>
    public WpfThemeService()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && _mode == AppTheme.System)
                Application.Current?.Dispatcher.BeginInvoke(() => Apply(_mode));
        };
        EventManager.RegisterClassHandler(
            typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => DarkTitleBar((Window)s, IsDark)));
    }

    /// <summary><c>true</c> si ahora se muestra el modo oscuro.</summary>
    public bool IsDark => _dark is not null;

    /// <inheritdoc />
    public void Apply(AppTheme mode)
    {
        _mode = mode;
        var dark = mode == AppTheme.Dark || (mode == AppTheme.System && WindowsUsesDarkTheme());
        var merged = Application.Current.Resources.MergedDictionaries;

        if (dark && _dark is null)
        {
            _dark = new ResourceDictionary { Source = DarkUri };
            merged.Add(_dark);
        }
        else if (!dark && _dark is not null)
        {
            merged.Remove(_dark);
            _dark = null;
        }

        foreach (Window window in Application.Current.Windows) DarkTitleBar(window, dark);
    }

    private static bool WindowsUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private const int DwmUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Oscurece (o aclara) la barra de título de una ventana; en Windows que no lo admite no hace nada.</summary>
    private static void DarkTitleBar(Window window, bool dark)
    {
        try
        {
            var handle = new WindowInteropHelper(window).EnsureHandle();
            var value = dark ? 1 : 0;
            _ = DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref value, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            // Solo es un detalle de aspecto.
        }
    }
}
