using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace pPdf.Forms;

/// <summary>Reading the fields of an AcroForm out of a PDF, and writing the user's values back into a copy.</summary>
public static class FormPdf
{
    // field flags (PDF 32000-1, table 221 / 226 / 228)
    const int ReadOnlyFlag = 1 << 0;
    const int MultilineFlag = 1 << 12;
    const int RadioFlag = 1 << 15;
    const int PushButtonFlag = 1 << 16;
    const int ComboFlag = 1 << 17;
    const int EditFlag = 1 << 18;

    /// <summary>One widget annotation with the field it belongs to, as found on a page.</summary>
    sealed record Widget(int PageIndex, PdfPage Page, PdfDictionary Dict, PdfDictionary Field, string Name, string FieldType, int Flags);

    // ------------------------------------------------------------------ dictionary helpers

    static PdfItem? Raw(PdfDictionary d, string key)
    {
        if (!d.Elements.TryGetValue(key, out var v) || v == null) return null;
        return v is PdfReference r ? r.Value : v;
    }

    static PdfDictionary? Parent(PdfDictionary d) => Raw(d, "/Parent") as PdfDictionary;

    /// <summary>A value that a field inherits from its ancestors.</summary>
    static PdfItem? Inherit(PdfDictionary d, string key)
    {
        for (int depth = 0; d != null && depth < 32; depth++, d = Parent(d)!)
            if (Raw(d, key) is { } v) return v;
        return null;
    }

    static string? Text(PdfItem? item) => item switch
    {
        PdfString s => s.Value,
        PdfName n => n.Value.TrimStart('/'),
        _ => null,
    };

    static int Int(PdfItem? item) => item switch
    {
        PdfInteger i => i.Value,
        PdfReal r => (int)r.Value,
        _ => 0,
    };

    // ------------------------------------------------------------------ walking the widgets

    static IEnumerable<Widget> Walk(PdfDocument doc)
    {
        for (int p = 0; p < doc.PageCount; p++)
        {
            var page = doc.Pages[p];
            if (Raw(page, "/Annots") is not PdfArray annots) continue;
            for (int i = 0; i < annots.Elements.Count; i++)
            {
                var d = annots.Elements.GetDictionary(i);
                if (d == null || Text(Raw(d, "/Subtype")) != "Widget") continue;

                var field = d;
                while (field != null && Raw(field, "/T") == null && Parent(field) is { } up) field = up;
                field ??= d;
                string? type = Text(Inherit(d, "/FT"));
                if (type == null) continue;
                yield return new Widget(p, page, d, field, FullName(d), type, Int(Inherit(d, "/Ff")));
            }
        }
    }

    static string FullName(PdfDictionary d)
    {
        var parts = new List<string>();
        for (int depth = 0; d != null && depth < 32; depth++, d = Parent(d)!)
            if (Raw(d, "/T") is PdfString t && t.Value.Length > 0) parts.Add(t.Value);
        parts.Reverse();
        return string.Join('.', parts);
    }

    // ------------------------------------------------------------------ reading

    /// <summary>The fillable fields of a document, or null if it has none (or cannot be read).</summary>
    public static FormModel? Read(byte[] pdf, string? password)
    {
        try
        {
            using var stream = new MemoryStream(pdf, writable: false);
            using var doc = password == null
                ? PdfReader.Open(stream, PdfDocumentOpenMode.Import)
                : PdfReader.Open(stream, password, PdfDocumentOpenMode.Import);
            return Read(doc);
        }
        catch (Exception)
        {
            return null;
        }
    }

