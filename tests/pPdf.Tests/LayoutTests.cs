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
        var r = PageLayoutEngine.Compute(Pages(3), rows, 0, false, 0, ZoomMode.FitWidth, 1, new Size(1000, 700), 14, 10, 12, 1);
        Assert.Equal((1000 - 28) / 595.0, r.Scale, 3);
        Assert.Equal(1000, r.ExtentWidth, 1);
        // pages stack vertically, in order, centered
        Assert.True(r.PageRects[1].Top > r.PageRects[0].Bottom);
        Assert.Equal(r.PageRects[0].X, r.PageRects[2].X, 1);
        Assert.InRange(r.PageRects[0].X + r.PageRects[0].Width / 2, 499, 501);
    }

    [Fact]
    public void Fit_page_in_single_page_mode_shows_only_the_current_page_whole()
    {
        var rows = PageLayoutEngine.BuildRows(3, 1, false);
        var r = PageLayoutEngine.Compute(Pages(3), rows, 0, true, 1, ZoomMode.FitPage, 1, new Size(1000, 700), 14, 10, 12, 1);
        Assert.True(r.PageRects[0].IsEmpty);
        Assert.False(r.PageRects[1].IsEmpty);
        Assert.True(r.PageRects[2].IsEmpty);
        Assert.True(r.PageRects[1].Height <= 700 - 28 + 1);
        Assert.True(r.ExtentHeight <= 700.5);
    }

    [Fact]
    public void Two_pages_sit_side_by_side_and_rotation_swaps_the_size()
    {
        var rows = PageLayoutEngine.BuildRows(4, 2, false);
        var r = PageLayoutEngine.Compute(Pages(4), rows, 0, true, 0, ZoomMode.FitPage, 1, new Size(1400, 800), 14, 10, 12, 1);
        Assert.Equal(r.PageRects[0].Top, r.PageRects[1].Top, 1);
        Assert.Equal(10, r.PageRects[1].Left - r.PageRects[0].Right, 1);

        var rot = PageLayoutEngine.Compute(Pages(1), PageLayoutEngine.BuildRows(1, 1, false), 1, false, 0, ZoomMode.Custom, 1, new Size(1000, 700), 14, 10, 12, 1);
        Assert.True(rot.PageRects[0].Width > rot.PageRects[0].Height); // a rotated A4 is landscape
    }

    [Fact]
    public void Custom_zoom_is_actual_size_at_100_percent_and_snaps_to_device_pixels()
    {
        var rows = PageLayoutEngine.BuildRows(1, 1, false);
        var r = PageLayoutEngine.Compute(Pages(1), rows, 0, false, 0, ZoomMode.Custom, 1.0, new Size(1000, 700), 14, 10, 12, 1.5);
        Assert.Equal(595 * 96 / 72.0, r.PageRects[0].Width, 0);
        double px = r.PageRects[0].Width * 1.5;
        Assert.Equal(Math.Round(px), px, 6);
    }
}
