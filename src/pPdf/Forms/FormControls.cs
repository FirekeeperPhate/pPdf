using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace pPdf.Forms;

/// <summary>The on-screen controls of form fields: they live in the page-space layer of a page, so zoom and rotation come for free.</summary>
public static class FormControls
{
    static readonly Brush Tint = Freeze(new SolidColorBrush(Color.FromRgb(0xE6, 0xEF, 0xFC)));
    static readonly Brush TintReadOnly = Freeze(new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE)));
    static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)));
    static Brush Freeze(Brush b) { b.Freeze(); return b; }

    /// <summary>Builds the control of one widget of a field. <paramref name="giveBack"/> hands the keyboard back to the document.</summary>
    public static FrameworkElement? Create(FormField field, FormWidget widget, Action giveBack)
    {
        FrameworkElement? control = field.Kind switch
        {
            FormFieldKind.Text => Text(field, widget, giveBack),
            FormFieldKind.CheckBox or FormFieldKind.Radio => new FormMark(field, widget),
            FormFieldKind.Combo => Combo(field, widget, giveBack),
            FormFieldKind.List => List(field, widget),
            _ => null,
        };
        if (control == null) return null;
        Canvas.SetLeft(control, widget.Bounds.X);
        Canvas.SetTop(control, widget.Bounds.Y);
        control.Width = widget.Bounds.Width;
        control.Height = widget.Bounds.Height;
        return control;
    }

    /// <summary>The font size to use for a field: the one the form asks for, or one that fits the box.</summary>
    public static double SizeFor(FormField field, FormWidget widget)
        => field.FontSize > 0 ? field.FontSize : field.Multiline ? 12 : Math.Clamp(widget.Bounds.Height * 0.7, 6, 14);

    // ------------------------------------------------------------------ text

    static TextBox Text(FormField field, FormWidget widget, Action giveBack)
    {
        var box = new TextBox
        {
            Text = field.Value,
            AcceptsReturn = field.Multiline,
            TextWrapping = field.Multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalContentAlignment = field.Multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
            TextAlignment = field.Alignment switch { 1 => TextAlignment.Center, 2 => TextAlignment.Right, _ => TextAlignment.Left },
            FontFamily = new FontFamily("Arial"),
            FontSize = SizeFor(field, widget),
            Padding = new Thickness(2, 0, 2, 0),
            MinHeight = 0, MinWidth = 0,
            MaxLength = field.MaxLength,
            IsReadOnly = field.ReadOnly,
            Foreground = TextBrush,
            Background = field.ReadOnly ? TintReadOnly : Tint,
            Cursor = field.ReadOnly ? Cursors.Arrow : Cursors.IBeam,
            Focusable = !field.ReadOnly,
            SelectionBrush = SystemColors.HighlightBrush,
            ToolTip = null,
        };
        TextOptions.SetTextRenderingMode(box, TextRenderingMode.Grayscale);
        if (Application.Current.TryFindResource("FormFieldBox") is Style style) box.Style = style;

        bool updating = false;
        box.TextChanged += (_, _) => { if (!updating) field.Value = box.Text; };
        PropertyChangedEventHandler onValue = (_, e) =>
        {
            if (e.PropertyName != nameof(FormField.Value) || box.Text == field.Value) return;
            updating = true;
            box.Text = field.Value;
            updating = false;
        };
        field.PropertyChanged += onValue;
        box.Unloaded += (_, _) => field.PropertyChanged -= onValue;
        box.Loaded += (_, _) => { field.PropertyChanged -= onValue; field.PropertyChanged += onValue; };
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape || (e.Key == Key.Enter && !field.Multiline) || (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control))
            {
                e.Handled = true;
                giveBack();
            }
        };
        return box;
    }

    // ------------------------------------------------------------------ choices

    static ComboBox Combo(FormField field, FormWidget widget, Action giveBack)
    {
        var combo = new ComboBox
        {
            IsEditable = field.EditableCombo,
            FontFamily = new FontFamily("Arial"),
            FontSize = SizeFor(field, widget),
            Padding = new Thickness(2, 0, 2, 0),
            MinHeight = 0, MinWidth = 0,
            IsEnabled = !field.ReadOnly,
            Background = Tint,
        };
        foreach (var o in field.Options) combo.Items.Add(o.Label);

        bool updating = false;
        void Show()
        {
            updating = true;
            string label = field.Options.FirstOrDefault(o => o.Value == field.Value).Label ?? field.Value;
            if (field.Options.Any(o => o.Value == field.Value)) combo.SelectedItem = label; else { combo.SelectedItem = null; if (field.EditableCombo) combo.Text = field.Value; }
            updating = false;
        }
        Show();
        combo.SelectionChanged += (_, _) =>
        {
            if (updating || combo.SelectedItem is not string label) return;
            field.Value = field.Options.FirstOrDefault(o => o.Label == label).Value ?? label;
        };
        if (field.EditableCombo)
            combo.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) =>
            {
                if (!updating && combo.SelectedItem == null) field.Value = combo.Text;
            }));
        PropertyChangedEventHandler onValue = (_, e) => { if (e.PropertyName == nameof(FormField.Value)) Show(); };
        field.PropertyChanged += onValue;
        combo.Unloaded += (_, _) => field.PropertyChanged -= onValue;
        combo.Loaded += (_, _) => { field.PropertyChanged -= onValue; field.PropertyChanged += onValue; };
        combo.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && !combo.IsDropDownOpen) { e.Handled = true; giveBack(); } };
        return combo;
    }

    static ListBox List(FormField field, FormWidget widget)
    {
        var list = new ListBox
        {
            FontFamily = new FontFamily("Arial"),
            FontSize = Math.Min(SizeFor(field, widget), 12),
            Padding = new Thickness(0),
            Background = Tint,
            IsEnabled = !field.ReadOnly,
        };
        foreach (var o in field.Options) list.Items.Add(o.Label);
        bool updating = false;
        void Show()
        {
            updating = true;
            string? label = field.Options.Where(o => o.Value == field.Value).Select(o => o.Label).FirstOrDefault();
            list.SelectedItem = label;
            updating = false;
        }
        Show();
        list.SelectionChanged += (_, _) =>
        {
            if (updating || list.SelectedItem is not string label) return;
            field.Value = field.Options.FirstOrDefault(o => o.Label == label).Value ?? label;
        };
        PropertyChangedEventHandler onValue = (_, e) => { if (e.PropertyName == nameof(FormField.Value)) Show(); };
        field.PropertyChanged += onValue;
        list.Unloaded += (_, _) => field.PropertyChanged -= onValue;
        list.Loaded += (_, _) => { field.PropertyChanged -= onValue; field.PropertyChanged += onValue; };
        return list;
    }
}

