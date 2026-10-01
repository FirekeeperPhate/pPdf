using System.Windows;
using pPdf.Pdf;
using pPdf.Viewer;

namespace pPdf.Tests;

public class LayoutTests
{
    static readonly PageSize A4 = new(595, 842);

    static PageSize[] Pages(int n) => Enumerable.Repeat(A4, n).ToArray();

    [Fact]
    public void Rows_single_two_and_cover()
    {
        Assert.Equal(5, PageLayoutEngine.BuildRows(5, 1, false).Count);
        var two = PageLayoutEngine.BuildRows(5, 2, false);
        Assert.Equal([[0, 1], [2, 3], [4]], two);
        var cover = PageLayoutEngine.BuildRows(5, 2, true);
        Assert.Equal([[0], [1, 2], [3, 4]], cover);
        Assert.Equal(1, PageLayoutEngine.RowOfPage(cover, 2));
    }

    [Fact]
    public void Fit_width_fills_the_viewport()
    {
        var rows = PageLayoutEngine.BuildRows(3, 1, false);
        var r = PageLayoutEngine.Compute(Pages(3), rows, 0, false, ZoomMode.FitWidth, 1, new Size(1000, 700), 14, 10, 12, 1);
        Assert.Equal((1000 - 28) / 595.0, r.Scale, 3);
        Assert.Equal(1000, r.ExtentWidth, 1);
        // pages stack vertically, in order, centered
        Assert.True(r.PageRects[1].Top > r.PageRects[0].Bottom);
        Assert.Equal(r.PageRects[0].X, r.PageRects[2].X, 1);
        Assert.InRange(r.PageRects[0].X + r.PageRects[0].Width / 2, 499, 501);
    }

    [Fact]
    public void Single_page_view_gives_each_page_a_whole_viewport_and_the_scroll_bar_spans_the_document()
    {
        var rows = PageLayoutEngine.BuildRows(10, 1, false);
        var r = PageLayoutEngine.Compute(Pages(10), rows, 0, true, ZoomMode.FitPage, 1, new Size(1000, 700), 14, 10, 12, 1);
        // fit page: every page fits whole inside one viewport-high slot, and the whole document is the extent
        Assert.All(r.RowSlots, s => Assert.Equal(700, s.Height, 1));
        Assert.Equal(10 * 700, r.ExtentHeight, 1);
        for (int i = 0; i < 10; i++)
        {
            Assert.True(r.PageRects[i].Height <= 700 - 28 + 1);
            Assert.True(r.RowSlots[i].Contains(r.PageRects[i]));
            if (i > 0) Assert.Equal(r.RowSlots[i - 1].Bottom, r.RowSlots[i].Top, 1);
        }
    }

    [Fact]
    public void Zoomed_page_in_single_page_view_owns_a_slot_as_tall_as_itself()
    {
        var rows = PageLayoutEngine.BuildRows(3, 1, false);
        var r = PageLayoutEngine.Compute(Pages(3), rows, 0, true, ZoomMode.Custom, 2.0, new Size(1000, 700), 14, 10, 12, 1);
        double pageH = 842 * 2.0 * PageLayoutEngine.DipsPerPoint;
        Assert.InRange(r.RowSlots[0].Height, pageH + 27, pageH + 29);
        Assert.InRange(r.ExtentHeight, 3 * (pageH + 28) - 3, 3 * (pageH + 28) + 3);
    }

    [Fact]
    public void Two_pages_sit_side_by_side_and_rotation_swaps_the_size()
    {
        var rows = PageLayoutEngine.BuildRows(4, 2, false);
        var r = PageLayoutEngine.Compute(Pages(4), rows, 0, true, ZoomMode.FitPage, 1, new Size(1400, 800), 14, 10, 12, 1);
        Assert.Equal(r.PageRects[0].Top, r.PageRects[1].Top, 1);
        Assert.Equal(10, r.PageRects[1].Left - r.PageRects[0].Right, 1);
        Assert.Equal(2 * 800, r.ExtentHeight, 1); // two rows, one viewport each

        var rot = PageLayoutEngine.Compute(Pages(1), PageLayoutEngine.BuildRows(1, 1, false), 1, false, ZoomMode.Custom, 1, new Size(1000, 700), 14, 10, 12, 1);
        Assert.True(rot.PageRects[0].Width > rot.PageRects[0].Height); // a rotated A4 is landscape
    }

    [Fact]
    public void Custom_zoom_is_actual_size_at_100_percent_and_snaps_to_device_pixels()
    {
        var rows = PageLayoutEngine.BuildRows(1, 1, false);
        var r = PageLayoutEngine.Compute(Pages(1), rows, 0, false, ZoomMode.Custom, 1.0, new Size(1000, 700), 14, 10, 12, 1.5);
        Assert.Equal(595 * 96 / 72.0, r.PageRects[0].Width, 0);
        double px = r.PageRects[0].Width * 1.5;
        Assert.Equal(Math.Round(px), px, 6);
    }

    [Fact]
    public void Continuous_extent_is_margins_pages_and_gaps()
    {
        var rows = PageLayoutEngine.BuildRows(3, 1, false);
        var r = PageLayoutEngine.Compute(Pages(3), rows, 0, false, ZoomMode.Custom, 1.0, new Size(1000, 700), 14, 10, 12, 1);
        double h = 842 * PageLayoutEngine.DipsPerPoint;
        Assert.InRange(r.ExtentHeight, 14 + 3 * h + 2 * 12 + 14 - 3, 14 + 3 * h + 2 * 12 + 14 + 3);
    }
}
