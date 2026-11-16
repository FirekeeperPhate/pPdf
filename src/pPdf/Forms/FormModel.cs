using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace pPdf.Forms;

public enum FormFieldKind { Text, CheckBox, Radio, Combo, List }

/// <summary>One place a field is drawn on a page (a radio group has one per button).</summary>
public sealed class FormWidget
{
    public int Page { get; init; }
    /// <summary>Display space: points from the top-left of the page as shown (before any view rotation).</summary>
    public Rect Bounds { get; init; }
    /// <summary>Check boxes and radio buttons: the name of the "on" state of this widget (without the leading slash).</summary>
    public string OnState { get; init; } = "Yes";
    /// <summary>The frame the document gives the field (/MK /BC), drawn when printing; null = none.</summary>
    public System.Windows.Media.Color? BorderColor { get; init; }
    /// <summary>The background of the field (/MK /BG); null = transparent.</summary>
    public System.Windows.Media.Color? FillColor { get; init; }
    /// <summary>Points (/BS /W, default 1).</summary>
    public double BorderWidth { get; init; } = 1;
    /// <summary>/BS /S: S solid, D dashed, B beveled, I inset, U underline.</summary>
    public char BorderStyle { get; init; } = 'S';
}

/// <summary>A fillable field of an AcroForm, with the value the user sees now.</summary>
public sealed partial class FormField : ObservableObject
{
    public string Name { get; init; } = "";
    public FormFieldKind Kind { get; init; }
    public bool ReadOnly { get; init; }
    public bool Multiline { get; init; }
    public bool EditableCombo { get; init; }
    public int MaxLength { get; init; }
    /// <summary>0 left, 1 center, 2 right.</summary>
    public int Alignment { get; init; }
    /// <summary>Points; 0 = choose from the size of the box.</summary>
    public double FontSize { get; init; }
    /// <summary>(stored value, shown text) of the choices of a combo box / list.</summary>
    public IReadOnlyList<(string Value, string Label)> Options { get; init; } = [];
    public List<FormWidget> Widgets { get; } = [];

    /// <summary>What the document had when it was opened.</summary>
    public string OriginalValue { get; init; } = "";

    /// <summary>Text and choices: the text / stored value. Check box and radio: the "on" state name, or "Off".</summary>
    [ObservableProperty] public partial string Value { get; set; } = "";

    public bool IsChanged => !string.Equals(Value, OriginalValue, StringComparison.Ordinal);

    /// <summary>Whether a check box or radio button <paramref name="widget"/> of this field shows as on.</summary>
    public bool IsOn(FormWidget widget) => IsOn(Kind, Value, widget.OnState, Widgets.Select(w => w.OnState));

    /// <summary>
    /// A radio button is on when the value is its own state. So is a check box, with one exception kept for odd forms: a value that is
    /// not Off and matches none of the field's states turns every box on. Boxes of one name with states of their own ("Yes" / "No")
    /// are then separate choices, as in Acrobat, not one box drawn in several places.
    /// </summary>
    public static bool IsOn(FormFieldKind kind, string value, string onState, IEnumerable<string> allStates)
    {
        if (string.IsNullOrEmpty(value) || string.Equals(value, "Off", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(value, onState, StringComparison.Ordinal)) return true;
        return kind == FormFieldKind.CheckBox && !allStates.Contains(value, StringComparer.Ordinal);
    }
}

/// <summary>The fillable fields of one document.</summary>
public sealed class FormModel
{
    readonly Dictionary<string, FormField> _byName = new(StringComparer.Ordinal);

    public IReadOnlyList<FormField> Fields { get; }

    /// <summary>Raised when the user changed any value.</summary>
    public event Action? Changed;

    public FormModel(IEnumerable<FormField> fields)
    {
        Fields = fields.ToList();
        foreach (var f in Fields)
        {
            _byName[f.Name] = f;
            f.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(FormField.Value)) Changed?.Invoke(); };
        }
    }

    public bool IsEmpty => Fields.Count == 0;

    public FormField? Find(string name) => _byName.TryGetValue(name, out var f) ? f : null;

    public IEnumerable<FormField> OnPage(int page) => Fields.Where(f => f.Widgets.Any(w => w.Page == page));

    /// <summary>Name -> value of every field the user changed.</summary>
    public Dictionary<string, string> ChangedValues()
        => Fields.Where(f => f.IsChanged).ToDictionary(f => f.Name, f => f.Value, StringComparer.Ordinal);

    /// <summary>Puts back values kept from an earlier session (unknown names are ignored).</summary>
    public void Apply(IReadOnlyDictionary<string, string> values)
    {
        foreach (var (name, value) in values)
            if (_byName.TryGetValue(name, out var f) && !f.ReadOnly) f.Value = value;
    }
}
