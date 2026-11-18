using SqlBackupN.Core.Domain;

namespace SqlBackupN.Presentation.Infrastructure;

/// <summary>Cambia la apariencia de la aplicación en caliente (se abstrae para probar los view-models sin interfaz).</summary>
public interface IThemeService
{
    /// <summary>Aplica el modo (en <see cref="AppTheme.System"/> sigue a Windows).</summary>
    void Apply(AppTheme mode);
}
