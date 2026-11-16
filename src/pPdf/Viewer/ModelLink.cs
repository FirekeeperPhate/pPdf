using System.Windows;

namespace pPdf;

/// <summary>
/// How a control on a page lets go of the model it listens to (an annotation, a form field) when its page leaves the screen.
/// Unloaded is not enough: a page dropped before it was ever shown (the scroll bar dragged fast) never gets it, and its control
/// would keep the whole page alive through the model's event.
/// </summary>
static class ModelLink
{
    static readonly DependencyProperty UnlinkProperty =
        DependencyProperty.RegisterAttached("Unlink", typeof(Action), typeof(ModelLink));

    public static void Set(DependencyObject element, Action unlink) => element.SetValue(UnlinkProperty, unlink);

    /// <summary>Unlinks every control inside <paramref name="root"/> (itself included).</summary>
    public static void UnlinkAll(DependencyObject root)
    {
        if (root.GetValue(UnlinkProperty) is Action unlink) unlink();
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) UnlinkAll(child);
    }
}
