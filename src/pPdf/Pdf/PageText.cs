using System.Globalization;
using System.Text;

namespace pPdf.Pdf;

/// <summary>A rectangle in 4 floats: PDF pages hold a lot of characters, so this is kept small.</summary>
public readonly record struct RectF(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;
    public float Bottom => Y + Height;
    public float CenterX => X + Width / 2;
    public float CenterY => Y + Height / 2;
    public bool IsEmpty => Width <= 0.01f && Height <= 0.01f;

    public bool Contains(double x, double y) => x >= X && x <= Right && y >= Y && y <= Bottom;

    public RectF Union(RectF o)
    {
        float l = Math.Min(X, o.X), t = Math.Min(Y, o.Y);
        return new RectF(l, t, Math.Max(Right, o.Right) - l, Math.Max(Bottom, o.Bottom) - t);
    }

    public System.Windows.Rect ToRect() => new(X, Y, Width, Height);
}

/// <summary>The text of one page together with the box of every UTF-16 unit, in display space.</summary>
public sealed class PageText(int page, string text, RectF[]? boxes)
{
    public int Page { get; } = page;
    public string Text { get; } = text;
    /// <summary>One box per UTF-16 unit of <see cref="Text"/>; null when the page was loaded for searching only.</summary>
    public RectF[]? Boxes { get; } = boxes;

    public bool HasBoxes => Boxes != null;

    // ------------------------------------------------------------------ hit testing

    /// <summary>The caret position (0..Text.Length) nearest to a point of the page, or -1 if the page has no text.</summary>
    public int CaretAt(double x, double y)
    {
        var b = Boxes;
        if (b == null || b.Length == 0) return -1;

        // 1) characters on the pointer's line: pick the horizontally nearest
        int best = -1;
        double bestDx = double.MaxValue;
        for (int i = 0; i < b.Length; i++)
        {
            ref readonly var r = ref b[i];
            if (r.IsEmpty || y < r.Y || y > r.Bottom) continue;
            double dx = x < r.X ? r.X - x : x > r.Right ? x - r.Right : 0;
            if (dx < bestDx) { bestDx = dx; best = i; if (dx == 0) break; }
        }

        // 2) otherwise the nearest box overall (vertical distance counts double: stay on a line when in doubt)
        if (best < 0)
        {
            double bestD = double.MaxValue;
            for (int i = 0; i < b.Length; i++)
            {
                ref readonly var r = ref b[i];
                if (r.IsEmpty) continue;
                double dx = x < r.X ? r.X - x : x > r.Right ? x - r.Right : 0;
                double dy = y < r.Y ? r.Y - y : y > r.Bottom ? y - r.Bottom : 0;
                double d = dx * dx + 4 * dy * dy;
                if (d < bestD) { bestD = d; best = i; }
            }
        }
        if (best < 0) return -1;
        return x > b[best].CenterX ? best + 1 : best;
    }

    /// <summary>True when the point is over a character (used for the I-beam cursor).</summary>
    public bool IsOverText(double x, double y)
    {
        var b = Boxes;
        if (b == null) return false;
        for (int i = 0; i < b.Length; i++)
            if (!b[i].IsEmpty && b[i].Contains(x, y)) return true;
        return false;
    }

