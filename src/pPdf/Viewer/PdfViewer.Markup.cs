using System.Windows;
using System.Windows.Media;
using pPdf.Annotations;
using pPdf.Pdf;

namespace pPdf.Viewer;

/// <summary>Highlight, underline and strike-through of the selected text.</summary>
public sealed partial class PdfViewer
{
    MarkupAnnotation? _pendingMarkup;

    /// <summary>Color given to new markups (updated when the user changes the color of one).</summary>
    public Color MarkupColor { get; set; } = Color.FromRgb(0xFF, 0xEB, 0x3B);

    /// <summary>Marks the selected text (it may span several lines and pages) and clears the selection. False if nothing is selected.</summary>
    public bool AddMarkupFromSelection(MarkupKind kind, Color color)
    {
        if (_pdf == null || SelectionRange() is not { } range) return false;
        var group = new List<Annotation>();
        for (int p = range.Lo.Page; p <= range.Hi.Page; p++)
        {
            // pages in the middle of a long selection may not have their boxes yet
            var text = DataFor(p)?.Text ?? _pdf.LoadText(p, true);
            if (text is not { HasBoxes: true }) continue;
            int len = text.Text.Length;
            int s = p == range.Lo.Page ? Math.Min(range.Lo.Caret, len) : 0;
            int e = p == range.Hi.Page ? Math.Min(range.Hi.Caret, len) : len;
            if (e <= s) continue;
            var rects = text.RectsForRange(s, e).Select(r => r.ToRect()).ToList();
            if (rects.Count > 0) group.Add(MarkupAnnotation.Create(p, kind, color, rects));
        }
        if (group.Count == 0) return false;
        Annotations.AddGroup(group);
        ClearSelection();
        MarkupColor = color;
        return true;
    }

    /// <summary>The markup under a point of a page, if any (the topmost one).</summary>
    MarkupAnnotation? FindMarkupAt(int page, Point pagePoint)
    {
        foreach (var m in Annotations.OnPage(page).OfType<MarkupAnnotation>().Reverse())
            foreach (var r in m.Rects)
                if (r.Contains(pagePoint)) return m;
        return null;
    }
}
