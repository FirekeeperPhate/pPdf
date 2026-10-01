using System.Windows;
using System.Windows.Media;

namespace pPdf.Annotations;

/// <summary>Draws a highlight / underline / strike-through over the lines of a <see cref="MarkupAnnotation"/> (page space, relative to its corner).</summary>
sealed class MarkupVisual : FrameworkElement
{
    readonly MarkupAnnotation _model;
    bool _selected;
    double _scale = 1;

    public MarkupVisual(MarkupAnnotation model)
    {
        _model = model;
        IsHitTestVisible = false; // the viewer decides what a click on text means: select the text, or select this
    }

    public bool IsSelected { get => _selected; set { _selected = value; InvalidateVisual(); } }
    public double Scale { get => _scale; set { _scale = value <= 0 ? 1 : value; if (_selected) InvalidateVisual(); } }

    protected override void OnRender(DrawingContext dc)
    {
        var color = _model.Color;
        foreach (var r in _model.Rects)
        {
            var rect = new Rect(r.X - _model.X, r.Y - _model.Y, r.Width, r.Height);
            switch (_model.Kind)
            {
                case MarkupKind.Highlight:
                    dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(MarkupAnnotation.HighlightAlpha, color.R, color.G, color.B)), null, rect);
                    break;
                case MarkupKind.Underline:
                {
                    double t = Math.Max(0.8, rect.Height * 0.07);
                    dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(rect.X, rect.Bottom - t * 1.5, rect.Width, t));
                    break;
                }
                case MarkupKind.Strikeout:
                {
                    double t = Math.Max(0.8, rect.Height * 0.07);
                    dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(rect.X, rect.Y + (rect.Height - t) / 2, rect.Width, t));
                    break;
                }
            }
        }
        if (_selected)
        {
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x2F, 0x80, 0xED)), 1.5 / _scale) { DashStyle = DashStyles.Dash };
            foreach (var r in _model.Rects)
                dc.DrawRectangle(null, pen, new Rect(r.X - _model.X, r.Y - _model.Y, r.Width, r.Height));
        }
    }
}
