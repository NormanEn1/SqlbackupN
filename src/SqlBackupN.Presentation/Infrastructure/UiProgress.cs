namespace SqlBackupN.Presentation.Infrastructure;

/// <summary>
/// <see cref="IProgress{T}"/> que entrega cada informe en el hilo donde se creó (el de la interfaz, si lo crea
/// el view-model desde un comando) y, si ese hilo no tiene contexto de sincronización, lo entrega de inmediato.
/// Mantiene el orden de los informes.
/// </summary>
/// <typeparam name="T">Tipo de informe.</typeparam>
/// <param name="handler">Qué hacer con cada informe.</param>
public sealed class UiProgress<T>(Action<T> handler) : IProgress<T>
{
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;

    /// <inheritdoc />
    public void Report(T value)
    {
        if (_context is null) handler(value);
        else _context.Post(_ => handler(value), null);
    }
}
