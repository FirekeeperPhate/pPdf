using System.Windows;
using System.Windows.Controls;

namespace pPdf.Views;

/// <summary>A message with custom button labels ("Update now", "Skip this version", "Later"...).</summary>
public static class ChoiceDialog
{
    /// <returns>The index of the button pressed, or the last one for Esc / closing the window.</returns>
    public static int Ask(Window? owner, string title, string message, params string[] buttons)
    {
        int result = buttons.Length - 1;
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var win = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Owner = owner,
            ShowInTaskbar = false,
        };
        for (int i = 0; i < buttons.Length; i++)
        {
            int index = i;
            var b = new Button
            {
                Content = buttons[i], MinWidth = 90, Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0), Padding = new Thickness(14, 6, 14, 6),
                IsDefault = i == 0, IsCancel = i == buttons.Length - 1,
            };
            b.Click += (_, _) => { result = index; win.Close(); };
            row.Children.Add(b);
        }
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(text);
        panel.Children.Add(row);
        win.Content = panel;
        win.Loaded += (_, _) => Services.ThemeService.ApplyTitleBar(win);
        win.ShowDialog();
        return result;
    }
}
