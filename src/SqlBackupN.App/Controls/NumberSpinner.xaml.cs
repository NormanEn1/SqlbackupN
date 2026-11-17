using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SqlBackupN.App.Controls;

/// <summary>
/// Selector numérico sencillo (hora, minuto, día del mes…). Se puede escribir, usar las flechas ↑↓ del teclado,
/// la rueda del ratón o los botones ▲▼. El valor siempre queda dentro de [<see cref="Minimum"/>, <see cref="Maximum"/>];
/// con <see cref="Wrap"/> pasa de 23 a 0 (y al revés).
/// </summary>
public partial class NumberSpinner : UserControl
{
    /// <summary>Valor actual (se enlaza en ambos sentidos).</summary>
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(int), typeof(NumberSpinner),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged, CoerceValue));

    /// <summary>Valor mínimo.</summary>
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(int), typeof(NumberSpinner), new PropertyMetadata(0, OnRangeChanged));

    /// <summary>Valor máximo.</summary>
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(int), typeof(NumberSpinner), new PropertyMetadata(59, OnRangeChanged));

    /// <summary>Si es <c>true</c>, al pasar del máximo vuelve al mínimo (y al revés).</summary>
    public static readonly DependencyProperty WrapProperty = DependencyProperty.Register(
        nameof(Wrap), typeof(bool), typeof(NumberSpinner), new PropertyMetadata(false));

    /// <summary>Formato numérico para mostrar el valor (por defecto "D2": 05).</summary>
    public static readonly DependencyProperty FormatProperty = DependencyProperty.Register(
        nameof(Format), typeof(string), typeof(NumberSpinner), new PropertyMetadata("D2", OnRangeChanged));

    /// <summary>Inicializa el control.</summary>
    public NumberSpinner()
    {
        InitializeComponent();

        UpButton.Click += (_, _) => Step(+1);
        DownButton.Click += (_, _) => Step(-1);

        Box.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsAsciiDigit);
        DataObject.AddPastingHandler(Box, (_, e) =>
        {
            if (e.DataObject.GetData(typeof(string)) is not string text || !text.All(char.IsAsciiDigit)) e.CancelCommand();
        });
        Box.PreviewKeyDown += OnKeyDown;
        Box.LostKeyboardFocus += (_, _) => Commit();
        Box.GotKeyboardFocus += (_, _) => Box.SelectAll();
        Root.MouseWheel += (_, e) =>
        {
            if (!IsKeyboardFocusWithin) return;
            Step(e.Delta > 0 ? +1 : -1);
            e.Handled = true;
        };

        ShowValue();
    }

    /// <summary>Valor actual.</summary>
    public int Value { get => (int)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    /// <summary>Valor mínimo.</summary>
    public int Minimum { get => (int)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }

    /// <summary>Valor máximo.</summary>
    public int Maximum { get => (int)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    /// <summary>Indica si el valor da la vuelta al llegar al límite.</summary>
    public bool Wrap { get => (bool)GetValue(WrapProperty); set => SetValue(WrapProperty, value); }

    /// <summary>Formato numérico.</summary>
    public string Format { get => (string)GetValue(FormatProperty); set => SetValue(FormatProperty, value); }

    private static object CoerceValue(DependencyObject d, object value)
    {
        var spinner = (NumberSpinner)d;
        return Math.Clamp((int)value, spinner.Minimum, Math.Max(spinner.Minimum, spinner.Maximum));
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((NumberSpinner)d).ShowValue();

    private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var spinner = (NumberSpinner)d;
        spinner.CoerceValue(ValueProperty);
        spinner.ShowValue();
    }

    private void ShowValue()
    {
        if (Box is null) return;
        Box.MaxLength = Math.Max(1, Maximum.ToString().Length);
        Box.Text = Value.ToString(Format);
    }

    private void Step(int delta)
    {
        Commit();
        var next = Value + delta;
        if (next > Maximum) next = Wrap ? Minimum : Maximum;
        else if (next < Minimum) next = Wrap ? Maximum : Minimum;
        Value = next;
    }

    /// <summary>Aplica lo escrito en la caja (o restaura el valor anterior si no es un número).</summary>
    private void Commit()
    {
        if (int.TryParse(Box.Text, out var typed)) Value = typed;
        ShowValue();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up:
                Step(+1);
                e.Handled = true;
                break;
            case Key.Down:
                Step(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                Commit();
                Box.SelectAll();
                e.Handled = true;
                break;
        }
    }
}
