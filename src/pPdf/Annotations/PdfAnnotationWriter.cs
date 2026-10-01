using System.Windows.Media;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Security;

namespace pPdf.Annotations;

/// <summary>Writes the annotations into the pages of a PDF (they become part of the page content).</summary>
public static class PdfAnnotationWriter
{
    /// <summary>Returns a copy of <paramref name="pdf"/> with <paramref name="annotations"/> drawn on its pages.</summary>
    public static byte[] Apply(byte[] pdf, string? password, IReadOnlyList<Annotation> annotations)
    {
        using var input = new MemoryStream(pdf, writable: false);
        using var doc = password == null
            ? PdfReader.Open(input, PdfDocumentOpenMode.Modify)
            : PdfReader.Open(input, password, PdfDocumentOpenMode.Modify);

        foreach (var group in annotations.GroupBy(a => a.Page).OrderBy(g => g.Key))
        {
            if (group.Key < 0 || group.Key >= doc.PageCount) continue;
            var page = doc.Pages[group.Key];
            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
            gfx.MultiplyTransform(DisplayToPage(page), XMatrixOrder.Prepend);

            foreach (var a in group)
            {
                switch (a)
                {
                    case TextAnnotation t: DrawText(gfx, t); break;
                    case ImageAnnotation i: DrawImage(gfx, i); break;
                }
            }
        }

        using var output = new MemoryStream();
        doc.Save(output, false);
        return output.ToArray();
    }

    /// <summary>
    /// Maps display space (points from the top-left of the page as it is shown, i.e. after /Rotate and cropped to the CropBox)
    /// to the coordinate system XGraphics draws in (points from the top-left of the unrotated MediaBox, y pointing down).
    /// </summary>
    internal static XMatrix DisplayToPage(PdfPage page)
    {
        var media = Normalize(page.MediaBox);
        var crop = page.Elements.ContainsKey("/CropBox") ? Intersect(Normalize(page.CropBox), media) : media;
        double ml = media.L, mt = media.T;
        double cl = crop.L, cb = crop.B, cr = crop.R, ct = crop.T;
        int rotate = ((page.Rotate % 360) + 360) % 360;
        return rotate switch
        {
            90 => new XMatrix(0, -1, 1, 0, cl - ml, mt - cb),
            180 => new XMatrix(-1, 0, 0, -1, cr - ml, mt - cb),
            270 => new XMatrix(0, 1, -1, 0, cr - ml, mt - ct),
            _ => new XMatrix(1, 0, 0, 1, cl - ml, mt - ct),
        };
    }

    readonly record struct Box(double L, double B, double R, double T);

    static Box Normalize(PdfRectangle r) => new(Math.Min(r.X1, r.X2), Math.Min(r.Y1, r.Y2), Math.Max(r.X1, r.X2), Math.Max(r.Y1, r.Y2));

    static Box Intersect(Box a, Box b)
    {
        var r = new Box(Math.Max(a.L, b.L), Math.Max(a.B, b.B), Math.Min(a.R, b.R), Math.Min(a.T, b.T));
        return r.R > r.L && r.T > r.B ? r : b;
    }

    static void DrawText(XGraphics gfx, TextAnnotation t)
    {
        if (string.IsNullOrEmpty(t.Text)) return;
        var style = (t.Bold ? XFontStyleEx.Bold : XFontStyleEx.Regular) | (t.Italic ? XFontStyleEx.Italic : XFontStyleEx.Regular);
        XFont font;
        try { font = new XFont(t.FontFamily, t.FontSize, style); }
        catch (Exception) { font = new XFont("Arial", t.FontSize, style); }

        if (t.Background is { } bg)
            gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(bg.A, bg.R, bg.G, bg.B)), t.X, t.Y, t.Width, t.Height);

        // same metrics as the on-screen TextBlock, so the text lands exactly where it was seen
        var family = new FontFamily(t.FontFamily);
        double lineHeight = family.LineSpacing * t.FontSize;
        double baseline = family.Baseline * t.FontSize;
        var brush = new XSolidBrush(XColor.FromArgb(t.Foreground.A, t.Foreground.R, t.Foreground.G, t.Foreground.B));
        var lines = t.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        double y = t.Y + TextAnnotation.Padding + baseline;
        foreach (var line in lines)
        {
            if (line.Length > 0)
                gfx.DrawString(line, font, brush, new XPoint(t.X + TextAnnotation.Padding, y), XStringFormats.BaseLineLeft);
            y += lineHeight;
        }
    }

    static void DrawImage(XGraphics gfx, ImageAnnotation i)
    {
        using var ms = new MemoryStream(i.Data, writable: false);
        using var image = XImage.FromStream(ms);
        gfx.DrawImage(image, i.X, i.Y, i.Width, i.Height);
    }
}
