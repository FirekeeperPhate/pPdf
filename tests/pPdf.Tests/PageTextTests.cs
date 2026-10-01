using pPdf.Pdf;

namespace pPdf.Tests;

public class PageTextTests
{
    static PageText Load(params string[] lines)
    {
        using var pdf = PdfFile.Open(TestPdf.Create([lines]));
        return pdf.LoadText(0, true)!;
    }

    [Fact]
    public void Caret_follows_the_pointer_within_a_line()
    {
        var t = Load("Hello world");
        int w = t.Text.IndexOf('w');
        var box = t.Boxes![w];
        // left half of the 'w' = caret before it, right half = after it
        Assert.Equal(w, t.CaretAt(box.X + box.Width * 0.25, box.CenterY));
        Assert.Equal(w + 1, t.CaretAt(box.X + box.Width * 0.75, box.CenterY));
        // far to the left / right of the line: start / end of that line
        Assert.Equal(t.Text.IndexOf('H'), t.CaretAt(-50, box.CenterY));
        Assert.Equal(t.Text.IndexOf('d') + 1, t.CaretAt(5000, box.CenterY));
    }

    [Fact]
    public void Pointer_between_lines_picks_the_nearest_line_not_a_random_char()
    {
        var t = Load("first line", "second line");
        int s = t.Text.IndexOf("second", StringComparison.Ordinal);
        var second = t.Boxes![s];
        // a bit below the second line
        int caret = t.CaretAt(second.X + 1, second.Bottom + 6);
        Assert.InRange(caret, s, s + 1);
        Assert.True(t.IsOverText(second.CenterX, second.CenterY));
        Assert.False(t.IsOverText(400, 700));
    }

    [Fact]
    public void Word_and_line_ranges()
    {
        var t = Load("one two three", "four");
        int two = t.Text.IndexOf("two", StringComparison.Ordinal);
        var (s, e) = t.WordAt(two + 1);
        Assert.Equal("two", t.Slice(s, e));
        var (ls, le) = t.LineAt(two);
        Assert.Equal("one two three", t.Slice(ls, le));
    }

    [Fact]
    public void A_range_over_two_lines_gives_one_rectangle_per_line_and_none_for_the_line_break()
    {
        var t = Load("alpha beta", "gamma delta");
        int a = t.Text.IndexOf("beta", StringComparison.Ordinal);
        int d = t.Text.IndexOf("gamma", StringComparison.Ordinal) + 5;
        var rects = t.RectsForRange(a, d);
        Assert.Equal(2, rects.Count);
        Assert.True(rects[1].Y > rects[0].Y);
        // the first rectangle ends at the end of "beta", not at the page edge
        Assert.True(rects[0].Right < 300);
    }
}
