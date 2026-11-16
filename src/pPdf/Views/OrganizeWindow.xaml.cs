using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;
using pPdf.Pdf;

namespace pPdf.Views;

/// <summary>One page in the organizer: where it comes from, how it is turned, its thumbnail.</summary>
public sealed partial class OrganizerPage : ObservableObject
{
    public int Source { get; init; }
    public int Index { get; init; }
    public string Label { get; init; } = "";
    public double ThumbWidth { get; init; }
    public double ThumbHeight { get; init; }
    [ObservableProperty] public partial BitmapSource? Thumb { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Angle))] public partial int QuarterTurns { get; set; }
    public double Angle => ((QuarterTurns % 4) + 4) % 4 * 90;
}

/// <summary>
/// Puts a new PDF together from the pages of the open document (and of other files): turn, leave out, reorder, insert, then save it
/// as a new file. The open document itself is not changed (unless the new file is saved over it).
/// </summary>
public partial class OrganizeWindow : Window
{
    const double Box = 136;

    readonly List<(PdfFile File, string Name, bool Own)> _sources = [];
    readonly ObservableCollection<OrganizerPage> _pages = [];
    readonly Func<bool, byte[]> _mainBytes;
    readonly string _mainPath;
    readonly CancellationTokenSource _cts = new();

    /// <summary>Where the result was saved (null if it was not).</summary>
    public string? SavedPath { get; private set; }

    /// <param name="mainBytes">The bytes of the open document, with (true) or without (false) the user's annotations and form entries drawn in.</param>
    public OrganizeWindow(Window owner, PdfFile main, string mainPath, bool hasAnnotations, Func<bool, byte[]> mainBytes)
    {
        InitializeComponent();
        Owner = owner;
        _mainBytes = mainBytes;
        _mainPath = mainPath;
        _sources.Add((main, Path.GetFileName(mainPath), true));
        WithAnnotationsBox.Visibility = hasAnnotations ? Visibility.Visible : Visibility.Collapsed;
        WithAnnotationsBox.IsChecked = hasAnnotations;
        for (int i = 0; i < main.PageCount; i++) _pages.Add(NewPage(0, i, main));
        PageList.ItemsSource = _pages;
        Loaded += (_, _) =>
        {
            Services.ThemeService.ApplyTitleBar(this);
            _ = LoadThumbsAsync();
            PageList.Focus();
            UpdateUi();
        };
        Closed += (_, _) =>
        {
            _cts.Cancel();
            // the open document belongs to its window; the others were opened here
            foreach (var (file, _, own) in _sources) if (!own) Task.Run(file.Dispose);
        };
    }

    OrganizerPage NewPage(int source, int index, PdfFile file)
    {
        var size = file.Pages[index];
        double scale = Math.Min(Box / size.Width, Box / size.Height);
        string label = source == 0 ? (index + 1).ToString() : $"{_sources[source].Name} · {index + 1}";
        return new OrganizerPage { Source = source, Index = index, Label = label, ThumbWidth = size.Width * scale, ThumbHeight = size.Height * scale };
    }

