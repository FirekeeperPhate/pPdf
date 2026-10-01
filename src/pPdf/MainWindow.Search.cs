using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using pPdf.Pdf;
using pPdf.Viewer;

namespace pPdf;

/// <summary>The find bar.</summary>
public partial class MainWindow
{
    SearchController _search = null!;
    DispatcherTimer _findDebounce = null!;

    void InitSearch()
    {
        _search = new SearchController(Dispatcher);
        _search.Changed += UpdateFindUi;
        _search.FirstHit += m => _ = Viewer.ShowMatchAsync(m);
        _findDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _findDebounce.Tick += (_, _) => { _findDebounce.Stop(); RunSearch(); };
    }

    SearchOptions CurrentOptions => new(MatchCaseToggle.IsChecked == true, WholeWordToggle.IsChecked == true);

    void ShowFind()
    {
        if (_pdf == null) return;
        FindBar.Visibility = Visibility.Visible;
        FindToggle.IsChecked = true;
        string selected = Viewer.GetSelectedText().Trim();
        if (selected.Length > 0 && selected.Length < 200 && !selected.Contains('\n') && !selected.Contains('\r'))
            FindBox.Text = selected;
        FindBox.Focus();
        FindBox.SelectAll();
        if (FindBox.Text.Length > 0 && _search.Query.Length == 0) RunSearch();
    }

    void HideFind()
    {
        FindBar.Visibility = Visibility.Collapsed;
        FindToggle.IsChecked = false;
        _findDebounce.Stop();
        _search.Clear();
        Viewer.ClearSearchHighlights();
        Viewer.Focus();
    }

    void ResetSearch()
    {
        _findDebounce?.Stop();
        _search?.Clear();
        Viewer.ClearSearchHighlights();
    }

    void OnFindToggle(object sender, RoutedEventArgs e)
    {
        if (FindToggle.IsChecked == true) ShowFind(); else HideFind();
    }

    void OnFindClose(object sender, RoutedEventArgs e) => HideFind();

    void OnFindTextChanged(object sender, TextChangedEventArgs e)
    {
        _findDebounce.Stop();
        _findDebounce.Start();
    }

    void OnFindOptionChanged(object sender, RoutedEventArgs e) => RunSearch();

    void RunSearch()
    {
        if (_pdf == null) return;
        _search.Start(_pdf, FindBox.Text, CurrentOptions, Viewer.CurrentPage);
        if (string.IsNullOrWhiteSpace(FindBox.Text)) Viewer.ClearSearchHighlights();
    }

    void OnFindKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                _findDebounce.Stop();
                // first Enter after typing: the scan may not have started yet
                if (_search.Query.Length == 0 || _search.Query != Normalized.NormalizeQuery(FindBox.Text)) RunSearch();
                else FindStep((Keyboard.Modifiers & ModifierKeys.Shift) == 0);
                e.Handled = true;
                break;
            case Key.Escape:
                HideFind();
                e.Handled = true;
                break;
        }
    }

    void OnFindNext(object sender, RoutedEventArgs e) => FindStep(true);
    void OnFindPrev(object sender, RoutedEventArgs e) => FindStep(false);

    void FindStep(bool forward)
    {
        if (_pdf == null) return;
        if (FindBar.Visibility != Visibility.Visible) { ShowFind(); return; }
        var m = forward ? _search.Next() : _search.Previous();
        if (m is { } hit) _ = Viewer.ShowMatchAsync(hit);
    }

    void UpdateFindUi()
    {
        Viewer.SetSearchHighlights(_search.Matches, _search.Current);
        int n = _search.Matches.Count;
        if (_search.Query.Length == 0) FindStatus.Text = "";
        else if (n == 0) FindStatus.Text = _search.IsRunning ? "Searching..." : "No results";
        else FindStatus.Text = _search.IsRunning ? $"{_search.CurrentIndex + 1} of {n}+" : $"{_search.CurrentIndex + 1} of {n}";
    }
}
