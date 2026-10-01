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
        var src = DecodeBitmap(data);
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
        return new ImageAnnotation { Data = data, Source = src, NaturalSize = new Size(src.PixelWidth, src.PixelHeight) };
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

    static BitmapSource DecodeBitmap(byte[] data)
    {
        using var ms = new MemoryStream(data);
        var frame = BitmapFrame.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
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
