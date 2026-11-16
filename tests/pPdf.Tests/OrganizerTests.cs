using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using pPdf.Pdf;

namespace pPdf.Tests;

public class OrganizerTests
{
    static byte[] WithOutline(byte[] bytes, params (string Title, int Page)[] entries)
    {
        using var input = new MemoryStream(bytes);
        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        foreach (var (title, page) in entries) doc.Outlines.Add(title, doc.Pages[page], false);
        using var output = new MemoryStream();
        doc.Save(output, false);
        return output.ToArray();
    }

    static string[] Texts(PdfFile pdf) => Enumerable.Range(0, pdf.PageCount).Select(i => pdf.LoadText(i, false)!.Text.Trim()).ToArray();

    [Fact]
    public void Pages_are_reordered_turned_left_out_and_merged_from_another_file()
    {
        var main = WithOutline(TestPdf.Create([["Alpha"], ["Bravo"], ["Charlie"], ["Delta"]]), ("To Bravo", 1), ("To Delta", 3));
        var other = TestPdf.Create([["Xray"], ["Yankee"]]);

        var result = PageOrganizer.Build([new PageSource(main, null), new PageSource(other, null)],
        [
            new PagePick(0, 3, 1),   // Delta, turned a quarter clockwise
            new PagePick(1, 0),      // Xray from the other file
            new PagePick(0, 0),      // Alpha
            new PagePick(1, 1, 2),   // Yankee upside down
            new PagePick(0, 0, -1),  // Alpha again, turned the other way
        ]);

        using var pdf = PdfFile.Open(result);
        Assert.Equal(["Delta", "Xray", "Alpha", "Yankee", "Alpha"], Texts(pdf));
        // a quarter turn swaps the shown width and height
        Assert.Equal(new PageSize(842, 595), pdf.Pages[0]);
        Assert.Equal(new PageSize(595, 842), pdf.Pages[1]);
        Assert.Equal(new PageSize(842, 595), pdf.Pages[4]);

        // the outline entry of a page that stayed follows it; the one of a removed page leads nowhere, and nothing breaks
        var outline = pdf.LoadOutline();
        Assert.Contains(outline, n => n.Title == "To Delta" && n.PageIndex == 0);
        Assert.DoesNotContain(outline, n => n.Title == "To Bravo" && n.PageIndex >= 0);
    }

    [Fact]
    public void Extracting_some_pages_keeps_only_those()
    {
        var main = TestPdf.Simple();
        var result = PageOrganizer.Build([new PageSource(main, null)], [new PagePick(0, 2), new PagePick(0, 1)]);
        using var pdf = PdfFile.Open(result);
        Assert.Equal(2, pdf.PageCount);
        Assert.StartsWith("Third page", Texts(pdf)[0]);
        Assert.StartsWith("Page two", Texts(pdf)[1]);
    }

    [Fact]
    public void A_page_that_does_not_exist_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PageOrganizer.Build([new PageSource(TestPdf.Simple(), null)], [new PagePick(0, 7)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => PageOrganizer.Build([new PageSource(TestPdf.Simple(), null)], [new PagePick(2, 0)]));
    }
}
