using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using pPdf.Annotations;
using pPdf.Viewer;

namespace pPdf;

/// <summary>The annotation tools and the property bar for the selected annotation.</summary>
public partial class MainWindow
{
    void OnTextTool(object sender, RoutedEventArgs e)
    {
        Viewer.Tool = TextToolButton.IsChecked == true ? ViewerTool.Text : ViewerTool.Select;
        UpdateAnnotationBar();
        UpdateUi();
    }

    void OnAddImage(object sender, RoutedEventArgs e)
    {
        if (_pdf == null) return;
        var dlg = new OpenFileDialog
        {
            Title = "Add image",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        if (!Viewer.AddImageFile(dlg.FileName))
            MessageBox.Show(this, "That image could not be read.", "pPdf", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    void OnUndo(object sender, RoutedEventArgs e) => Viewer.Annotations.Undo();
    void OnRedo(object sender, RoutedEventArgs e) => Viewer.Annotations.Redo();
    void OnDeleteAnnotation(object sender, RoutedEventArgs e) => Viewer.DeleteSelected();

    // ------------------------------------------------------------------ the property bar

    /// <summary>The text style the bar edits: the selected text box, or the defaults for new ones.</summary>
    TextAnnotation StyleSource => Viewer.SelectedAnnotation as TextAnnotation ?? Viewer.TextDefaults;

    void UpdateAnnotationBar()
    {
        var sel = Viewer.SelectedAnnotation;
        bool textTool = Viewer.Tool == ViewerTool.Text;
        AnnotationBar.Visibility = sel != null || textTool ? Visibility.Visible : Visibility.Collapsed;
        bool isText = sel is TextAnnotation || (sel == null && textTool);
        TextFormatPanel.Visibility = isText ? Visibility.Visible : Visibility.Collapsed;
        ImageHint.Visibility = sel is ImageAnnotation ? Visibility.Visible : Visibility.Collapsed;
        if (!isText) return;

        var t = StyleSource;
        _syncing = true;
        try
        {
            FontBox.SelectedItem = FontBox.Items.Cast<string>().FirstOrDefault(f => string.Equals(f, t.FontFamily, StringComparison.OrdinalIgnoreCase));
            if (FontBox.SelectedItem == null) FontBox.Text = t.FontFamily;
            SizeBox.Text = t.FontSize.ToString("0.#", CultureInfo.InvariantCulture);
            BoldToggle.IsChecked = t.Bold;
            ItalicToggle.IsChecked = t.Italic;
            TextColorButton.Value = t.Foreground;
            FillColorButton.Value = t.Background;
        }
        finally { _syncing = false; }
    }

    /// <summary>Applies a style change to the selected text box, or to the defaults when none is selected.</summary>
    void ChangeText(Action<TextAnnotation> change)
    {
        if (_syncing) return;
        if (Viewer.SelectedAnnotation is TextAnnotation) Viewer.ModifySelected(a => change((TextAnnotation)a));
        else change(Viewer.TextDefaults);
    }

    void OnFontChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || FontBox.SelectedItem is not string family) return;
        ChangeText(t => t.FontFamily = family);
    }

    void OnSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || SizeBox.SelectedItem is not string s) return;
        ApplySize(s);
    }

    void OnSizeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplySize(SizeBox.Text);
        e.Handled = true;
        Viewer.Focus();
    }

    void ApplySize(string text)
    {
        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out double size) &&
            !double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out size)) return;
        size = Math.Clamp(size, 4, 400);
        ChangeText(t => t.FontSize = size);
    }

    void OnBoldClick(object sender, RoutedEventArgs e) { bool v = BoldToggle.IsChecked == true; ChangeText(t => t.Bold = v); }
    void OnItalicClick(object sender, RoutedEventArgs e) { bool v = ItalicToggle.IsChecked == true; ChangeText(t => t.Italic = v); }
    void OnTextColorChanged(object? sender, EventArgs e) { var c = TextColorButton.Value ?? System.Windows.Media.Colors.Black; ChangeText(t => t.Foreground = c); }
    void OnFillColorChanged(object? sender, EventArgs e) { var c = FillColorButton.Value; ChangeText(t => t.Background = c); }
}
