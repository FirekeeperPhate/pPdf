using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace pPdf.Forms;

/// <summary>Draws the values of the form fields of a page (page space), for printing and for exporting a page as an image.</summary>
public static class FormDrawing
{
    public static void Draw(DrawingContext dc, FormModel form, int page)
    {
        var ink = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14));
        ink.Freeze();
        foreach (var field in form.OnPage(page))
        {
            foreach (var widget in field.Widgets.Where(w => w.Page == page))
            {
                var b = widget.Bounds;
                switch (field.Kind)
                {
                    case FormFieldKind.Text:
                    case FormFieldKind.Combo:
                    case FormFieldKind.List:
                        string text = field.Kind == FormFieldKind.Text ? field.Value
                            : field.Options.Where(o => o.Value == field.Value).Select(o => o.Label).FirstOrDefault() ?? field.Value;
                        if (text.Length == 0) break;
                        DrawText(dc, text, b, FormControls.SizeFor(field, widget), field, ink);
                        break;
                    case FormFieldKind.CheckBox:
                        if (!string.IsNullOrEmpty(field.Value) && !string.Equals(field.Value, "Off", StringComparison.OrdinalIgnoreCase)) DrawTick(dc, b, ink);
                        break;
                    case FormFieldKind.Radio:
                        if (string.Equals(field.Value, widget.OnState, StringComparison.Ordinal))
                            dc.DrawEllipse(ink, null, new Point(b.X + b.Width / 2, b.Y + b.Height / 2), b.Width * 0.24, b.Height * 0.24);
                        break;
                }
            }
        }
    }

    static void DrawText(DrawingContext dc, string text, Rect b, double size, FormField field, Brush ink)
    {
        var face = new Typeface("Arial");
        var ft = new FormattedText(field.Multiline ? text.Replace("\r\n", "\n") : text.Replace("\r", " ").Replace("\n", " "), CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, face, size, ink, 1.0)
        {
            MaxTextWidth = Math.Max(1, b.Width - 4),
            MaxTextHeight = field.Multiline ? Math.Max(1, b.Height - 2) : size * 1.4,
            Trimming = TextTrimming.None,
            TextAlignment = field.Alignment switch { 1 => TextAlignment.Center, 2 => TextAlignment.Right, _ => TextAlignment.Left },
        };
        double y = field.Multiline ? b.Y + 1 : b.Y + Math.Max(0, (b.Height - ft.Height) / 2);
        dc.PushClip(new RectangleGeometry(b));
        dc.DrawText(ft, new Point(b.X + 2, y));
        dc.Pop();
    }

    static void DrawTick(DrawingContext dc, Rect b, Brush ink)
    {
        var tick = new StreamGeometry();
        using (var g = tick.Open())
        {
            g.BeginFigure(new Point(b.X + b.Width * 0.2, b.Y + b.Height * 0.55), false, false);
            g.LineTo(new Point(b.X + b.Width * 0.42, b.Y + b.Height * 0.76), true, true);
            g.LineTo(new Point(b.X + b.Width * 0.8, b.Y + b.Height * 0.26), true, true);
        }
        tick.Freeze();
        dc.DrawGeometry(null, new Pen(ink, Math.Max(1, Math.Min(b.Width, b.Height) * 0.14)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, tick);
    }
}
