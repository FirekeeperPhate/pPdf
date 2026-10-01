using System.Windows;
using pPdf.Pdf;

namespace pPdf.Tests;

public class PdfFileTests
{
    [Fact]
    public void Opens_document_and_reads_page_sizes()
    {
        using var pdf = PdfFile.Open(TestPdf.Simple());
        Assert.Equal(3, pdf.PageCount);
        Assert.Equal(595, pdf.Pages[0].Width, 1);
        Assert.Equal(842, pdf.Pages[0].Height, 1);
    }

    [Fact]
    public void Garbage_is_rejected()
    {
        Assert.Throws<PdfException>(() => PdfFile.Open([1, 2, 3, 4, 5]));
        Assert.Throws<PdfException>(() => PdfFile.Open(Array.Empty<byte>()));
    }

    [Fact]
    public void Extracts_text_with_boxes_in_display_space()
    {
        using var pdf = PdfFile.Open(TestPdf.Simple());
        var t = pdf.LoadText(0, true)!;
        Assert.Contains("Hello world", t.Text);
        Assert.NotNull(t.Boxes);
        Assert.Equal(t.Text.Length, t.Boxes!.Length);
        int i = t.Text.IndexOf("Hello", StringComparison.Ordinal);
        var rects = t.RectsForRange(i, i + 5);
        Assert.Single(rects);
        // drawn at x = 60, baseline y = 60 in top-left coordinates
        Assert.InRange(rects[0].X, 55, 65);
        Assert.InRange(rects[0].Y, 40, 62);
        Assert.InRange(rects[0].Bottom, 58, 75);
    }

    [Fact]
    public void Text_boxes_follow_page_rotation()
    {
        using var pdf = PdfFile.Open(TestPdf.Create([["Rotated"]], rotate: 90));
        Assert.Equal(842, pdf.Pages[0].Width, 1);
        Assert.Equal(595, pdf.Pages[0].Height, 1);
        var t = pdf.LoadText(0, true)!;
        int i = t.Text.IndexOf("Rotated", StringComparison.Ordinal);
        var rects = t.RectsForRange(i, i + 7);
        // after a 90 degree rotation the text runs top to bottom along the right edge: each letter sits below the previous one
        Assert.True(rects.Count > 3);
        Assert.InRange(rects[0].X, 770, 800);
        Assert.True(rects[3].Y > rects[0].Y + 10);
    }

    [Fact]
    public void Renders_a_page_region()
    {
        using var pdf = PdfFile.Open(TestPdf.Simple());
        var bmp = pdf.Render(0, 595, 842, new Int32Rect(0, 0, 595, 842), 0, false)!;
        Assert.Equal(595, bmp.PixelWidth);
        Assert.True(bmp.IsFrozen);
        var px = new byte[4];
        bmp.CopyPixels(new Int32Rect(2, 2, 1, 1), px, 4, 0);
        Assert.Equal(255, px[0]); // white corner

        var inverted = pdf.Render(0, 595, 842, new Int32Rect(0, 0, 100, 100), 0, true)!;
        inverted.CopyPixels(new Int32Rect(2, 2, 1, 1), px, 4, 0);
        Assert.Equal(24, px[0]); // night mode: white paper becomes a soft dark gray

        // a region deep inside a heavily zoomed page costs only its own size
        var tile = pdf.Render(0, 5950, 8420, new Int32Rect(500, 400, 300, 200), 0, false)!;
        Assert.Equal(300, tile.PixelWidth);
        Assert.Equal(200, tile.PixelHeight);
    }

    [Fact]
    public void Search_is_case_and_accent_insensitive_and_maps_back_to_the_text()
    {
        using var pdf = PdfFile.Open(TestPdf.Simple());
        var t = pdf.LoadText(1, false)!;
        var hits = TextSearch.FindAll(t, Normalized.NormalizeQuery("NEEDLE"), default);
        Assert.Single(hits);
        Assert.Equal("needle", t.Slice(hits[0].Start, hits[0].End));

        var accents = TextSearch.FindAll(t, "perche a caffe", default);
        Assert.Single(accents);

        Assert.Empty(TextSearch.FindAll(t, "NEEDLE", new SearchOptions(MatchCase: true)));
        Assert.Empty(TextSearch.FindAll(t, "needl", new SearchOptions(WholeWord: true)));
        Assert.Single(TextSearch.FindAll(t, "needle", new SearchOptions(WholeWord: true)));
    }

    [Fact]
    public void Search_spans_line_breaks()
    {
        using var pdf = PdfFile.Open(TestPdf.Create([["alpha beta", "gamma delta"]]));
        var t = pdf.LoadText(0, false)!;
        var hits = TextSearch.FindAll(t, "beta gamma", default);
        Assert.Single(hits);
    }
}

