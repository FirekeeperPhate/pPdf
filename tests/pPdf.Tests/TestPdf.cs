using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace pPdf.Tests;

/// <summary>Builds small PDFs for the tests with PDFsharp.</summary>
static class TestPdf
{
    public static byte[] Create(string[][] pages, int rotate = 0, double width = 595, double height = 842)
    {
        var doc = new PdfDocument();
        foreach (var lines in pages)
        {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(width);
            page.Height = XUnit.FromPoint(height);
            if (rotate != 0) page.Rotate = rotate;
            using var gfx = XGraphics.FromPdfPage(page);
            var font = new XFont("Arial", 14);
            double y = 60;
            foreach (var line in lines)
            {
                gfx.DrawString(line, font, XBrushes.Black, new XPoint(60, y));
                y += 24;
            }
        }
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    public static byte[] Simple() => Create([
        ["Hello world", "Second line of the first page"],
        ["Page two has the word needle here", "and perché a caffè"],
        ["Third page", "needle again"],
    ]);
}
