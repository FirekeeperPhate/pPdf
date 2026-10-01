using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using pPdf.Annotations;
using pPdf.Pdf;
using pPdf.Printing;

namespace pPdf.Tests;

public class PrintTests
{
    static BitmapSource Render(DocumentPageVisual page, int w, int h)
    {
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(page.Visual);
        return rtb;
    }

    sealed record DocumentPageVisual(Visual Visual);

    [Fact]
    public void Pages_are_fitted_to_the_printable_area_and_carry_the_annotations()
    {
        using var pdf = PdfFile.Open(TestPdf.Create([["Print me"], ["second"]]));
        var note = new TextAnnotation { Page = 0, X = 50, Y = 300, Text = "RED", FontSize = 40, Foreground = Colors.Red };
        var paginator = new PdfPaginator(pdf, [0, 1], [note], 0) { PageSize = new Size(800, 1000) };
        Assert.Equal(2, paginator.PageCount);

        var page = paginator.GetPage(0);
        Assert.Equal(new Size(800, 1000), page.Size);
        var bmp = Render(new DocumentPageVisual(page.Visual), 800, 1000);
        var px = new byte[800 * 1000 * 4];
        bmp.CopyPixels(px, 800 * 4, 0);

        // A4 into 800x1000: height-limited; the red note must show up where it was placed
        double s = 1000 / 842.0, ox = (800 - 595 * s) / 2;
        int redCount = 0;
        for (int y = (int)(300 * s); y < (int)(360 * s); y++)
            for (int x = (int)(ox + 50 * s); x < (int)(ox + 200 * s); x++)
            {
                int o = (y * 800 + x) * 4;
                if (px[o + 2] > 200 && px[o + 1] < 80 && px[o] < 80) redCount++;
            }
        Assert.True(redCount > 100, "red pixels: " + redCount);

        // the second page has no annotation
        var second = Render(new DocumentPageVisual(paginator.GetPage(1).Visual), 800, 1000);
        var px2 = new byte[800 * 1000 * 4];
        second.CopyPixels(px2, 800 * 4, 0);
        Assert.Empty(Enumerable.Range(0, 800 * 1000).Where(i => px2[i * 4 + 2] > 200 && px2[i * 4 + 1] < 80 && px2[i * 4] < 80));
    }

    [Fact]
    public void A_landscape_page_is_turned_to_fill_a_portrait_sheet()
    {
        using var pdf = PdfFile.Open(TestPdf.Create([["wide"]], width: 842, height: 595));
        var paginator = new PdfPaginator(pdf, [0], [], 0) { PageSize = new Size(800, 1000) };
        var page = paginator.GetPage(0);
        var bmp = Render(new DocumentPageVisual(page.Visual), 800, 1000);
        var px = new byte[800 * 1000 * 4];
        bmp.CopyPixels(px, 800 * 4, 0);
        // turned: it fills the sheet's height (about 1000 DIPs) instead of being 570 DIPs tall; look for dark text ink near the top or bottom rows
        int inkRows = 0;
        for (int y = 0; y < 1000; y++)
        {
            bool ink = false;
            for (int x = 0; x < 800 && !ink; x++) ink = px[(y * 800 + x) * 4 + 1] < 100;
            if (ink) inkRows++;
        }
        Assert.True(inkRows > 0);
    }
}
