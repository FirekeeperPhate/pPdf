using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace pPdf.Views;

/// <summary>Asks for the password of an encrypted PDF.</summary>
public static class PasswordDialog
{
    public static string? Ask(Window? owner, string fileName, bool wrong)
    {
        var box = new PasswordBox { MinWidth = 280, Margin = new Thickness(0, 10, 0, 0) };
        var message = new TextBlock
        {
            Text = wrong ? "That password is not correct. Try again:" : $"\"{fileName}\" is password protected.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 340,
        };
        var ok = new Button { Content = "Open", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
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
            Title = "Password required",
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
        };
        return win.ShowDialog() == true ? box.Password : null;
    }
}
