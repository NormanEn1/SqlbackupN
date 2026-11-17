using SqlBackupN.Core.Domain;

namespace SqlBackupN.Presentation.Jobs;

/// <summary>
/// Traduce un <see cref="RunStatus"/> a la clave de color que usan las vistas
/// (<c>ok</c>, <c>warn</c>, <c>fail</c>, <c>skip</c>, <c>none</c>) y a su texto visible.
/// Así los colores se deciden en XAML sin que la vista conozca el dominio.
/// </summary>
public static class StatusKinds
{
    /// <summary>Clave de color del estado; <c>none</c> si todavía no hay ejecuciones.</summary>
    public static string Of(RunStatus? status) => status switch
    {
        RunStatus.Succeeded => "ok",
        RunStatus.Warning => "warn",
        RunStatus.Failed => "fail",
        RunStatus.Skipped => "skip",
        _ => "none"
    };

    /// <summary>Texto corto del estado para insignias.</summary>
    public static string LabelOf(RunStatus status) => status switch
    {
        RunStatus.Succeeded => "Correcto",
        RunStatus.Warning => "Advertencia",
        RunStatus.Failed => "Fallo",
        RunStatus.Skipped => "Omitido",
        _ => string.Empty
    };
}
