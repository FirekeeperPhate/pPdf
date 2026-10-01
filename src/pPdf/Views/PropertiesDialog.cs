using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using pPdf.Pdf;

namespace pPdf.Views;

/// <summary>The "Document properties" window: file, pages and what the PDF says about itself.</summary>
public static class PropertiesDialog
{
    public static void Show(Window owner, PdfFile pdf, string? path, int currentPage)
    {
        var info = pdf.ReadInfo();
        var rows = new List<(string Label, string Value)>();
        void Add(string label, string? value) { if (!string.IsNullOrWhiteSpace(value)) rows.Add((label, value!)); }

        if (path != null)
        {
            Add("File", Path.GetFileName(path));
            Add("Folder", Path.GetDirectoryName(path));
            try { Add("Size", FormatSize(new FileInfo(path).Length)); } catch (IOException) { }
        }
        Add("Pages", pdf.PageCount.ToString(CultureInfo.CurrentCulture));
        if (pdf.PageCount > 0)
        {
            int p = Math.Clamp(currentPage, 0, pdf.PageCount - 1);
            Add($"Page {p + 1} size", PdfInfo.DescribePageSize(pdf.Pages[p].Width, pdf.Pages[p].Height));
        }
        Add("PDF version", info.Version);
        Add("Title", info.Title);
        Add("Author", info.Author);
        Add("Subject", info.Subject);
        Add("Keywords", info.Keywords);
        Add("Created with", info.Creator);
        Add("PDF producer", info.Producer);
        Add("Created", info.Created?.ToString("f", CultureInfo.CurrentCulture));
        Add("Modified", info.Modified?.ToString("f", CultureInfo.CurrentCulture));
        Add("Protection", info.IsEncrypted ? "Encrypted" : "None");
        Add("Form", info.HasForm ? "Has fillable fields" : null);
        if (pdf.IsOnDemand) Add("Reading", "From disk on demand (very big file)");

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < rows.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = rows[i].Label, Margin = new Thickness(0, 4, 18, 4), VerticalAlignment = VerticalAlignment.Top };
            label.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush");
            var value = new TextBox
            {
                Text = rows[i].Value, IsReadOnly = true, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent,
                TextWrapping = TextWrapping.Wrap, MaxWidth = 440, Padding = new Thickness(0, 4, 0, 4), VerticalAlignment = VerticalAlignment.Top,
            };
            Grid.SetRow(label, i); Grid.SetRow(value, i); Grid.SetColumn(value, 1);
            grid.Children.Add(label);
            grid.Children.Add(value);
        }

        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true, MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(grid);
        panel.Children.Add(close);
        var win = new Window
        {
            Title = "Document properties",
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 640 },
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            ShowInTaskbar = false,
            MinWidth = 420,
        };
        close.Click += (_, _) => win.Close();
        win.Loaded += (_, _) => Services.ThemeService.ApplyTitleBar(win);
        win.ShowDialog();
    }

    static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB ({bytes:N0} bytes)",
        _ => $"{bytes / 1048576.0:0.##} MB ({bytes:N0} bytes)",
    };
}
