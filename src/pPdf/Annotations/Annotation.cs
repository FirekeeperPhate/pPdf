using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace pPdf.Annotations;

/// <summary>
/// An annotation lives in "page space": points (1/72 in) from the top-left corner of the page as it is displayed
/// without any viewer rotation, i.e. after the page's own /Rotate. That is the space PDFium reports text boxes in.
/// </summary>
public abstract partial class Annotation : ObservableObject
{
    public Guid Id { get; private set; } = Guid.NewGuid();

    [ObservableProperty] public partial int Page { get; set; }
    [ObservableProperty] public partial double X { get; set; }
    [ObservableProperty] public partial double Y { get; set; }
    [ObservableProperty] public partial double Width { get; set; }
    [ObservableProperty] public partial double Height { get; set; }

    public Rect Bounds => new(X, Y, Width, Height);

    public Annotation Clone()
    {
        var c = CreateEmpty();
        c.Id = Id;
        c.CopyFrom(this);
        return c;
    }

    protected abstract Annotation CreateEmpty();

    /// <summary>Copies every editable property, so an annotation can be restored from a snapshot.</summary>
    public virtual void CopyFrom(Annotation o)
    {
        Page = o.Page; X = o.X; Y = o.Y; Width = o.Width; Height = o.Height;
    }

    public abstract bool SameAs(Annotation o);

    protected bool SameGeometry(Annotation o)
        => Page == o.Page && X == o.X && Y == o.Y && Width == o.Width && Height == o.Height;
}

public sealed partial class TextAnnotation : Annotation
{
    public const double Padding = 2;

    [ObservableProperty] public partial string Text { get; set; } = "";
    [ObservableProperty] public partial string FontFamily { get; set; } = "Arial";
    [ObservableProperty] public partial double FontSize { get; set; } = 14;
    [ObservableProperty] public partial bool Bold { get; set; }
    [ObservableProperty] public partial bool Italic { get; set; }
    [ObservableProperty] public partial Color Foreground { get; set; } = Colors.Black;
    /// <summary>Optional fill behind the text (null = transparent).</summary>
    [ObservableProperty] public partial Color? Background { get; set; }

    protected override Annotation CreateEmpty() => new TextAnnotation();

    public override void CopyFrom(Annotation o)
    {
        base.CopyFrom(o);
        if (o is not TextAnnotation t) return;
        Text = t.Text; FontFamily = t.FontFamily; FontSize = t.FontSize; Bold = t.Bold; Italic = t.Italic;
        Foreground = t.Foreground; Background = t.Background;
    }

    public override bool SameAs(Annotation o)
        => o is TextAnnotation t && SameGeometry(t) && Text == t.Text && FontFamily == t.FontFamily && FontSize == t.FontSize
           && Bold == t.Bold && Italic == t.Italic && Foreground == t.Foreground && Background == t.Background;
}

public sealed partial class ImageAnnotation : Annotation
{
    /// <summary>The original encoded file (PNG, JPEG...), kept so saving embeds it untouched.</summary>
    public byte[] Data { get; private set; } = [];
    public BitmapSource Source { get; private set; } = null!;
    /// <summary>Pixel size of the source in points at 96 dpi, used to size a new image.</summary>
    public Size NaturalSize { get; private set; }

    public static ImageAnnotation FromBytes(byte[] data)
    {
        var src = DecodeBitmap(data, out var natural);
        // only PNG and JPEG are embedded as they are; anything else (GIF, TIFF, BMP, WebP...) is converted once, here,
        // so saving never depends on which codecs PDFsharp understands
        if (!IsPng(data) && !IsJpeg(data))
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(src));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            data = ms.ToArray();
        }
        return new ImageAnnotation { Data = data, Source = src, NaturalSize = natural };
    }

    static bool IsPng(byte[] d) => d.Length > 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47;
    static bool IsJpeg(byte[] d) => d.Length > 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF;

    public static ImageAnnotation FromBitmap(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return FromBytes(ms.ToArray());
    }

    /// <summary>The longest side, in pixels, of the bitmap kept in memory for display (the original file is embedded as it is).</summary>
    internal const int MaxDecodePixels = 4096;

    /// <summary>Decodes an image for display, no larger than <see cref="MaxDecodePixels"/>: a 50-megapixel photo would take 200 MB as a bitmap.</summary>
    static BitmapSource DecodeBitmap(byte[] data, out Size natural)
    {
        using var ms = new MemoryStream(data);
        var probe = BitmapDecoder.Create(ms, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None).Frames[0];
        int w = probe.PixelWidth, h = probe.PixelHeight;
        natural = new Size(w, h);

        ms.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = ms;
        if (Math.Max(w, h) > MaxDecodePixels)
        {
            if (w >= h) image.DecodePixelWidth = MaxDecodePixels; else image.DecodePixelHeight = MaxDecodePixels;
        }
        image.EndInit();
        var converted = new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    protected override Annotation CreateEmpty() => new ImageAnnotation();

    public override void CopyFrom(Annotation o)
    {
        base.CopyFrom(o);
        if (o is not ImageAnnotation i) return;
        Data = i.Data; Source = i.Source; NaturalSize = i.NaturalSize;
    }

    public override bool SameAs(Annotation o) => o is ImageAnnotation i && SameGeometry(i) && ReferenceEquals(Data, i.Data);
}

public enum MarkupKind { Highlight, Underline, Strikeout }

/// <summary>A highlight, underline or strike-through over text: the rectangles of its lines, in page space.</summary>
public sealed partial class MarkupAnnotation : Annotation
{
    /// <summary>How opaque the band of a highlight is: the text under it must stay easy to read.</summary>
    public const byte HighlightAlpha = 0x5A;

    [ObservableProperty] public partial MarkupKind Kind { get; set; } = MarkupKind.Highlight;
    [ObservableProperty] public partial Color Color { get; set; } = Color.FromRgb(0xFF, 0xEB, 0x3B);

    /// <summary>One rectangle per line of the marked text (shared between snapshots: never edited in place).</summary>
    public IReadOnlyList<Rect> Rects { get; private set; } = [];

    public static MarkupAnnotation Create(int page, MarkupKind kind, Color color, IEnumerable<Rect> rects)
    {
        var list = rects.ToArray();
        var m = new MarkupAnnotation { Page = page, Kind = kind, Color = color, Rects = list };
        if (list.Length > 0)
        {
            var union = list.Aggregate(Rect.Union);
            m.X = union.X; m.Y = union.Y; m.Width = union.Width; m.Height = union.Height;
        }
        return m;
    }

    protected override Annotation CreateEmpty() => new MarkupAnnotation();

    public override void CopyFrom(Annotation o)
    {
        base.CopyFrom(o);
        if (o is not MarkupAnnotation m) return;
        Kind = m.Kind; Color = m.Color; Rects = m.Rects;
    }

    public override bool SameAs(Annotation o)
        => o is MarkupAnnotation m && SameGeometry(m) && Kind == m.Kind && Color == m.Color && ReferenceEquals(Rects, m.Rects);
}
