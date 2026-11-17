using SqlBackupN.Presentation.Jobs;

namespace SqlBackupN.Presentation.Infrastructure;

/// <summary>
/// Ventanas y cuadros de diálogo que necesitan los view-models. Se abstrae para poder
/// probarlos sin interfaz (en las pruebas se usa un doble).
/// </summary>
public interface IDialogService
{
    /// <summary>Muestra el editor de jobs de forma modal. Devuelve <c>true</c> si el usuario guardó.</summary>
    bool ShowJobEditor(JobEditorViewModel editor);

    /// <summary>Pide confirmación (Sí/No).</summary>
    bool Confirm(string title, string message);

    /// <summary>Muestra un mensaje informativo o de error.</summary>
    void ShowMessage(string title, string message);

    /// <summary>Abre una dirección web en el navegador predeterminado.</summary>
    void OpenUrl(string url);

    /// <summary>Abre el selector de carpetas. Devuelve la carpeta elegida o <c>null</c> si se cancela.</summary>
    string? PickFolder(string? initialDirectory);
}
