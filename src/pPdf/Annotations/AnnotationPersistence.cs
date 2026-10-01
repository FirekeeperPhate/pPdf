using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using pPdf.Services;

namespace pPdf.Annotations;

/// <summary>
/// Keeps the annotations of each document (and the values typed in its form) on disk, in %AppData%\pPdf\annotations, so they are
/// back and still editable the next time the same file is opened, and survive a crash. The PDF itself is never touched:
/// "Save a copy" is what writes them into pages.
/// </summary>
public sealed class AnnotationPersistence(string root)
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static AnnotationPersistence Default { get; } = new(Path.Combine(AppSettings.Folder, "annotations"));

    /// <summary>What is kept for one document.</summary>
    public sealed class Snapshot
    {
        public List<Annotation> Annotations { get; init; } = [];
        public Dictionary<string, string> FormValues { get; init; } = [];
        public bool IsEmpty => Annotations.Count == 0 && FormValues.Count == 0;
    }

    sealed class Dto
    {
        public string Type { get; set; } = "";
        public int Page { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double W { get; set; }
        public double H { get; set; }
        // text
        public string? Text { get; set; }
        public string? Font { get; set; }
        public double Size { get; set; }
        public bool Bold { get; set; }
        public bool Italic { get; set; }
        public string? Color { get; set; }
        public string? Fill { get; set; }
        // image
        public string? Image { get; set; }
        // markup
        public MarkupKind Kind { get; set; }
        public List<double[]>? Rects { get; set; }
    }

    sealed class File_
    {
        public int Version { get; set; } = 1;
        public string Path { get; set; } = "";
        public List<Dto> Items { get; set; } = [];
        public Dictionary<string, string> Form { get; set; } = [];
    }

    public string DirectoryFor(string pdfPath)
    {
        string key = System.IO.Path.GetFullPath(pdfPath).ToLowerInvariant();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..20];
        return System.IO.Path.Combine(root, hash);
    }

    // ------------------------------------------------------------------ save

    /// <summary>Writes the annotations (a snapshot: clones are fine) and form values; with nothing to keep, removes what was kept.</summary>
    public void Save(string pdfPath, IReadOnlyList<Annotation> annotations, IReadOnlyDictionary<string, string>? formValues = null)
    {
        string dir = DirectoryFor(pdfPath);
        formValues ??= new Dictionary<string, string>();
        if (annotations.Count == 0 && formValues.Count == 0) { Delete(pdfPath); return; }

        Directory.CreateDirectory(dir);
        var file = new File_ { Path = pdfPath, Form = new Dictionary<string, string>(formValues) };
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "annotations.json" };
        foreach (var a in annotations)
        {
            var dto = new Dto { Page = a.Page, X = a.X, Y = a.Y, W = a.Width, H = a.Height };
            switch (a)
            {
                case TextAnnotation t:
                    dto.Type = "text"; dto.Text = t.Text; dto.Font = t.FontFamily; dto.Size = t.FontSize; dto.Bold = t.Bold; dto.Italic = t.Italic;
                    dto.Color = t.Foreground.ToString(); dto.Fill = t.Background?.ToString();
                    break;
                case ImageAnnotation i:
                    dto.Type = "image";
                    dto.Image = WriteImage(dir, i.Data);
                    keep.Add(dto.Image);
                    break;
                case MarkupAnnotation m:
                    dto.Type = "markup"; dto.Kind = m.Kind; dto.Color = m.Color.ToString();
                    dto.Rects = m.Rects.Select(r => new[] { r.X, r.Y, r.Width, r.Height }).ToList();
                    break;
                default: continue;
            }
            file.Items.Add(dto);
        }

        string json = System.IO.Path.Combine(dir, "annotations.json");
        string tmp = json + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(file, Json), new UTF8Encoding(false));
        File.Move(tmp, json, overwrite: true);

        // images no annotation uses any more
        foreach (string f in Directory.EnumerateFiles(dir))
            if (!keep.Contains(System.IO.Path.GetFileName(f)) && !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) TryDelete(f);
    }

    static string WriteImage(string dir, byte[] data)
    {
        string ext = data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 ? ".jpg" : ".png";
        string name = "img-" + Convert.ToHexString(SHA256.HashData(data))[..16] + ext;
        string path = System.IO.Path.Combine(dir, name);
        if (!File.Exists(path)) File.WriteAllBytes(path, data);
        return name;
    }

    public void Delete(string pdfPath)
    {
        string dir = DirectoryFor(pdfPath);
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    static void TryDelete(string f)
    {
        try { File.Delete(f); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------ load

    /// <summary>What was kept for a document; empty if nothing, or if what is there cannot be read.</summary>
    public Snapshot Load(string pdfPath)
    {
        var result = new Snapshot();
        string dir = DirectoryFor(pdfPath);
        string json = System.IO.Path.Combine(dir, "annotations.json");
        if (!File.Exists(json)) return result;
        File_? file;
        try { file = JsonSerializer.Deserialize<File_>(File.ReadAllText(json), Json); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return result; }
        if (file == null) return result;

        foreach (var (k, v) in file.Form) result.FormValues[k] = v;
        foreach (var d in file.Items)
        {
            try
            {
                Annotation? a = d.Type switch
                {
                    "text" => new TextAnnotation
                    {
                        Text = d.Text ?? "", FontFamily = d.Font ?? "Arial", FontSize = d.Size > 0 ? d.Size : 14, Bold = d.Bold, Italic = d.Italic,
                        Foreground = ParseColor(d.Color) ?? Colors.Black, Background = ParseColor(d.Fill),
                    },
                    "image" => LoadImage(dir, d),
                    "markup" => LoadMarkup(d),
                    _ => null,
                };
                if (a == null) continue;
                a.Page = Math.Max(0, d.Page);
                if (a is not MarkupAnnotation) { a.X = d.X; a.Y = d.Y; a.Width = d.W; a.Height = d.H; }
                result.Annotations.Add(a);
            }
            catch (Exception ex) when (ex is IOException or FormatException or NotSupportedException or InvalidOperationException or ArgumentException)
            {
                // one damaged entry (a missing image...) must not lose the others
            }
        }
        return result;
    }

    static ImageAnnotation? LoadImage(string dir, Dto d)
    {
        if (string.IsNullOrEmpty(d.Image) || d.Image.Contains('\\') || d.Image.Contains('/')) return null;
        string path = System.IO.Path.Combine(dir, d.Image);
        return File.Exists(path) ? ImageAnnotation.FromBytes(File.ReadAllBytes(path)) : null;
    }

    static MarkupAnnotation? LoadMarkup(Dto d)
    {
        if (d.Rects == null || d.Rects.Count == 0) return null;
        var rects = d.Rects.Where(r => r.Length == 4 && r[2] > 0 && r[3] > 0).Select(r => new Rect(r[0], r[1], r[2], r[3])).ToList();
        return rects.Count == 0 ? null : MarkupAnnotation.Create(d.Page, d.Kind, ParseColor(d.Color) ?? Colors.Yellow, rects);
    }

    static Color? ParseColor(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        try { return (Color)ColorConverter.ConvertFromString(s); }
        catch (FormatException) { return null; }
    }

    // ------------------------------------------------------------------ housekeeping

    /// <summary>Forgets the oldest documents beyond <paramref name="keep"/> (and anything older than a year).</summary>
    public void Prune(int keep = 150)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            var dirs = new DirectoryInfo(root).EnumerateDirectories().OrderByDescending(d => d.LastWriteTimeUtc).ToList();
            for (int i = 0; i < dirs.Count; i++)
                if (i >= keep || DateTime.UtcNow - dirs[i].LastWriteTimeUtc > TimeSpan.FromDays(365))
                    try { dirs[i].Delete(true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
