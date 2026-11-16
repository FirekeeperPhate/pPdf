using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using pPdf.Pdf;

namespace pPdf.Tests;

public class OcrTests
{
    /// <summary>A "scanned" PDF: one page that is only a picture of some printed lines, no text of its own.</summary>
    internal static byte[] ScannedPdf(params string[] lines)
    {
        const int W = 1240, H = 1754; // A4 at 150 dpi
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, W, H));
            double y = 150;
            foreach (var line in lines)
            {
                var ft = new FormattedText(line, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Arial"), 40, Brushes.Black, 1.0);
                dc.DrawText(ft, new Point(120, y));
                y += 70;
            }
        }
        var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var png = new MemoryStream();
        enc.Save(png);
        png.Position = 0;

        var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(595);
        page.Height = XUnit.FromPoint(842);
        using (var gfx = XGraphics.FromPdfPage(page))
        using (var img = XImage.FromStream(png))
            gfx.DrawImage(img, 0, 0, 595, 842);
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    [Fact]
    public void A_scanned_page_has_no_text_until_it_is_recognized_and_then_is_searchable()
    {
        using var pdf = PdfFile.Open(ScannedPdf("The quick brown fox jumps", "over the lazy dog 2026"));
        Assert.True(PageOcr.HasNoText(pdf, 0));

        var text = PageOcr.Recognize(pdf, 0);
        if (Environment.GetEnvironmentVariable("PPDF_REQUIRE_OCR") == "1") Assert.NotNull(text);
        if (text == null) return; // no recognizer for the user's languages on this machine: nothing to check
        Assert.Contains("quick", text.Text);
        Assert.Contains("lazy", text.Text);
        Assert.Equal(text.Text.Length, text.Boxes!.Length);

        // the word lands where it was drawn: "quick" starts about 120 + width of "The " pixels at 150 dpi, line at y 150
        int at = text.Text.IndexOf("quick", StringComparison.Ordinal);
        var box = text.Boxes[at];
        Assert.InRange(box.Y, 150 * 72 / 150.0 - 6, 150 * 72 / 150.0 + 24);
        Assert.InRange(box.X, 120 * 72 / 150.0, 400 * 72 / 150.0);

        // once kept on the document, search and selection see it
        pdf.OcrPages[0] = text;
        Assert.NotEmpty(TextSearch.FindAll(pdf.LoadText(0, false)!, "lazy dog", default));
        Assert.True(PageOcr.HasNoText(pdf, 0)); // its own text is still empty
    }

    [Fact]
    public void Recognized_words_become_characters_with_boxes_and_back_into_words()
    {
        var lines = new List<List<WinOcr.Word>>
        {
            new() { new("Hello", 100, 50, 100, 20), new("world", 220, 50, 100, 20) },
            new() { new("again", 100, 100, 100, 20) },
        };
        var text = PageOcr.Build(0, lines, 2.0);
        Assert.Equal("Hello world\nagain", text.Text);
        Assert.Equal(text.Text.Length, text.Boxes!.Length);
        Assert.Equal(new RectF(50, 25, 10, 10), text.Boxes[0]);   // "H": a fifth of the word, halved by the scale
        Assert.Equal(new RectF(100, 25, 10, 10), text.Boxes[5]);  // the space fills the gap between the words
        Assert.Equal([("Hello", new Rect(50, 25, 50, 10)), ("world", new Rect(110, 25, 50, 10)), ("again", new Rect(50, 50, 50, 10))],
            PageOcr.Words(text).Select(w => (w.Word, new Rect(Math.Round(w.Box.X, 3), Math.Round(w.Box.Y, 3), Math.Round(w.Box.Width, 3), Math.Round(w.Box.Height, 3)))));
    }

    [Fact]
    public void A_saved_copy_carries_the_recognized_text_invisibly()
    {
        var scan = ScannedPdf("Searchable everywhere");
        var lines = new List<List<WinOcr.Word>> { new() { new("Searchable", 240, 300, 380, 40), new("everywhere", 640, 300, 380, 40) } };
        var text = PageOcr.Build(0, lines, 150 / 72.0);
        var copy = pPdf.Annotations.PdfAnnotationWriter.Apply(scan, null, [], null, null, new Dictionary<int, PageText> { [0] = text });

        using var pdf = PdfFile.Open(copy);
        var own = pdf.LoadText(0, true, ownTextOnly: true)!;
        Assert.Contains("Searchable", own.Text);
        Assert.Contains("everywhere", own.Text);
        Assert.False(PageOcr.HasNoText(pdf, 0));
        // where it was read
        int at = own.Text.IndexOf("everywhere", StringComparison.Ordinal);
        Assert.InRange(own.Boxes![at].X, 640 * 72 / 150.0 - 4, 640 * 72 / 150.0 + 8);

        // and it cannot be seen: the copy looks like the scan
        using var original = PdfFile.Open(scan);
        var a = Pixels(original.Render(0, 300, 424, new Int32Rect(0, 0, 300, 424), 0, false)!);
        var b = Pixels(pdf.Render(0, 300, 424, new Int32Rect(0, 0, 300, 424), 0, false)!);
        var diff = Enumerable.Range(0, a.Length).Where(i => a[i] != b[i]).ToList();
        Assert.True(diff.Count == 0, $"{diff.Count} bytes differ, first at pixel {(diff.Count > 0 ? diff[0] / 4 % 300 : 0)},{(diff.Count > 0 ? diff[0] / 4 / 300 : 0)}, max {(diff.Count > 0 ? diff.Max(i => Math.Abs(a[i] - b[i])) : 0)}");
    }

    static byte[] Pixels(BitmapSource bmp)
    {
        var px = new byte[bmp.PixelWidth * bmp.PixelHeight * 4];
        bmp.CopyPixels(px, bmp.PixelWidth * 4, 0);
        return px;
    }
}
