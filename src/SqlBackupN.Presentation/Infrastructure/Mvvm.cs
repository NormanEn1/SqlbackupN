using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace SqlBackupN.Presentation.Infrastructure;

/// <summary>Base mínima de MVVM: notificación de cambios de propiedades.</summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Asigna el campo y notifica si el valor cambió. Devuelve <c>true</c> si cambió.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    /// <summary>Notifica el cambio de una propiedad calculada.</summary>
    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Comando síncrono. Llame a <see cref="NotifyCanExecuteChanged"/> cuando cambie la condición.</summary>
public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    /// <summary>Crea un comando que no usa parámetro.</summary>
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    /// <inheritdoc />
    public void Execute(object? parameter) => execute(parameter);

    /// <summary>Avisa a la interfaz de que debe volver a evaluar si el comando está disponible.</summary>
    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Comando asíncrono: se deshabilita mientras se ejecuta y nunca deja escapar una excepción
/// (con <c>async void</c> tumbaría la aplicación); la entrega a <paramref name="onError"/>.
/// </summary>
public sealed class AsyncRelayCommand(
    Func<Task> execute, Func<bool>? canExecute = null, Action<Exception>? onError = null) : ICommand
{
    private bool _running;

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <summary>Indica si el comando se está ejecutando.</summary>
    public bool IsRunning => _running;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);

    /// <inheritdoc />
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _running = true;
        NotifyCanExecuteChanged();
        try
        {
            await execute();
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
        }
        finally
        {
            _running = false;
            NotifyCanExecuteChanged();
        }
    }

    /// <summary>Avisa a la interfaz de que debe volver a evaluar si el comando está disponible.</summary>
    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
