using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using pPdf.Annotations;

namespace pPdf;

/// <summary>The highlight / underline / strike-through button: it marks the selection with the last kind chosen; its arrow chooses.</summary>
public partial class MainWindow
{
    MarkupKind _markupKind = MarkupKind.Highlight;

    void InitMarkupButton() => ShowMarkupKind(MarkupKind.Highlight);

    static string MarkupName(MarkupKind kind) => kind switch
    {
        MarkupKind.Underline => "Underline",
        MarkupKind.Strikeout => "Strike through",
        _ => "Highlight",
    };

    /// <summary>The icon of a kind of markup (strike-through has no glyph in the icon font: a struck S).</summary>
    static object MarkupIcon(MarkupKind kind, double size = 16) => kind switch
    {
        MarkupKind.Underline => new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = size },
        MarkupKind.Strikeout => new TextBlock { Text = "S", FontFamily = new FontFamily("Segoe UI"), FontSize = size - 1, FontWeight = FontWeights.SemiBold, TextDecorations = TextDecorations.Strikethrough },
        _ => new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = size },
    };

    void ShowMarkupKind(MarkupKind kind)
    {
        _markupKind = kind;
        MarkupButton.Content = MarkupIcon(kind);
        MarkupButton.ToolTip = MarkupName(kind) + " the selected text (the arrow chooses another kind)";
    }

    void OnMarkupClick(object sender, RoutedEventArgs e) => ApplyMarkup(_markupKind);

    void OnMarkupMenu(object sender, RoutedEventArgs e)
    {
        var menu = NewMenu(MarkupGroup);
        AddMarkupItems(menu.Items);
        menu.IsOpen = true;
    }

    /// <summary>The three kinds, each marking the selection (or changing the selected markup) and becoming the button's kind.</summary>
    void AddMarkupItems(ItemCollection items)
    {
        foreach (var kind in new[] { MarkupKind.Highlight, MarkupKind.Underline, MarkupKind.Strikeout })
        {
            var item = new MenuItem { Header = MarkupName(kind), Icon = MarkupIcon(kind, 14), IsChecked = kind == _markupKind };
            item.Click += (_, _) => { ShowMarkupKind(kind); ApplyMarkup(kind); };
            items.Add(item);
        }
    }

    /// <summary>On a selected markup the buttons change its kind; with selected text they mark it.</summary>
    async void ApplyMarkup(MarkupKind kind)
    {
        if (_pdf == null) return;
        if (Viewer.SelectedAnnotation is MarkupAnnotation)
        {
            Viewer.ModifySelected(a => ((MarkupAnnotation)a).Kind = kind);
            return;
        }
        var color = MarkupColorButton.Value ?? Viewer.MarkupColor;
        if (!await Viewer.AddMarkupFromSelectionAsync(kind, color))
            StatusLeft.Text = "Select some text first, then choose highlight, underline or strike-through.";
        Viewer.Focus();
    }

    void OnMarkupColorChanged(object? sender, EventArgs e)
    {
        if (_syncing) return;
        var color = MarkupColorButton.Value ?? Viewer.MarkupColor;
        Viewer.MarkupColor = color;
        if (Viewer.SelectedAnnotation is MarkupAnnotation) Viewer.ModifySelected(a => ((MarkupAnnotation)a).Color = color);
    }
}
