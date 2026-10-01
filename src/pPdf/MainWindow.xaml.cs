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

    bool _busy;

    /// <summary>No document and nothing being opened: a new file can use this window.</summary>
    public bool IsEmpty => _pdf == null && !_busy;

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
        Viewer.ToolChanged += (_, _) => { UpdateAnnotationBar(); UpdateUi(); };
        Viewer.SelectedAnnotationChanged += (_, _) => { UpdateAnnotationBar(); UpdateUi(); };
        Viewer.SelectionChanged += (_, _) => UpdateUi();
        Viewer.HistoryChanged += (_, _) => UpdateUi();
        Viewer.Annotations.HistoryChanged += UpdateUi;
        InitAnnotationKeeping();

        InitMemoryTrim();
        InitPositionSaving();
        InitSearch();
        InitSidebar();
        BuildRecentList();
        UpdateUi();
        ScheduleStartupUpdateCheck();
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

        Viewer.MarkupColor = ParseColor(_settings.MarkupColor) ?? Viewer.MarkupColor;
        MarkupColorButton.Value = Viewer.MarkupColor;

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
        _settings.MarkupColor = ColorText(Viewer.MarkupColor);
        var d = Viewer.TextDefaults;
        _settings.TextFont = d.FontFamily;
        _settings.TextSize = d.FontSize;
        _settings.TextBold = d.Bold;
        _settings.TextItalic = d.Italic;
        _settings.TextColor = ColorText(d.Foreground);
        _settings.TextFill = d.Background is { } bg ? ColorText(bg) : null;

        _settings.Maximized = (_fullScreen ? _beforeFullScreen : WindowState) == WindowState.Maximized; // not "maximized" just because it was full screen
        var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (!b.IsEmpty) { _settings.WindowLeft = b.Left; _settings.WindowTop = b.Top; _settings.WindowWidth = b.Width; _settings.WindowHeight = b.Height; }
        RememberPosition();
        _settings.Save();
    }

    // ------------------------------------------------------------------ window events

    void OnLoaded(object sender, RoutedEventArgs e) => ThemeService.ApplyTitleBar(this);

    // ------------------------------------------------------------------ memory

    readonly System.Windows.Threading.DispatcherTimer _trimTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    bool _trimmed;

    void InitMemoryTrim()
    {
        _trimTimer.Tick += (_, _) =>
        {
            _trimTimer.Stop();
            if (WindowState != WindowState.Minimized) return;
            // minimized for a while: drop the bitmaps and the caches, and hand the freed memory back to the system
            _trimmed = true;
            Viewer.TrimMemory();
            MemoryTrimmer.Trim();
        };
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized)
        {
            _trimTimer.Stop();
            _trimTimer.Start();
            return;
        }
        _trimTimer.Stop();
        if (_trimmed)
        {
            _trimmed = false;
            Viewer.ResumeAfterTrim();
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeService.ApplyTitleBar(this);
    }

    void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // the annotations are kept for the next time: nothing to ask
        Viewer.CommitEdits();
        FlushAnnotations();
        // closing the view empties the store: that must not be saved over what was kept
        _keepSuspended = true;
        _keepTimer.Stop();
        SaveSettings();
        Viewer.Close();
        _pdf?.Dispose();
    }

    /// <summary>
    /// Before leaving a document: whatever is being typed is committed and the annotations are written to their place in %AppData%
    /// (they come back, editable, when the file is opened again). Always true: there is nothing to ask any more.
    /// </summary>
    Task<bool> ConfirmDiscardAnnotationsAsync()
    {
        Viewer.CommitEdits();
        FlushAnnotations();
        return Task.FromResult(true);
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
        _keepSuspended = true; // opening empties and refills the store: not a change made by the user
        Viewer.Open(pdf, _settings.FindPosition(path));
        _keepSuspended = false;
        RestoreKeptAnnotations(path, pdf);
        _ = LoadFormAsync(pdf);
        WelcomePanel.Visibility = Visibility.Collapsed;
        _settings.AddRecent(path);
        BuildRecentList();
        LoadSidebar(pdf);
        if (FindBar.Visibility == Visibility.Visible && FindBox.Text.Length > 0) RunSearch();
        UpdateUi();
        Viewer.Focus();
    }

    // a few seconds after the last movement the position is written to disk, so a crash or a kill does not lose it
    readonly System.Windows.Threading.DispatcherTimer _positionTimer = new() { Interval = TimeSpan.FromSeconds(8) };

    void InitPositionSaving()
    {
        _positionTimer.Tick += (_, _) =>
        {
            _positionTimer.Stop();
            if (_pdf == null) return;
            RememberPosition();
            _settings.Save();
        };
        Viewer.ViewMoved += (_, _) => { _positionTimer.Stop(); _positionTimer.Start(); };
    }

    /// <summary>Notes where the open document is (page, how far down, zoom, rotation) so it opens there next time.</summary>
    void RememberPosition()
    {
        if (_path == null || _pdf == null || Viewer.CapturePosition() is not { } position) return;
        position.Path = _path;
        _settings.AddRecent(_path);
        _settings.SavePosition(position);
    }

    void CloseDocument(bool saveRecent)
    {
        if (_pdf == null) return;
        if (saveRecent) RememberPosition();
        FlushAnnotations();
        ResetSearch();
        _keepSuspended = true; // closing the view empties the store: that is not "the user deleted everything"
        Viewer.Close();
        _keepSuspended = false;
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
            BackButton.IsEnabled = Viewer.CanGoBack;
            ForwardButton.IsEnabled = Viewer.CanGoForward;
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
            if (Viewer.Form is { } form) parts.Add(form.Fields.Count == 1 ? "form: 1 field" : $"form: {form.Fields.Count} fields");
            if (!Viewer.Annotations.IsEmpty) parts.Add($"{Viewer.Annotations.Items.Count} annotation" + (Viewer.Annotations.Items.Count == 1 ? "" : "s"));
            StatusRight.Text = string.Join("   ·   ", parts);

            SyncSidebarSelection();
        }
        finally { _syncing = false; }
    }

    // ------------------------------------------------------------------ toolbar: navigation and zoom

    void OnBack(object sender, RoutedEventArgs e) => Viewer.GoBack();
    void OnForward(object sender, RoutedEventArgs e) => Viewer.GoForward();

    // the extra mouse buttons (browser style) go back and forward too
    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        if (_pdf == null || e.Handled) return;
        if (e.ChangedButton == MouseButton.XButton1) { Viewer.GoBack(); e.Handled = true; }
        else if (e.ChangedButton == MouseButton.XButton2) { Viewer.GoForward(); e.Handled = true; }
    }

    void OnPrevPage(object sender, RoutedEventArgs e) => Viewer.PreviousPage();
    void OnNextPage(object sender, RoutedEventArgs e) => Viewer.NextPage();

    void OnPageBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (int.TryParse(PageBox.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int n)) Viewer.JumpToPage(n - 1);
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
        var menu = NewMenu(LayoutButton);
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

    /// <summary>
    /// A drop-down menu under a toolbar button. A ContextMenu inherits the font of its PlacementTarget, and the toolbar
    /// buttons use the icon font: without setting the text font here every item would be drawn as empty squares.
    /// </summary>
    ContextMenu NewMenu(FrameworkElement target) => new()
    {
        PlacementTarget = target,
        Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        FontFamily = FontFamily,
        FontSize = FontSize,
        FontWeight = FontWeights.Normal,
        FontStyle = FontStyles.Normal,
    };

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
        var menu = NewMenu(ThemeButton);
        void Theme(string header, AppTheme t) => menu.Items.Add(CheckItem(header, _settings.Theme == t, () => SetTheme(t), radio: true));
        Theme("Follow system", AppTheme.System);
        Theme("Light", AppTheme.Light);
        Theme("Dark", AppTheme.Dark);
        menu.Items.Add(new Separator());
        menu.Items.Add(CheckItem("Night mode for the pages (keeps colors)", Viewer.InvertColors, () => Viewer.InvertColors = !Viewer.InvertColors));
        menu.IsOpen = true;
    }

    void SetTheme(AppTheme t)
    {
        _settings.Theme = t;
        ThemeService.Apply(t);
    }

    void OnMoreClick(object sender, RoutedEventArgs e)
    {
        var menu = NewMenu(MoreButton);
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
        var export = new MenuItem { Header = "Export this page as an image...", IsEnabled = _pdf != null };
        export.Click += (_, _) => _ = ExportPageImageAsync();
        menu.Items.Add(export);
        menu.Items.Add(new Separator());
        MenuItem Doc(string header, Action action) { var mi = new MenuItem { Header = header, IsEnabled = _pdf != null }; mi.Click += (_, _) => action(); return mi; }
        menu.Items.Add(Doc("Document properties...", ShowProperties));
        menu.Items.Add(Doc("Show in folder", ShowInFolder));
        menu.Items.Add(Doc("Copy file path", CopyPath));
        menu.Items.Add(new Separator());
        var newWindow = new MenuItem { Header = "New window", InputGestureText = "Ctrl+N" };
        newWindow.Click += (_, _) => ((App)Application.Current).NewWindow();
        menu.Items.Insert(0, newWindow);
        var check = new MenuItem { Header = "Check for updates..." };
        check.Click += (_, _) => _ = CheckForUpdatesAsync(manual: true);
        menu.Items.Add(check);
        var auto = new MenuItem { Header = "Check for updates automatically", IsCheckable = true, IsChecked = _settings.CheckForUpdates, StaysOpenOnClick = false };
        auto.Click += (_, _) => _settings.CheckForUpdates = auto.IsChecked;
        menu.Items.Add(auto);
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
            "Ctrl+G  Go to page   Alt+Left / Alt+Right (or the mouse back / forward buttons)  Back / forward\nF11  Full screen\n" +
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

        if (_pdf.IsOnDemand && string.Equals(Path.GetFullPath(dlg.FileName), Path.GetFullPath(_path!), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "This is a very large file: pPdf reads it from disk while it is open, so it cannot be overwritten. Save the copy with another name.",
                "pPdf", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        var pdf = _pdf;
        var anns = Viewer.Annotations.Items.ToList();
        var formValues = Viewer.Form?.ChangedValues() ?? [];
        bool overwroteOpenFile = false;
        SetBusy(true, "Saving...");
        try
        {
            byte[] bytes = await Task.Run(() => anns.Count == 0 && formValues.Count == 0 ? pdf.GetBytes() : PdfAnnotationWriter.Apply(pdf.GetBytes(), pdf.Password, anns, formValues));
            await File.WriteAllBytesAsync(dlg.FileName, bytes);
            Viewer.Annotations.MarkSaved();
            UpdateUi();
            overwroteOpenFile = string.Equals(Path.GetFullPath(dlg.FileName), Path.GetFullPath(_path!), StringComparison.OrdinalIgnoreCase);
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

        if (overwroteOpenFile && _path is { } saved)
        {
            // the annotations are now part of the file itself: the kept ones would be drawn a second time. Open the new file instead.
            _keepTimer.Stop();
            _keepChanged = false;
            AnnotationPersistence.Default.Delete(saved);
            Viewer.Annotations.Clear();
            await OpenAsync(saved);
        }
        return true;
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

        var paginator = new PdfPaginator(_pdf, pages, Viewer.Annotations.Items.ToList(), Viewer.Rotation, Viewer.Form)
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
        if (alt)
        {
            if (_pdf != null && !ctrl && !shift && !typing)
            {
                if (key == Key.Left) { Viewer.GoBack(); e.Handled = true; }
                else if (key == Key.Right) { Viewer.GoForward(); e.Handled = true; }
            }
            return;
        }

        if (ctrl)
        {
            switch (key)
            {
                case Key.G when _pdf != null: PageBox.Focus(); PageBox.SelectAll(); e.Handled = true; return;
                case Key.O: _ = BrowseAndOpenAsync(); e.Handled = true; return;
                case Key.N: ((App)Application.Current).NewWindow(); e.Handled = true; return;
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
            case Key.F11: ToggleFullScreen(); e.Handled = true; return;
            case Key.Escape when _fullScreen && !typing && Viewer.SelectedAnnotation == null && Viewer.Tool == ViewerTool.Select && !Viewer.HasSelection:
                ToggleFullScreen(); e.Handled = true; return;
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
