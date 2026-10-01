using System.Windows.Threading;
using pPdf.Pdf;

namespace pPdf.Viewer;

/// <summary>Searches the whole document in the background and keeps the list of hits, in reading order, and the current one.</summary>
public sealed class SearchController
{
    readonly Dispatcher _dispatcher;
    CancellationTokenSource? _cts;
    int _generation;
    readonly List<TextMatch> _matches = [];

    public SearchController(Dispatcher dispatcher) { _dispatcher = dispatcher; }

    public IReadOnlyList<TextMatch> Matches => _matches;
    public int CurrentIndex { get; private set; } = -1;
    public TextMatch? Current => CurrentIndex >= 0 && CurrentIndex < _matches.Count ? _matches[CurrentIndex] : null;
    public bool IsRunning { get; private set; }
    public string Query { get; private set; } = "";

    /// <summary>Raised on the UI thread whenever hits were added, the search finished, or the current hit changed.</summary>
    public event Action? Changed;
    /// <summary>Raised when the first hit at or after the start page is known, so the view can jump to it.</summary>
    public event Action<TextMatch>? FirstHit;

    public void Cancel()
    {
        _cts?.Cancel();
        _cts = null;
        _generation++;
        IsRunning = false;
    }

    public void Clear()
    {
        Cancel();
        _matches.Clear();
        CurrentIndex = -1;
        Query = "";
        Changed?.Invoke();
    }

    public void Start(PdfFile pdf, string query, SearchOptions options, int startPage)
    {
        Cancel();
        _matches.Clear();
        CurrentIndex = -1;
        string q = Normalized.NormalizeQuery(query);
        Query = q;
        if (q.Length == 0) { Changed?.Invoke(); return; }

        var cts = _cts = new CancellationTokenSource();
        int gen = ++_generation;
        IsRunning = true;
        bool announced = false;
        Changed?.Invoke();

        _ = Task.Run(() =>
        {
            var batch = new List<TextMatch>();
            var last = DateTime.UtcNow;
            for (int p = 0; p < pdf.PageCount; p++)
            {
                if (cts.IsCancellationRequested) return;
                PageText? text;
                try { text = pdf.LoadText(p, false); }
                catch (Exception) { text = null; }
                if (text != null) batch.AddRange(TextSearch.FindAll(text, q, options));
                bool done = p == pdf.PageCount - 1;
                if (done || (DateTime.UtcNow - last).TotalMilliseconds > 80)
                {
                    var chunk = batch.ToArray();
                    batch.Clear();
                    last = DateTime.UtcNow;
                    _dispatcher.BeginInvoke(() =>
                    {
                        if (gen != _generation) return;
                        _matches.AddRange(chunk);
                        if (done) IsRunning = false;
                        if (CurrentIndex < 0 && _matches.Count > 0)
                        {
                            int idx = _matches.FindIndex(m => m.Page >= startPage);
                            // nothing after the start page yet: wait for the end of the scan, then wrap around to the first hit
                            if (idx < 0 && done) idx = 0;
                            if (idx >= 0)
                            {
                                CurrentIndex = idx;
                                if (!announced) { announced = true; FirstHit?.Invoke(_matches[idx]); }
                            }
                        }
                        Changed?.Invoke();
                    });
                }
            }
        });
    }

    public TextMatch? Next()
    {
        if (_matches.Count == 0) return null;
        CurrentIndex = (CurrentIndex + 1) % _matches.Count;
        Changed?.Invoke();
        return Current;
    }

    public TextMatch? Previous()
    {
        if (_matches.Count == 0) return null;
        CurrentIndex = CurrentIndex <= 0 ? _matches.Count - 1 : CurrentIndex - 1;
        Changed?.Invoke();
        return Current;
    }
}
