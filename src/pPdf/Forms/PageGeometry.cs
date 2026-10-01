using System.Windows;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace pPdf.Forms;

/// <summary>
/// How a page's PDF coordinates (points from the bottom-left of the user space, y up) relate to the "display space" the viewer works in
/// (points from the top-left of the page as shown: after /Rotate, cropped to the CropBox) and to the space PDFsharp draws in.
/// </summary>
public readonly record struct PageGeometry(double MediaLeft, double MediaTop, double CropLeft, double CropBottom, double CropRight, double CropTop, int Rotate)
{
    public static PageGeometry Of(PdfPage page)
    {
        var media = Normalize(page.MediaBox);
        var crop = page.Elements.ContainsKey("/CropBox") ? Intersect(Normalize(page.CropBox), media) : media;
        return new PageGeometry(media.L, media.T, crop.L, crop.B, crop.R, crop.T, ((page.Rotate % 360) + 360) % 360);
    }

    /// <summary>A user-space point in display space.</summary>
    public Point ToDisplay(double u, double v) => Rotate switch
    {
        90 => new Point(v - CropBottom, u - CropLeft),
        180 => new Point(CropRight - u, v - CropBottom),
        270 => new Point(CropTop - v, CropRight - u),
        _ => new Point(u - CropLeft, CropTop - v),
    };

    /// <summary>A user-space rectangle (any corner order) as a display-space rectangle.</summary>
    public Rect ToDisplay(PdfRectangle r)
    {
        var a = ToDisplay(r.X1, r.Y1);
        var b = ToDisplay(r.X2, r.Y2);
        return new Rect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
    }

    /// <summary>The matrix that lets display-space coordinates be drawn with an XGraphics of this page.</summary>
    public XMatrix DisplayToGraphics() => Rotate switch
    {
        90 => new XMatrix(0, -1, 1, 0, CropLeft - MediaLeft, MediaTop - CropBottom),
        180 => new XMatrix(-1, 0, 0, -1, CropRight - MediaLeft, MediaTop - CropBottom),
        270 => new XMatrix(0, 1, -1, 0, CropRight - MediaLeft, MediaTop - CropTop),
        _ => new XMatrix(1, 0, 0, 1, CropLeft - MediaLeft, MediaTop - CropTop),
    };

    readonly record struct Box(double L, double B, double R, double T);

    static Box Normalize(PdfRectangle r) => new(Math.Min(r.X1, r.X2), Math.Min(r.Y1, r.Y2), Math.Max(r.X1, r.X2), Math.Max(r.Y1, r.Y2));

    static Box Intersect(Box a, Box b)
    {
        var r = new Box(Math.Max(a.L, b.L), Math.Max(a.B, b.B), Math.Min(a.R, b.R), Math.Min(a.T, b.T));
        return r.R > r.L && r.T > r.B ? r : b;
    }
}
