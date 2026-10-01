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
        Assert.Equal(0, px[0]);

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
