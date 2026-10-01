using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using pPdf.Annotations;
using pPdf.Pdf;

namespace pPdf.Printing;

/// <summary>Feeds the pages of a PDF (with its annotations) to WPF printing, each fitted into the printable area.</summary>
public sealed class PdfPaginator : DocumentPaginator
{
    const double PrintDpi = 300;

    readonly PdfFile _pdf;
    readonly int[] _pages;
    readonly IReadOnlyList<Annotation> _annotations;
    readonly int _viewRotation;

    /// <param name="pages">0-based page indices to print, in order.</param>
    /// <param name="viewRotation">Quarter turns the user rotated the view by; printed pages follow it.</param>
    public PdfPaginator(PdfFile pdf, int[] pages, IReadOnlyList<Annotation> annotations, int viewRotation)
    {
        _pdf = pdf;
        _pages = pages;
        _annotations = annotations;
        _viewRotation = viewRotation & 3;
    }

    public override bool IsPageCountValid => true;
    public override int PageCount => _pages.Length;
    public override Size PageSize { get; set; } = new(816, 1056);
    public override IDocumentPaginatorSource? Source => null;

    public override DocumentPage GetPage(int pageNumber)
    {
        int page = _pages[pageNumber];
        var size = _pdf.Pages[page];
        var area = PageSize;

        // turn the page by a quarter if that makes it use the sheet much better (a landscape page on a portrait sheet)
        int rotation = _viewRotation;
        double w = (rotation & 1) == 1 ? size.Height : size.Width, h = (rotation & 1) == 1 ? size.Width : size.Height;
        double fit = Math.Min(area.Width / w, area.Height / h);
        double fitTurned = Math.Min(area.Width / h, area.Height / w);
        if (fitTurned > fit * 1.1)
        {
            rotation = (rotation + 1) & 3;
            (w, h) = (h, w);
            fit = fitTurned;
        }

        double dipW = w * fit, dipH = h * fit;
        double x = (area.Width - dipW) / 2, y = (area.Height - dipH) / 2;
        int pxW = Math.Max(1, (int)Math.Round(dipW / 96 * PrintDpi)), pxH = Math.Max(1, (int)Math.Round(dipH / 96 * PrintDpi));

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, area.Width, area.Height));
            PageComposer.Draw(dc, _pdf, page, new Rect(x, y, dipW, dipH), rotation, pxW, pxH, _annotations);
        }
        return new DocumentPage(visual, area, new Rect(area), new Rect(area));
    }
}
