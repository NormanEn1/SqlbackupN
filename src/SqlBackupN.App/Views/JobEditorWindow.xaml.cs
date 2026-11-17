using System.Windows;
using System.Windows.Controls;
using SqlBackupN.Presentation.Jobs;

namespace SqlBackupN.App.Views;

/// <summary>
/// Editor de un job. Solo conecta la ventana con <see cref="JobEditorViewModel"/>:
/// cierra el diálogo cuando el view-model lo pide y traslada la contraseña
/// (un <see cref="PasswordBox"/> no admite enlace de datos por seguridad).
/// </summary>
public partial class JobEditorWindow : Window
{
    /// <summary>Inicializa la ventana.</summary>
    public JobEditorWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is JobEditorViewModel vm) vm.CloseRequested += saved => DialogResult = saved;
        };
    }

    private void Password_Changed(object sender, RoutedEventArgs e)
    {
        if (DataContext is JobEditorViewModel vm) vm.Password = ((PasswordBox)sender).Password;
    }

    /// <summary>Traslada el secreto de cliente de Google al destino al que pertenece la fila.</summary>
    private void DriveSecret_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox { DataContext: DestinationEditorViewModel destination } box)
            destination.ClientSecret = box.Password;
    }

    /// <summary>Al elegir autenticación SQL se desmarca la de Windows en el view-model.</summary>
    private void SqlAuth_Checked(object sender, RoutedEventArgs e)
    {
        if (DataContext is JobEditorViewModel { UseWindowsAuthentication: true } vm)
            vm.UseWindowsAuthentication = false;
    }
}
