using System.Windows.Threading;
using pPdf.Annotations;
using pPdf.Forms;
using pPdf.Pdf;

namespace pPdf;

/// <summary>Annotations and form values are kept on disk for each document: autosaved a moment after every change, restored on opening.</summary>
public partial class MainWindow
{
    readonly DispatcherTimer _keepTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    bool _keepChanged;
    bool _keepSuspended;
    /// <summary>Form values kept from an earlier session, until the form of the document has been read.</summary>
    Dictionary<string, string>? _pendingFormValues;

    void InitAnnotationKeeping()
    {
        _keepTimer.Tick += (_, _) => { _keepTimer.Stop(); FlushAnnotations(); };
        Viewer.Annotations.HistoryChanged += MarkKeepChanged;
        Viewer.FormChanged += (_, _) => MarkKeepChanged();
    }

    void MarkKeepChanged()
    {
        if (_keepSuspended || _pdf == null) return;
        _keepChanged = true;
        _keepTimer.Stop();
        _keepTimer.Start();
    }

    /// <summary>Writes what changed since the last time (no-op otherwise).</summary>
    void FlushAnnotations()
    {
        _keepTimer.Stop();
        if (!_keepChanged || _pdf == null || _path == null) return;
        _keepChanged = false;
        try
        {
            // clones: the live objects belong to the UI
            var snapshot = Viewer.Annotations.Items.Select(a => a.Clone()).ToList();
            AnnotationPersistence.Default.Save(_path, snapshot, KeptFormValues());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the disk refused: the annotations stay on screen, and "Save a copy" still works
        }
    }

    /// <summary>The values typed in the form; before the form has been read, the ones kept from last time (so they are not lost).</summary>
    Dictionary<string, string> KeptFormValues() => Viewer.Form?.ChangedValues() ?? _pendingFormValues ?? [];

    void RestoreKeptAnnotations(string path, PdfFile pdf)
    {
        _keepChanged = false;
        _pendingFormValues = null;
        AnnotationPersistence.Snapshot kept;
        try { kept = AnnotationPersistence.Default.Load(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        if (kept.IsEmpty) return;
        if (kept.FormValues.Count > 0) _pendingFormValues = kept.FormValues;
        _keepSuspended = true;
        try { Viewer.Annotations.LoadKept(kept.Annotations.Where(a => a.Page < pdf.PageCount)); }
        finally { _keepSuspended = false; }
    }

    /// <summary>Reads the fillable fields in the background (only documents that have a form pay for it) and shows them.</summary>
    async Task LoadFormAsync(PdfFile pdf)
    {
        // a very big file is read from disk on demand: not worth loading it whole to look for fields
        if (pdf.IsOnDemand || pdf.FormType != 1) return;
        if (pdf.GetBytes().Length > 150 * 1024 * 1024) return; // reading every object of a huge file is not worth it
        var bytes = pdf.GetBytes();
        string? password = pdf.Password;
        var model = await Task.Run(() => FormPdf.Read(bytes, password));
        if (_pdf != pdf || model == null) return;

        if (_pendingFormValues is { } kept)
        {
            _keepSuspended = true;
            try { model.Apply(kept); }
            finally { _keepSuspended = false; }
            _pendingFormValues = null;
        }
        Viewer.SetForm(model);
        UpdateUi();
    }
}
