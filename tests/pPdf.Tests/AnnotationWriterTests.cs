using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using pPdf.Annotations;
using pPdf.Pdf;

namespace pPdf.Tests;

public class AnnotationWriterTests
{
    static byte[] SolidPng(Color c, int w = 8, int h = 4)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            // premultiplied BGRA
            px[i * 4] = (byte)(c.B * c.A / 255); px[i * 4 + 1] = (byte)(c.G * c.A / 255); px[i * 4 + 2] = (byte)(c.R * c.A / 255); px[i * 4 + 3] = c.A;
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, w * 4);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    static byte[] BlankPdf(int rotate, bool crop)
    {
        var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(595);
        page.Height = XUnit.FromPoint(842);
        if (rotate != 0) page.Rotate = rotate;
        if (crop) page.CropBox = new PdfRectangle(new XPoint(30, 50), new XPoint(430, 650));
        using (var gfx = XGraphics.FromPdfPage(page)) gfx.DrawString(" ", new XFont("Arial", 10), XBrushes.Black, new XPoint(10, 10));
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    /// <summary>Bounding box of the pixels that are clearly red, in device pixels at 1 px per point.</summary>
    static (int L, int T, int R, int B)? RedBox(PdfFile pdf)
    {
        int w = (int)Math.Round(pdf.Pages[0].Width), h = (int)Math.Round(pdf.Pages[0].Height);
        var bmp = pdf.Render(0, w, h, new Int32Rect(0, 0, w, h), 0, false)!;
        var px = new byte[w * h * 4];
        bmp.CopyPixels(px, w * 4, 0);
        int l = int.MaxValue, t = int.MaxValue, r = -1, b = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                if (px[o + 2] > 200 && px[o + 1] < 60 && px[o] < 60)
                {
                    l = Math.Min(l, x); t = Math.Min(t, y); r = Math.Max(r, x); b = Math.Max(b, y);
                }
            }
        return r < 0 ? null : (l, t, r, b);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(90, false)]
    [InlineData(180, false)]
    [InlineData(270, false)]
    [InlineData(0, true)]
    [InlineData(90, true)]
    [InlineData(180, true)]
    [InlineData(270, true)]
    public void Image_lands_where_the_user_placed_it_for_every_rotation_and_crop(int rotate, bool crop)
    {
        var img = ImageAnnotation.FromBytes(SolidPng(Colors.Red));
        img.Page = 0; img.X = 100; img.Y = 200; img.Width = 40; img.Height = 20;

        var saved = PdfAnnotationWriter.Apply(BlankPdf(rotate, crop), null, [img]);
        using var pdf = PdfFile.Open(saved);
        var box = RedBox(pdf);
        Assert.NotNull(box);
        // pdfium draws in the displayed orientation: the box must be exactly the rectangle that was placed
        Assert.InRange(box!.Value.L, 99, 101);
        Assert.InRange(box.Value.T, 199, 201);
        Assert.InRange(box.Value.R, 138, 140);
        Assert.InRange(box.Value.B, 218, 220);
    }

    [Fact]
    public void Huge_image_is_shown_at_reduced_size_but_keeps_its_natural_size_and_original_bytes()
    {
        var bytes = SolidPng(Colors.Blue, 6000, 300);
        var img = ImageAnnotation.FromBytes(bytes);
        Assert.Equal(6000, img.NaturalSize.Width);
        Assert.Equal(300, img.NaturalSize.Height);
        Assert.Equal(ImageAnnotation.MaxDecodePixels, img.Source.PixelWidth);
        Assert.True(img.Source.PixelHeight is >= 200 and <= 210);
        Assert.Same(bytes, img.Data); // the embedded file is the original, untouched
    }

    [Fact]
    public void Transparent_png_lets_the_page_show_through()
    {
        var img = ImageAnnotation.FromBytes(SolidPng(Color.FromArgb(128, 255, 0, 0)));
        img.Page = 0; img.X = 100; img.Y = 200; img.Width = 40; img.Height = 20;
        var saved = PdfAnnotationWriter.Apply(BlankPdf(0, false), null, [img]);
        using var pdf = PdfFile.Open(saved);
        var bmp = pdf.Render(0, 595, 842, new Int32Rect(0, 0, 595, 842), 0, false)!;
        var px = new byte[4];
        bmp.CopyPixels(new Int32Rect(120, 210, 1, 1), px, 4, 0);
        // half red over white = light red: green and blue stay high
        Assert.True(px[2] > 240, $"R {px[2]}");
        Assert.InRange(px[1], 100, 160);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Text_is_written_as_real_text_at_the_right_place(int rotate)
    {
        var t = new TextAnnotation { Page = 0, X = 100, Y = 200, Text = "Ciao perché mondo", FontSize = 18 };
        var saved = PdfAnnotationWriter.Apply(BlankPdf(rotate, false), null, [t]);
        using var pdf = PdfFile.Open(saved);
        var text = pdf.LoadText(0, true)!;
        int i = text.Text.IndexOf("Ciao", StringComparison.Ordinal);
        Assert.True(i >= 0, text.Text);
        Assert.Contains("perché", text.Text);
        var first = text.RectsForRange(i, i + 4);
        Assert.NotEmpty(first);
        // the first letter sits at the annotation's top-left corner (plus padding)
        Assert.InRange(first[0].X, 99, 110);
        Assert.InRange(first[0].Y, 198, 215);
    }

    [Fact]
    public void Text_background_is_filled()
    {
        var t = new TextAnnotation { Page = 0, X = 100, Y = 200, Width = 80, Height = 30, Text = "x", Background = Colors.Red };
        var saved = PdfAnnotationWriter.Apply(BlankPdf(0, false), null, [t]);
        using var pdf = PdfFile.Open(saved);
        var box = RedBox(pdf)!.Value;
        Assert.InRange(box.L, 99, 101);
        Assert.InRange(box.T, 199, 201);
        Assert.InRange(box.R, 178, 180);
        Assert.InRange(box.B, 228, 230);
    }
}