    static FormModel? Read(PdfDocument doc)
    {
        var fields = new Dictionary<string, FormField>(StringComparer.Ordinal);
        foreach (var w in Walk(doc))
        {
            if (w.Name.Length == 0) continue;
            if (!IsVisible(w.Dict)) continue;
            var kind = KindOf(w);
            if (kind == null) continue;

            if (!fields.TryGetValue(w.Name, out var field))
            {
                var value = ValueOf(w, kind.Value);
                field = new FormField
                {
                    Name = w.Name,
                    Kind = kind.Value,
                    ReadOnly = (w.Flags & ReadOnlyFlag) != 0,
                    Multiline = kind == FormFieldKind.Text && (w.Flags & MultilineFlag) != 0,
                    EditableCombo = kind == FormFieldKind.Combo && (w.Flags & EditFlag) != 0,
                    MaxLength = Math.Max(0, Int(Inherit(w.Dict, "/MaxLen"))),
                    Alignment = Math.Clamp(Int(Inherit(w.Dict, "/Q")), 0, 2),
                    FontSize = FontSizeOf(Text(Inherit(w.Dict, "/DA"))),
                    Options = OptionsOf(Inherit(w.Dict, "/Opt")),
                    OriginalValue = value,
                    Value = value,
                };
                fields[w.Name] = field;
            }

            var rect = w.Dict.Elements.GetRectangle("/Rect");
            var bounds = PageGeometry.Of(w.Page).ToDisplay(rect);
            if (bounds.Width < 1 || bounds.Height < 1) continue;
            field.Widgets.Add(new FormWidget { Page = w.PageIndex, Bounds = bounds, OnState = OnStateOf(w.Dict) });
        }
        var model = new FormModel(fields.Values.Where(f => f.Widgets.Count > 0));
        return model.IsEmpty ? null : model;
    }

    static bool IsVisible(PdfDictionary widget)
    {
        int f = Int(Raw(widget, "/F"));
        return (f & 2) == 0 && (f & 32) == 0; // not Hidden, not NoView
    }

    static FormFieldKind? KindOf(Widget w) => w.FieldType switch
    {
        "Tx" => FormFieldKind.Text,
        "Btn" when (w.Flags & PushButtonFlag) != 0 => null,
        "Btn" when (w.Flags & RadioFlag) != 0 => FormFieldKind.Radio,
        "Btn" => FormFieldKind.CheckBox,
        "Ch" when (w.Flags & ComboFlag) != 0 => FormFieldKind.Combo,
        "Ch" => FormFieldKind.List,
        _ => null, // signatures and the like
    };

    static string ValueOf(Widget w, FormFieldKind kind)
    {
        var v = Inherit(w.Field, "/V");
        if (kind is FormFieldKind.CheckBox or FormFieldKind.Radio)
        {
            string? name = Text(v);
            return string.IsNullOrEmpty(name) ? "Off" : name;
        }
        if (v is PdfArray arr && arr.Elements.Count > 0) return Text(arr.Elements[0] is PdfReference r ? r.Value : arr.Elements[0]) ?? "";
        return Text(v) ?? "";
    }

    /// <summary>The name of the "on" appearance of a check box / radio button: the key of /AP /N that is not Off.</summary>
    static string OnStateOf(PdfDictionary widget)
    {
        if (Raw(widget, "/AP") is PdfDictionary ap && Raw(ap, "/N") is PdfDictionary normal)
            foreach (var key in normal.Elements.Keys)
                if (key != "/Off") return key.TrimStart('/');
        return "Yes";
    }

    static double FontSizeOf(string? da)
    {
        if (string.IsNullOrWhiteSpace(da)) return 0;
        var tokens = da.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);
        for (int i = 2; i < tokens.Length; i++)
            if (tokens[i] == "Tf" && double.TryParse(tokens[i - 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double size))
                return Math.Max(0, size);
        return 0;
    }

    static IReadOnlyList<(string Value, string Label)> OptionsOf(PdfItem? opt)
    {
        var list = new List<(string, string)>();
        if (opt is not PdfArray arr) return list;
        for (int i = 0; i < arr.Elements.Count; i++)
        {
            var item = arr.Elements[i] is PdfReference r ? r.Value : arr.Elements[i];
            switch (item)
            {
                case PdfString s: list.Add((s.Value, s.Value)); break;
                case PdfArray pair when pair.Elements.Count >= 2:
                    string export = Text(pair.Elements[0] is PdfReference r0 ? r0.Value : pair.Elements[0]) ?? "";
                    string label = Text(pair.Elements[1] is PdfReference r1 ? r1.Value : pair.Elements[1]) ?? export;
                    list.Add((export, label));
                    break;
            }
        }
        return list;
    }

