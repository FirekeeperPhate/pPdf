using System.Windows;
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
    /// <summary>What is kept for the open document could not be read: nothing is written over it until it is opened again.</summary>
    bool _keepBlocked;
    /// <summary>The content mark of the open document (<see cref="AnnotationPersistence.FingerprintOf"/>), written with its annotations.</summary>
    string? _fingerprint;
    /// <summary>Kept annotations on pages this file no longer has: not shown, but written back so they are not lost.</summary>
    List<Annotation> _keptElsewhere = [];
    /// <summary>The bookmarks of the open document (also those on pages the file no longer has, so they are not lost).</summary>
    List<Bookmark> _bookmarks = [];
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

    /// <summary>Writes what changed since the last time (no-op otherwise). True when nothing is left unwritten.</summary>
    bool FlushAnnotations()
    {
        _keepTimer.Stop();
        if (!_keepChanged || _pdf == null || _path == null) return true;
        if (_keepBlocked) return false;
        try
        {
            // clones: the live objects belong to the UI
            var snapshot = Viewer.Annotations.Items.Select(a => a.Clone()).Concat(_keptElsewhere).ToList();
            AnnotationPersistence.Default.Save(_path, snapshot, KeptFormValues(), _fingerprint, _bookmarks.ToList());
            _keepChanged = false;
            return true;
        }
        catch (Exception)
        {
            // the disk refused (full, locked by an antivirus...): the annotations stay on screen, "Save a copy" still works, and it is tried again
            _keepTimer.Start();
            return false;
        }
    }

    /// <summary>The values typed in the form; before the form has been read, the ones kept from last time (so they are not lost).</summary>
    Dictionary<string, string> KeptFormValues() => Viewer.Form?.ChangedValues() ?? _pendingFormValues ?? [];

    void RestoreKeptAnnotations(string path, PdfFile pdf, string? fingerprint)
    {
        _keepChanged = false;
        _keepBlocked = false;
        _pendingFormValues = null;
        _keptElsewhere = [];
        _bookmarks = [];
        _fingerprint = fingerprint;
        AnnotationPersistence.Snapshot kept;
        try { kept = AnnotationPersistence.Default.Load(path); }
        catch (Exception) { _keepBlocked = true; return; } // kept annotations are a convenience: a damaged store must never stop a document from opening
        if (kept.Unreadable) { _keepBlocked = true; return; }
        if (kept.IsEmpty) return;

        if (kept.Fingerprint != null && fingerprint != null && kept.Fingerprint != fingerprint)
        {
            string what = Epub.EpubConverter.IsEpub(path) ? "This book was laid out again since" : "This file has changed (or was replaced) since";
            var answer = MessageBox.Show(this,
                $"{what} your annotations and form entries were made, so they may not fit it any more.\n\nShow them anyway?\n\n" +
                "(No puts them aside: the document opens as it is.)", "pPdf", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                AnnotationPersistence.Default.SetAside(path);
                return;
            }
            _keepChanged = true; // written again with the new mark, so the question is asked once
        }

        if (kept.FormValues.Count > 0) _pendingFormValues = kept.FormValues;
        _bookmarks = kept.Bookmarks.ToList();
        _keptElsewhere = kept.Annotations.Where(a => a.Page >= pdf.PageCount).ToList();
        _keepSuspended = true;
        try { Viewer.Annotations.LoadKept(kept.Annotations.Where(a => a.Page < pdf.PageCount)); }
        finally { _keepSuspended = false; }
        if (_keepChanged) _keepTimer.Start();
    }

    /// <summary>Reads the fillable fields in the background (only documents that have a form pay for it) and shows them.</summary>
    async Task LoadFormAsync(PdfFile pdf)
    {
        // a very big file is read from disk on demand: not worth loading it whole to look for fields
        if (pdf.IsOnDemand || await Task.Run(() => pdf.FormType) != 1 || _pdf != pdf) return;
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

    /// <summary>Windows is logging off or shutting down (no Closing event comes then): what is being typed and everything unsaved is written now.</summary>
    public void FlushForSessionEnd()
    {
        try
        {
            Viewer.CommitEdits();
            FlushAnnotations();
            RememberPosition();
            SaveSettings();
        }
        catch (Exception) { /* nothing can be shown at this point */ }
    }
}