    /// <summary>The thumbnails, drawn one after the other in the background (PDFium works one call at a time anyway).</summary>
    async Task LoadThumbsAsync()
    {
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        while (!_cts.IsCancellationRequested)
        {
            var next = _pages.FirstOrDefault(p => p.Thumb == null);
            if (next == null) return;
            var file = _sources[next.Source].File;
            int w = Math.Max(1, (int)(next.ThumbWidth * dpi)), h = Math.Max(1, (int)(next.ThumbHeight * dpi));
            BitmapSource? bmp = null;
            try { bmp = await Task.Run(() => file.Render(next.Index, w, h, new Int32Rect(0, 0, w, h), 0, invert: false), _cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (Exception) { /* a page that cannot be drawn keeps a blank thumbnail */ }
            // a blank white bitmap marks a page done even if drawing failed
            foreach (var p in _pages.Where(p => p.Source == next.Source && p.Index == next.Index && p.Thumb == null))
                p.Thumb = bmp ?? BitmapSource.Create(1, 1, 96, 96, PixelFormats.Gray8, null, new byte[] { 255 }, 1);
        }
    }

    List<OrganizerPage> Selected => PageList.SelectedItems.Cast<OrganizerPage>().OrderBy(p => _pages.IndexOf(p)).ToList();

    void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateUi();

    void UpdateUi()
    {
        int n = PageList.SelectedItems.Count;
        RotateLeftButton.IsEnabled = RotateRightButton.IsEnabled = DeleteButton.IsEnabled = EarlierButton.IsEnabled = LaterButton.IsEnabled = n > 0;
        ExtractButton.IsEnabled = n > 0;
        SaveButton.IsEnabled = _pages.Count > 0;
        string pages = _pages.Count == 1 ? "1 page" : $"{_pages.Count} pages";
        StatusText.Text = (n == 0 ? pages : $"{pages}, {n} selected") +
            "   ·   Click, Ctrl+click and Shift+click select; Ctrl+A selects all. The open document is not changed: the result is saved as a new file.";
    }

    void Rotate(int quarter)
    {
        foreach (var p in Selected) p.QuarterTurns = ((p.QuarterTurns + quarter) % 4 + 4) % 4;
    }

    void OnRotateLeft(object sender, RoutedEventArgs e) => Rotate(-1);
    void OnRotateRight(object sender, RoutedEventArgs e) => Rotate(1);

    void OnDelete(object sender, RoutedEventArgs e) => Delete();

    void Delete()
    {
        var sel = Selected;
        if (sel.Count == 0) return;
        int first = _pages.IndexOf(sel[0]);
        foreach (var p in sel) _pages.Remove(p);
        if (_pages.Count > 0) PageList.SelectedItem = _pages[Math.Min(first, _pages.Count - 1)];
        UpdateUi();
    }

    void OnEarlier(object sender, RoutedEventArgs e) => Move(-1);
    void OnLater(object sender, RoutedEventArgs e) => Move(1);

    /// <summary>Moves the selected pages one place (a block keeps its order); at the start or end nothing moves.</summary>
    void Move(int direction)
    {
        var sel = Selected;
        if (sel.Count == 0) return;
        var indices = sel.Select(p => _pages.IndexOf(p)).ToList();
        if (direction < 0 && indices[0] == 0) return;
        if (direction > 0 && indices[^1] == _pages.Count - 1) return;
        foreach (int i in direction < 0 ? indices : Enumerable.Reverse(indices)) _pages.Move(i, i + direction);
        PageList.SelectedItems.Clear();
        foreach (var p in sel) PageList.SelectedItems.Add(p);
        PageList.ScrollIntoView(sel[direction < 0 ? 0 : ^1]);
    }

    void OnInsert(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "PDF documents (*.pdf)|*.pdf", Multiselect = true, Title = "Insert the pages of" };
        if (dlg.ShowDialog(this) != true) return;
        int at = Selected is { Count: > 0 } sel ? _pages.IndexOf(sel[^1]) + 1 : _pages.Count;
        var added = new List<OrganizerPage>();
        foreach (var path in dlg.FileNames)
        {
            PdfFile? file = null;
            string? password = null;
            bool wrong = false;
            while (file == null)
            {
                try { file = PdfFile.Open(path, password); }
                catch (PdfPasswordException)
                {
                    password = PasswordDialog.Ask(this, Path.GetFileName(path), wrong);
                    if (password == null) break;
                    wrong = true;
                }
                catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException)
                {
                    MessageBox.Show(this, $"{Path.GetFileName(path)} could not be opened.\n\n{ex.Message}", "pPdf", MessageBoxButton.OK, MessageBoxImage.Warning);
                    break;
                }
            }
            if (file == null) continue;
            _sources.Add((file, Path.GetFileName(path), false));
            int source = _sources.Count - 1;
            for (int i = 0; i < file.PageCount; i++)
            {
                var page = NewPage(source, i, file);
                _pages.Insert(at++, page);
                added.Add(page);
            }
        }
        if (added.Count == 0) return;
        PageList.SelectedItems.Clear();
        foreach (var p in added) PageList.SelectedItems.Add(p);
        PageList.ScrollIntoView(added[0]);
        _ = LoadThumbsAsync();
        UpdateUi();
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0, shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        switch (e.Key)
        {
            case Key.Delete: Delete(); e.Handled = true; break;
            case Key.Left when ctrl: Move(-1); e.Handled = true; break;
            case Key.Right when ctrl: Move(1); e.Handled = true; break;
            case Key.R when ctrl: Rotate(shift ? -1 : 1); e.Handled = true; break;
            case Key.S when ctrl: OnSave(this, new RoutedEventArgs()); e.Handled = true; break;
        }
    }

