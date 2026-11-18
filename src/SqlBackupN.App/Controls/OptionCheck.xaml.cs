using System.Windows;
using System.Windows.Controls;

namespace SqlBackupN.App.Controls;

/// <summary>
/// Opción con casilla: un título corto y una explicación debajo. Todas las pantallas usan este control para las
/// opciones, así el texto se ajusta al ancho en lugar de cortarse y todo se ve igual.
/// </summary>
public partial class OptionCheck : UserControl
{
    /// <summary>Título corto de la opción.</summary>
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(OptionCheck), new PropertyMetadata(string.Empty));

    /// <summary>Explicación (opcional) bajo el título.</summary>
    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
        nameof(Hint), typeof(string), typeof(OptionCheck), new PropertyMetadata(string.Empty));

    /// <summary>Si la opción está marcada (se enlaza en ambos sentidos).</summary>
    public static readonly DependencyProperty IsCheckedProperty = DependencyProperty.Register(
        nameof(IsChecked), typeof(bool), typeof(OptionCheck),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    /// <summary>Inicializa el control.</summary>
    public OptionCheck() => InitializeComponent();

    /// <summary>Título corto de la opción.</summary>
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    /// <summary>Explicación (opcional) bajo el título.</summary>
    public string Hint { get => (string)GetValue(HintProperty); set => SetValue(HintProperty, value); }

    /// <summary>Si la opción está marcada.</summary>
    public bool IsChecked { get => (bool)GetValue(IsCheckedProperty); set => SetValue(IsCheckedProperty, value); }
}
