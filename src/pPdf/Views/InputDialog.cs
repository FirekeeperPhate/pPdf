using System.Windows;
using System.Windows.Controls;

namespace pPdf.Views;

/// <summary>Asks for one line of text (the name of a bookmark...).</summary>
public static class InputDialog
{
    public static string? Ask(Window? owner, string title, string prompt, string initial, string okText = "OK")
    {
        var box = new TextBox { MinWidth = 300, Margin = new Thickness(0, 10, 0, 0), Text = initial };
        var message = new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 };
        var ok = new Button { Content = okText, IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(message);
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var win = new Window
        {
            Title = title,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Owner = owner,
            ShowInTaskbar = false,
        };
        ok.Click += (_, _) => win.DialogResult = true;
        win.Loaded += (_, _) =>
        {
            Services.ThemeService.ApplyTitleBar(win);
            box.Focus();
            box.SelectAll();
        };
        return win.ShowDialog() == true ? box.Text.Trim() : null;
    }
}