    void OnExtract(object sender, RoutedEventArgs e) => _ = SaveAsync(Selected, " (pages)");
    void OnSave(object sender, RoutedEventArgs e) => _ = SaveAsync(_pages.ToList(), " (organized)");

    bool _saving;

    async Task SaveAsync(List<OrganizerPage> pages, string suffix)
    {
        if (_saving || pages.Count == 0) return;
        var dlg = new SaveFileDialog
        {
            Filter = "PDF documents (*.pdf)|*.pdf",
            FileName = Path.GetFileNameWithoutExtension(_mainPath) + suffix + ".pdf",
            InitialDirectory = Path.GetDirectoryName(_mainPath),
            OverwritePrompt = true,
            Title = "Save the pages as",
        };
        if (dlg.ShowDialog(this) != true) return;
        string target = dlg.FileName;
        bool overMain = string.Equals(Path.GetFullPath(target), Path.GetFullPath(_mainPath), StringComparison.OrdinalIgnoreCase);
        if (overMain && _sources[0].File.IsOnDemand)
        {
            MessageBox.Show(this, "This is a very large file: pPdf reads it from disk while it is open, so it cannot be overwritten. Save it with another name.",
                "pPdf", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // over the open document: what was kept for it is now drawn into the pages, so it goes in (it would not fit the new pages otherwise)
        bool withAnnotations = overMain || (WithAnnotationsBox.Visibility == Visibility.Visible && WithAnnotationsBox.IsChecked == true);
        var picks = pages.Select(p => new PagePick(p.Source, p.Index, p.QuarterTurns)).ToList();
        var files = _sources.Select(s => s.File).ToList();
        _saving = true;
        BusyOverlay.Visibility = Visibility.Visible;
        string tmp = target + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            byte[] main = await Task.Run(() => _mainBytes(withAnnotations));
            var sources = new List<PageSource> { new(main, files[0].Password) };
            sources.AddRange(files.Skip(1).Select(f => new PageSource(f.GetBytes(), f.Password)));
            byte[] bytes = await Task.Run(() => PageOrganizer.Build(sources, picks));
            await File.WriteAllBytesAsync(tmp, bytes);
            await Task.Run(() =>
            {
                if (!File.Exists(target)) { File.Move(tmp, target); return; }
                try { File.Replace(tmp, target, null, ignoreMetadataErrors: true); }
                catch (Exception ex) when (ex is IOException or PlatformNotSupportedException) { File.Move(tmp, target, overwrite: true); }
            });
            SavedPath = target;
        }
        catch (Exception ex)
        {
            try { File.Delete(tmp); } catch (Exception) { }
            string reason = ex.Message.Contains("owner password", StringComparison.OrdinalIgnoreCase)
                ? "One of the documents is protected and does not allow changes without its owner password."
                : ex.Message;
            MessageBox.Show(this, "The file could not be saved.\n\n" + reason, "pPdf", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        finally
        {
            _saving = false;
            BusyOverlay.Visibility = Visibility.Collapsed;
        }
        DialogResult = true;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_saving) e.Cancel = true; // half a file must not be left behind
        base.OnClosing(e);
    }
}
