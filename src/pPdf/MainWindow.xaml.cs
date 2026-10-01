using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using pPdf.Annotations;
using pPdf.Pdf;
using pPdf.Printing;
using pPdf.Services;
using pPdf.Viewer;
using pPdf.Views;

namespace pPdf;

public partial class MainWindow : Window
{
    static readonly string[] ZoomItems = ["Fit width", "Fit page", "Actual size", "50%", "75%", "100%", "125%", "150%", "200%", "300%", "400%"];

    readonly AppSettings _settings;
    PdfFile? _pdf;
    string? _path;
    bool _syncing;
    bool _forceClose;
    bool _busy;

    public MainWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        RestoreWindowBounds();
        ZoomBox.ItemsSource = ZoomItems;
        FontBox.ItemsSource = Fonts.SystemFontFamilies.Select(f => f.Source).Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        SizeBox.ItemsSource = new[] { "8", "9", "10", "11", "12", "14", "16", "18", "20", "24", "28", "36", "48", "72" };

        ApplySettingsToViewer();
        Viewer.StateChanged += (_, _) => UpdateUi();
        Viewer.ToolChanged += (_, _) => UpdateUi();
        Viewer.SelectedAnnotationChanged += (_, _) => { UpdateAnnotationBar(); UpdateUi(); };
        Viewer.SelectionChanged += (_, _) => UpdateUi();
        Viewer.Annotations.HistoryChanged += UpdateUi;

