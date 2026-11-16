using System.Windows;
using System.Windows.Controls;

namespace pPdf.Views;

/// <summary>
/// A toolbar on one row: the tools are laid out left to right, and those that do not fit in the window are left out from the end
/// (never wrapped onto a second row). The last child is the overflow button: shown only when something is left out, it offers the
/// tools that are not on the bar (see <see cref="HiddenItems"/>). Separators (a childless Border) never end the row.
/// </summary>
public sealed class ToolStripPanel : Panel
{
    readonly HashSet<UIElement> _hidden = [];

    public ToolStripPanel() => ClipToBounds = true;

    /// <summary>The tools not on the bar now, in their order (separators left out).</summary>
    public IReadOnlyList<UIElement> HiddenItems => Items.Where(c => _hidden.Contains(c) && !IsSeparator(c)).ToList();

    /// <summary>Whether a child is off the bar now (separators included).</summary>
    public bool IsHidden(UIElement child) => _hidden.Contains(child);

    /// <summary>The tools left out changed (the window was resized).</summary>
    public event EventHandler? OverflowChanged;

    UIElement? OverflowButton => InternalChildren.Count > 0 ? InternalChildren[^1] : null;

    IEnumerable<UIElement> Items => InternalChildren.Cast<UIElement>().Take(Math.Max(0, InternalChildren.Count - 1));

    static bool IsSeparator(UIElement e) => e is Border { Child: null };

    protected override Size MeasureOverride(Size available)
    {
        var inf = new Size(double.PositiveInfinity, available.Height);
        foreach (UIElement c in InternalChildren) c.Measure(inf);

        var items = Items.Where(c => c.Visibility != Visibility.Collapsed).ToList();
        double total = items.Sum(c => c.DesiredSize.Width);
        var hidden = new HashSet<UIElement>();
        double used;
        if (total <= available.Width)
        {
            used = total;
            if (OverflowButton is { } o) hidden.Add(o);
        }
        else
        {
            double budget = available.Width - (OverflowButton?.DesiredSize.Width ?? 0);
            used = 0;
            bool full = false;
            foreach (var c in items)
            {
                if (!full && used + c.DesiredSize.Width <= budget) used += c.DesiredSize.Width;
                else { full = true; hidden.Add(c); }
            }
            // a separator does not end the row
            foreach (var c in items.Where(c => !hidden.Contains(c)).Reverse())
            {
                if (!IsSeparator(c)) break;
                hidden.Add(c);
                used -= c.DesiredSize.Width;
            }
            used += OverflowButton?.DesiredSize.Width ?? 0;
        }

        bool changed = !hidden.SetEquals(_hidden);
        _hidden.Clear();
        _hidden.UnionWith(hidden);
        if (changed) Dispatcher.BeginInvoke(() => OverflowChanged?.Invoke(this, EventArgs.Empty));

        double height = InternalChildren.Cast<UIElement>().Select(c => c.DesiredSize.Height).DefaultIfEmpty(0).Max();
        return new Size(Math.Min(used, double.IsInfinity(available.Width) ? used : available.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        foreach (UIElement c in InternalChildren)
        {
            if (_hidden.Contains(c) || c.Visibility == Visibility.Collapsed)
            {
                // out of sight (the panel clips): not drawn, not clickable
                c.Arrange(new Rect(finalSize.Width + 10000, 0, c.DesiredSize.Width, c.DesiredSize.Height));
                continue;
            }
            double h = c.DesiredSize.Height;
            c.Arrange(new Rect(x, (finalSize.Height - h) / 2, c.DesiredSize.Width, h));
            x += c.DesiredSize.Width;
        }
        return finalSize;
    }
}
