#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property PublishAot=false
#:property ImplicitUsings=enable
// Draws Assets/pPdf.ico: a page with a folded corner and a red band. Usage: dotnet run DrawIcon.cs -- <out.ico>
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

var t = new Thread(() =>
{
    int[] sizes = [16, 24, 32, 48, 64, 128, 256];
    var pngs = new List<byte[]>();
    foreach (int s in sizes)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            double u = s / 256.0;
            var page = new PathGeometry();
            var f = new PathFigure(new Point(48 * u, 16 * u), [
                new LineSegment(new Point(160 * u, 16 * u), true), new LineSegment(new Point(208 * u, 64 * u), true),
                new LineSegment(new Point(208 * u, 240 * u), true), new LineSegment(new Point(48 * u, 240 * u), true)], true);
            page.Figures.Add(f);
            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xF9)), new Pen(new SolidColorBrush(Color.FromRgb(0x6B, 0x6F, 0x78)), Math.Max(1, 8 * u)), page);
            var fold = new PathGeometry();
            fold.Figures.Add(new PathFigure(new Point(160 * u, 16 * u), [new LineSegment(new Point(160 * u, 64 * u), true), new LineSegment(new Point(208 * u, 64 * u), true)], true));
            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0xD5, 0xD8, 0xDE)), new Pen(new SolidColorBrush(Color.FromRgb(0x6B, 0x6F, 0x78)), Math.Max(1, 6 * u)), fold);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0xE0, 0x3A, 0x3E)), null, new Rect(24 * u, 128 * u, 160 * u, 72 * u), 10 * u, 10 * u);
            if (s >= 32)
            {
                var ft = new FormattedText("PDF", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 52 * u, Brushes.White, 1.0);
                dc.DrawText(ft, new Point(104 * u - ft.Width / 2, 164 * u - ft.Height / 2));
            }
        }
        var rtb = new RenderTargetBitmap(s, s, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream(); enc.Save(ms); pngs.Add(ms.ToArray());
    }
    using var fs = File.Create(args[0]);
    using var bw = new BinaryWriter(fs);
    bw.Write((short)0); bw.Write((short)1); bw.Write((short)sizes.Length);
    int offset = 6 + 16 * sizes.Length;
    for (int i = 0; i < sizes.Length; i++)
    {
        bw.Write((byte)(sizes[i] == 256 ? 0 : sizes[i])); bw.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
        bw.Write((byte)0); bw.Write((byte)0); bw.Write((short)1); bw.Write((short)32); bw.Write(pngs[i].Length); bw.Write(offset);
        offset += pngs[i].Length;
    }
    foreach (var p in pngs) bw.Write(p);
});
t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