    // ------------------------------------------------------------------ writing

    /// <summary>Puts the values into the fields of <paramref name="doc"/> (a document opened for modification).</summary>
    public static void Apply(PdfDocument doc, IReadOnlyDictionary<string, string> values)
    {
        if (values.Count == 0) return;
        PdfDictionary? fontResource = null;
        bool lossy = false;
        var widgets = Walk(doc).ToList();
        // the "on" states of all the widgets of each name: boxes of one name with states of their own are separate choices
        var states = widgets.GroupBy(w => w.Name).ToDictionary(g => g.Key, g => g.Select(w => OnStateOf(w.Dict)).ToList(), StringComparer.Ordinal);
        foreach (var w in widgets)
        {
            if (!values.TryGetValue(w.Name, out string? value)) continue;
            try
            {
                var kind = KindOf(w);
                switch (kind)
                {
                    case FormFieldKind.CheckBox:
                    case FormFieldKind.Radio:
                    {
                        string on = OnStateOf(w.Dict);
                        bool off = value.Length == 0 || string.Equals(value, "Off", StringComparison.OrdinalIgnoreCase);
                        bool selected = FormField.IsOn(kind.Value, value, on, states[w.Name]);
                        w.Field.Elements.SetName("/V", "/" + (off ? "Off" : value));
                        w.Dict.Elements.SetName("/AS", "/" + (selected ? on : "Off"));
                        break;
                    }
                    case FormFieldKind.Text:
                    case FormFieldKind.Combo:
                    case FormFieldKind.List:
                    {
                        w.Field.Elements.SetString("/V", value);
                        // a choice stores its export value but shows the label
                        string shown = value;
                        if (kind != FormFieldKind.Text)
                            foreach (var (export, label) in OptionsOf(Inherit(w.Dict, "/Opt")))
                                if (export == value) { shown = label; break; }
                        fontResource ??= CreateFont(doc);
                        lossy |= SetTextAppearance(doc, w, shown, fontResource, kind.Value, w.Dict.Elements.GetRectangle("/Rect"));
                        break;
                    }
                }
            }
            catch (Exception)
            {
                // one odd widget must not lose the values of all the others
            }
        }
        // text the plain Helvetica appearance could not hold (Greek, Cyrillic, Polish...): viewers are asked to draw the fields
        // themselves from the values, which are stored in full
        if (lossy && doc.Internals.Catalog.Elements.GetDictionary("/AcroForm") is { } acroForm)
            acroForm.Elements.SetBoolean("/NeedAppearances", true);
    }

    static PdfDictionary CreateFont(PdfDocument doc)
    {
        var font = new PdfDictionary(doc);
        font.Elements.SetName("/Type", "/Font");
        font.Elements.SetName("/Subtype", "/Type1");
        font.Elements.SetName("/BaseFont", "/Helvetica");
        font.Elements.SetName("/Encoding", "/WinAnsiEncoding");
        doc.Internals.AddObject(font);
        return font;
    }

