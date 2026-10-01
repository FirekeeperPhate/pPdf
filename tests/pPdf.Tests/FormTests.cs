using System.Windows;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using pPdf.Forms;
using pPdf.Pdf;

namespace pPdf.Tests;

public class FormTests
{
    [Fact]
    public void The_fields_are_found_with_their_kinds_values_and_options()
    {
        var model = FormPdf.Read(TestForm.Create(), null)!;
        Assert.NotNull(model);
        Assert.Equal(6, model.Fields.Count);

        var name = model.Find("name")!;
        Assert.Equal(FormFieldKind.Text, name.Kind);
        Assert.Equal("Marco", name.Value);
        Assert.Equal(12, name.FontSize);
        Assert.False(name.Multiline);

        Assert.True(model.Find("notes")!.Multiline);
        Assert.Equal(FormFieldKind.CheckBox, model.Find("agree")!.Kind);
        Assert.Equal("Off", model.Find("agree")!.Value);
        Assert.Equal("Yes", model.Find("agree")!.Widgets[0].OnState.Length > 0 ? "Yes" : "");

        var size = model.Find("size")!;
        Assert.Equal(FormFieldKind.Radio, size.Kind);
        Assert.Equal("M", size.Value);
        Assert.Equal(3, size.Widgets.Count);
        Assert.Equal(["S", "M", "L"], size.Widgets.Select(w => w.OnState).ToArray());

        var color = model.Find("color")!;
        Assert.Equal(FormFieldKind.Combo, color.Kind);
        Assert.Equal("g", color.Value);
        Assert.Equal([("r", "Red"), ("g", "Green"), ("b", "Blue")], color.Options.ToArray());

        Assert.True(model.Find("id")!.ReadOnly);
    }

    [Fact]
    public void A_document_without_fields_gives_nothing()
    {
        Assert.Null(FormPdf.Read(TestPdf.Simple(), null));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(90, false)]
    [InlineData(180, false)]
    [InlineData(270, false)]
    [InlineData(0, true)]
    [InlineData(90, true)]
    public void Widgets_are_located_where_PDFium_shows_them(int rotate, bool crop)
    {
        // the same rectangle given as a link annotation: PDFium reports where it appears
        var bytes = TestForm.Create(rotate, crop);
        var model = FormPdf.Read(bytes, null)!;
        var widget = model.Find("name")!.Widgets[0].Bounds;

        using var doc = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Modify);
        var link = new PdfSharp.Pdf.PdfDictionary(doc);
        link.Elements.SetName("/Type", "/Annot"); link.Elements.SetName("/Subtype", "/Link");
        link.Elements.SetRectangle("/Rect", TestForm.Rect(100, 700, 300, 720));
        link.Elements.SetInteger("/Border", 0);
        var action = new PdfSharp.Pdf.PdfDictionary(doc);
        action.Elements.SetName("/S", "/URI"); action.Elements.SetString("/URI", "https://example.com/");
        link.Elements.SetValue("/A", action);
        doc.Internals.AddObject(link);
        doc.Pages[0].Elements.GetArray("/Annots")!.Elements.Add(link.Reference!);
        using var ms = new MemoryStream();
        doc.Save(ms, false);

