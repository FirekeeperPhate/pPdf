using System.Windows;
using System.Windows.Media.Imaging;
using pPdf.Pdf;

namespace pPdf.Viewer;

/// <summary>What to rasterise: a region of a page laid out at FullW x FullH pixels.</summary>
public readonly record struct RenderKey(int Page, int FullW, int FullH, int X, int Y, int W, int H, int Rotation, bool Invert)
{
    public Int32Rect Region => new(X, Y, W, H);
    public long Bytes => (long)W * H * 4;
}

/// <summary>
/// Renders pages on one background thread (PDFium serialises anyway), newest request first, and keeps the results
/// in a size-bounded LRU cache so scrolling back is instant.
/// </summary>
public sealed class RenderService : IDisposable
{
    sealed class Job
    {
        public PdfFile Pdf = null!;
        public RenderKey Key;
        public int Priority;
        public long Seq;
        public CancellationToken Ct;
        public TaskCompletionSource<BitmapSource?> Tcs = null!;
    }

    readonly object _gate = new();
    readonly List<Job> _queue = [];
    readonly Thread _worker;
    readonly long _budget;
    long _seq;
    bool _stop;

    readonly Dictionary<(PdfFile, RenderKey), LinkedListNode<CacheEntry>> _cache = [];
    readonly LinkedList<CacheEntry> _lru = new();
    long _cacheBytes;

    sealed record CacheEntry((PdfFile Pdf, RenderKey Key) Id, BitmapSource Bitmap);

    /// <summary>One service (one thread, one budget) for every window of the process.</summary>
    public static RenderService Shared { get; } = new(AdaptiveBudget());

    /// <summary>About three full screens of bitmaps: enough to scroll back and forth, far less than a fixed 384 MB.</summary>
    static long AdaptiveBudget() => Math.Clamp(Services.MemoryTrimmer.ScreenPixels() * 4 * 3, 64L << 20, 192L << 20);

    public RenderService(long cacheBudgetBytes)
    {
        _budget = cacheBudgetBytes;
        _worker = new Thread(Run) { IsBackground = true, Name = "pPdf render", Priority = ThreadPriority.BelowNormal };
        _worker.Start();
    }

    public bool TryGetCached(PdfFile pdf, RenderKey key, out BitmapSource bitmap)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue((pdf, key), out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                bitmap = node.Value.Bitmap;
                return true;
            }
        }
        bitmap = null!;
        return false;
    }

    /// <summary>Queues a render. Lower <paramref name="priority"/> runs first; among equals the newest request wins.</summary>
    public Task<BitmapSource?> RenderAsync(PdfFile pdf, RenderKey key, int priority, CancellationToken ct)
    {
        if (TryGetCached(pdf, key, out var hit)) return Task.FromResult<BitmapSource?>(hit);
        var job = new Job
        {
            Pdf = pdf, Key = key, Priority = priority, Ct = ct,
            Tcs = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        lock (_gate)
        {
            job.Seq = ++_seq;
            _queue.Add(job);
            Monitor.Pulse(_gate);
        }
        return job.Tcs.Task;
    }

    void Run()
    {
        while (true)
        {
            Job job;
            lock (_gate)
            {
                while (true)
                {
                    if (_stop) return;
                    // drop what nobody wants any more
                    for (int i = _queue.Count - 1; i >= 0; i--)
                        if (_queue[i].Ct.IsCancellationRequested) { _queue[i].Tcs.TrySetCanceled(); _queue.RemoveAt(i); }
                    if (_queue.Count > 0) break;
                    Monitor.Wait(_gate);
                }
                int best = 0;
                for (int i = 1; i < _queue.Count; i++)
                {
                    var a = _queue[i]; var b = _queue[best];
                    if (a.Priority < b.Priority || (a.Priority == b.Priority && a.Seq > b.Seq)) best = i;
                }
                job = _queue[best];
                _queue.RemoveAt(best);
            }

            try
            {
                if (job.Pdf.IsDisposed) { job.Tcs.TrySetResult(null); continue; }
                var bmp = job.Pdf.Render(job.Key.Page, job.Key.FullW, job.Key.FullH, job.Key.Region, job.Key.Rotation, job.Key.Invert);
                if (bmp != null) Store(job.Pdf, job.Key, bmp);
                job.Tcs.TrySetResult(bmp);
            }
            catch (Exception ex)
            {
                job.Tcs.TrySetException(ex);
            }
        }
    }

    void Store(PdfFile pdf, RenderKey key, BitmapSource bmp)
    {
        lock (_gate)
        {
            var id = (pdf, key);
            if (_cache.ContainsKey(id)) return;
            var node = _lru.AddFirst(new CacheEntry(id, bmp));
            _cache[id] = node;
            _cacheBytes += key.Bytes;
            while (_cacheBytes > _budget && _lru.Count > 1)
            {
                var last = _lru.Last!;
                _lru.RemoveLast();
                _cache.Remove(last.Value.Id);
                _cacheBytes -= last.Value.Id.Key.Bytes;
            }
        }
    }

    /// <summary>Forgets everything cached for a document (it was closed or its content changed).</summary>
    public void Forget(PdfFile pdf)
    {
        lock (_gate)
        {
            foreach (var j in _queue.Where(j => j.Pdf == pdf).ToList()) { j.Tcs.TrySetCanceled(); _queue.Remove(j); }
            foreach (var kv in _cache.Where(kv => kv.Key.Item1 == pdf).ToList())
            {
                _lru.Remove(kv.Value);
                _cache.Remove(kv.Key);
                _cacheBytes -= kv.Key.Item2.Bytes;
            }
        }
    }

    /// <summary>Forgets cached bitmaps that depend on a view option (e.g. inverted colors changed).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _cache.Clear();
            _lru.Clear();
            _cacheBytes = 0;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stop = true;
            foreach (var j in _queue) j.Tcs.TrySetCanceled();
            _queue.Clear();
            Monitor.PulseAll(_gate);
        }
    }
}
