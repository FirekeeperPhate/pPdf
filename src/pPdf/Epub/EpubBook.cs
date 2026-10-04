using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using pPdf.Pdf;

namespace pPdf.Epub;

/// <summary>A book that cannot be shown: damaged, empty, or protected by DRM.</summary>
public sealed class EpubException(string message) : PdfException(message);

/// <summary>
/// An EPUB unpacked on disk: the spine (the chapters in reading order) and a single HTML page made of all of them, ready to be laid
/// out into pages by a browser engine. Links between chapters become links inside that page, so they survive in the PDF.
/// </summary>
public sealed partial class EpubBook
{
    /// <summary>Name the combined page is saved under, in the folder the book was unpacked into.</summary>
    public const string PageName = "ppdf-book.html";

    /// <summary>Host name the unpacked folder is served under while the page is laid out.</summary>
    public const string Host = "ppdf.epub";

    const long MaxUnpackedBytes = 2L << 30;

    public sealed record Chapter(int Index, string Path);

    public string Folder { get; }
    public string Title { get; private set; } = "";
    public string Language { get; private set; } = "";
    public IReadOnlyList<Chapter> Spine => _spine;

    readonly List<Chapter> _spine = [];
    readonly Dictionary<string, Chapter> _byPath = new(StringComparer.OrdinalIgnoreCase);

    EpubBook(string folder) { Folder = folder; }

    // ------------------------------------------------------------------ unpacking

