using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using pPdf.Pdf;
using pPdf.Viewer;

namespace pPdf.Services;

/// <summary>A page in the thumbnail list: its size is known at once, the bitmap arrives when the row scrolls into view.</summary>
public sealed partial class ThumbItem : ObservableObject
{
    public const double ThumbWidth = 128;

    readonly int _page;
    bool _requested;

    public ThumbItem(int page, PageSize size)
    {
        _page = page;
        double aspect = size.Height / size.Width;
        Width = ThumbWidth;
        Height = Math.Clamp(ThumbWidth * aspect, 40, ThumbWidth * 3);
    }

    public int Number => _page + 1;
    public double Width { get; }
    public double Height { get; }

    [ObservableProperty] public partial BitmapSource? Image { get; set; }

    public async void RequestLoad(RenderService renderer, PdfFile pdf, double dpiScale)
    {
        if (_requested) return;
        _requested = true;
        try
        {
            int w = Math.Max(8, (int)Math.Round(Width * dpiScale)), h = Math.Max(8, (int)Math.Round(Height * dpiScale));
            var key = new RenderKey(_page, w, h, 0, 0, w, h, 0, false);
            Image = await renderer.RenderAsync(pdf, key, priority: 2, CancellationToken.None);
        }
        catch (Exception)
        {
            _requested = false; // cancelled when the document closed: nothing to show
        }
    }
}
