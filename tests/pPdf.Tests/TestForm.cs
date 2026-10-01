using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace pPdf.Tests;

/// <summary>Builds a small AcroForm PDF by hand (PDFsharp has no API to create fields).</summary>
static class TestForm
{
    public static PdfRectangle Rect(double x1, double y1, double x2, double y2) => new(new XPoint(x1, y1), new XPoint(x2, y2));

    static PdfDictionary Stream(PdfDocument doc, string content, double w, double h)
    {
        var form = new PdfDictionary(doc);
        form.Elements.SetName("/Type", "/XObject");
        form.Elements.SetName("/Subtype", "/Form");
        form.Elements.SetRectangle("/BBox", Rect(0, 0, w, h));
        form.CreateStream(System.Text.Encoding.ASCII.GetBytes(content));
        doc.Internals.AddObject(form);
        return form;
    }

    static PdfDictionary Widget(PdfDocument doc, PdfPage page, PdfRectangle rect)
    {
        var d = new PdfDictionary(doc);
        d.Elements.SetName("/Type", "/Annot");
        d.Elements.SetName("/Subtype", "/Widget");
        d.Elements.SetRectangle("/Rect", rect);
        d.Elements.SetInteger("/F", 4);
        doc.Internals.AddObject(d);
        d.Elements.SetReference("/P", page);
        var annots = page.Elements.GetArray("/Annots");
        if (annots == null) { annots = new PdfArray(doc); page.Elements.SetValue("/Annots", annots); }
        annots.Elements.Add(d.Reference!);
        return d;
    }

    /// <summary>
    /// One page with: text "name" ("Marco"), multi-line text "notes", check box "agree" (off), radio group "size" (S / M / L, M selected),
    /// combo "color" (options Red / Green / Blue, Green selected) and a read-only text "id".
    /// </summary>
    public static byte[] Create(int rotate = 0, bool crop = false)
    {
        var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(595);
        page.Height = XUnit.FromPoint(842);
        if (rotate != 0) page.Rotate = rotate;
        if (crop) page.CropBox = Rect(30, 50, 430, 650);
        using (var gfx = XGraphics.FromPdfPage(page)) gfx.DrawString(" ", new XFont("Arial", 10), XBrushes.Black, new XPoint(1, 1));

        var top = new PdfArray(doc);

        var name = Widget(doc, page, Rect(100, 700, 300, 720));
        name.Elements.SetName("/FT", "/Tx"); name.Elements.SetString("/T", "name"); name.Elements.SetString("/V", "Marco");
        name.Elements.SetString("/DA", "/Helv 12 Tf 0 g");
        top.Elements.Add(name.Reference!);

        var notes = Widget(doc, page, Rect(100, 600, 300, 680));
        notes.Elements.SetName("/FT", "/Tx"); notes.Elements.SetString("/T", "notes"); notes.Elements.SetInteger("/Ff", 1 << 12);
        top.Elements.Add(notes.Reference!);

        var agree = Widget(doc, page, Rect(100, 560, 114, 574));
        agree.Elements.SetName("/FT", "/Btn"); agree.Elements.SetString("/T", "agree"); agree.Elements.SetName("/V", "/Off"); agree.Elements.SetName("/AS", "/Off");
        var agreeAp = new PdfDictionary(doc); var agreeN = new PdfDictionary(doc);
        agreeN.Elements.SetReference("/Yes", Stream(doc, "q 0 0 1 rg 2 2 10 10 re f Q", 14, 14));
        agreeN.Elements.SetReference("/Off", Stream(doc, "", 14, 14));
        agreeAp.Elements.SetValue("/N", agreeN); agree.Elements.SetValue("/AP", agreeAp);
        top.Elements.Add(agree.Reference!);

        // radio group: the parent is the field, the buttons are its kids
        var group = new PdfDictionary(doc);
        group.Elements.SetName("/FT", "/Btn"); group.Elements.SetString("/T", "size"); group.Elements.SetInteger("/Ff", 1 << 15); group.Elements.SetName("/V", "/M");
        doc.Internals.AddObject(group);
        var kids = new PdfArray(doc);
        int x = 100;
        foreach (var (state, on) in new[] { ("S", false), ("M", true), ("L", false) })
        {
            var r = Widget(doc, page, Rect(x, 520, x + 14, 534));
            r.Elements.SetReference("/Parent", group);
            r.Elements.SetName("/AS", on ? "/" + state : "/Off");
            var ap = new PdfDictionary(doc); var n = new PdfDictionary(doc);
            n.Elements.SetReference("/" + state, Stream(doc, "q 1 0 0 rg 3 3 8 8 re f Q", 14, 14));
            n.Elements.SetReference("/Off", Stream(doc, "", 14, 14));
            ap.Elements.SetValue("/N", n); r.Elements.SetValue("/AP", ap);
            kids.Elements.Add(r.Reference!);
            x += 30;
        }
        group.Elements.SetValue("/Kids", kids);
        top.Elements.Add(group.Reference!);

        var color = Widget(doc, page, Rect(100, 480, 220, 500));
        color.Elements.SetName("/FT", "/Ch"); color.Elements.SetString("/T", "color"); color.Elements.SetInteger("/Ff", 1 << 17);
        var opt = new PdfArray(doc);
        foreach (var (v, l) in new[] { ("r", "Red"), ("g", "Green"), ("b", "Blue") })
        {
            var pair = new PdfArray(doc);
            pair.Elements.Add(new PdfString(v)); pair.Elements.Add(new PdfString(l));
            opt.Elements.Add(pair);
        }
        color.Elements.SetValue("/Opt", opt); color.Elements.SetString("/V", "g");
        top.Elements.Add(color.Reference!);

        var id = Widget(doc, page, Rect(100, 440, 200, 460));
        id.Elements.SetName("/FT", "/Tx"); id.Elements.SetString("/T", "id"); id.Elements.SetString("/V", "A-1"); id.Elements.SetInteger("/Ff", 1);
        top.Elements.Add(id.Reference!);

        var acro = new PdfDictionary(doc);
        acro.Elements.SetValue("/Fields", top);
        doc.Internals.Catalog.Elements.SetValue("/AcroForm", acro);

        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }
}
