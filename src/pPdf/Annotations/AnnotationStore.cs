namespace pPdf.Annotations;

/// <summary>The annotations of the open document, with undo / redo.</summary>
public sealed class AnnotationStore
{
    sealed record UndoEntry(string Name, Action Undo, Action Redo);

    readonly List<Annotation> _items = [];
    readonly Stack<UndoEntry> _undo = new();
    readonly Stack<UndoEntry> _redo = new();
    int _savedDepth;
    bool _everModified;

    public IReadOnlyList<Annotation> Items => _items;
    public IEnumerable<Annotation> OnPage(int page) => _items.Where(a => a.Page == page);
    public bool IsEmpty => _items.Count == 0;

    public event Action<Annotation>? Added;
    public event Action<Annotation>? Removed;
    /// <summary>An annotation was changed by undo/redo (live edits raise the model's own PropertyChanged).</summary>
    public event Action<Annotation>? Restored;
    public event Action? HistoryChanged;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>True when there is something that would be lost on close.</summary>
    public bool IsDirty => _items.Count > 0 && (_undo.Count != _savedDepth || _everModified);

    public void MarkSaved()
    {
        _savedDepth = _undo.Count;
        _everModified = false;
        HistoryChanged?.Invoke();
    }

    void Push(UndoEntry entry)
    {
        _undo.Push(entry);
        _redo.Clear();
        _everModified = true;
        HistoryChanged?.Invoke();
    }

    public void Add(Annotation a)
    {
        AddCore(a);
        Push(new UndoEntry("Add", () => RemoveCore(a), () => AddCore(a)));
    }

    /// <summary>Adds several annotations as one step (one undo takes them all away).</summary>
    public void AddGroup(IReadOnlyList<Annotation> group)
    {
        if (group.Count == 0) return;
        foreach (var a in group) AddCore(a);
        Push(new UndoEntry("Add", () => { foreach (var a in group) RemoveCore(a); }, () => { foreach (var a in group) AddCore(a); }));
    }

    public void Remove(Annotation a)
    {
        if (!_items.Contains(a)) return;
        int index = _items.IndexOf(a);
        RemoveCore(a);
        Push(new UndoEntry("Delete", () => AddCore(a, index), () => RemoveCore(a)));
    }

    /// <summary>Records a change made to <paramref name="a"/> since <paramref name="before"/> was cloned.</summary>
    public void Commit(Annotation a, Annotation before)
    {
        if (before.SameAs(a)) return;
        var after = a.Clone();
        Push(new UndoEntry("Edit", () => Restore(a, before), () => Restore(a, after)));
    }

    /// <summary>Brings an annotation to the front of the page's stacking order (not undoable: it is only cosmetic).</summary>
    public void BringToFront(Annotation a)
    {
        int i = _items.IndexOf(a);
        if (i < 0 || i == _items.Count - 1) return;
        _items.RemoveAt(i);
        _items.Add(a);
    }

    void Restore(Annotation target, Annotation snapshot)
    {
        target.CopyFrom(snapshot);
        Restored?.Invoke(target);
    }

    void AddCore(Annotation a, int index = -1)
    {
        if (index < 0 || index > _items.Count) _items.Add(a); else _items.Insert(index, a);
        Added?.Invoke(a);
    }

    void RemoveCore(Annotation a)
    {
        if (_items.Remove(a)) Removed?.Invoke(a);
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var e = _undo.Pop();
        e.Undo();
        _redo.Push(e);
        HistoryChanged?.Invoke();
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        var e = _redo.Pop();
        e.Redo();
        _undo.Push(e);
        HistoryChanged?.Invoke();
        return true;
    }

    public void Clear()
    {
        foreach (var a in _items.ToArray()) RemoveCore(a);
        _undo.Clear();
        _redo.Clear();
        _savedDepth = 0;
        _everModified = false;
        HistoryChanged?.Invoke();
    }
}
