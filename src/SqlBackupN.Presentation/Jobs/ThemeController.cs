using SqlBackupN.Core.Abstractions;
using SqlBackupN.Core.Domain;
using SqlBackupN.Presentation.Infrastructure;

namespace SqlBackupN.Presentation.Jobs;

/// <summary>
/// Modo claro, oscuro o el de Windows: al crearse aplica el guardado y cada pulsación del botón pasa al siguiente
/// (automático → claro → oscuro) y lo guarda en las preferencias del usuario.
/// </summary>
public sealed class ThemeController : ViewModelBase
{
    private readonly IUserSettingsStore _store;
    private readonly IThemeService _service;
    private AppTheme _mode;

    /// <summary>Lee el modo guardado y lo aplica.</summary>
    public ThemeController(IUserSettingsStore store, IThemeService service)
    {
        _store = store;
        _service = service;
        _mode = store.Load().Theme;
        service.Apply(_mode);
        CycleCommand = new RelayCommand(Cycle);
    }

    /// <summary>Pasa al siguiente modo.</summary>
    public RelayCommand CycleCommand { get; }

    /// <summary>Modo actual.</summary>
    public AppTheme Mode
    {
        get => _mode;
        private set
        {
            if (!Set(ref _mode, value)) return;
            Raise(nameof(Label));
            Raise(nameof(Glyph));
            Raise(nameof(ToolTip));
        }
    }

    /// <summary>Texto del botón.</summary>
    public string Label => _mode switch
    {
        AppTheme.Light => "Tema: claro",
        AppTheme.Dark => "Tema: oscuro",
        _ => "Tema: automático"
    };

    /// <summary>Símbolo del botón (sol, luna o pantalla).</summary>
    public string Glyph => _mode switch
    {
        AppTheme.Light => "\uE706",
        AppTheme.Dark => "\uE708",
        _ => "\uE7F4"
    };

    /// <summary>Ayuda del botón: qué pasa al pulsarlo.</summary>
    public string ToolTip => _mode switch
    {
        AppTheme.Light => "Siempre claro. Pulse para pasar al modo oscuro.",
        AppTheme.Dark => "Siempre oscuro. Pulse para seguir el modo de Windows.",
        _ => "Sigue el modo de Windows. Pulse para pasar al modo claro."
    };

    private void Cycle()
    {
        Mode = _mode switch
        {
            AppTheme.System => AppTheme.Light,
            AppTheme.Light => AppTheme.Dark,
            _ => AppTheme.System
        };
        _service.Apply(_mode);
        try
        {
            _store.Save(new UserSettings { Theme = _mode });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No poder recordar la preferencia no debe impedir cambiar el modo.
        }
    }
}
