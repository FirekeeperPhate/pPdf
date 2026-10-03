using System.IO.Compression;
using System.Text;
using pPdf.Epub;

namespace pPdf.Tests;

public class EpubTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ppdf-epub-test-" + Guid.NewGuid().ToString("N")[..8]);

    public EpubTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    EpubBook Open(byte[] epub, string name = "book")
    {
        string file = Path.Combine(_dir, name + ".epub");
        File.WriteAllBytes(file, epub);
        return EpubBook.Open(file, Path.Combine(_dir, name));
    }

    [Fact]
    public void Reads_title_language_and_spine_in_order_with_encoded_names()
    {
        var book = Open(TestEpub.Create());
        Assert.Equal("The Test Book", book.Title);
        Assert.Equal("en", book.Language);
        Assert.Equal(["OEBPS/text/ch1.xhtml", "OEBPS/text/chapter two.xhtml", "OEBPS/text/ch3.xhtml"], book.Spine.Select(c => c.Path));
    }

    [Fact]
    public void Chapters_are_joined_and_start_on_new_pages()
    {
        string html = Open(TestEpub.Create()).BuildHtml();
        Assert.Contains("<div id=\"c0\"", html);
        Assert.Contains("<div id=\"c1\" class=\"ppdf-chapter main\" style=\"break-before: page\">", html);
        Assert.Contains("<div id=\"c2\"", html);
        Assert.DoesNotContain("<body class=\"main\"", html); // the chapters' own body tags are gone
        Assert.Contains("<title>The Test Book</title>", html);
    }

    [Fact]
    public void Links_between_chapters_become_links_inside_the_page()
    {
        string html = Open(TestEpub.Create()).BuildHtml();
        Assert.Contains("href=\"#c1_sec\"", html);   // chapter%20two.xhtml#sec
        Assert.Contains("href=\"#c0_top\"", html);   // #top inside chapter one
        Assert.Contains("href=\"#c0\"", html);       // ch1.xhtml from chapter two
        Assert.Contains("id=\"c1_sec\"", html);
        Assert.Contains("id=\"c0_top\"", html);
        Assert.Contains("href=\"https://example.com/x\"", html); // outside links are left alone
    }

    [Fact]
    public void Pictures_and_stylesheets_are_served_from_the_book_folder_once()
    {
        string html = Open(TestEpub.Create()).BuildHtml();
        Assert.Contains("src=\"/OEBPS/images/pic.png\"", html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "href=\"/OEBPS/css/style.css\""));
    }

    [Fact]
    public void Empty_elements_written_as_xhtml_do_not_swallow_what_follows()
    {
        string html = Open(TestEpub.Create()).BuildHtml();
        Assert.Contains("<a id=\"c1_sec\"></a>", html);
        Assert.Contains("<div class=\"x\"></div>", html);
        Assert.Contains("<img src=\"/OEBPS/images/pic.png\" alt=\"pic\"/>", html); // real void elements stay as they are
    }

    [Fact]
    public void A_book_without_content_or_without_a_container_is_refused()
    {
        Assert.Throws<EpubException>(() => Open(TestEpub.Create(withSpine: false), "empty"));
        var notBook = new MemoryStream();
        using (var zip = new ZipArchive(notBook, ZipArchiveMode.Create, true)) zip.CreateEntry("hello.txt");
        Assert.Throws<EpubException>(() => Open(notBook.ToArray(), "notbook"));
        Assert.Throws<EpubException>(() => Open(Encoding.UTF8.GetBytes("not a zip at all"), "garbage"));
    }

    [Fact]
    public void Entries_cannot_write_outside_the_folder()
    {
        string outside = Path.Combine(_dir, "evil.txt");
        Open(TestEpub.Create(z => { using var w = new StreamWriter(z.CreateEntry("../../evil.txt").Open()); w.Write("x"); }), "slip");
        Assert.False(File.Exists(outside));
        Assert.False(File.Exists(Path.Combine(_dir, "..", "evil.txt")));
    }

    [Fact]
    public void DRM_protected_books_are_recognised_but_scrambled_fonts_are_not_DRM()
    {
        const string drm = """<encryption xmlns="urn:oasis:names:tc:opendocument:xmlns:container" xmlns:enc="http://www.w3.org/2001/04/xmlenc#"><enc:EncryptedData><enc:EncryptionMethod Algorithm="http://www.w3.org/2001/04/xmlenc#aes128-cbc"/><enc:CipherData><enc:CipherReference URI="OEBPS/text/ch1.xhtml"/></enc:CipherData></enc:EncryptedData></encryption>""";
        var ex = Assert.Throws<EpubException>(() => Open(TestEpub.Create(z => { using var w = new StreamWriter(z.CreateEntry("META-INF/encryption.xml").Open()); w.Write(drm); }), "drm"));
        Assert.Contains("DRM", ex.Message);

        const string fonts = """<encryption xmlns="urn:oasis:names:tc:opendocument:xmlns:container" xmlns:enc="http://www.w3.org/2001/04/xmlenc#"><enc:EncryptedData><enc:EncryptionMethod Algorithm="http://www.idpf.org/2008/embedding"/><enc:CipherData><enc:CipherReference URI="OEBPS/fonts/f.otf"/></enc:CipherData></enc:EncryptedData></encryption>""";
        var scrambled = new byte[3000];
        new Random(1).NextBytes(scrambled);
        var book = Open(TestEpub.Create(z =>
        {
            using (var w = new StreamWriter(z.CreateEntry("META-INF/encryption.xml").Open())) w.Write(fonts);
            using var f = z.CreateEntry("OEBPS/fonts/f.otf").Open(); f.Write(scrambled);
        }), "fonts");
        byte[] now = File.ReadAllBytes(Path.Combine(book.Folder, "OEBPS", "fonts", "f.otf"));
        Assert.NotEqual(scrambled, now);                       // the first 1040 bytes were unscrambled
        Assert.Equal(scrambled[1100..], now[1100..]);          // the rest is untouched
        // scrambling is its own inverse
        string copy = Path.Combine(_dir, "copy.bin");
        File.WriteAllBytes(copy, now);
        EpubBook.Unscramble(copy, "http://www.idpf.org/2008/embedding", "urn:uuid:12345678-1234-1234-1234-123456789abc");
        Assert.Equal(scrambled, File.ReadAllBytes(copy));
    }

    [Theory]
    [InlineData("a/b/../c", "a/c")]
    [InlineData("../../x", "x")]
    [InlineData("./a//b", "a/b")]
    [InlineData("a\\b", "a/b")]
    public void Paths_are_normalised_and_never_leave_the_book(string input, string expected)
        => Assert.Equal(expected, EpubBook.Normalize(input));
}