[Collection("OnDemand")]
public class OnDemandFileTests
{
    [Fact]
    public void A_file_above_the_threshold_is_read_from_disk_on_demand()
    {
        string path = Path.Combine(Path.GetTempPath(), "ppdf-ondemand-" + Guid.NewGuid().ToString("N") + ".pdf");
        var bytes = TestPdf.Simple();
        File.WriteAllBytes(path, bytes);
        long old = PdfFile.OnDemandThreshold;
        PdfFile.OnDemandThreshold = 100;
        try
        {
            using (var pdf = PdfFile.Open(path))
            {
                Assert.True(pdf.IsOnDemand);
                Assert.Equal(3, pdf.PageCount);
                Assert.Contains("needle", pdf.LoadText(1, true)!.Text);
                Assert.NotNull(pdf.Render(0, 300, 400, new System.Windows.Int32Rect(0, 0, 300, 400), 0, false));
                Assert.Equal(bytes, pdf.GetBytes());
                // the file stays open for reading: it cannot be changed under PDFium's feet
                Assert.Throws<IOException>(() => File.WriteAllBytes(path, [1, 2, 3]));
            }
            File.Delete(path); // released on dispose
            Assert.False(File.Exists(path));
        }
        finally
        {
            PdfFile.OnDemandThreshold = old;
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void A_wrong_file_above_the_threshold_still_gives_a_clean_error()
    {
        string path = Path.Combine(Path.GetTempPath(), "ppdf-bad-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(path, new byte[500]);
        long old = PdfFile.OnDemandThreshold;
        PdfFile.OnDemandThreshold = 100;
        try { Assert.Throws<PdfException>(() => PdfFile.Open(path)); File.Delete(path); }
        finally { PdfFile.OnDemandThreshold = old; if (File.Exists(path)) File.Delete(path); }
    }
}

public class NightModeTests
{
    static byte[] PageWithBlocks()
    {
        var doc = new PdfSharp.Pdf.PdfDocument();
        var page = doc.AddPage();
        page.Width = PdfSharp.Drawing.XUnit.FromPoint(200);
        page.Height = PdfSharp.Drawing.XUnit.FromPoint(100);
        using (var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page))
        {
            gfx.DrawRectangle(new PdfSharp.Drawing.XSolidBrush(PdfSharp.Drawing.XColor.FromArgb(255, 220, 30, 30)), 0, 0, 50, 100);   // red
            gfx.DrawRectangle(new PdfSharp.Drawing.XSolidBrush(PdfSharp.Drawing.XColor.FromArgb(255, 30, 60, 220)), 50, 0, 50, 100);  // blue
            gfx.DrawRectangle(new PdfSharp.Drawing.XSolidBrush(PdfSharp.Drawing.XColor.FromArgb(255, 0, 0, 0)), 100, 0, 50, 100);     // black
        }
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    static (byte R, byte G, byte B) Pixel(System.Windows.Media.Imaging.BitmapSource bmp, int x, int y)
    {
        var px = new byte[4];
        bmp.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), px, 4, 0);
        return (px[2], px[1], px[0]);
    }

    [Fact]
    public void Night_mode_flips_lightness_but_keeps_the_hues()
    {
        using var pdf = PdfFile.Open(PageWithBlocks());
        var night = pdf.Render(0, 200, 100, new System.Windows.Int32Rect(0, 0, 200, 100), 0, true)!;
        var normal = pdf.Render(0, 200, 100, new System.Windows.Int32Rect(0, 0, 200, 100), 0, false)!;

        var white = Pixel(night, 180, 50);
        Assert.Equal((24, 24, 24), ((int)white.R, (int)white.G, (int)white.B)); // white paper -> soft dark gray
        var black = Pixel(night, 125, 50);
        Assert.True(black.R > 200 && black.G > 200 && black.B > 200, $"black -> {black}"); // black -> light gray

        var red = Pixel(night, 25, 50);
        Assert.True(red.R > red.G + 30 && red.R > red.B + 30, $"red stays reddish: {red}");
        var blue = Pixel(night, 75, 50);
        Assert.True(blue.B > blue.R + 20 && blue.B > blue.G + 10, $"blue stays bluish: {blue}");

        // a plain inversion would have made the red block cyan
        var original = Pixel(normal, 25, 50);
        Assert.True(original.R > 200);
    }
}

public class PdfInfoTests
{
    [Fact]
    public void Pdf_dates_are_read_with_their_zone()
    {
        var utc = PdfInfo.ParseDate("D:20240131093000Z")!.Value.ToUniversalTime();
        Assert.Equal(new DateTime(2024, 1, 31, 9, 30, 0), utc);
        var plus = PdfInfo.ParseDate("D:20240131093000+02'00'")!.Value.ToUniversalTime();
        Assert.Equal(new DateTime(2024, 1, 31, 7, 30, 0), plus);
        Assert.Equal(new DateTime(2023, 5, 1), PdfInfo.ParseDate("D:20230501")!.Value.Date);
        Assert.Null(PdfInfo.ParseDate(""));
        Assert.Null(PdfInfo.ParseDate("nonsense"));
    }

    [Fact]
    public void Page_sizes_get_their_paper_name()
    {
        Assert.StartsWith("A4", PdfInfo.DescribePageSize(595, 842));
        Assert.StartsWith("A4", PdfInfo.DescribePageSize(842, 595));
        Assert.StartsWith("Letter", PdfInfo.DescribePageSize(612, 792));
        Assert.DoesNotContain("(", PdfInfo.DescribePageSize(300, 300));
    }

    [Fact]
    public void Document_information_is_read()
    {
        var doc = new PdfSharp.Pdf.PdfDocument();
        doc.Info.Title = "Titolo di prova è";
        doc.Info.Author = "Phate";
        doc.AddPage();
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        using var pdf = PdfFile.Open(ms.ToArray());
        var info = pdf.ReadInfo();
        Assert.Equal("Titolo di prova è", info.Title);
        Assert.Equal("Phate", info.Author);
        Assert.False(info.IsEncrypted);
        Assert.False(info.HasForm);
        Assert.NotEmpty(info.Version);
        Assert.NotNull(info.Created);
    }
}
