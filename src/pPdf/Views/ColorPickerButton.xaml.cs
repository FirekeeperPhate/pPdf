using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace pPdf.Views;

/// <summary>A toolbar button with a color swatch under its icon that opens a small palette.</summary>
public partial class ColorPickerButton : UserControl
{
    static readonly string[] Colors =
    [
        "#000000", "#434343", "#7F7F7F", "#BFBFBF", "#FFFFFF", "#7B1FA2", "#3949AB", "#1E88E5",
        "#D32F2F", "#F4511E", "#FB8C00", "#FDD835", "#7CB342", "#2E7D32", "#00897B", "#00ACC1",
        "#FFCDD2", "#FFE0B2", "#FFF9C4", "#C8E6C9", "#B2EBF2", "#BBDEFB", "#E1BEE7", "#F8BBD0",
    ];

    Color? _value = System.Windows.Media.Colors.Black;

    public ColorPickerButton()
    {
        InitializeComponent();
        foreach (var hex in Colors)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            var b = new Button
            {
                Width = 24, Height = 24, Margin = new Thickness(2), Padding = new Thickness(0), MinWidth = 0, MinHeight = 0,
                Background = new SolidColorBrush(c), BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x50, 0x80, 0x80, 0x80)),
                BorderThickness = new Thickness(1), Focusable = false, Tag = c, ToolTip = hex,
            };
            b.Click += (_, _) => Choose(c);
            Palette.Children.Add(b);
        }
        UpdateSwatch();
    }

    public event EventHandler? ValueChanged;

    public string Glyph { get => GlyphText.Text; set => GlyphText.Text = value; }
    public bool AllowNone { get => NoneButton.Visibility == Visibility.Visible; set => NoneButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }
    public string Tip { set => Toggle.ToolTip = value; }

    /// <summary>The chosen color, or null for "no color".</summary>
    public Color? Value
    {
        get => _value;
        set { _value = value; UpdateSwatch(); }
    }

    void UpdateSwatch()
    {
        Swatch.Background = _value is { } c ? new SolidColorBrush(c) : null;
        Swatch.BorderBrush = _value == null ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x90, 0x80, 0x80, 0x80)) : null;
        Swatch.BorderThickness = new Thickness(_value == null ? 1 : 0);
        HexBox.Text = _value is { } v ? $"{v.R:X2}{v.G:X2}{v.B:X2}" : "";
    }

    void Choose(Color? c)
    {
        _value = c;
        UpdateSwatch();
        Toggle.IsChecked = false;
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    void OnNoneClick(object sender, RoutedEventArgs e) => Choose(null);

    void OnHexKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        string text = HexBox.Text.Trim().TrimStart('#');
        try
        {
            if (text.Length is 6 or 8 && ColorConverter.ConvertFromString("#" + text) is Color c)
            {
                if (text.Length == 6) c.A = 255;
                Choose(c);
            }
        }
        catch (FormatException) { }
        e.Handled = true;
    }
}
