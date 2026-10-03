using System.Text.Json;
using System.Text.Json.Serialization;
using pPdf.Viewer;

namespace pPdf.Services;

public enum AppTheme { System, Light, Dark }

/// <summary>User preferences, kept in %AppData%\pPdf\settings.json (PPDF_DATA_DIR overrides the folder).</summary>
public sealed class AppSettings
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public AppTheme Theme { get; set; } = AppTheme.System;
    /// <summary>Invert the colors of the page bitmaps (a dark reading mode for the pages themselves).</summary>
    public bool InvertPages { get; set; }
    public ViewLayout Layout { get; set; } = ViewLayout.Continuous;
    public ZoomMode ZoomMode { get; set; } = ZoomMode.FitWidth;
    public double Zoom { get; set; } = 1.0;
    public bool CoverAlone { get; set; }
    public bool SidebarVisible { get; set; } = true;
    public double SidebarWidth { get; set; } = 210;
    public bool SidebarOutline { get; set; }
    public bool MatchCase { get; set; }
    public bool WholeWord { get; set; }

    public bool CheckForUpdates { get; set; } = true;
    public DateTime? LastUpdateCheck { get; set; }
    /// <summary>A version the user chose not to be offered again.</summary>
    public string? SkippedVersion { get; set; }

    public string MarkupColor { get; set; } = "#FFFFEB3B";

    public string TextFont { get; set; } = "Arial";
    public double TextSize { get; set; } = 14;
    public bool TextBold { get; set; }
    public bool TextItalic { get; set; }
    public string TextColor { get; set; } = "#FF000000";
    public string? TextFill { get; set; }

    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 1180;
    public double WindowHeight { get; set; } = 820;
    public bool Maximized { get; set; }

    public List<string> Recent { get; set; } = [];
    /// <summary>Older versions: last page of each recent file (0-based). Only read, for files that have no <see cref="Positions"/> entry yet.</summary>
    public Dictionary<string, int> LastPages { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where each document was left, most recently closed first (at most <see cref="MaxPositions"/>).</summary>
    public List<DocPosition> Positions { get; set; } = [];

    public const int MaxPositions = 200;

    public static string Folder
    {
        get
        {
            string? over = Environment.GetEnvironmentVariable("PPDF_DATA_DIR");
            return !string.IsNullOrWhiteSpace(over) ? over : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "pPdf");
        }
    }

    /// <summary>For things that can be rebuilt (converted books): local to this PC, not roaming. PPDF_DATA_DIR keeps it with the rest.</summary>
    public static string CacheFolder
    {
        get
        {
            string? over = Environment.GetEnvironmentVariable("PPDF_DATA_DIR");
            return !string.IsNullOrWhiteSpace(over) ? Path.Combine(over, "cache") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pPdf");
        }
    }

    static string FilePath =>Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void AddRecent(string path)
    {
        Recent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        Recent.Insert(0, path);
        if (Recent.Count > 12) Recent.RemoveRange(12, Recent.Count - 12);
    }

    /// <summary>The saved position of a document, or null if it was never opened (or only by an older version: then just its page).</summary>
    public DocPosition? FindPosition(string path)
    {
        var found = Positions.FirstOrDefault(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
        if (found != null) return found;
        return LastPages.TryGetValue(path, out int page) ? new DocPosition { Path = path, Page = page } : null;
    }

    public void SavePosition(DocPosition position)
    {
        Positions.RemoveAll(p => string.Equals(p.Path, position.Path, StringComparison.OrdinalIgnoreCase));
        Positions.Insert(0, position);
        if (Positions.Count > MaxPositions) Positions.RemoveRange(MaxPositions, Positions.Count - MaxPositions);
        LastPages.Remove(position.Path);
    }
}

/// <summary>Where a document was left: the page at the top of the window, how far down it, and how it was being viewed.</summary>
public sealed class DocPosition
{
    public string Path { get; set; } = "";
    /// <summary>0-based page (the first page of the row in the two-page views).</summary>
    public int Page { get; set; }
    /// <summary>How far down that page the top of the window is, 0..1 of its height.</summary>
    public double OffsetY { get; set; }
    /// <summary>How it was zoomed; null when only the page is known (a position migrated from an older version).</summary>
    public ZoomMode? ZoomMode { get; set; }
    /// <summary>Zoom factor (1.0 = 100 %), meaningful for <see cref="Viewer.ZoomMode.Custom"/>.</summary>
    public double Zoom { get; set; } = 1.0;
    /// <summary>Quarter turns clockwise.</summary>
    public int? Rotation { get; set; }
}
