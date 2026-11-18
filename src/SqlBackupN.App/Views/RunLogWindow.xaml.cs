using System.Collections.Specialized;
using System.Windows;
using System.Windows.Threading;
using SqlBackupN.Presentation.Jobs;

namespace SqlBackupN.App.Views;

/// <summary>
/// Ventana del registro de ejecución. Sin lógica de negocio: lleva la vista a la última línea mientras llegan
/// (WPF no lo puede enlazar) y copia el texto al portapapeles.
/// </summary>
public partial class RunLogWindow : Window
{
    /// <summary>Inicializa la ventana.</summary>
    public RunLogWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is not RunLogViewModel vm) return;
            vm.Lines.CollectionChanged += (_, args) =>
            {
                if (args.Action == NotifyCollectionChangedAction.Add)
                    LogScroll.Dispatcher.BeginInvoke(LogScroll.ScrollToEnd, DispatcherPriority.Background);
            };
        };
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not RunLogViewModel vm) return;
        try { Clipboard.SetText(vm.AsText); }
        catch (System.Runtime.InteropServices.COMException) { /* el portapapeles estaba ocupado: se puede reintentar */ }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
