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
    public static byte[] Apply(byte[] pdf, string? password, IReadOnlyList<Annotation> annotations, IReadOnlyDictionary<string, string>? formValues = null,
        IReadOnlyList<Bookmark>? bookmarks = null, IReadOnlyDictionary<int, Pdf.PageText>? recognized = null)
    {
        using var input = new MemoryStream(pdf, writable: false);
        using var doc = password == null
            ? PdfReader.Open(input, PdfDocumentOpenMode.Modify)
            : PdfReader.Open(input, password, PdfDocumentOpenMode.Modify);

        // text recognized on scanned pages goes in as invisible text over the picture: other programs can search and copy it too
        foreach (var (pageIndex, text) in (recognized ?? new Dictionary<int, Pdf.PageText>()).OrderBy(kv => kv.Key))
        {
            if (pageIndex < 0 || pageIndex >= doc.PageCount) continue;
            var page = doc.Pages[pageIndex];
            using (var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append))
            {
                gfx.MultiplyTransform(DisplayToPage(page), XMatrixOrder.Prepend);
                DrawInvisibleText(gfx, text);
            }
            MakeTextInvisible(page);
        }

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

        // the user's bookmarks join the document's outline, so any viewer lists them
        foreach (var b in (bookmarks ?? []).Where(b => b.Page >= 0 && b.Page < doc.PageCount).OrderBy(b => b.Page))
            doc.Outlines.Add(string.IsNullOrWhiteSpace(b.Title) ? $"Page {b.Page + 1}" : b.Title, doc.Pages[b.Page], false);

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

    /// <summary>
    /// The text just appended to a page becomes invisible: every text object of that last content stream gets render mode 3 (neither
    /// filled nor stroked: there to be found, searched and copied, as scanners' OCR text is). XGraphics cannot set it itself.
    /// </summary>
    static void MakeTextInvisible(PdfPage page)
    {
        var contents = page.Contents.Elements;
        if (contents.Count == 0) return;
        var item = contents[contents.Count - 1];
        if ((item is PdfSharp.Pdf.Advanced.PdfReference r ? r.Value : item) is not PdfDictionary content || content.Stream == null) return;
        var bytes = content.Stream.UnfilteredValue;
        var text = System.Text.Encoding.Latin1.GetString(bytes);
        // "BT" as an operator (hex strings, the only strings XGraphics writes for Unicode fonts, cannot contain a T)
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?<![A-Za-z0-9])BT(?![A-Za-z0-9])", "BT 3 Tr");
        content.Elements.Remove("/Filter");
        content.Elements.Remove("/DecodeParms");
        content.Stream.Value = System.Text.Encoding.Latin1.GetBytes(text);
    }

    /// <summary>Each recognized word, sized and stretched to the box it was read in (made invisible afterwards).</summary>
    static void DrawInvisibleText(XGraphics gfx, Pdf.PageText text)
    {
        var clear = XBrushes.Black;
        var options = new XPdfFontOptions(PdfFontEncoding.Unicode);
        var fonts = new Dictionary<int, XFont>();
        foreach (var (word, box) in Pdf.PageOcr.Words(text))
        {
            if (box.Height < 0.5 || box.Width < 0.5) continue;
            int key = (int)Math.Round(box.Height * 4); // quarter points
            if (!fonts.TryGetValue(key, out var font)) fonts[key] = font = new XFont("Arial", Math.Max(1, key / 4.0 * 0.9), XFontStyleEx.Regular, options);
            double measured = gfx.MeasureString(word, font).Width;
            if (measured <= 0) continue;
            var state = gfx.Save();
            gfx.TranslateTransform(box.X, box.Bottom - box.Height * 0.2);
            gfx.ScaleTransform(box.Width / measured, 1);
            gfx.DrawString(word, font, clear, 0, 0, XStringFormats.BaseLineLeft);
            gfx.Restore(state);
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
