using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using SqlBackupN.App.Views;
using SqlBackupN.Presentation.Infrastructure;
using SqlBackupN.Presentation.Jobs;

namespace SqlBackupN.App;

/// <summary>Implementación WPF de <see cref="IDialogService"/>.</summary>
public sealed class WpfDialogService : IDialogService
{
    /// <inheritdoc />
    public bool ShowJobEditor(JobEditorViewModel editor)
    {
        var window = new JobEditorWindow
        {
            DataContext = editor,
            Owner = Application.Current.MainWindow
        };
        return window.ShowDialog() == true;
    }

    /// <inheritdoc />
    public void ShowRunLog(RunLogViewModel log)
    {
        // No modal: se puede seguir usando la ventana principal y dejar el registro abierto mientras corre el backup.
        new RunLogWindow { DataContext = log, Owner = Application.Current.MainWindow }.Show();
    }

    /// <inheritdoc />
    public bool Confirm(string title, string message) =>
        MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question,
            MessageBoxResult.No) == MessageBoxResult.Yes;

    /// <inheritdoc />
    public void ShowMessage(string title, string message) =>
        MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    /// <inheritdoc />
    public void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowMessage("No se pudo abrir el navegador", $"Abra esta dirección a mano:\n{url}\n\n{ex.Message}");
        }
    }

    /// <inheritdoc />
    public string? PickFolder(string? initialDirectory)
    {
        var dialog = new OpenFolderDialog { Title = "Seleccione la carpeta de destino" };
        if (!string.IsNullOrWhiteSpace(initialDirectory)) dialog.InitialDirectory = initialDirectory;
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FolderName : null;
    }
}
