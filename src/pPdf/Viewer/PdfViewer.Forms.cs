using System.Windows.Input;
using pPdf.Forms;

namespace pPdf.Viewer;

/// <summary>The fillable fields of the open document, shown as controls over the page.</summary>
public sealed partial class PdfViewer
{
    FormModel? _form;

    public FormModel? Form => _form;

    /// <summary>The user changed the value of a field.</summary>
    public event EventHandler? FormChanged;

    public void SetForm(FormModel? form)
    {
        if (_form != null) _form.Changed -= OnFormChanged;
        _form = form;
        if (form != null) form.Changed += OnFormChanged;
        foreach (var slot in _slots.Values) RebuildForm(slot);
    }

    void OnFormChanged() => FormChanged?.Invoke(this, EventArgs.Empty);

    void RebuildForm(PageSlot slot)
    {
        slot.FormLayer.Children.Clear();
        if (_form == null) return;
        foreach (var field in _form.OnPage(slot.PageIndex))
            foreach (var widget in field.Widgets.Where(w => w.Page == slot.PageIndex))
                if (FormControls.Create(field, widget, () => Keyboard.Focus(this)) is { } control)
                    slot.FormLayer.Children.Add(control);
    }
}
