using System.Windows;
using pPdf.Annotations;

namespace pPdf;

/// <summary>The highlight / underline / strike-through buttons.</summary>
public partial class MainWindow
{
    void OnHighlight(object sender, RoutedEventArgs e) => ApplyMarkup(MarkupKind.Highlight);
    void OnUnderline(object sender, RoutedEventArgs e) => ApplyMarkup(MarkupKind.Underline);
    void OnStrike(object sender, RoutedEventArgs e) => ApplyMarkup(MarkupKind.Strikeout);

    /// <summary>On a selected markup the buttons change its kind; with selected text they mark it.</summary>
    void ApplyMarkup(MarkupKind kind)
    {
        if (_pdf == null) return;
        if (Viewer.SelectedAnnotation is MarkupAnnotation)
        {
            Viewer.ModifySelected(a => ((MarkupAnnotation)a).Kind = kind);
            return;
        }
        var color = MarkupColorButton.Value ?? Viewer.MarkupColor;
        if (!Viewer.AddMarkupFromSelection(kind, color))
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
