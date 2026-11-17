using System.Windows;
using System.Windows.Controls;

namespace SqlBackupN.App.Controls;

/// <summary>Tarjeta informativa reutilizable (icono, título y texto) del panel principal.</summary>
public partial class InfoCard : UserControl
{
    /// <summary>Glifo de Segoe Fluent Icons que se muestra en el círculo.</summary>
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(nameof(Icon), typeof(string), typeof(InfoCard), new PropertyMetadata(string.Empty));

    /// <summary>Título de la tarjeta.</summary>
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(InfoCard), new PropertyMetadata(string.Empty));

    /// <summary>Texto descriptivo.</summary>
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(InfoCard), new PropertyMetadata(string.Empty));

    /// <summary>Inicializa los componentes.</summary>
    public InfoCard() => InitializeComponent();

    /// <summary>Glifo del icono.</summary>
    public string Icon { get => (string)GetValue(IconProperty); set => SetValue(IconProperty, value); }

    /// <summary>Título.</summary>
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    /// <summary>Texto descriptivo.</summary>
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
}
