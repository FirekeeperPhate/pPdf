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
    /// <summary>Last page seen in each recent file (0-based).</summary>
    public Dictionary<string, int> LastPages { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static string Folder
    {
        get
        {
            string? over = Environment.GetEnvironmentVariable("PPDF_DATA_DIR");
            return !string.IsNullOrWhiteSpace(over) ? over : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "pPdf");
        }
    }

    static string FilePath => Path.Combine(Folder, "settings.json");

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

    public void AddRecent(string path, int lastPage)
    {
        Recent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        Recent.Insert(0, path);
        if (Recent.Count > 12) Recent.RemoveRange(12, Recent.Count - 12);
        LastPages[path] = lastPage;
        foreach (var k in LastPages.Keys.Where(k => !Recent.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList()) LastPages.Remove(k);
    }
}
