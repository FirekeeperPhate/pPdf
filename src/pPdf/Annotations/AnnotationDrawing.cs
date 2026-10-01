using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace pPdf.Annotations;

/// <summary>Draws annotations on a <see cref="DrawingContext"/> in page space (used for printing).</summary>
public static class AnnotationDrawing
{
    public static void Draw(DrawingContext dc, Annotation a)
    {
        switch (a)
        {
            case TextAnnotation t: DrawText(dc, t); break;
            case ImageAnnotation i:
                dc.DrawImage(i.Source, new Rect(i.X, i.Y, i.Width, i.Height));
                break;
            case MarkupAnnotation m: DrawMarkup(dc, m); break;
        }
    }

    /// <summary>Same look as <see cref="MarkupVisual"/>: a translucent band, or a line under / through the text.</summary>
    static void DrawMarkup(DrawingContext dc, MarkupAnnotation m)
    {
        var solid = new SolidColorBrush(m.Color);
        var band = new SolidColorBrush(Color.FromArgb(MarkupAnnotation.HighlightAlpha, m.Color.R, m.Color.G, m.Color.B));
        foreach (var r in m.Rects)
        {
            double t = Math.Max(0.8, r.Height * 0.07);
            switch (m.Kind)
            {
                case MarkupKind.Highlight: dc.DrawRectangle(band, null, r); break;
                case MarkupKind.Underline: dc.DrawRectangle(solid, null, new Rect(r.X, r.Bottom - t * 1.5, r.Width, t)); break;
                case MarkupKind.Strikeout: dc.DrawRectangle(solid, null, new Rect(r.X, r.Y + (r.Height - t) / 2, r.Width, t)); break;
            }
        }
    }

    static void DrawText(DrawingContext dc, TextAnnotation t)
    {
        if (t.Background is { } bg)
        {
            var brush = new SolidColorBrush(bg);
            brush.Freeze();
            dc.DrawRectangle(brush, null, new Rect(t.X, t.Y, t.Width, t.Height));
        }
        if (string.IsNullOrEmpty(t.Text)) return;
        var fg = new SolidColorBrush(t.Foreground);
        fg.Freeze();
        var face = new Typeface(new FontFamily(t.FontFamily), t.Italic ? FontStyles.Italic : FontStyles.Normal,
            t.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        var ft = new FormattedText(t.Text.Replace("\r\n", "\n"), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, t.FontSize, fg, 1.0);
        dc.DrawText(ft, new Point(t.X + TextAnnotation.Padding, t.Y + TextAnnotation.Padding));
    }
}
