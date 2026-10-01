using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace pPdf.Services;

/// <summary>The open and the closed hand (pan the page / panning), drawn at run time: WPF has no such cursors of its own.</summary>
public static class HandCursors
{
    static Cursor? _open, _closed;

    public static Cursor Open => _open ??= Build(closed: false);
    public static Cursor Closed => _closed ??= Build(closed: true);

    /// <summary>The cursor image as a PNG (also used by the tests / for a look at the drawing).</summary>
    internal static byte[] DrawPng(bool closed, int size = 32)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            double u = size / 32.0;
            var fill = Brushes.White;
            var pen = new Pen(Brushes.Black, 1.6 * u) { LineJoin = PenLineJoin.Round };
            void Finger(double x, double top, double bottom) =>
                dc.DrawRoundedRectangle(fill, pen, new Rect(x * u, top * u, 4.2 * u, (bottom - top) * u), 2.1 * u, 2.1 * u);

            if (!closed)
            {
                // palm first, fingers over its top edge, thumb to the left
                dc.DrawRoundedRectangle(fill, pen, new Rect(8 * u, 14 * u, 17 * u, 13 * u), 5 * u, 5 * u);
                Finger(9.2, 5, 18);
                Finger(13.4, 3, 18);
                Finger(17.6, 3.5, 18);
                Finger(21.8, 6, 18);
                dc.DrawRoundedRectangle(fill, pen, new Rect(4.5 * u, 15 * u, 4.4 * u, 9 * u), 2.2 * u, 2.2 * u);
                dc.DrawRoundedRectangle(fill, null, new Rect(9.6 * u, 15.2 * u, 14 * u, 10.5 * u), 3 * u, 3 * u);
            }
            else
            {
                dc.DrawRoundedRectangle(fill, pen, new Rect(7.5 * u, 14 * u, 18 * u, 12 * u), 5 * u, 5 * u);
                Finger(8.6, 10, 17);
                Finger(12.8, 9, 17);
                Finger(17, 9.5, 17);
                Finger(21.2, 11, 17);
                dc.DrawRoundedRectangle(fill, null, new Rect(9 * u, 15.2 * u, 15.4 * u, 9.6 * u), 3 * u, 3 * u);
            }
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    static Cursor Build(bool closed)
    {
        try
        {
            const int size = 32;
            byte[] png = DrawPng(closed, size);
            using var cur = new MemoryStream();
            using var w = new BinaryWriter(cur);
            w.Write((short)0); w.Write((short)2); w.Write((short)1);          // ICONDIR: type 2 = cursor, one image
            w.Write((byte)size); w.Write((byte)size); w.Write((byte)0); w.Write((byte)0);
            w.Write((short)(size / 2)); w.Write((short)(size / 2));            // hot spot: the middle of the hand
            w.Write(png.Length); w.Write(22);
            w.Write(png);
            w.Flush();
            cur.Position = 0;
            return new Cursor(cur);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return closed ? Cursors.ScrollAll : Cursors.Hand;
        }
    }
}