    /// <summary>
    /// A plain appearance (Helvetica) so any viewer shows the text, even one that does not regenerate appearances.
    /// True if some characters could not be written in it (they show as "?" there).
    /// </summary>
    static bool SetTextAppearance(PdfDocument doc, Widget w, string text, PdfDictionary font, FormFieldKind kind, PdfRectangle rect)
    {
        double width = Math.Abs(rect.X2 - rect.X1), height = Math.Abs(rect.Y2 - rect.Y1);
        if (width < 1 || height < 1) return false;
        // a field on a turned page is usually turned with it (/MK /R): its text is laid out along the field's own width
        int turn = ((Int(w.Dict.Elements.GetDictionary("/MK")?.Elements["/R"]) % 360) + 360) % 360;
        if (turn is 90 or 270) (width, height) = (height, width);
        bool lossy = false;

        bool multiline = kind == FormFieldKind.Text && (w.Flags & MultilineFlag) != 0;
        double size = FontSizeOf(Text(Inherit(w.Dict, "/DA")));
        if (size <= 0) size = multiline ? 12 : Math.Clamp(height - 4, 6, 14);
        int align = Math.Clamp(Int(Inherit(w.Dict, "/Q")), 0, 2);

        var lines = (multiline ? text.Replace("\r\n", "\n").Replace('\r', '\n') : text.Replace("\r", " ").Replace("\n", " ")).Split('\n');
        var sb = new StringBuilder();
        sb.Append("/Tx BMC q BT /Helv ").Append(Num(size)).Append(" Tf 0 g\n");
        double y = multiline ? height - 2 - size : (height - size) / 2 + size * 0.22;
        double lineHeight = size * 1.2;
        double previousX = 0, previousY = 0;
        foreach (var line in lines)
        {
            double textWidth = line.Length * size * 0.5; // a rough Helvetica width: enough to place centered / right-aligned text
            double x = align == 1 ? Math.Max(2, (width - textWidth) / 2) : align == 2 ? Math.Max(2, width - textWidth - 2) : 2;
            sb.Append(Num(x - previousX)).Append(' ').Append(Num(y - previousY)).Append(" Td (").Append(Escape(line, ref lossy)).Append(") Tj\n");
            previousX = x; previousY = y;
            y -= lineHeight;
        }
        sb.Append("ET Q EMC");

        var resources = new PdfDictionary(doc);
        var fonts = new PdfDictionary(doc);
        fonts.Elements.SetReference("/Helv", font);
        resources.Elements.SetValue("/Font", fonts);

        var form = new PdfDictionary(doc);
        form.Elements.SetName("/Type", "/XObject");
        form.Elements.SetName("/Subtype", "/Form");
        form.Elements.SetRectangle("/BBox", new PdfRectangle(new PdfSharp.Drawing.XRect(0, 0, width, height)));
        if (turn != 0)
        {
            // turns the box back onto the field (a viewer fits the turned box to /Rect, so no offset is needed)
            var matrix = new PdfArray(doc);
            int[] m = turn switch { 90 => [0, 1, -1, 0], 180 => [-1, 0, 0, -1], _ => [0, -1, 1, 0] };
            foreach (int v in m) matrix.Elements.Add(new PdfInteger(v));
            matrix.Elements.Add(new PdfInteger(0));
            matrix.Elements.Add(new PdfInteger(0));
            form.Elements.SetValue("/Matrix", matrix);
        }
        form.Elements.SetValue("/Resources", resources);
        form.CreateStream(Encoding.ASCII.GetBytes(sb.ToString()));
        doc.Internals.AddObject(form);

        var ap = new PdfDictionary(doc);
        ap.Elements.SetReference("/N", form);
        w.Dict.Elements.SetValue("/AP", ap);
        return lossy;
    }

    static string Num(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Windows-1252, which is what WinAnsiEncoding is: "?" for anything it cannot hold.</summary>
    static readonly Encoding WinAnsi = CreateWinAnsi();

    static Encoding CreateWinAnsi()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252, new EncoderReplacementFallback("?"), DecoderFallback.ReplacementFallback);
    }

    /// <summary>A PDF string literal in WinAnsi; <paramref name="lossy"/> is set when a character could not be written.</summary>
    static string Escape(string s, ref bool lossy)
    {
        var sb = new StringBuilder(s.Length + 8);
        var one = new char[1];
        foreach (char c in s)
        {
            byte b;
            one[0] = c;
            // 0x80-0x9F are control characters in Unicode but other letters in WinAnsi: they cannot be passed through as they are
            if (c < 128) b = (byte)c;
            else
            {
                var bytes = WinAnsi.GetBytes(one);
                b = bytes.Length == 1 ? bytes[0] : (byte)'?';
                if (b == '?' || (c >= 0x80 && c <= 0x9F)) { lossy = true; b = (byte)'?'; }
            }
            if (b == '(' || b == ')' || b == '\\') sb.Append('\\');
            if (b < 32 || b > 126) sb.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
            else sb.Append((char)b);
        }
        return sb.ToString();
    }
}