    /// <summary>Like <see cref="IsOverText"/>, but also true within <paramref name="tolerance"/> points of a character.</summary>
    public bool IsNearText(double x, double y, double tolerance)
    {
        var b = Boxes;
        if (b == null) return false;
        for (int i = 0; i < b.Length; i++)
        {
            ref readonly var r = ref b[i];
            if (r.IsEmpty) continue;
            if (x >= r.X - tolerance && x <= r.Right + tolerance && y >= r.Y - tolerance && y <= r.Bottom + tolerance) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ ranges

    static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '\'' || c == '’';

    /// <summary>The word under a caret position, as a [start, end) range (empty if the position is not on a word).</summary>
    public (int Start, int End) WordAt(int caret)
    {
        if (Text.Length == 0) return (0, 0);
        int i = Math.Clamp(caret, 0, Text.Length - 1);
        if (!IsWordChar(Text[i]) && i > 0 && IsWordChar(Text[i - 1])) i--;
        if (!IsWordChar(Text[i]))
        {
            // a run of the same kind of separator (spaces, punctuation): just that character
            return (i, i + 1);
        }
        int s = i, e = i + 1;
        while (s > 0 && IsWordChar(Text[s - 1])) s--;
        while (e < Text.Length && IsWordChar(Text[e])) e++;
        return (s, e);
    }

    /// <summary>The line under a caret position, as a [start, end) range (without the line break).</summary>
    public (int Start, int End) LineAt(int caret)
    {
        if (Text.Length == 0) return (0, 0);
        int i = Math.Clamp(caret, 0, Text.Length - 1);
        int s = i, e = i;
        while (s > 0 && Text[s - 1] != '\n' && Text[s - 1] != '\r') s--;
        while (e < Text.Length && Text[e] != '\n' && Text[e] != '\r') e++;
        return (s, e);
    }

    /// <summary>The rectangles that cover a character range, one per line of text.</summary>
    public List<RectF> RectsForRange(int start, int end)
    {
        var result = new List<RectF>();
        var b = Boxes;
        if (b == null) return result;
        start = Math.Clamp(start, 0, b.Length);
        end = Math.Clamp(end, 0, b.Length);

        bool open = false;
        RectF cur = default;
        for (int i = start; i < end; i++)
        {
            var r = b[i];
            if (r.IsEmpty || char.IsControl(Text[i])) continue;
            if (open && SameLine(cur, r)) cur = cur.Union(r);
            else
            {
                if (open) result.Add(cur);
                cur = r;
                open = true;
            }
        }
        if (open) result.Add(cur);
        return result;
    }

    static bool SameLine(RectF line, RectF r)
    {
        float top = Math.Max(line.Y, r.Y), bottom = Math.Min(line.Bottom, r.Bottom);
        float overlap = bottom - top;
        float smaller = Math.Min(line.Height, r.Height);
        if (smaller <= 0 || overlap < smaller * 0.5f) return false;
        // far apart on the same baseline = another column
        float gap = r.X >= line.Right ? r.X - line.Right : line.X >= r.Right ? line.X - r.Right : 0;
        return gap <= Math.Max(line.Height, r.Height) * 2f;
    }

    public string Slice(int start, int end)
    {
        start = Math.Clamp(start, 0, Text.Length);
        end = Math.Clamp(end, start, Text.Length);
        return Text[start..end];
    }

    // ------------------------------------------------------------------ searching

    Normalized? _normalized;

    /// <summary>The text with whitespace collapsed and ligatures expanded, plus the way back to <see cref="Text"/> indices.</summary>
    public Normalized Norm => _normalized ??= Normalized.Build(Text);
}

public sealed class Normalized
{
    public string Text { get; }
    /// <summary>For each character of <see cref="Text"/>, the index of the source character in the page text.</summary>
    public int[] Map { get; }

    Normalized(string text, int[] map) { Text = text; Map = map; }

    public static Normalized Build(string source)
    {
        var sb = new StringBuilder(source.Length);
        var map = new List<int>(source.Length);
        bool lastSpace = true;
        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            if (char.IsWhiteSpace(c) || c == ' ' || char.IsControl(c))
            {
                if (lastSpace) continue;
                sb.Append(' '); map.Add(i); lastSpace = true;
                continue;
            }
            if (c == '­') continue; // soft hyphen
            lastSpace = false;
            if (c >= 'ﬀ' && c <= 'ﬆ')
            {
                // ligatures (fi, fl, ffi...) are one character in the PDF but several in what people type
                foreach (char x in c.ToString().Normalize(NormalizationForm.FormKC)) { sb.Append(x); map.Add(i); }
            }
            else
            {
                sb.Append(c); map.Add(i);
            }
        }
        return new Normalized(sb.ToString(), map.ToArray());
    }

    public static string NormalizeQuery(string q) => Build(q).Text.Trim();
}

public readonly record struct SearchOptions(bool MatchCase = false, bool WholeWord = false);

public readonly record struct TextMatch(int Page, int Start, int End);

public static class TextSearch
{
    /// <summary>All matches of <paramref name="query"/> (already normalised) in a page. Accents never matter unless MatchCase is on.</summary>
    public static List<TextMatch> FindAll(PageText page, string query, SearchOptions options)
    {
        var result = new List<TextMatch>();
        if (query.Length == 0) return result;
        var norm = page.Norm;
        var text = norm.Text;
        if (text.Length < query.Length) return result;

        var cmp = CultureInfo.CurrentCulture.CompareInfo;
        var opt = options.MatchCase ? CompareOptions.None : CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        int pos = 0;
        while (pos <= text.Length - 1)
        {
            int idx = cmp.IndexOf(text.AsSpan(pos), query.AsSpan(), opt, out int len);
            if (idx < 0) break;
            idx += pos;
            if (len <= 0) len = query.Length;
            int endNorm = idx + len; // exclusive
            if (!options.WholeWord || IsWholeWord(text, idx, endNorm))
            {
                int s = norm.Map[idx];
                int e = norm.Map[endNorm - 1] + 1;
                result.Add(new TextMatch(page.Page, s, e));
            }
            pos = Math.Max(endNorm, idx + 1);
        }
        return result;
    }

    static bool IsWholeWord(string text, int start, int end)
    {
        bool before = start == 0 || !char.IsLetterOrDigit(text[start - 1]);
        bool after = end >= text.Length || !char.IsLetterOrDigit(text[end]);
        return before && after;
    }
}
