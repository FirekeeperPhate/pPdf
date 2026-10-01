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
    public static byte[] Apply(byte[] pdf, string? password, IReadOnlyList<Annotation> annotations, IReadOnlyDictionary<string, string>? formValues = null)
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
                    case MarkupAnnotation m: DrawMarkup(gfx, m); break;
                }
            }
        }

        // what the user typed into the form fields
        if (formValues is { Count: > 0 }) Forms.FormPdf.Apply(doc, formValues);

        using var output = new MemoryStream();
        doc.Save(output, false);
        return output.ToArray();
    }

    /// <summary>
    /// Maps display space (points from the top-left of the page as it is shown, i.e. after /Rotate and cropped to the CropBox)
    /// to the coordinate system XGraphics draws in (points from the top-left of the unrotated MediaBox, y pointing down).
    /// </summary>
    internal static XMatrix DisplayToPage(PdfPage page) => Forms.PageGeometry.Of(page).DisplayToGraphics();

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
            {
                var at = new XPoint(t.X + TextAnnotation.Padding, y);
                try { gfx.DrawString(line, font, brush, at, XStringFormats.BaseLineLeft); }
                catch (Exception)
                {
                    // a font PDFsharp cannot embed (e.g. CFF-based OpenType): one odd font must not lose the whole save
                    font = new XFont("Arial", t.FontSize, style);
                    gfx.DrawString(line, font, brush, at, XStringFormats.BaseLineLeft);
                }
            }
            y += lineHeight;
        }
    }

    static void DrawMarkup(XGraphics gfx, MarkupAnnotation m)
    {
        var c = m.Color;
        var band = new XSolidBrush(XColor.FromArgb(MarkupAnnotation.HighlightAlpha, c.R, c.G, c.B));
        var solid = new XSolidBrush(XColor.FromArgb(255, c.R, c.G, c.B));
        foreach (var r in m.Rects)
        {
            double t = Math.Max(0.8, r.Height * 0.07);
            switch (m.Kind)
            {
                case MarkupKind.Highlight: gfx.DrawRectangle(band, r.X, r.Y, r.Width, r.Height); break;
                case MarkupKind.Underline: gfx.DrawRectangle(solid, r.X, r.Bottom - t * 1.5, r.Width, t); break;
                case MarkupKind.Strikeout: gfx.DrawRectangle(solid, r.X, r.Y + (r.Height - t) / 2, r.Width, t); break;
            }
        }
    }

    static void DrawImage(XGraphics gfx, ImageAnnotation i)
    {
        using var ms = new MemoryStream(i.Data, writable: false);
        using var image = XImage.FromStream(ms);
        gfx.DrawImage(image, i.X, i.Y, i.Width, i.Height);
    }
}
