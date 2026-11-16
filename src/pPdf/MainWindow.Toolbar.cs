using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using pPdf.Viewer;
using pPdf.Views;

namespace pPdf;

/// <summary>The » menu: the tools the window is too narrow to show, with their names and shortcuts.</summary>
public partial class MainWindow
{
    void OnOverflowClick(object sender, RoutedEventArgs e)
    {
        var menu = NewMenu(OverflowButton);
        foreach (UIElement child in ToolStrip.Children)
        {
            if (child == OverflowButton || !ToolStrip.IsHidden(child)) continue;
            if (child is Border { Child: null })
            {
                if (menu.Items.Count > 0 && menu.Items[^1] is not Separator) menu.Items.Add(new Separator());
                continue;
            }
            AddOverflowEntries(menu.Items, child);
        }
        if (menu.Items.Count > 0 && menu.Items[^1] is Separator) menu.Items.RemoveAt(menu.Items.Count - 1);
        menu.IsOpen = true;
    }

    void AddOverflowEntries(ItemCollection items, UIElement element)
    {
        if (element == LayoutButton)
        {
            var view = new MenuItem { Header = "View", Icon = Glyph(""), IsEnabled = _pdf != null };
            FillViewMenu(view.Items);
            items.Add(view);
            return;
        }
        if (element == MarkupGroup)
        {
            AddMarkupItems(items);
            var color = new MenuItem { Header = "Color of highlights and lines", Icon = Glyph("") };
            foreach (var hex in ColorPickerButton.PaletteColors)
            {
                var c = (Color)ColorConverter.ConvertFromString(hex);
                var swatch = new Border { Width = 14, Height = 14, Background = new SolidColorBrush(c), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(0.5) };
                var item = new MenuItem { Header = hex, Icon = swatch, IsChecked = MarkupColorButton.Value == c };
                item.Click += (_, _) => MarkupColorButton.Pick(c);
                color.Items.Add(item);
            }
            items.Add(color);
            return;
        }
        if (element == ZoomGroup)
        {
            items.Add(Entry("Zoom in", "Ctrl++", "", () => Viewer.ZoomIn()));
            items.Add(Entry("Zoom out", "Ctrl+-", "", () => Viewer.ZoomOut()));
            items.Add(Entry("Fit width", "Ctrl+2", null, () => Viewer.SetZoomMode(ZoomMode.FitWidth)));
            items.Add(Entry("Fit page", "Ctrl+0", null, () => Viewer.SetZoomMode(ZoomMode.FitPage)));
            return;
        }
        if (element == PageGroup)
        {
            items.Add(Entry("Previous page", null, "", Viewer.PreviousPage));
            items.Add(Entry("Next page", null, "", Viewer.NextPage));
            items.Add(Entry("Go to page...", "Ctrl+G", null, AskPage));
            return;
        }
        if (element is Panel panel)
        {
            foreach (UIElement c in panel.Children) AddOverflowEntries(items, c);
            return;
        }
        if (element is ButtonBase button && button.ToolTip is string tip)
        {
            var (header, gesture) = SplitTip(tip);
            if (button.Tag is string label) header = label; // a short name for the menu where the tooltip explains more
            var item = new MenuItem { Header = header, InputGestureText = gesture ?? "", Icon = button.Content is string g ? Glyph(g) : null, IsEnabled = button.IsEnabled };
            if (button is ToggleButton toggle) item.IsChecked = toggle.IsChecked == true;
            item.Click += (_, _) =>
            {
                if (button is ToggleButton t) t.IsChecked = t.IsChecked != true;
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
            };
            items.Add(item);
        }
    }

    void AskPage()
    {
        if (_pdf == null) return;
        var text = InputDialog.Ask(this, "Go to page", $"Page (1 to {_pdf.PageCount}):", (Viewer.CurrentPage + 1).ToString(), "Go");
        if (int.TryParse(text, out int n)) Viewer.JumpToPage(n - 1);
    }

    MenuItem Entry(string header, string? gesture, string? glyph, Action action)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture ?? "", Icon = glyph == null ? null : Glyph(glyph), IsEnabled = _pdf != null };
        item.Click += (_, _) => action();
        return item;
    }

    TextBlock Glyph(string glyph) => new() { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 14 };

    /// <summary>"Zoom out (Ctrl+-)" gives ("Zoom out", "Ctrl+-"); a tip without a shortcut keeps its words.</summary>
    static (string Header, string? Gesture) SplitTip(string tip)
    {
        var m = Regex.Match(tip, @"^(.*?)\s*\(((?:Ctrl|Alt|Shift|F\d|[A-Z])[^)]*)\)(.*)$");
        if (!m.Success) return (tip, null);
        string rest = m.Groups[3].Value.Trim();
        return ((m.Groups[1].Value + (rest.Length > 0 ? " " + rest : "")).Trim(), m.Groups[2].Value);
    }
}
