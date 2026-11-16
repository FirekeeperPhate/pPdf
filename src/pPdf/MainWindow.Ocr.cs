using System.Windows;
using pPdf.Pdf;

namespace pPdf;

/// <summary>Scanned documents: their pages are pictures, so Windows' text recognition reads them to make them searchable.</summary>
public partial class MainWindow
{
    CancellationTokenSource? _ocrCts;
    bool OcrRunning => _ocrCts != null;
    /// <summary>What the notice bar's button does now.</summary>
    Action? _noticeAction;

    /// <summary>
    /// After opening: the text read earlier comes back from the cache; a document whose first pages have no text at all gets the offer
    /// to read it (nothing runs by itself: recognition takes a few seconds a page).
    /// </summary>
    async Task CheckScannedAsync(PdfFile pdf, string path, string? fingerprint)
    {
        var kept = await Task.Run(() => PageOcr.Load(path, fingerprint));
        if (_pdf != pdf) return;
        foreach (var (page, text) in kept)
            if (page < pdf.PageCount) { pdf.OcrPages[page] = text; Viewer.RefreshText(page); }

        int probe = Math.Min(3, pdf.PageCount);
        bool scanned = await Task.Run(() => Enumerable.Range(0, probe).All(p => !pdf.IsDisposed && !pdf.OcrPages.ContainsKey(p) && PageOcr.HasNoText(pdf, p)));
        if (_pdf != pdf || !scanned) return;
        ShowNotice("This document looks scanned: its pages are pictures, so there is no text to search or copy.", "Recognize the text", () => _ = RecognizeTextAsync());
    }

    void ShowNotice(string text, string? button, Action? action, bool progress = false)
    {
        NoticeText.Text = text;
        NoticeButton.Content = button;
        NoticeButton.Visibility = button == null ? Visibility.Collapsed : Visibility.Visible;
        NoticeProgress.Visibility = progress ? Visibility.Visible : Visibility.Collapsed;
        _noticeAction = action;
        NoticeBar.Visibility = Visibility.Visible;
    }

    void HideNotice()
    {
        NoticeBar.Visibility = Visibility.Collapsed;
        _noticeAction = null;
    }

    void OnNoticeAction(object sender, RoutedEventArgs e) => _noticeAction?.Invoke();

    void OnNoticeClose(object sender, RoutedEventArgs e)
    {
        StopRecognizing();
        HideNotice();
    }

    void StopRecognizing() => _ocrCts?.Cancel();

    /// <summary>Reads the text of every page that has none, one page after the other, showing how far it got.</summary>
    async Task RecognizeTextAsync()
    {
        if (_pdf is not { } pdf || _path is not { } path || OcrRunning) return;
        var cts = new CancellationTokenSource();
        _ocrCts = cts;
        string? fingerprint = _fingerprint;
        int done = 0;
        try
        {
            ShowNotice("Looking for the pages without text...", "Stop", StopRecognizing, progress: true);
            NoticeProgress.IsIndeterminate = true;
            var pages = await Task.Run(() => Enumerable.Range(0, pdf.PageCount)
                .Where(p => !cts.IsCancellationRequested && !pdf.IsDisposed && !pdf.OcrPages.ContainsKey(p) && PageOcr.HasNoText(pdf, p)).ToList());
            if (cts.IsCancellationRequested || _pdf != pdf) return;
            if (pages.Count == 0)
            {
                ShowNotice(pdf.OcrPages.IsEmpty ? "Every page already has text of its own: there is nothing to recognize."
                    : "The text of every page has been recognized already.", null, null);
                return;
            }

            NoticeProgress.IsIndeterminate = false;
            NoticeProgress.Maximum = pages.Count;
            foreach (int page in pages)
            {
                if (cts.IsCancellationRequested || _pdf != pdf) break;
                NoticeProgress.Value = done;
                NoticeText.Text = $"Recognizing the text: page {page + 1} ({done + 1} of {pages.Count})...";
                PageText? text;
                try { text = await Task.Run(() => PageOcr.Recognize(pdf, page)); }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // one page that fails (a damaged picture) does not stop the others
                    System.Diagnostics.Debug.WriteLine($"ocr page {page}: {ex.Message}");
                    done++;
                    continue;
                }
                if (_pdf != pdf) break;
                if (text == null)
                {
                    HideNotice();
                    MessageBox.Show(this,
                        "Windows has no text recognition installed for your languages.\n\nIt can be added in Settings > Time & language > Language & region: " +
                        "open the options of a language and install \"Optical character recognition\".",
                        "pPdf", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                pdf.OcrPages[page] = text;
                Viewer.RefreshText(page);
                done++;
                if (done % 5 == 0) await Task.Run(() => PageOcr.Save(path, fingerprint, pdf.OcrPages));
            }
        }
        finally
        {
            if (_ocrCts == cts) _ocrCts = null;
        }

        if (done > 0) await Task.Run(() => PageOcr.Save(path, fingerprint, pdf.OcrPages));
        if (_pdf != pdf) return;
        if (FindBar.Visibility == Visibility.Visible && FindBox.Text.Length > 0) RunSearch();
        string pagesText = done == 1 ? "1 page" : $"{done} pages";
        if (cts.IsCancellationRequested)
            ShowNotice($"Stopped: the text of {pagesText} was recognized.", "Go on", () => _ = RecognizeTextAsync());
        else
            ShowNotice($"The text of {pagesText} was recognized: it can be searched, selected and copied, and \"Save a copy\" keeps it in the file.", null, null);
    }

    /// <summary>The document is being closed: recognition stops and its notice goes.</summary>
    void EndOcr()
    {
        StopRecognizing();
        HideNotice();
    }
}