        InitSearch();
        InitSidebar();
        BuildRecentList();
        UpdateUi();
    }

    // ------------------------------------------------------------------ settings

    void RestoreWindowBounds()
    {
        Width = Math.Max(MinWidth, _settings.WindowWidth);
        Height = Math.Max(MinHeight, _settings.WindowHeight);
        if (!double.IsNaN(_settings.WindowLeft) && !double.IsNaN(_settings.WindowTop))
        {
            // only if the saved position is still on a screen
            var r = new Rect(_settings.WindowLeft, _settings.WindowTop, Width, Height);
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (screen.IntersectsWith(r) && r.Left > screen.Left - Width + 80 && r.Top >= screen.Top - 10)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = _settings.WindowLeft;
                Top = _settings.WindowTop;
            }
        }
        if (_settings.Maximized) WindowState = WindowState.Maximized;
    }

    void ApplySettingsToViewer()
    {
        Viewer.Layout = _settings.Layout;
        Viewer.CoverAlone = _settings.CoverAlone;
        if (_settings.ZoomMode == ZoomMode.Custom) Viewer.SetZoom(_settings.Zoom); else Viewer.SetZoomMode(_settings.ZoomMode);
        Viewer.InvertColors = _settings.InvertPages;

        var d = Viewer.TextDefaults;
        d.FontFamily = _settings.TextFont;
        d.FontSize = _settings.TextSize;
        d.Bold = _settings.TextBold;
        d.Italic = _settings.TextItalic;
        d.Foreground = ParseColor(_settings.TextColor) ?? Colors.Black;
        d.Background = ParseColor(_settings.TextFill);

        _settings.SidebarWidth = Math.Clamp(_settings.SidebarWidth, 120, 500);
        SidebarColumn.Width = new GridLength(_settings.SidebarVisible ? _settings.SidebarWidth : 0);
        SidebarSplitter.Visibility = _settings.SidebarVisible ? Visibility.Visible : Visibility.Collapsed;
        SidebarToggle.IsChecked = _settings.SidebarVisible;
        (_settings.SidebarOutline ? OutlineTab : ThumbTab).IsChecked = true;
        MatchCaseToggle.IsChecked = _settings.MatchCase;
        WholeWordToggle.IsChecked = _settings.WholeWord;
    }

    static Color? ParseColor(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        try { return (Color)ColorConverter.ConvertFromString(s); }
        catch (FormatException) { return null; }
    }

    static string ColorText(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    void SaveSettings()
    {
        _settings.Layout = Viewer.Layout;
        _settings.ZoomMode = Viewer.ZoomMode;
        if (Viewer.ZoomMode == ZoomMode.Custom) _settings.Zoom = Viewer.EffectiveZoom;
        _settings.CoverAlone = Viewer.CoverAlone;
        _settings.InvertPages = Viewer.InvertColors;
        _settings.MatchCase = MatchCaseToggle.IsChecked == true;
        _settings.WholeWord = WholeWordToggle.IsChecked == true;
        var d = Viewer.TextDefaults;
        _settings.TextFont = d.FontFamily;
        _settings.TextSize = d.FontSize;
        _settings.TextBold = d.Bold;
        _settings.TextItalic = d.Italic;
        _settings.TextColor = ColorText(d.Foreground);
        _settings.TextFill = d.Background is { } bg ? ColorText(bg) : null;

        _settings.Maximized = WindowState == WindowState.Maximized;
        var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (!b.IsEmpty) { _settings.WindowLeft = b.Left; _settings.WindowTop = b.Top; _settings.WindowWidth = b.Width; _settings.WindowHeight = b.Height; }
        if (_path != null) _settings.AddRecent(_path, Viewer.CurrentPage);
        _settings.Save();
    }

    // ------------------------------------------------------------------ window events

    void OnLoaded(object sender, RoutedEventArgs e) => ThemeService.ApplyTitleBar(this);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeService.ApplyTitleBar(this);
    }

    void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        Viewer.CommitEdits();
        if (!_forceClose && Viewer.Annotations.IsDirty)
        {
            e.Cancel = true;
            _ = CloseAfterAskingAsync();
            return;
        }
        SaveSettings();
        Viewer.Close();
        _pdf?.Dispose();
    }

    async Task CloseAfterAskingAsync()
    {
        if (!await ConfirmDiscardAnnotationsAsync()) return;
        _forceClose = true;
        Close();
    }

    /// <summary>If there are annotations that were never written into a PDF, ask what to do. False = the user cancelled.</summary>
    async Task<bool> ConfirmDiscardAnnotationsAsync()
    {
        Viewer.CommitEdits();
        if (!Viewer.Annotations.IsDirty) return true;
        var answer = MessageBox.Show(this,
            "The annotations you added are not saved in a PDF yet.\n\nSave a copy with the annotations before continuing?",
            "pPdf", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.No) return true;
        return await SaveCopyAsync();
    }

    // ------------------------------------------------------------------ drag and drop

    void OnWindowDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    async void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (e.Handled || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var pdf = files.FirstOrDefault(f => string.Equals(Path.GetExtension(f), ".pdf", StringComparison.OrdinalIgnoreCase));
        if (pdf != null)
        {
            e.Handled = true;
            await OpenAsync(pdf);
        }
    }

    // ------------------------------------------------------------------ opening

    void OnOpenClick(object sender, RoutedEventArgs e) => _ = BrowseAndOpenAsync();

    async Task BrowseAndOpenAsync()
    {
        var dlg = new OpenFileDialog { Filter = "PDF documents (*.pdf)|*.pdf|All files (*.*)|*.*", Title = "Open PDF" };
        if (_path != null) dlg.InitialDirectory = Path.GetDirectoryName(_path);
        if (dlg.ShowDialog(this) == true) await OpenAsync(dlg.FileName);
    }

    public async Task OpenAsync(string path)
    {
        if (_busy) return;
        if (!await ConfirmDiscardAnnotationsAsync()) return;
        if (!File.Exists(path))
        {
            MessageBox.Show(this, $"The file was not found:\n{path}", "pPdf", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetBusy(true, "Opening " + Path.GetFileName(path) + "...");
        PdfFile? pdf = null;
        try
        {
            string? password = null;
            bool wrong = false;
            while (pdf == null)
            {
                try { pdf = await Task.Run(() => PdfFile.Open(path, password)); }
                catch (PdfPasswordException)
                {
                    SetBusy(false);
                    password = PasswordDialog.Ask(this, Path.GetFileName(path), wrong);
                    if (password == null) return;
                    wrong = true;
                    SetBusy(true, "Opening " + Path.GetFileName(path) + "...");
                }
            }
        }
        catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException)
        {
            SetBusy(false);
            MessageBox.Show(this, $"{Path.GetFileName(path)} could not be opened.\n\n{ex.Message}", "pPdf", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        finally { SetBusy(false); }

        CloseDocument(saveRecent: true);
        _pdf = pdf;
        _path = path;
        int start = _settings.LastPages.TryGetValue(path, out int last) ? last : 0;
        Viewer.Open(pdf, start);
        WelcomePanel.Visibility = Visibility.Collapsed;
        _settings.AddRecent(path, start);
        BuildRecentList();
        LoadSidebar(pdf);
        if (FindBar.Visibility == Visibility.Visible && FindBox.Text.Length > 0) RunSearch();
        UpdateUi();
        Viewer.Focus();
    }

    void CloseDocument(bool saveRecent)
    {
        if (_pdf == null) return;
        if (saveRecent && _path != null) _settings.AddRecent(_path, Viewer.CurrentPage);
        ResetSearch();
        Viewer.Close();
        var old = _pdf;
        _pdf = null;
        _path = null;
        ClearSidebar();
        Task.Run(old.Dispose);
    }

    void SetBusy(bool busy, string? text = null)
    {
        _busy = busy;
        BusyOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyText.Text = text ?? "";
        Cursor = busy ? Cursors.Wait : null;
    }

    void BuildRecentList()
    {
        RecentPanel.Children.Clear();
        var existing = _settings.Recent.Where(File.Exists).Take(6).ToList();
        if (existing.Count == 0) return;
        RecentPanel.Children.Add(new TextBlock { Text = "Recent", Margin = new Thickness(0, 0, 0, 6), Foreground = (Brush)FindResource("SubtleTextBrush") });
        foreach (var p in existing)
        {
            var b = new Button
            {
                Content = new TextBlock { Text = Path.GetFileName(p), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = p },
                HorizontalContentAlignment = HorizontalAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 6, 10, 6),
                Focusable = false,
            };
            string path = p;
            b.Click += async (_, _) => await OpenAsync(path);
            RecentPanel.Children.Add(b);
        }
    }

    // ------------------------------------------------------------------ UI state

    void UpdateUi()
    {
        bool hasDoc = _pdf != null;
        _syncing = true;
        try
        {
            PrevButton.IsEnabled = NextButton.IsEnabled = hasDoc;
            PrintButton.IsEnabled = SaveButton.IsEnabled = hasDoc;
            FindToggle.IsEnabled = hasDoc;
            PageBox.IsEnabled = ZoomBox.IsEnabled = hasDoc;
            LayoutButton.IsEnabled = hasDoc;
            UndoButton.IsEnabled = Viewer.Annotations.CanUndo;
            RedoButton.IsEnabled = Viewer.Annotations.CanRedo;

            if (!PageBox.IsKeyboardFocusWithin) PageBox.Text = hasDoc ? (Viewer.CurrentPage + 1).ToString(CultureInfo.InvariantCulture) : "";
            PageCountText.Text = "/ " + (_pdf?.PageCount ?? 0);

            if (!ZoomBox.IsKeyboardFocusWithin)
            {
                ZoomBox.Text = !hasDoc ? "" : Viewer.ZoomMode switch
                {
                    ZoomMode.FitWidth => "Fit width",
                    ZoomMode.FitPage => "Fit page",
                    _ => Math.Round(Viewer.EffectiveZoom * 100).ToString(CultureInfo.InvariantCulture) + "%",
                };
            }

            SelectToolButton.IsChecked = Viewer.Tool == ViewerTool.Select;
            HandToolButton.IsChecked = Viewer.Tool == ViewerTool.Hand;
            TextToolButton.IsChecked = Viewer.Tool == ViewerTool.Text;

            Title = hasDoc ? $"{Path.GetFileName(_path)} - pPdf" : "pPdf";
            StatusLeft.Text = _path ?? "";
            var parts = new List<string>();
            if (hasDoc) parts.Add($"Page {Viewer.CurrentPage + 1} of {_pdf!.PageCount}");
            if (hasDoc) parts.Add(Math.Round(Viewer.EffectiveZoom * 100).ToString(CultureInfo.InvariantCulture) + "%");
            if (!Viewer.Annotations.IsEmpty) parts.Add($"{Viewer.Annotations.Items.Count} annotation" + (Viewer.Annotations.Items.Count == 1 ? "" : "s") + (Viewer.Annotations.IsDirty ? " (not saved)" : ""));
            StatusRight.Text = string.Join("   ·   ", parts);

            SyncSidebarSelection();
        }
        finally { _syncing = false; }
    }

    // ------------------------------------------------------------------ toolbar: navigation and zoom

    void OnPrevPage(object sender, RoutedEventArgs e) => Viewer.PreviousPage();
    void OnNextPage(object sender, RoutedEventArgs e) => Viewer.NextPage();

    void OnPageBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (int.TryParse(PageBox.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int n)) Viewer.GoToPage(n - 1);
        e.Handled = true;
        Viewer.Focus();
        UpdateUi();
    }

    void OnBoxGotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        switch (sender)
        {
            case TextBox tb: tb.SelectAll(); break;
            case ComboBox cb:
                cb.Dispatcher.BeginInvoke(() => (cb.Template.FindName("PART_EditableTextBox", cb) as TextBox)?.SelectAll());
                break;
        }
    }

    void OnZoomIn(object sender, RoutedEventArgs e) => Viewer.ZoomIn();
    void OnZoomOut(object sender, RoutedEventArgs e) => Viewer.ZoomOut();

    void OnZoomSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || ZoomBox.SelectedItem is not string s) return;
        ApplyZoomText(s);
        Viewer.Focus();
    }

    void OnZoomBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyZoomText(ZoomBox.Text);
        e.Handled = true;
        Viewer.Focus();
        UpdateUi();
    }

    void ApplyZoomText(string text)
    {
        text = text.Trim();
        if (text.Equals("Fit width", StringComparison.OrdinalIgnoreCase)) { Viewer.SetZoomMode(ZoomMode.FitWidth); return; }
        if (text.Equals("Fit page", StringComparison.OrdinalIgnoreCase)) { Viewer.SetZoomMode(ZoomMode.FitPage); return; }
        if (text.Equals("Actual size", StringComparison.OrdinalIgnoreCase)) { Viewer.SetZoom(1.0); return; }
        text = text.TrimEnd('%').Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double pct) && pct > 0)
            Viewer.SetZoom(pct / 100.0);
    }

    // ------------------------------------------------------------------ toolbar: layout and rotation

    void OnLayoutClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = LayoutButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void Layout(string header, ViewLayout l) => menu.Items.Add(CheckItem(header, Viewer.Layout == l, () => Viewer.Layout = l, radio: true));
        Layout("Continuous", ViewLayout.Continuous);
        Layout("Single page", ViewLayout.SinglePage);
        Layout("Two pages", ViewLayout.TwoPages);
        Layout("Two pages, continuous", ViewLayout.TwoPagesContinuous);
        menu.Items.Add(new Separator());
        menu.Items.Add(CheckItem("Fit width", Viewer.ZoomMode == ZoomMode.FitWidth, () => Viewer.SetZoomMode(ZoomMode.FitWidth), radio: true));
        menu.Items.Add(CheckItem("Fit page", Viewer.ZoomMode == ZoomMode.FitPage, () => Viewer.SetZoomMode(ZoomMode.FitPage), radio: true));
        menu.Items.Add(CheckItem("Actual size", false, () => Viewer.SetZoom(1.0), radio: true));
        menu.Items.Add(new Separator());
        menu.Items.Add(CheckItem("Show the cover page alone (two-page views)", Viewer.CoverAlone, () => Viewer.CoverAlone = !Viewer.CoverAlone));
        menu.IsOpen = true;
    }

    static MenuItem CheckItem(string header, bool isChecked, Action action, bool radio = false)
    {
        var item = new MenuItem { Header = header, IsChecked = isChecked, IsCheckable = false };
        if (radio && isChecked) item.Icon = new TextBlock { Text = "●", FontSize = 9, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        item.Click += (_, _) => action();
        return item;
    }

    void OnRotateRight(object sender, RoutedEventArgs e) => Viewer.Rotation += 1;
    void OnRotateLeft(object sender, RoutedEventArgs e) => Viewer.Rotation -= 1;

    void OnSelectTool(object sender, RoutedEventArgs e) { Viewer.Tool = ViewerTool.Select; UpdateUi(); }
    void OnHandTool(object sender, RoutedEventArgs e) { Viewer.Tool = ViewerTool.Hand; UpdateUi(); }

    // ------------------------------------------------------------------ menus: theme and more

    void OnThemeClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ThemeButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void Theme(string header, AppTheme t) => menu.Items.Add(CheckItem(header, _settings.Theme == t, () => SetTheme(t), radio: true));
        Theme("Follow system", AppTheme.System);
        Theme("Light", AppTheme.Light);
        Theme("Dark", AppTheme.Dark);
        menu.Items.Add(new Separator());
        menu.Items.Add(CheckItem("Invert page colors (night reading)", Viewer.InvertColors, () => Viewer.InvertColors = !Viewer.InvertColors));
        menu.IsOpen = true;
    }

    void SetTheme(AppTheme t)
    {
        _settings.Theme = t;
        ThemeService.Apply(t);
    }

    void OnMoreClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MoreButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var recent = new MenuItem { Header = "Open recent" };
        foreach (var p in _settings.Recent.Where(File.Exists).Take(10))
        {
            string path = p;
            var mi = new MenuItem { Header = Path.GetFileName(p).Replace("_", "__"), ToolTip = p };
            mi.Click += async (_, _) => await OpenAsync(path);
            recent.Items.Add(mi);
        }
        recent.IsEnabled = recent.Items.Count > 0;
        menu.Items.Add(recent);
        var save = new MenuItem { Header = "Save a copy with annotations...", IsEnabled = _pdf != null };
        save.Click += (_, _) => _ = SaveCopyAsync();
        menu.Items.Add(save);
        menu.Items.Add(new Separator());
        var help = new MenuItem { Header = "Keyboard shortcuts" };
        help.Click += (_, _) => ShowShortcuts();
        menu.Items.Add(help);
        var about = new MenuItem { Header = "About pPdf" };
        about.Click += (_, _) => MessageBox.Show(this,
            $"pPdf {typeof(App).Assembly.GetName().Version?.ToString(3)}\nA small PDF reader.\n\nRendering: PDFium. Saving annotations: PDFsharp.",
            "About pPdf", MessageBoxButton.OK, MessageBoxImage.Information);
        menu.Items.Add(about);
        menu.IsOpen = true;
    }

    void ShowShortcuts()
    {
        MessageBox.Show(this,
            "Ctrl+O  Open\nCtrl+S  Save a copy with annotations\nCtrl+P  Print\nCtrl+F  Find   (Enter / F3 next, Shift+Enter / Shift+F3 previous)\n" +
            "Ctrl+C  Copy selected text\nCtrl+A  Select all text\nF4  Pages / outline panel\n\n" +
            "Ctrl+Wheel, Ctrl++ / Ctrl+-  Zoom\nCtrl+0  Fit page   Ctrl+1  Actual size   Ctrl+2  Fit width\nCtrl+R / Ctrl+Shift+R  Rotate\n" +
            "PgUp / PgDn, Space, arrows, Home / End  Navigate\nMiddle mouse drag  Pan\n\n" +
            "T  Add text    I  Add image    Ctrl+V  Paste image or text as annotation\nDel  Delete annotation   Enter / F2  Edit text\n" +
            "Ctrl+Z / Ctrl+Y  Undo / redo annotations",
            "Keyboard shortcuts", MessageBoxButton.OK, MessageBoxImage.None);
    }

    // ------------------------------------------------------------------ save / print

    void OnSaveClick(object sender, RoutedEventArgs e) => _ = SaveCopyAsync();

    /// <summary>Writes the document plus its annotations to a new file. Returns true if a file was written.</summary>
    async Task<bool> SaveCopyAsync()
    {
        if (_pdf == null || _busy) return false;
        Viewer.CommitEdits();
        string name = Path.GetFileNameWithoutExtension(_path) ?? "document";
        var dlg = new SaveFileDialog
        {
            Filter = "PDF documents (*.pdf)|*.pdf",
            FileName = Viewer.Annotations.IsEmpty ? name + " (copy).pdf" : name + " (annotated).pdf",
            InitialDirectory = _path != null ? Path.GetDirectoryName(_path) : null,
            OverwritePrompt = true,
            Title = "Save a copy",
        };
        if (dlg.ShowDialog(this) != true) return false;

        var pdf = _pdf;
        var anns = Viewer.Annotations.Items.ToList();
        SetBusy(true, "Saving...");
        try
        {
            byte[] bytes = await Task.Run(() => anns.Count == 0 ? pdf.Data : PdfAnnotationWriter.Apply(pdf.Data, pdf.Password, anns));
            await File.WriteAllBytesAsync(dlg.FileName, bytes);
            Viewer.Annotations.MarkSaved();
            UpdateUi();
            return true;
        }
        catch (Exception ex)
        {
            SetBusy(false);
            string reason = ex.Message.Contains("owner password", StringComparison.OrdinalIgnoreCase)
                ? "This PDF is protected and does not allow changes without its owner password, so annotations cannot be written into it.\nYou can still print it with the annotations."
                : ex.Message;
            MessageBox.Show(this, "The file could not be saved.\n\n" + reason, "pPdf", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally { SetBusy(false); }
    }

    async void OnPrintClick(object sender, RoutedEventArgs e) => await PrintAsync();

    async Task PrintAsync()
    {
        if (_pdf == null || _busy) return;
        Viewer.CommitEdits();
        var dlg = new PrintDialog { MinPage = 1, MaxPage = (uint)_pdf.PageCount, UserPageRangeEnabled = true };
        if (dlg.ShowDialog() != true) return;

        int[] pages = dlg.PageRangeSelection == PageRangeSelection.UserPages
            ? Enumerable.Range(dlg.PageRange.PageFrom, dlg.PageRange.PageTo - dlg.PageRange.PageFrom + 1).Where(p => p >= 1 && p <= _pdf.PageCount).Select(p => p - 1).ToArray()
            : Enumerable.Range(0, _pdf.PageCount).ToArray();
        if (pages.Length == 0) return;

        var paginator = new PdfPaginator(_pdf, pages, Viewer.Annotations.Items.ToList(), Viewer.Rotation)
        {
            PageSize = new Size(dlg.PrintableAreaWidth, dlg.PrintableAreaHeight),
        };
        SetBusy(true, $"Printing {pages.Length} page" + (pages.Length == 1 ? "" : "s") + "...");
        try
        {
            // let the overlay paint before the (synchronous) spooling starts
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            dlg.PrintDocument(paginator, Path.GetFileName(_path) ?? "PDF");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Printing failed.\n\n" + ex.Message, "pPdf", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SetBusy(false); }
    }

    // ------------------------------------------------------------------ keyboard

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        bool ctrl = (mods & ModifierKeys.Control) != 0, shift = (mods & ModifierKeys.Shift) != 0, alt = (mods & ModifierKeys.Alt) != 0;
        bool typing = Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or ComboBox or PasswordBox;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (alt) return;

        if (ctrl)
        {
            switch (key)
            {
                case Key.O: _ = BrowseAndOpenAsync(); e.Handled = true; return;
                case Key.S when _pdf != null: _ = SaveCopyAsync(); e.Handled = true; return;
                case Key.P when _pdf != null: _ = PrintAsync(); e.Handled = true; return;
                case Key.F when _pdf != null: ShowFind(); e.Handled = true; return;
            }
            if (typing || _pdf == null) return;
            switch (key)
            {
                case Key.Z when !shift: Viewer.Annotations.Undo(); e.Handled = true; return;
                case Key.Y:
                case Key.Z when shift: Viewer.Annotations.Redo(); e.Handled = true; return;
                case Key.V: if (Viewer.PasteFromClipboard()) e.Handled = true; return;
                case Key.D0 or Key.NumPad0: Viewer.SetZoomMode(ZoomMode.FitPage); e.Handled = true; return;
                case Key.D1 or Key.NumPad1: Viewer.SetZoom(1.0); e.Handled = true; return;
                case Key.D2 or Key.NumPad2: Viewer.SetZoomMode(ZoomMode.FitWidth); e.Handled = true; return;
                case Key.OemPlus or Key.Add: Viewer.ZoomIn(); e.Handled = true; return;
                case Key.OemMinus or Key.Subtract: Viewer.ZoomOut(); e.Handled = true; return;
                case Key.R: Viewer.Rotation += shift ? -1 : 1; e.Handled = true; return;
            }
            return;
        }

        switch (key)
        {
            case Key.F3 when _pdf != null: FindStep(!shift); e.Handled = true; return;
            case Key.F4: ToggleSidebar(); e.Handled = true; return;
            case Key.Escape when FindBar.IsVisible && FindBar.IsKeyboardFocusWithin: HideFind(); e.Handled = true; return;
        }
        if (typing || _pdf == null || shift) return;
        switch (key)
        {
            case Key.T: Viewer.Tool = Viewer.Tool == ViewerTool.Text ? ViewerTool.Select : ViewerTool.Text; e.Handled = true; UpdateUi(); return;
            case Key.I: OnAddImage(this, new RoutedEventArgs()); e.Handled = true; return;
        }
    }
}
