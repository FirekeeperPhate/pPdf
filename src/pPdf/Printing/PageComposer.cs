using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using pPdf.Annotations;
using pPdf.Pdf;
using pPdf.Viewer;

namespace pPdf.Printing;

/// <summary>Draws a page the way it is saved or printed: PDFium's rendering with the annotations on top (shared by printing and image export).</summary>
public static class PageComposer
{
    /// <summary>
    /// Draws <paramref name="page"/> into <paramref name="dest"/> (device-independent pixels) using a PDFium bitmap of
    /// <paramref name="pixelWidth"/> x <paramref name="pixelHeight"/>, then the annotations of that page.
    /// </summary>
    public static void Draw(DrawingContext dc, PdfFile pdf, int page, Rect dest, int rotation, int pixelWidth, int pixelHeight,
        IReadOnlyList<Annotation> annotations, Forms.FormModel? form = null)
    {
        var bmp = pdf.Render(page, pixelWidth, pixelHeight, new Int32Rect(0, 0, pixelWidth, pixelHeight), rotation, invert: false, printing: true);
        if (bmp != null) dc.DrawImage(bmp, dest);

        var mine = annotations.Where(a => a.Page == page).ToList();
        bool hasForm = form != null && form.OnPage(page).Any();
        if (mine.Count == 0 && !hasForm) return;
        var size = pdf.Pages[page];
        double fit = (rotation & 1) == 1 ? dest.Width / size.Height : dest.Width / size.Width;
        var m = PageSlot.OverlayMatrix(size, fit, rotation);
        m.Translate(dest.X, dest.Y);
        dc.PushClip(new RectangleGeometry(dest));
        dc.PushTransform(new MatrixTransform(m));
        // the values typed into the form fields sit under the annotations
        if (hasForm) Forms.FormDrawing.Draw(dc, form!, page);
        foreach (var a in mine) AnnotationDrawing.Draw(dc, a);
        dc.Pop();
        dc.Pop();
    }

    /// <summary>A page as an image, at <paramref name="dpi"/>, with its annotations and form values.</summary>
    public static BitmapSource RenderImage(PdfFile pdf, int page, int dpi, int rotation, IReadOnlyList<Annotation> annotations, Forms.FormModel? form = null)
    {
        var size = pdf.Pages[page];
        bool turned = (rotation & 1) == 1;
        double wPt = turned ? size.Height : size.Width, hPt = turned ? size.Width : size.Height;
        // keep the longest side within what a bitmap can reasonably be
        double scale = dpi / 72.0;
        double longest = Math.Max(wPt, hPt) * scale;
        if (longest > 16000) scale *= 16000 / longest;
        int pxW = Math.Max(1, (int)Math.Round(wPt * scale)), pxH = Math.Max(1, (int)Math.Round(hPt * scale));
        double dipW = pxW * 96.0 / dpi, dipH = pxH * 96.0 / dpi;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, dipW, dipH));
            Draw(dc, pdf, page, new Rect(0, 0, dipW, dipH), rotation, pxW, pxH, annotations, form);
        }
        var rtb = new RenderTargetBitmap(pxW, pxH, dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }
}
