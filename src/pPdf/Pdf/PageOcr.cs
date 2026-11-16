using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using pPdf.Services;

namespace pPdf.Pdf;

/// <summary>
/// Reads the text of pages that have none (scans, photos of pages) with Windows' recognizer, as a <see cref="PageText"/> like the
/// one PDFium gives, so search, selection, copy and highlights work on them. The results are kept in the cache folder.
/// </summary>
public static class PageOcr
{
    /// <summary>Fewer letters than this and the page counts as having no text (a scan may carry a page number or a stamp).</summary>
    const int MinLetters = 12;

    /// <summary>Whether PDFium finds (almost) no text on a page.</summary>
    public static bool HasNoText(PdfFile pdf, int page)
    {
        var text = pdf.LoadText(page, false, ownTextOnly: true);
        if (text == null) return false; // closed: nothing to do
        int letters = 0;
        foreach (char c in text.Text) if (char.IsLetterOrDigit(c) && ++letters >= MinLetters) return false;
        return true;
    }

    /// <summary>
    /// Recognizes the text of a page: drawn at up to 300 dpi (within what the recognizer accepts), read, and turned into characters with
    /// boxes in display space. Null when Windows has no recognizer for the user's languages.
    /// </summary>
    public static PageText? Recognize(PdfFile pdf, int page)
    {
        var size = pdf.Pages[page];
        double longest = Math.Max(size.Width, size.Height);
        int max = Math.Min(WinOcr.MaxImageDimension, 6000);
        double scale = Math.Min(300 / 72.0, max / longest);
        int w = Math.Max(1, (int)(size.Width * scale)), h = Math.Max(1, (int)(size.Height * scale));
        var bmp = pdf.Render(page, w, h, new Int32Rect(0, 0, w, h), 0, invert: false, printing: true)
                  ?? throw new ObjectDisposedException(nameof(PdfFile));
        int stride = w * 4;
        var pixels = new byte[stride * h];
        bmp.CopyPixels(pixels, stride, 0);
        var lines = WinOcr.Recognize(pixels, w, h, stride, TimeSpan.FromMinutes(2));
        return lines == null ? null : Build(page, lines, scale);
    }

    /// <summary>Lines of words (pixels) into page text: words separated by spaces, lines by line breaks, a box for every character.</summary>
    internal static PageText Build(int page, List<List<WinOcr.Word>> lines, double scale)
    {
        var sb = new StringBuilder();
        var boxes = new List<RectF>();
        float s = (float)scale;
        foreach (var line in lines)
        {
            if (sb.Length > 0) { sb.Append('\n'); boxes.Add(default); }
            for (int i = 0; i < line.Count; i++)
            {
                var word = line[i];
                float x = word.X / s, y = word.Y / s, width = word.Width / s, height = word.Height / s;
                if (i > 0)
                {
                    // the space covers the gap, so a selection runs on across the line
                    var prev = line[i - 1];
                    float gapStart = (prev.X + prev.Width) / s;
                    sb.Append(' ');
                    boxes.Add(x > gapStart ? new RectF(gapStart, y, x - gapStart, height) : default);
                }
                // the word's box shared out between its letters
                float each = width / Math.Max(1, word.Text.Length);
                for (int c = 0; c < word.Text.Length; c++)
                {
                    sb.Append(word.Text[c]);
                    boxes.Add(new RectF(x + c * each, y, each, height));
                }
            }
        }
        return new PageText(page, sb.ToString(), boxes.ToArray());
    }

    // ------------------------------------------------------------------ cache

    static string Folder => Path.Combine(AppSettings.CacheFolder, "ocr");

    static string FileFor(string documentPath)
    {
        string key = Path.GetFullPath(documentPath).ToLowerInvariant();
        return Path.Combine(Folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..20] + ".json");
    }

    sealed class CacheFile
    {
        public string? Fingerprint { get; set; }
        public Dictionary<int, CachePage> Pages { get; set; } = [];
    }

    sealed class CachePage
    {
        public string Text { get; set; } = "";
        /// <summary>x, y, width, height of every character, one after the other.</summary>
        public float[] Boxes { get; set; } = [];
    }

    /// <summary>The text read earlier for a document, if it is still the same file (same <paramref name="fingerprint"/>).</summary>
    public static Dictionary<int, PageText> Load(string documentPath, string? fingerprint)
    {
        var result = new Dictionary<int, PageText>();
        if (fingerprint == null) return result;
        try
        {
            string file = FileFor(documentPath);
            if (!File.Exists(file)) return result;
            var cache = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(file));
            if (cache == null || cache.Fingerprint != fingerprint) return result;
            foreach (var (page, p) in cache.Pages)
            {
                if (page < 0 || p.Boxes.Length != p.Text.Length * 4) continue;
                var boxes = new RectF[p.Text.Length];
                for (int i = 0; i < boxes.Length; i++) boxes[i] = new RectF(p.Boxes[i * 4], p.Boxes[i * 4 + 1], p.Boxes[i * 4 + 2], p.Boxes[i * 4 + 3]);
                result[page] = new PageText(page, p.Text, boxes);
            }
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow); // used: the housekeeping keeps it
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return result;
    }

    public static void Save(string documentPath, string? fingerprint, IReadOnlyDictionary<int, PageText> pages)
    {
        if (fingerprint == null || pages.Count == 0) return;
        var cache = new CacheFile { Fingerprint = fingerprint };
        foreach (var (page, text) in pages)
        {
            var b = text.Boxes ?? [];
            var flat = new float[b.Length * 4];
            for (int i = 0; i < b.Length; i++) { flat[i * 4] = b[i].X; flat[i * 4 + 1] = b[i].Y; flat[i * 4 + 2] = b[i].Width; flat[i * 4 + 3] = b[i].Height; }
            cache.Pages[page] = new CachePage { Text = text.Text, Boxes = flat };
        }
        try
        {
            Directory.CreateDirectory(Folder);
            string file = FileFor(documentPath), tmp = file + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(cache), new UTF8Encoding(false));
            File.Move(tmp, file, overwrite: true);
            Prune();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static void Delete(string documentPath)
    {
        try { File.Delete(FileFor(documentPath)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The text of the 200 documents used last is kept; it can always be read again.</summary>
    static void Prune()
    {
        var files = new DirectoryInfo(Folder).EnumerateFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).Skip(200);
        foreach (var f in files) { try { f.Delete(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    // ------------------------------------------------------------------ words, for writing the text into a copy

    /// <summary>The words of a page with their boxes (display space), for drawing them as invisible text into a saved copy.</summary>
    public static IEnumerable<(string Word, Rect Box)> Words(PageText text)
    {
        var b = text.Boxes;
        if (b == null) yield break;
        int start = -1;
        for (int i = 0; i <= text.Text.Length; i++)
        {
            bool letter = i < text.Text.Length && !char.IsWhiteSpace(text.Text[i]);
            if (letter && start < 0) start = i;
            if (!letter && start >= 0)
            {
                var box = b[start];
                for (int k = start + 1; k < i; k++) box = box.Union(b[k]);
                if (!box.IsEmpty) yield return (text.Text[start..i], box.ToRect());
                start = -1;
            }
        }
    }
}
