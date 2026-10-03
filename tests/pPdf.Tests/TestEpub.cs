using System.IO.Compression;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace pPdf.Tests;

/// <summary>Builds small EPUB books in memory for the tests.</summary>
static class TestEpub
{
    public static byte[] Png()
    {
        const int w = 40, h = 20;
        var px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++) { px[i * 4] = 40; px[i * 4 + 1] = 90; px[i * 4 + 2] = 220; px[i * 4 + 3] = 255; }
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, w * 4)));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    const string Container = """
        <?xml version="1.0"?>
        <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
          <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
        </container>
        """;

    static string Opf(bool linearSecond = true) => """
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="bookid">
          <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
            <dc:identifier id="bookid">urn:uuid:12345678-1234-1234-1234-123456789abc</dc:identifier>
            <dc:title>The Test Book</dc:title>
            <dc:language>en</dc:language>
          </metadata>
          <manifest>
            <item id="css" href="css/style.css" media-type="text/css"/>
            <item id="c1" href="text/ch1.xhtml" media-type="application/xhtml+xml"/>
            <item id="c2" href="text/chapter%20two.xhtml" media-type="application/xhtml+xml"/>
            <item id="c3" href="text/ch3.xhtml" media-type="application/xhtml+xml"/>
            <item id="pic" href="images/pic.png" media-type="image/png"/>
          </manifest>
          <spine>
            <itemref idref="c1"/><itemref idref="c2"/><itemref idref="c3"/>
          </spine>
        </package>
        """;

    static string Chapter(string title, string body) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <!DOCTYPE html>
        <html xmlns="http://www.w3.org/1999/xhtml">
        <head><title>{title}</title><link rel="stylesheet" type="text/css" href="../css/style.css"/></head>
        <body class="main">
        <h1 id="top">{title}</h1>
        {body}
        </body></html>
        """;

    public static string Paragraphs(string seed, int count)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++)
            sb.Append($"<p>{seed} paragraph {i}: the quick brown fox jumps over the lazy dog, again and again, so that the page fills up with text worth reading.</p>\n");
        return sb.ToString();
    }

    /// <summary>Three chapters: the first links to the second (by file and by an anchor written as an empty element) and shows a picture.</summary>
    public static byte[] Create(Action<ZipArchive>? extra = null, bool withSpine = true)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string text) { var e = zip.CreateEntry(name); using var w = new StreamWriter(e.Open(), new UTF8Encoding(false)); w.Write(text); }
            Add("mimetype", "application/epub+zip");
            Add("META-INF/container.xml", Container);
            Add("OEBPS/content.opf", withSpine ? Opf() : Opf().Replace("<itemref idref=\"c1\"/><itemref idref=\"c2\"/><itemref idref=\"c3\"/>", ""));
            Add("OEBPS/css/style.css", "p { margin: 0 0 0.5em 0; } h1 { color: #224488; } .main { }");
            Add("OEBPS/text/ch1.xhtml", Chapter("Chapter One", "<p>Go to <a href=\"chapter%20two.xhtml#sec\">the second chapter</a> or <a href=\"#top\">the top</a>.</p>\n<p><img src=\"../images/pic.png\" alt=\"pic\"/></p>\n" + Paragraphs("First", 40)));
            Add("OEBPS/text/chapter two.xhtml", Chapter("Chapter Two", "<a id=\"sec\"/><h2>A section</h2>\n<div class=\"x\"/>\n" + Paragraphs("Second", 30) + "<p><a href=\"ch1.xhtml\">back to the start</a> and <a href=\"https://example.com/x\">outside</a></p>"));
            Add("OEBPS/text/ch3.xhtml", Chapter("Chapter Three", Paragraphs("Third", 10)));
            var png = zip.CreateEntry("OEBPS/images/pic.png");
            using (var s = png.Open()) s.Write(Png());
            extra?.Invoke(zip);
        }
        return ms.ToArray();
    }
}
