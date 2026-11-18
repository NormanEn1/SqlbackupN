using SqlBackupN.Presentation.Infrastructure;

namespace SqlBackupN.Presentation.Jobs;

/// <summary>
/// Una base de datos del servidor que se puede marcar en la lista de "Bases de datos".
/// Al marcarla o desmarcarla se avisa al editor para que actualice la lista de nombres del job.
/// </summary>
public sealed class DatabaseChoice : ViewModelBase
{
    private static readonly HashSet<string> SystemNames =
        new(["master", "model", "msdb", "tempdb", "distribution"], StringComparer.OrdinalIgnoreCase);

    private readonly Action<DatabaseChoice> _toggled;
    private bool _isSelected;

    /// <summary>Crea la opción.</summary>
    /// <param name="name">Nombre de la base.</param>
    /// <param name="selected">Si empieza marcada.</param>
    /// <param name="toggled">Se invoca cuando el usuario la marca o desmarca.</param>
    public DatabaseChoice(string name, bool selected, Action<DatabaseChoice> toggled)
    {
        Name = name;
        _isSelected = selected;
        _toggled = toggled;
    }

    /// <summary>Nombre de la base de datos.</summary>
    public string Name { get; }

    /// <summary><c>true</c> para las bases de sistema (master, model, msdb…).</summary>
    public bool IsSystem => SystemNames.Contains(Name);

    /// <summary>Texto que se muestra: el nombre, con "(sistema)" si es de sistema.</summary>
    public string Label => IsSystem ? $"{Name}  (sistema)" : Name;

    /// <summary>Si está elegida para el backup.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Set(ref _isSelected, value)) _toggled(this);
        }
    }

    /// <summary>Cambia el estado sin avisar al editor (para sincronizar con el texto de nombres).</summary>
    internal void SetSelected(bool value) => Set(ref _isSelected, value, nameof(IsSelected));
}