        using var pdf = PdfFile.Open(ms.ToArray());
        var l = pdf.LoadLinks(0).First(x => x.Uri != null).Box;
        Assert.InRange(widget.X, l.X - 1, l.X + 1);
        Assert.InRange(widget.Y, l.Y - 1, l.Y + 1);
        Assert.InRange(widget.Width, l.Width - 1, l.Width + 1);
        Assert.InRange(widget.Height, l.Height - 1, l.Height + 1);
    }

    [Fact]
    public void Values_written_into_a_copy_are_read_back_and_get_appearances()
    {
        var original = TestForm.Create();
        var changes = new Dictionary<string, string>
        {
            ["name"] = "Giulia è qui (test)",
            ["notes"] = "line one\nline two",
            ["agree"] = "Yes",
            ["size"] = "L",
            ["color"] = "b",
        };

        using var doc = PdfReader.Open(new MemoryStream(original), PdfDocumentOpenMode.Modify);
        FormPdf.Apply(doc, changes);
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        var saved = ms.ToArray();

        var back = FormPdf.Read(saved, null)!;
        Assert.Equal("Giulia è qui (test)", back.Find("name")!.Value);
        Assert.Equal("line one\nline two", back.Find("notes")!.Value);
        Assert.Equal("Yes", back.Find("agree")!.Value);
        Assert.Equal("L", back.Find("size")!.Value);
        Assert.Equal("b", back.Find("color")!.Value);
        Assert.Equal("A-1", back.Find("id")!.Value); // untouched

        // PDFium draws form widgets only through its form environment, so look at what a viewer that uses the appearances finds:
        // a text appearance with the text in it, and the right on / off state for the marks
        using var check = PdfReader.Open(new MemoryStream(saved), PdfDocumentOpenMode.Import);
        var annots = check.Pages[0].Elements.GetArray("/Annots")!;
        string? Stream(string fieldName)
        {
            for (int i = 0; i < annots.Elements.Count; i++)
            {
                var d = annots.Elements.GetDictionary(i);
                if (d?.Elements.GetString("/T") != fieldName) continue;
                var n = d.Elements.GetDictionary("/AP")?.Elements.GetDictionary("/N");
                return n?.Stream == null ? null : System.Text.Encoding.Latin1.GetString(n.Stream.Value);
            }
            return null;
        }
        var nameAp = Stream("name")!;
        Assert.Contains("Tj", nameAp);
        Assert.Contains(@"Giulia \350 qui \(test\)", nameAp); // WinAnsi octal escape for the accent, escaped parentheses
        var notesAp = Stream("notes")!;
        Assert.Contains("(line one) Tj", notesAp);
        Assert.Contains("(line two) Tj", notesAp);
        string? As(string fieldName, int index)
        {
            var parent = fieldName == "size" ? null : fieldName;
            for (int i = 0, seen = 0; i < annots.Elements.Count; i++)
            {
                var d = annots.Elements.GetDictionary(i);
                bool match = fieldName == "size" ? d?.Elements.GetDictionary("/Parent")?.Elements.GetString("/T") == "size" : d?.Elements.GetString("/T") == fieldName;
                if (!match) continue;
                if (seen++ == index) return d!.Elements.GetName("/AS");
            }
            return null;
        }
        Assert.Equal("/Yes", As("agree", 0));
        Assert.Equal("/Off", As("size", 0));
        Assert.Equal("/Off", As("size", 1));
        Assert.Equal("/L", As("size", 2));
    }

    [Fact]
    public void Turning_a_check_box_off_and_an_unchanged_form_leave_the_rest_alone()
    {
        var original = TestForm.Create();
        using var doc = PdfReader.Open(new MemoryStream(original), PdfDocumentOpenMode.Modify);
        FormPdf.Apply(doc, new Dictionary<string, string> { ["agree"] = "Yes" });
        using var ms = new MemoryStream();
        doc.Save(ms, false);

        using var doc2 = PdfReader.Open(new MemoryStream(ms.ToArray()), PdfDocumentOpenMode.Modify);
        FormPdf.Apply(doc2, new Dictionary<string, string> { ["agree"] = "Off" });
        using var ms2 = new MemoryStream();
        doc2.Save(ms2, false);
        var back = FormPdf.Read(ms2.ToArray(), null)!;
        Assert.Equal("Off", back.Find("agree")!.Value);
        Assert.Equal("Marco", back.Find("name")!.Value);
        Assert.Equal("M", back.Find("size")!.Value);
    }

    [Fact]
    public void Changed_values_and_restoring_them()
    {
        var model = FormPdf.Read(TestForm.Create(), null)!;
        Assert.Empty(model.ChangedValues());
        int raised = 0;
        model.Changed += () => raised++;
        model.Find("name")!.Value = "Zed";
        model.Find("agree")!.Value = "Yes";
        Assert.Equal(2, raised);
        var changed = model.ChangedValues();
        Assert.Equal(2, changed.Count);

        var again = FormPdf.Read(TestForm.Create(), null)!;
        again.Apply(changed);
        Assert.Equal("Zed", again.Find("name")!.Value);
        again.Apply(new Dictionary<string, string> { ["id"] = "hacked", ["nothing"] = "x" });
        Assert.Equal("A-1", again.Find("id")!.Value); // read-only fields are never changed
    }
}

public class FormPrintTests
{
    [Fact]
    public void Form_values_are_drawn_when_printing_and_exporting_a_page()
    {
        using var pdf = PdfFile.Open(TestForm.Create());
        var model = FormPdf.Read(TestForm.Create(), null)!;
        model.Find("name")!.Value = "PRINTED VALUE";
        model.Find("agree")!.Value = "Yes";

        var withForm = pPdf.Printing.PageComposer.RenderImage(pdf, 0, 96, 0, [], model);
        var without = pPdf.Printing.PageComposer.RenderImage(pdf, 0, 96, 0, [], null);

        // A4 at 96 dpi: 794 x 1123 pixels, one pixel per DIP; the name field is at x 100..300, y (842-720)..(842-700) in points
        static int Ink(System.Windows.Media.Imaging.BitmapSource bmp, double x1, double y1, double x2, double y2)
        {
            double s = 96 / 72.0;
            int count = 0;
            var px = new byte[4];
            for (int y = (int)(y1 * s); y < (int)(y2 * s); y++)
                for (int x = (int)(x1 * s); x < (int)(x2 * s); x++)
                {
                    bmp.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), px, 4, 0);
                    if (px[0] < 120 && px[1] < 120 && px[2] < 120) count++;
                }
            return count;
        }
        Assert.True(Ink(withForm, 100, 122, 300, 142) > 40, "the typed text is drawn");
        Assert.Equal(0, Ink(without, 100, 122, 300, 142));
        Assert.True(Ink(withForm, 100, 268, 114, 282) > 5, "the check box is ticked");
    }
}

public class FormChoiceAppearanceTests
{
    [Fact]
    public void A_choice_shows_its_label_not_its_stored_value()
    {
        using var doc = PdfReader.Open(new MemoryStream(TestForm.Create()), PdfDocumentOpenMode.Modify);
        FormPdf.Apply(doc, new Dictionary<string, string> { ["color"] = "b" });
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        using var check = PdfReader.Open(new MemoryStream(ms.ToArray()), PdfDocumentOpenMode.Import);
        var annots = check.Pages[0].Elements.GetArray("/Annots")!;
        string? ap = null;
        for (int i = 0; i < annots.Elements.Count; i++)
        {
            var d = annots.Elements.GetDictionary(i);
            if (d?.Elements.GetString("/T") != "color") continue;
            ap = System.Text.Encoding.Latin1.GetString(d.Elements.GetDictionary("/AP")!.Elements.GetDictionary("/N")!.Stream.Value);
        }
        Assert.NotNull(ap);
        Assert.Contains("(Blue) Tj", ap);
        Assert.DoesNotContain("(b) Tj", ap);
        Assert.Equal("b", FormPdf.Read(ms.ToArray(), null)!.Find("color")!.Value); // the value stays the export value
    }
}