    /// <summary>Unpacks <paramref name="epubPath"/> into <paramref name="folder"/> and reads its package.</summary>
    public static EpubBook Open(string epubPath, string folder)
    {
        try
        {
            using var zip = ZipFile.OpenRead(epubPath);
            long total = 0;
            foreach (var entry in zip.Entries) total += entry.Length;
            if (total > MaxUnpackedBytes) throw new EpubException("This book is too big to open.");

            string root = System.IO.Path.GetFullPath(folder) + System.IO.Path.DirectorySeparatorChar;
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/') || entry.Name.Length == 0) continue;
                // one odd entry (a name Windows refuses, a damaged piece) must not cost the whole book: a missing picture is better than no book
                try
                {
                    string[] parts = entry.FullName.Replace('\\', '/').Split('/');
                    if (parts.Any(IsDeviceName)) continue; // CON, NUL, AUX...: Windows would open a device instead of making a file
                    string target = System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, string.Join(System.IO.Path.DirectorySeparatorChar, parts)));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // an entry trying to leave the folder
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                }
                catch (Exception ex) when (ex is NotSupportedException or ArgumentException or PathTooLongException or InvalidDataException) { }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new EpubException(ex is InvalidDataException ? "The file is not a valid EPUB book." : ex.Message);
        }

        var book = new EpubBook(folder);
        book.ReadPackage();
        return book;
    }

    static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
        { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    /// <summary>A file name Windows reads as a device, with or without an extension ("aux.xhtml" is still AUX).</summary>
    static bool IsDeviceName(string part)
    {
        int dot = part.IndexOf('.');
        return DeviceNames.Contains((dot < 0 ? part : part[..dot]).TrimEnd(' '));
    }

    static string? FindFile(string folder, string relative)
    {
        string p = System.IO.Path.Combine(folder, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        return File.Exists(p) ? p : null;
    }

    void ReadPackage()
    {
        string? container = FindFile(Folder, "META-INF/container.xml");
        if (container == null) throw new EpubException("The file is not a valid EPUB book (no container).");
        string? opfPath;
        try
        {
            opfPath = XDocument.Load(container).Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile")?.Attribute("full-path")?.Value;
        }
        catch (System.Xml.XmlException) { throw new EpubException("The book's container is damaged."); }
        if (string.IsNullOrWhiteSpace(opfPath) || FindFile(Folder, Uri.UnescapeDataString(opfPath)) is not { } opfFile)
            throw new EpubException("The book has no package file.");

        XDocument opf;
        try { opf = XDocument.Load(opfFile); }
        catch (System.Xml.XmlException) { throw new EpubException("The book's package file is damaged."); }

        string opfDir = DirectoryOf(Normalize(Uri.UnescapeDataString(opfPath)));
        var manifest = new Dictionary<string, string>(); // id -> path from the root of the book
        foreach (var item in opf.Descendants().Where(e => e.Name.LocalName == "item"))
        {
            string? id = item.Attribute("id")?.Value, href = item.Attribute("href")?.Value;
            if (id != null && href != null) manifest[id] = Normalize(Combine(opfDir, Uri.UnescapeDataString(StripFragment(href))));
        }

        foreach (var itemref in opf.Descendants().Where(e => e.Name.LocalName == "itemref"))
        {
            if (itemref.Attribute("idref")?.Value is not { } idref || !manifest.TryGetValue(idref, out string? path)) continue;
            if (_byPath.ContainsKey(path) || FindFile(Folder, path) == null) continue;
            var chapter = new Chapter(_spine.Count, path);
            _spine.Add(chapter);
            _byPath[path] = chapter;
        }
        if (_spine.Count == 0) throw new EpubException("The book has no readable content.");

        Title = opf.Descendants().FirstOrDefault(e => e.Name.LocalName == "title")?.Value.Trim() ?? "";
        Language = opf.Descendants().FirstOrDefault(e => e.Name.LocalName == "language")?.Value.Trim() ?? "";

        HandleEncryption(opf, manifest);
    }

    // ------------------------------------------------------------------ encryption

    const string IdpfFonts = "http://www.idpf.org/2008/embedding";
    const string AdobeFonts = "http://ns.adobe.com/pdf/enc#RC";

    /// <summary>
    /// Embedded fonts are often only scrambled (not protected): unscramble them. Anything else that is encrypted is DRM,
    /// which pPdf will not (and cannot) open.
    /// </summary>
    void HandleEncryption(XDocument opf, Dictionary<string, string> manifest)
    {
        string? file = FindFile(Folder, "META-INF/encryption.xml");
        if (file == null) return;
        XDocument enc;
        try { enc = XDocument.Load(file); }
        catch (System.Xml.XmlException) { return; }

        string? uid = UniqueIdentifier(opf);
        foreach (var data in enc.Descendants().Where(e => e.Name.LocalName == "EncryptedData"))
        {
            string? algorithm = data.Descendants().FirstOrDefault(e => e.Name.LocalName == "EncryptionMethod")?.Attribute("Algorithm")?.Value;
            string? uri = data.Descendants().FirstOrDefault(e => e.Name.LocalName == "CipherReference")?.Attribute("URI")?.Value;
            if (algorithm is IdpfFonts or AdobeFonts)
            {
                if (uri != null && uid != null && FindFile(Folder, Normalize(Uri.UnescapeDataString(uri))) is { } font)
                    try { Unscramble(font, algorithm, uid); } catch (IOException) { }
                continue;
            }
            throw new EpubException("This book is protected by DRM (copy protection), which pPdf cannot open.");
        }
    }

    static string? UniqueIdentifier(XDocument opf)
    {
        string? id = opf.Root?.Attribute("unique-identifier")?.Value;
        var ids = opf.Descendants().Where(e => e.Name.LocalName == "identifier").ToList();
        return (ids.FirstOrDefault(e => e.Attribute("id")?.Value == id) ?? ids.FirstOrDefault())?.Value;
    }

    /// <summary>Reverses the font scrambling of the EPUB specification (IDPF) or of Adobe: the first bytes are XORed with a key made from the book's identifier.</summary>
    internal static void Unscramble(string path, string algorithm, string identifier)
    {
        byte[] key;
        int length;
        if (algorithm == IdpfFonts)
        {
            key = SHA1.HashData(Encoding.UTF8.GetBytes(new string(identifier.Where(c => c is not (' ' or '\t' or '\r' or '\n')).ToArray())));
            length = 1040;
        }
        else
        {
            string hex = new(identifier.Replace("urn:uuid:", "", StringComparison.OrdinalIgnoreCase).Where(Uri.IsHexDigit).ToArray());
            if (hex.Length < 32) return;
            key = Convert.FromHexString(hex[..32]);
            length = 1024;
        }
        var bytes = File.ReadAllBytes(path);
        for (int i = 0; i < Math.Min(length, bytes.Length); i++) bytes[i] ^= key[i % key.Length];
        File.WriteAllBytes(path, bytes);
    }

    // ------------------------------------------------------------------ paths

    static string StripFragment(string url) => url.Contains('#') ? url[..url.IndexOf('#')] : url;

    static string DirectoryOf(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

    static string Combine(string dir, string relative) => relative.StartsWith('/') ? relative : dir.Length == 0 ? relative : dir + "/" + relative;

    /// <summary>A path inside the book, with "." and ".." resolved (never above the root), forward slashes, no leading slash.</summary>
    internal static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (string part in path.Replace('\\', '/').Split('/'))
        {
            if (part.Length == 0 || part == ".") continue;
            if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(part);
        }
        return string.Join('/', parts);
    }

    static string UrlOf(string path) => "/" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    // ------------------------------------------------------------------ the combined page

    [GeneratedRegex(@"<body\b([^>]*)>(.*)</body\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BodyRx();
    [GeneratedRegex(@"<head\b.*?</head\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HeadRx();
    [GeneratedRegex(@"<link\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex LinkRx();
    [GeneratedRegex(@"<style\b[^>]*>.*?</style\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex StyleRx();
    [GeneratedRegex(@"(?<![\w:-])(src|href|xlink:href)\s*=\s*(?:""([^""]*)""|'([^']*)')", RegexOptions.IgnoreCase)]
    private static partial Regex UrlAttrRx();
    [GeneratedRegex(@"(?<![\w:-])id\s*=\s*(?:""([^""]*)""|'([^']*)')", RegexOptions.IgnoreCase)]
    private static partial Regex IdAttrRx();
    [GeneratedRegex(@"<a\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex AnchorRx();
    [GeneratedRegex(@"(?<![\w:-])name\s*=\s*(?:""([^""]*)""|'([^']*)')", RegexOptions.IgnoreCase)]
    private static partial Regex NameAttrRx();
    [GeneratedRegex(@"(?<![\w:-])(?:rel\s*=\s*[""']?[^""'>]*stylesheet)", RegexOptions.IgnoreCase)]
    private static partial Regex StylesheetRelRx();
    [GeneratedRegex(@"(?<![\w:-])class\s*=\s*(?:""([^""]*)""|'([^']*)')", RegexOptions.IgnoreCase)]
    private static partial Regex ClassAttrRx();
    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9+.-]*:")]
    private static partial Regex SchemeRx();
    // an element written as <name ... /> that HTML does not know to be empty: XHTML allows it, an HTML parser would leave it open
    [GeneratedRegex(@"<(?!(?:area|base|br|col|embed|hr|img|input|link|meta|param|source|track|wbr)\b)([a-zA-Z][\w:-]*)((?:[^<>""']|""[^""]*""|'[^']*')*?)\s*/>", RegexOptions.IgnoreCase)]
    private static partial Regex SelfClosingRx();

    static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp"];

    const string PageCss ="@page { size: 5.5in 8.5in; margin: 0.6in 0.55in; }";

    /// <summary>Printing styles first, so the book's own stylesheets can override them.</summary>
    const string BaseCss = """
        html { -webkit-print-color-adjust: exact; print-color-adjust: exact; }
        body { font-family: Georgia, 'Times New Roman', serif; font-size: 11.5pt; line-height: 1.45; color: #111; margin: 0; orphans: 2; widows: 2; overflow-wrap: break-word; }
        img, svg, video { max-width: 100%; height: auto; max-height: 7.2in; object-fit: contain; }
        img { break-inside: avoid; }
        h1, h2, h3, h4, h5, h6 { break-after: avoid; }
        table { max-width: 100%; }
        pre { white-space: pre-wrap; }
        """;

    /// <summary>All the chapters in one HTML page: each starts on a new page, ids and links are renamed so they stay unique and keep working.</summary>
    public string BuildHtml()
    {
        // the chapters do not depend on each other (the rename tables are read-only): process them on all cores, then join them in order
        var parts = new ChapterParts[_spine.Count];
        Parallel.For(0, _spine.Count, i => parts[i] = BuildChapter(_spine[i]));

        var styleLinks = new List<string>();
        var seenLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var styles = new List<string>();
        var seenStyles = new HashSet<string>();
        var bodies = new StringBuilder();
        foreach (var p in parts)
        {
            foreach (string url in p.StyleUrls) if (seenLinks.Add(url)) styleLinks.Add($"<link rel=\"stylesheet\" href=\"{url}\">");
            foreach (string s in p.Styles) if (seenStyles.Add(s)) styles.Add(s);
            bodies.Append(p.Html);
        }

        var sb = new StringBuilder(bodies.Length + 4096);
        sb.Append("<!DOCTYPE html>\n<html").Append(Language.Length > 0 ? $" lang=\"{System.Net.WebUtility.HtmlEncode(Language)}\"" : "").Append(">\n<head>\n<meta charset=\"utf-8\">\n");
        sb.Append("<title>").Append(System.Net.WebUtility.HtmlEncode(Title)).Append("</title>\n");
        sb.Append("<style>").Append(BaseCss).Append("</style>\n");
        foreach (string l in styleLinks) sb.Append(l).Append('\n');
        foreach (string s in styles) sb.Append(s).Append('\n');
        // last, so the page size and margins are ours: many books carry an @page rule of their own (a few points of margin, made for another reader)
        sb.Append("<style>").Append(PageCss).Append("</style>\n");
        sb.Append("</head>\n<body>\n").Append(bodies).Append("</body>\n</html>\n");
        return sb.ToString();
    }

    /// <summary>What one chapter contributes: the stylesheets it asks for, its own style blocks, and its content (wrapped in a div of its own).</summary>
    readonly record struct ChapterParts(List<string> StyleUrls, List<string> Styles, string Html);

    ChapterParts BuildChapter(Chapter chapter)
    {
        var urls = new List<string>();
        var styles = new List<string>();
        string dir = DirectoryOf(chapter.Path);
        string open = $"<div id=\"c{chapter.Index}\" class=\"ppdf-chapter";
        string pageBreak = chapter.Index > 0 ? " style=\"break-before: page\">" : ">";

        if (ImageExtensions.Contains(System.IO.Path.GetExtension(chapter.Path), StringComparer.OrdinalIgnoreCase))
        {
            // a picture listed in the spine (against the specification, but it happens: a cover): it is shown as a page of its own
            return new ChapterParts(urls, styles, open + "\"" + pageBreak + $"<img src=\"{UrlOf(chapter.Path)}\" alt=\"\"></div>\n");
        }
        string text = ReadText(System.IO.Path.Combine(Folder, chapter.Path.Replace('/', System.IO.Path.DirectorySeparatorChar)));

        string head = HeadRx().Match(text) is { Success: true } hm ? hm.Value : "";
        foreach (Match m in LinkRx().Matches(head))
        {
            if (!StylesheetRelRx().IsMatch(m.Value)) continue;
            var href = UrlAttrRx().Match(m.Value);
            if (!href.Success || href.Groups[1].Value.ToLowerInvariant() != "href") continue;
            string url = ResolveResource(dir, href.Groups[2].Success && href.Groups[2].Length > 0 ? href.Groups[2].Value : href.Groups[3].Value);
            if (url.Length > 0) urls.Add(url);
        }
        foreach (Match m in StyleRx().Matches(head)) styles.Add(m.Value);

        string attrs = "", inner;
        if (BodyRx().Match(text) is { Success: true } bm) { attrs = bm.Groups[1].Value; inner = bm.Groups[2].Value; }
        else inner = HeadRx().Replace(text, "");

        inner = SelfClosingRx().Replace(inner, "<$1$2></$1>");
        inner = IdAttrRx().Replace(inner, m => $"id=\"c{chapter.Index}_{Quote(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)}\"");
        inner = AnchorRx().Replace(inner, a => NameAttrRx().Replace(a.Value, n =>
            $"id=\"c{chapter.Index}_{Quote(n.Groups[1].Success ? n.Groups[1].Value : n.Groups[2].Value)}\""));
        inner = UrlAttrRx().Replace(inner, m =>
        {
            string name = m.Groups[1].Value, value = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            return $"{name}=\"{Rewrite(chapter, dir, name.ToLowerInvariant() == "src", value)}\"";
        });

        string cls = ClassAttrRx().Match(attrs) is { Success: true } cm ? (cm.Groups[1].Success ? cm.Groups[1].Value : cm.Groups[2].Value) : "";
        return new ChapterParts(urls, styles, open + " " + System.Net.WebUtility.HtmlEncode(cls) + "\"" + pageBreak + inner + "</div>\n");
    }

    /// <summary>An attribute value from the source, safe to put between double quotes (the source may have used single quotes around a double quote).</summary>
    static string Quote(string raw) => raw.Replace("\"", "&quot;");

    static string ReadText(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        // XHTML is UTF-8 unless it says otherwise (UTF-16 with a mark is the only other thing worth handling)
        if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
            return Encoding.Unicode.GetString(bytes);
        string text = Encoding.UTF8.GetString(bytes);
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }

    /// <summary>A reference inside a chapter: to another place of the book (becomes a link inside the page) or to a file (a path under <see cref="Host"/>).</summary>
    string Rewrite(Chapter chapter, string dir, bool resourceOnly, string value)
    {
        value = System.Net.WebUtility.HtmlDecode(value.Trim());
        if (value.Length == 0 || SchemeRx().IsMatch(value) || value.StartsWith("//")) return System.Net.WebUtility.HtmlEncode(value);

        if (value.StartsWith('#'))
            return resourceOnly ? System.Net.WebUtility.HtmlEncode(value) : "#c" + chapter.Index + "_" + System.Net.WebUtility.HtmlEncode(value[1..]);

        string path = value, fragment = "";
        int hash = value.IndexOf('#');
        if (hash >= 0) { path = value[..hash]; fragment = value[(hash + 1)..]; }
        string full = Normalize(Combine(dir, Uri.UnescapeDataString(path)));
        if (!resourceOnly && _byPath.TryGetValue(full, out var target))
            return fragment.Length > 0 ? $"#c{target.Index}_{System.Net.WebUtility.HtmlEncode(fragment)}" : $"#c{target.Index}";
        return UrlOf(full) + (fragment.Length > 0 ? "#" + System.Net.WebUtility.HtmlEncode(fragment) : "");
    }

    string ResolveResource(string dir, string value)
    {
        value = System.Net.WebUtility.HtmlDecode(value.Trim());
        if (value.Length == 0 || SchemeRx().IsMatch(value) || value.StartsWith("//")) return "";
        return UrlOf(Normalize(Combine(dir, Uri.UnescapeDataString(StripFragment(value)))));
    }
}
