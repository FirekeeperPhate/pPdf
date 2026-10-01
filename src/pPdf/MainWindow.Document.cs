using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using pPdf.Printing;
using pPdf.Views;

namespace pPdf;

/// <summary>Document-level commands of the More menu: properties, show in folder, copy path, export a page as an image.</summary>
public partial class MainWindow
{
    void ShowProperties()
    {
        if (_pdf != null) PropertiesDialog.Show(this, _pdf, _path, Viewer.CurrentPage);
    }

    void ShowInFolder()
    {
        if (_path == null || !File.Exists(_path)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_path}\"") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    void CopyPath()
    {
        if (_path == null) return;
        try { Clipboard.SetDataObject(_path, true); }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    /// <summary>Saves the page that is on screen (with its annotations) as a PNG or JPEG.</summary>
    async Task ExportPageImageAsync()
    {
        if (_pdf == null || _busy) return;
        Viewer.CommitEdits();
        int choice = ChoiceDialog.Ask(this, "Export page as image", $"Page {Viewer.CurrentPage + 1}: choose the resolution.", "150 dpi", "300 dpi", "600 dpi", "Cancel");
        if (choice is < 0 or > 2) return;
        int dpi = choice switch { 0 => 150, 1 => 300, _ => 600 };

        var dlg = new SaveFileDialog
        {
            Title = "Export page as image",
            Filter = "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg",
            FileName = $"{Path.GetFileNameWithoutExtension(_path) ?? "page"} - page {Viewer.CurrentPage + 1}",
            InitialDirectory = _path != null ? Path.GetDirectoryName(_path) : null,
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != true) return;

        var pdf = _pdf;
        int page = Viewer.CurrentPage, rotation = Viewer.Rotation;
        var annotations = Viewer.Annotations.Items.ToList();
        var form = Viewer.Form;
        bool jpeg = dlg.FilterIndex == 2 || dlg.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || dlg.FileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);
        SetBusy(true, "Exporting...");
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background); // let the overlay paint before the heavy part
        try
        {
            // WPF drawing needs this thread: PDFium does the heavy part, the encoding follows off it
            var image = PageComposer.RenderImage(pdf, page, dpi, rotation, annotations, form);
            await Task.Run(() =>
            {
                BitmapEncoder encoder = jpeg ? new JpegBitmapEncoder { QualityLevel = 92 } : new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using var fs = File.Create(dlg.FileName);
                encoder.Save(fs);
            });
        }
        catch (Exception ex)
        {
            SetBusy(false);
            MessageBox.Show(this, "The image could not be saved.\n\n" + ex.Message, "pPdf", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SetBusy(false); }
    }
}
