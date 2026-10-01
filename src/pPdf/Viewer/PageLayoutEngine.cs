using System.Windows;
using pPdf.Pdf;

namespace pPdf.Viewer;

public enum ViewLayout
{
    /// <summary>One column, all pages scrolling.</summary>
    Continuous,
    /// <summary>One page at a time.</summary>
    SinglePage,
    /// <summary>Two pages side by side, one spread at a time.</summary>
    TwoPages,
    /// <summary>Two columns, all spreads scrolling.</summary>
    TwoPagesContinuous,
}

public enum ZoomMode { Custom, FitWidth, FitPage }

public sealed class LayoutResult
{
    /// <summary>Device-independent pixels per point.</summary>
    public double Scale { get; init; }
    public double ExtentWidth { get; init; }
    public double ExtentHeight { get; init; }
    /// <summary>Rectangle of every page, in canvas coordinates.</summary>
    public Rect[] PageRects { get; init; } = [];
    /// <summary>Rectangle of every row (index = row number).</summary>
    public Rect[] RowRects { get; init; } = [];
    /// <summary>The room each row owns in the column: the same as the row in the continuous views, at least a whole viewport in the paged ones.</summary>
    public Rect[] RowSlots { get; init; } = [];
}

/// <summary>Pure layout maths of the viewer: which pages share a row, how big they are, where they go.</summary>
public static class PageLayoutEngine
{
    /// <summary>1 DIP = 1/96 in, 1 point = 1/72 in: 100 % zoom is actual size.</summary>
    public const double DipsPerPoint = 96.0 / 72.0;
    public const double MinZoom = 0.05;
    public const double MaxZoom = 8.0;

    public static bool IsPaged(ViewLayout l) => l is ViewLayout.SinglePage or ViewLayout.TwoPages;
    public static int PagesPerRow(ViewLayout l) => l is ViewLayout.TwoPages or ViewLayout.TwoPagesContinuous ? 2 : 1;

    public static List<int[]> BuildRows(int pageCount, int pagesPerRow, bool coverAlone)
    {
        var rows = new List<int[]>();
        int i = 0;
        if (pagesPerRow == 2 && coverAlone && pageCount > 0) { rows.Add([0]); i = 1; }
        while (i < pageCount)
        {
            if (pagesPerRow == 2 && i + 1 < pageCount) { rows.Add([i, i + 1]); i += 2; }
            else { rows.Add([i]); i++; }
        }
        return rows;
    }

    public static int RowOfPage(List<int[]> rows, int page)
    {
        for (int r = 0; r < rows.Count; r++)
            if (Array.IndexOf(rows[r], page) >= 0) return r;
        return 0;
    }

    static (double W, double H) Dim(PageSize p, int rotation)
        => (rotation & 1) == 1 ? (p.Height, p.Width) : (p.Width, p.Height);

    /// <param name="paged">
    /// Single page / two pages views: every row still gets its place in one long scrollable column (so the scroll bar
    /// spans the whole document), but each row's slot is at least as tall as the viewport, so one row fills the window.
    /// </param>
    /// <param name="zoom">Used by <see cref="ZoomMode.Custom"/>: 1.0 = 100 %.</param>
    /// <param name="dpiScale">Device pixels per DIP: page rectangles are snapped to whole device pixels.</param>
    public static LayoutResult Compute(PageSize[] pages, List<int[]> rows, int rotation, bool paged,
        ZoomMode mode, double zoom, Size viewport, double margin, double pageGap, double rowGap, double dpiScale)
    {
        double vw = Math.Max(1, viewport.Width), vh = Math.Max(1, viewport.Height);
        double scale = Math.Clamp(zoom, MinZoom, MaxZoom) * DipsPerPoint;
        if (mode != ZoomMode.Custom && rows.Count > 0)
        {
            double best = double.MaxValue;
            for (int r = 0; r < rows.Count; r++)
            {
                double sumW = 0, maxH = 0;
                foreach (int p in rows[r])
                {
                    var (w, h) = Dim(pages[p], rotation);
                    sumW += w; maxH = Math.Max(maxH, h);
                }
                double s = (vw - 2 * margin - pageGap * (rows[r].Length - 1)) / sumW;
                if (mode == ZoomMode.FitPage) s = Math.Min(s, (vh - 2 * margin) / maxH);
                best = Math.Min(best, s);
            }
            scale = Math.Clamp(best, MinZoom * DipsPerPoint, MaxZoom * DipsPerPoint);
        }

        double Snap(double v) => Math.Round(v * dpiScale) / dpiScale;

        var rects = new Rect[pages.Length];
        Array.Fill(rects, Rect.Empty);
        var rowRects = new Rect[rows.Count];
        var rowSlots = new Rect[rows.Count];

        // measure the rows first (the extent depends on the widest one)
        var rowW = new double[rows.Count];
        var rowH = new double[rows.Count];
        double maxW = 0;
        for (int r = 0; r < rows.Count; r++)
        {
            double w = pageGap * (rows[r].Length - 1), h = 0;
            foreach (int p in rows[r])
            {
                var d = Dim(pages[p], rotation);
                w += Snap(d.W * scale);
                h = Math.Max(h, Snap(d.H * scale));
            }
            rowW[r] = w; rowH[r] = h;
            maxW = Math.Max(maxW, w);
        }

        double extentW = Math.Max(vw, maxW + 2 * margin);
        double y = paged ? 0 : margin;
        for (int r = 0; r < rows.Count; r++)
        {
            // the slot is the room a row owns in the column: in the paged views at least a whole viewport
            double slotH = paged ? Math.Max(vh, rowH[r] + 2 * margin) : rowH[r];
            double top = paged ? y + Snap((slotH - rowH[r]) / 2) : y;
            double x = Snap((extentW - rowW[r]) / 2);
            rowSlots[r] = new Rect(0, y, extentW, slotH);
            rowRects[r] = new Rect(x, top, rowW[r], rowH[r]);
            foreach (int p in rows[r])
            {
                var d = Dim(pages[p], rotation);
                double w = Snap(d.W * scale), h = Snap(d.H * scale);
                rects[p] = new Rect(x, Snap(top + (rowH[r] - h) / 2), w, h);
                x += w + pageGap;
            }
            y += slotH + (paged ? 0 : rowGap);
        }

        double extentH = rows.Count == 0 ? 0 : paged ? y : Math.Max(1, y - rowGap + margin);
        return new LayoutResult { Scale = scale, ExtentWidth = extentW, ExtentHeight = extentH, PageRects = rects, RowRects = rowRects, RowSlots = rowSlots };
    }
}
