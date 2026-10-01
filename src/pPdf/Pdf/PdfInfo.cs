namespace pPdf.Pdf;

/// <summary>What the document says about itself (the Info dictionary) and a few facts PDFium knows.</summary>
public sealed record PdfInfo(string Title, string Author, string Subject, string Keywords, string Creator, string Producer,
    DateTime? Created, DateTime? Modified, string Version, bool IsEncrypted, bool HasForm)
{
    /// <summary>Reads a PDF date ("D:20240131093000+01'00'") into a local time; null if it is not one.</summary>
    public static DateTime? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.StartsWith("D:", StringComparison.Ordinal)) s = s[2..];
        // YYYY MM DD HH mm SS then an optional zone
        int Part(int start, int length, int fallback) => s!.Length >= start + length && int.TryParse(s.AsSpan(start, length), out int v) ? v : fallback;
        if (s.Length < 4 || !int.TryParse(s.AsSpan(0, 4), out int year) || year < 1) return null;
        int month = Part(4, 2, 1), day = Part(6, 2, 1), hour = Part(8, 2, 0), minute = Part(10, 2, 0), second = Part(12, 2, 0);
        try
        {
            month = Math.Clamp(month, 1, 12);
            var dt = new DateTime(year, month, Math.Clamp(day, 1, DateTime.DaysInMonth(year, month)),
                Math.Clamp(hour, 0, 23), Math.Clamp(minute, 0, 59), Math.Clamp(second, 0, 59), DateTimeKind.Unspecified);
            // zone: Z, +HH'mm' or -HH'mm'
            string zone = s.Length > 14 ? s[14..] : "";
            if (zone.StartsWith('Z')) return DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToLocalTime();
            if (zone.Length >= 3 && (zone[0] == '+' || zone[0] == '-') && int.TryParse(zone.AsSpan(1, 2), out int zh))
            {
                int zm = zone.Length >= 6 && int.TryParse(zone.AsSpan(4, 2), out int m2) ? m2 : 0;
                var offset = new TimeSpan(zh, zm, 0) * (zone[0] == '-' ? -1 : 1);
                return new DateTimeOffset(dt, offset).LocalDateTime;
            }
            return dt;
        }
        catch (ArgumentException) { return null; }
    }

    /// <summary>"A4 (210 x 297 mm)" for the usual paper sizes, otherwise just the size.</summary>
    public static string DescribePageSize(double widthPt, double heightPt)
    {
        double w = Math.Min(widthPt, heightPt) / 72 * 25.4, h = Math.Max(widthPt, heightPt) / 72 * 25.4;
        string? name = null;
        (string Name, double W, double H)[] known =
        [
            ("A3", 297, 420), ("A4", 210, 297), ("A5", 148, 210), ("A6", 105, 148), ("Letter", 215.9, 279.4), ("Legal", 215.9, 355.6),
        ];
        foreach (var k in known) if (Math.Abs(w - k.W) < 2 && Math.Abs(h - k.H) < 2) { name = k.Name; break; }
        string size = $"{w:0.#} x {h:0.#} mm";
        return name == null ? size : $"{name} ({size})";
    }
}