/// <summary>A check box or one radio button of a group, drawn to fit its box.</summary>
sealed class FormMark : FrameworkElement
{
    readonly FormField _field;
    readonly FormWidget _widget;
    bool _hot;

    public FormMark(FormField field, FormWidget widget)
    {
        _field = field;
        _widget = widget;
        Cursor = field.ReadOnly ? Cursors.Arrow : Cursors.Hand;
        Focusable = false;
        field.PropertyChanged += OnField;
        Unloaded += (_, _) => field.PropertyChanged -= OnField;
        Loaded += (_, _) => { field.PropertyChanged -= OnField; field.PropertyChanged += OnField; InvalidateVisual(); };
    }

    bool IsRadio => _field.Kind == FormFieldKind.Radio;

    bool IsOn => IsRadio
        ? string.Equals(_field.Value, _widget.OnState, StringComparison.Ordinal)
        : !string.IsNullOrEmpty(_field.Value) && !string.Equals(_field.Value, "Off", StringComparison.OrdinalIgnoreCase);

    void OnField(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FormField.Value)) InvalidateVisual();
    }

    protected override void OnMouseEnter(MouseEventArgs e) { base.OnMouseEnter(e); _hot = true; InvalidateVisual(); }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); _hot = false; InvalidateVisual(); }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_field.ReadOnly) return;
        if (IsRadio) _field.Value = _widget.OnState;                         // radio buttons only ever turn on; another one turns this off
        else _field.Value = IsOn ? "Off" : _widget.OnState;
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters)
        => new PointHitTestResult(this, hitTestParameters.HitPoint);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        var fill = _field.ReadOnly ? Brushes.Gainsboro : _hot ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xE6, 0xEF, 0xFC));
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x5B, 0x76, 0xA0)), Math.Max(0.7, Math.Min(w, h) * 0.06));
        var ink = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14));
        if (IsRadio)
        {
            dc.DrawEllipse(fill, pen, new Point(w / 2, h / 2), w / 2 - pen.Thickness / 2, h / 2 - pen.Thickness / 2);
            if (IsOn) dc.DrawEllipse(ink, null, new Point(w / 2, h / 2), w * 0.24, h * 0.24);
            return;
        }
        dc.DrawRectangle(fill, pen, new Rect(pen.Thickness / 2, pen.Thickness / 2, w - pen.Thickness, h - pen.Thickness));
        if (IsOn)
        {
            var tick = new StreamGeometry();
            using (var g = tick.Open())
            {
                g.BeginFigure(new Point(w * 0.2, h * 0.55), false, false);
                g.LineTo(new Point(w * 0.42, h * 0.76), true, true);
                g.LineTo(new Point(w * 0.8, h * 0.26), true, true);
            }
            tick.Freeze();
            dc.DrawGeometry(null, new Pen(ink, Math.Max(1, Math.Min(w, h) * 0.14)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, tick);
        }
    }
}