public class MarkupWriterTests
{
    [Fact]
    public void Highlight_is_translucent_so_the_text_stays_readable_and_underline_and_strike_are_drawn()
    {
        var bytes = TestPdf.Create([["Highlight me please"]]);
        using var src = PdfFile.Open(bytes);
        var text = src.LoadText(0, true)!;
        int s = text.Text.IndexOf("me", StringComparison.Ordinal);
        var rect = text.RectsForRange(s, s + 2)[0];
        var rects = new[] { rect.ToRect() };

        var hi = MarkupAnnotation.Create(0, MarkupKind.Highlight, Colors.Yellow, rects);
        var ul = MarkupAnnotation.Create(0, MarkupKind.Underline, Colors.Red, [new Rect(60, 200, 80, 14)]);
        var st = MarkupAnnotation.Create(0, MarkupKind.Strikeout, Colors.Blue, [new Rect(60, 300, 80, 14)]);
        var saved = PdfAnnotationWriter.Apply(bytes, null, [hi, ul, st]);
        using var pdf = PdfFile.Open(saved);
        var bmp = pdf.Render(0, 595, 842, new Int32Rect(0, 0, 595, 842), 0, false)!;

        byte[] Px(int x, int y) { var p = new byte[4]; bmp.CopyPixels(new Int32Rect(x, y, 1, 1), p, 4, 0); return p; }
        // somewhere in the band the paper is tinted light yellow (red and green high, blue clearly below white)
        bool yellow = false;
        for (int y = (int)rect.Y; y < (int)rect.Bottom && !yellow; y++)
            for (int x = (int)rect.X; x < (int)rect.Right && !yellow; x++) { var p = Px(x, y); yellow = p[2] > 230 && p[1] > 230 && p[0] is > 100 and < 200; }
        Assert.True(yellow, "the highlighted band is tinted yellow");
        // the text is still dark somewhere in the band
        bool dark = false;
        for (int y = (int)rect.Y; y < (int)rect.Bottom && !dark; y++)
            for (int x = (int)rect.X; x < (int)rect.Right && !dark; x++) { var p = Px(x, y); dark = p[0] < 120 && p[1] < 120 && p[2] < 120; }
        Assert.True(dark, "text under the highlight is still readable");

        var under = Px(100, 213);   // red line near the bottom of the underline rect (212.5 .. 213.5)
        Assert.True(under[2] > 200 && under[1] < 80, $"underline pixel {under[2]},{under[1]},{under[0]}");
        // a blue line through the middle of the rect (a 1 pt line may be spread over two pixel rows)
        bool blue = false;
        for (int y = 303; y <= 311 && !blue; y++) { var p = Px(100, y); blue = p[0] > 200 && p[2] < 120; }
        Assert.True(blue, "strike-through line is drawn");
    }
}
