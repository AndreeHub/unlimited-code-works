using ReviewScope.App.ViewModels;
using ReviewScope.Canvas;
using ReviewScope.Domain;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ReviewScope.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _vm;
    private TextBox? _activeSessionNameEditor;
    private bool _isCompletingSessionRename;

    // Code panel dock state (phase: dockable/floating code tools).
    private enum CodeDock { Closed, Right, Left, Floating }
    private CodeDock _codeDock = CodeDock.Closed;
    private CodeDock _lastCodeDock = CodeDock.Right;
    private Controls.CodeToolsPanel? _codePanel;
    private Window? _codeFloatWindow;

    public MainWindow(MainWindowViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        // Wire canvas events
        CanvasViewport.BlockActivatedCommand = new RelayCommand<BlockActivatedArgs>(OnBlockActivated);
        CanvasViewport.ExtractRequestedCommand = new RelayCommand<ExtractRequestedArgs>(OnExtractRequested);
        CanvasViewport.FocusRequestedCommand = new RelayCommand<FocusRequestedArgs>(OnFocusRequested);
        CanvasViewport.RestoreRequestedCommand = new RelayCommand<RestoreRequestedArgs>(OnRestoreRequested);
        CanvasViewport.ConnectionDrawnCommand = new RelayCommand<ConnectionDrawnArgs>(OnConnectionDrawn);
        CanvasViewport.AnnotationRequestedCommand = new RelayCommand<AnnotationRequestedArgs>(OnAnnotationRequested);
        CanvasViewport.CopyRequestedCommand = new RelayCommand<object>(_ => _vm.CopySelectedBoardItemsCommand.Execute(null));
        CanvasViewport.PasteRequestedCommand = new RelayCommand<PasteRequestedArgs>(OnCanvasPasteRequested);

        // When scene is mutated inside the canvas (drag, delete, resize), sync back
        CanvasViewport.BlockMovedCommand = new RelayCommand<CanvasSceneChangedArgs>(OnSceneChangedByCanvas);

        // Reading-progress: a gutter drag in a code block toggles reviewed lines.
        CanvasViewport.ReviewLinesToggledCommand = new RelayCommand<ReviewLinesToggledArgs>(OnReviewLinesToggled);

        // After a Text/Note is added (toolbox button, canvas placement, etc), drop
        // straight into in-canvas edit so the user can type immediately.
        vm.PostCreateEditRequested += OnPostCreateEditRequested;

        // When edit starts, surface the most useful right-side tab: Text-tab for text
        // cards (typography/spacing/alignment), Inspector-tab for notes (their body
        // + sticky color live there).
        CanvasViewport.EditStarted += OnCanvasEditStarted;
        CanvasViewport.PagePortalEdited += (pageName, body) => _ = _vm.OnPagePortalEditedAsync(pageName, body);

        // Feed inline autocomplete (#tag, [[wiki link]]) from the per-workspace
        // TagIndex held by the view-model.
        CanvasViewport.AutocompleteSuggestionsProvider = vm.GetAutocompleteSuggestions;

        // Reading-progress overlay: resolve a code block's reviewed line spans live from the
        // workspace-scoped store. Returns None when no workspace is open or the file is untracked.
        CanvasViewport.ReviewedRangeResolver = filePath =>
        {
            string? key = vm.CurrentWorkspaceKey;
            if (key is null || string.IsNullOrEmpty(filePath)) return ReviewedFileState.None;
            // GetFileState also flags staleness (file changed since lines were marked) → amber overlay.
            return vm.ReviewProgress.GetFileState(key, filePath);
        };

        // The code search/extraction tools live in a re-parentable panel (docked right/left or
        // floating). Created once; DataContext pinned to the VM so bindings survive re-parenting.
        _codePanel = new Controls.CodeToolsPanel { DataContext = vm };
        _codePanel.DockLeftRequested += () => PlaceCodePanel(CodeDock.Left);
        _codePanel.DockRightRequested += () => PlaceCodePanel(CodeDock.Right);
        _codePanel.FloatRequested += () => PlaceCodePanel(CodeDock.Floating);
        _codePanel.CloseRequested += () => PlaceCodePanel(CodeDock.Closed);
        _codePanel.FileActivated += OnPanelFileActivated;
        _codePanel.BoardSearchActivated += OnPanelBoardSearchActivated;

        // Outline editor (Page/Journal) <-> view-model wiring.
        vm.OutlineDocumentReloadRequested += OnOutlineReloadRequested;
        OutlineEditor.DocumentChanged += body => _vm.OnOutlineBodyEdited(body);
        OutlineEditor.CollapsedChanged += ids => _vm.OnOutlineCollapsedChanged(ids);
        OutlineEditor.PageLinkActivated += name => _ = _vm.NavigateToDocumentAsync(name);

        // Mode-aware chrome: collapse the right inspector panel and swap the left tab set
        // when the active document switches between Canvas and Outline.
        _vm.PropertyChanged += OnVmPropertyChanged;
        Loaded += (_, _) => UpdateModeChrome();
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsCanvasDocumentActive)
            || e.PropertyName == nameof(MainWindowViewModel.IsOutlineDocumentActive)
            || e.PropertyName == nameof(MainWindowViewModel.IsSplitViewActive))
            UpdateModeChrome();
    }

    /// <summary>
    /// Canvas mode shows Explorer/Symbols/Search on the left and the Inspector + Overview on
    /// the right. Outline mode swaps the left tabs to Notes/Graph/Search and hides the right
    /// panel entirely so the writing column gets the full width.
    /// </summary>
    private void UpdateModeChrome()
    {
        bool canvas = _vm.IsCanvasDocumentActive;
        bool codeRight = _codeDock == CodeDock.Right;
        bool codeLeft = _codeDock == CodeDock.Left;

        // Inspector shows in canvas mode unless the user collapsed it or the Code panel owns the
        // right dock. The right dock column is visible when either occupant is shown.
        bool showInspector = canvas && !_rightUserCollapsed && !codeRight;
        bool showRightDock = showInspector || codeRight;

        RightPanelCol.Width = showRightDock ? new GridLength(320) : new GridLength(0);
        RightPanelCol.MinWidth = showRightDock ? 220 : 0;
        RightSplitterCol.Width = showRightDock ? new GridLength(5) : new GridLength(0);
        RightSplitter.Visibility = showRightDock ? Visibility.Visible : Visibility.Collapsed;
        RightSidebar.Visibility = showInspector ? Visibility.Visible : Visibility.Collapsed;
        CodeRightHost.Visibility = codeRight ? Visibility.Visible : Visibility.Collapsed;
        RightPanelToggle.IsEnabled = canvas && !codeRight;

        // Code panel dock-left column.
        CodeLeftCol.Width = codeLeft ? new GridLength(300) : new GridLength(0);
        CodeLeftCol.MinWidth = codeLeft ? 220 : 0;
        CodeLeftSplitterCol.Width = codeLeft ? new GridLength(5) : new GridLength(0);
        CodeLeftSplitter.Visibility = codeLeft ? Visibility.Visible : Visibility.Collapsed;

        // Reflect open state on the toolbar Code button.
        CodePanelToggle.Background = _codeDock == CodeDock.Closed
            ? System.Windows.Media.Brushes.Transparent
            : (System.Windows.Media.Brush)FindResource("AccentSoft");

        // Center area: canvas | splitter | writing. In split view both panes share the width with a
        // draggable splitter; otherwise the active mode's pane takes the whole cell.
        bool split = _vm.IsSplitViewActive;
        if (split)
        {
            SplitCanvasCol.Width = new GridLength(1, GridUnitType.Star);
            SplitSplitterCol.Width = new GridLength(5);
            SplitOutlineCol.Width = new GridLength(1, GridUnitType.Star);
            SplitSplitter.Visibility = Visibility.Visible;
        }
        else
        {
            bool outline = _vm.IsOutlineDocumentActive;
            SplitCanvasCol.Width = outline ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            SplitOutlineCol.Width = outline ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            SplitSplitterCol.Width = new GridLength(0);
            SplitSplitter.Visibility = Visibility.Collapsed;
        }
    }

    // -----------------------------------------------------------------------
    // Code panel: dock right/left, float, close
    // -----------------------------------------------------------------------
    private void OnToggleCodePanel(object sender, RoutedEventArgs e)
    {
        if (_codeDock == CodeDock.Closed)
            PlaceCodePanel(_lastCodeDock == CodeDock.Closed ? CodeDock.Right : _lastCodeDock);
        else
            PlaceCodePanel(CodeDock.Closed);
    }

    private void PlaceCodePanel(CodeDock dock)
    {
        if (_codePanel is null) return;

        DetachCodePanel();
        _codeDock = dock;
        if (dock is CodeDock.Right or CodeDock.Left) _lastCodeDock = dock;

        switch (dock)
        {
            case CodeDock.Right: CodeRightHost.Content = _codePanel; break;
            case CodeDock.Left: CodeLeftHost.Content = _codePanel; break;
            case CodeDock.Floating: ShowCodeFloatWindow(); break;
            case CodeDock.Closed: break;
        }
        UpdateModeChrome();
    }

    /// <summary>Remove the panel from whatever currently hosts it, without firing the float-closed
    /// handler (so re-docking doesn't recurse).</summary>
    private void DetachCodePanel()
    {
        if (CodeRightHost.Content == _codePanel) CodeRightHost.Content = null;
        if (CodeLeftHost.Content == _codePanel) CodeLeftHost.Content = null;
        if (_codeFloatWindow is not null)
        {
            var w = _codeFloatWindow;
            _codeFloatWindow = null;
            w.Closed -= OnCodeFloatClosed;
            w.Content = null;
            w.Close();
        }
    }

    private void ShowCodeFloatWindow()
    {
        _codeFloatWindow = new Window
        {
            Title = "Code — ReviewScope",
            Width = 360,
            Height = 620,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = _codePanel,
            Background = (System.Windows.Media.Brush)FindResource("PanelBg"),
        };
        _codeFloatWindow.Closed += OnCodeFloatClosed;
        _codeFloatWindow.Show();
    }

    private void OnCodeFloatClosed(object? sender, EventArgs e)
    {
        if (_codeFloatWindow is not null) { _codeFloatWindow.Content = null; _codeFloatWindow = null; }
        if (_codeDock == CodeDock.Floating)
        {
            _codeDock = CodeDock.Closed;
            UpdateModeChrome();
        }
    }

    // Re-homed from the old inline handlers (need host helpers / canvas access).
    private async void OnPanelFileActivated(FileExplorerItemViewModel item)
    {
        if (!item.IsFile || item.FilePath is null) return;
        if (IsImageFile(item.FilePath)) { await _vm.AddImageFileToCanvasAsync(item.FilePath); return; }
        if (IsTextLikeBoardFile(item.FilePath))
        {
            await _vm.LoadSymbolsForFileAsync(item.FilePath);
            await _vm.AddFileToCanvasAsync(item.FilePath);
        }
    }

    private async void OnPanelBoardSearchActivated(BoardSearchResultViewModel result)
    {
        await _vm.NavigateBoardSearchResultAsync(result.Key);
        CanvasViewport.Scene = _vm.Scene;
        CanvasViewport.FrameSelection();
    }

    private void OnNavFilterChanged(object sender, TextChangedEventArgs e)
    {
        string q = ProjectNavFilter.Text?.Trim() ?? string.Empty;
        _vm.RefreshProjectBrowser(q);
    }

    // -----------------------------------------------------------------------
    // Collapsible panels (phase 4)
    // -----------------------------------------------------------------------
    private bool _leftCollapsed;
    private bool _rightUserCollapsed;
    private const double LeftPanelWidth = 264;
    private const double LeftRailWidth = 44;

    private void OnToggleLeftPanel(object sender, RoutedEventArgs e)
    {
        _leftCollapsed = !_leftCollapsed;
        ApplyLeftPanelState();
    }

    private void ApplyLeftPanelState()
    {
        LeftPanelCol.Width = _leftCollapsed ? new GridLength(LeftRailWidth) : new GridLength(LeftPanelWidth);
        LeftPanelCol.MinWidth = _leftCollapsed ? LeftRailWidth : 160;
        LeftPanelFull.Visibility = _leftCollapsed ? Visibility.Collapsed : Visibility.Visible;
        LeftRail.Visibility = _leftCollapsed ? Visibility.Visible : Visibility.Collapsed;
        LeftSplitter.Visibility = _leftCollapsed ? Visibility.Collapsed : Visibility.Visible;
        LeftSplitterCol.Width = _leftCollapsed ? new GridLength(0) : new GridLength(5);
        LeftPanelToggle.IsEnabled = true;
    }

    private void OnToggleRightPanel(object sender, RoutedEventArgs e)
    {
        if (!_vm.IsCanvasDocumentActive) return;
        _rightUserCollapsed = !_rightUserCollapsed;
        UpdateModeChrome();
    }

    private void OnOutlineReloadRequested()
    {
        OutlineEditor.Document = _vm.OutlineDocumentBody;
        OutlineEditor.SetCollapsed(SplitCollapsed(_vm.OutlineDocumentCollapsed));
    }

    private async void OnOutlineTitleKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return || e.Key == Key.Enter)
        {
            e.Handled = true;
            await _vm.CommitOutlineTitleAsync();
            OutlineEditor.FocusEditor();   // drop into the body, Logseq-style
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            OutlineEditor.FocusEditor();
        }
    }

    private async void OnOutlineTitleLostFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        await _vm.CommitOutlineTitleAsync();

    private static IEnumerable<string> SplitCollapsed(string? s) =>
        string.IsNullOrEmpty(s)
            ? Array.Empty<string>()
            : s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private async void OnProjectDocSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        // While the pointer is pressed on an item we defer "open" to the mouse-up handler, so a
        // press that turns into a drag-to-canvas never opens the page. Keyboard / programmatic
        // selection changes (no pointer down) still open immediately.
        if (_docPointerDown) return;
        if (IsLoaded && e.NewValue is ProjectBrowserItemViewModel { Session: not null } item)
        {
            _vm.SelectedSession = item.Session;
            await _vm.ActivateSelectedSessionAsync();
        }
    }

    // -------- Project browser: drag a page onto the canvas as a page portal --------
    // Private in-process clipboard format carrying the dragged page's name.
    private const string PageDragFormat = "ReviewScope.PageName";
    private Point _docDragStart;
    private ReviewSession? _docDragSession;
    private bool _docPointerDown;
    private bool _docDragging;

    private void OnDocItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Let double-click (rename) flow through untouched.
        if (e.ClickCount >= 2) { _docPointerDown = false; return; }
        _docDragStart = e.GetPosition(this);
        _docDragSession = GetSessionFromDataContext((sender as FrameworkElement)?.DataContext);
        _docPointerDown = _docDragSession is not null;
        _docDragging = false;
    }

    private void OnDocItemMouseMove(object sender, MouseEventArgs e)
    {
        if (!_docPointerDown || _docDragging || e.LeftButton != MouseButtonState.Pressed || _docDragSession is null)
            return;

        Point pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _docDragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(pos.Y - _docDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        // Canvases can't be embedded as a page portal — only Pages / Journals.
        if (_docDragSession.Kind == DocumentKind.Canvas) { _docPointerDown = false; return; }

        _docDragging = true;
        var session = _docDragSession;
        try
        {
            var data = new DataObject(PageDragFormat, session.Name);
            DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
        }
        finally
        {
            _docPointerDown = false;
            _docDragging = false;
            _docDragSession = null;
        }
    }

    private async void OnDocItemMouseUp(object sender, MouseButtonEventArgs e)
    {
        // A plain click (no drag) opens the page — the open we deferred from SelectionChanged.
        bool wasClick = _docPointerDown && !_docDragging && _docDragSession is not null;
        var session = _docDragSession;
        _docPointerDown = false;
        _docDragging = false;
        _docDragSession = null;
        if (wasClick && IsLoaded)
        {
            _vm.SelectedSession = session;
            await _vm.ActivateSelectedSessionAsync();
        }
    }

    private void OnCanvasEditStarted(Domain.BlockKind kind)
    {
        // Both Text cards and Sticky Notes now share the Text tab (typography,
        // alignment, spacing), so flip there on edit for either kind.
        const int TextTab = 2;
        if (kind is Domain.BlockKind.Text or Domain.BlockKind.Note)
            _vm.SelectedRightTabIndex = TextTab;
    }

    private void OnPostCreateEditRequested(Domain.BlockKind kind)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (kind == Domain.BlockKind.Text)
                CanvasViewport.BeginEditLastBlockOfKind(Domain.BlockKind.Text);
            else if (kind == Domain.BlockKind.Note)
                CanvasViewport.BeginEditLastBlockOfKind(Domain.BlockKind.Note);
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Command palette is global (works in any mode / focus, including text fields).
        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            ToggleCommandPalette();
            return;
        }
        if (e.Key == Key.Escape && CommandPalettePopup.IsOpen)
        {
            e.Handled = true;
            CloseCommandPalette();
            return;
        }

        if (IsTextInputSource(e.OriginalSource as DependencyObject))
            return;

        // When a Page/Journal outline is the active document, ALL keystrokes belong to the
        // OutlineView editor (its own HwndHost handles Tab/arrows/typing). Bail out before the
        // canvas tool shortcuts below, which would otherwise consume single letters like
        // q/w/e/t/v (the canvas tool keys) and stop them ever reaching the outline.
        if (_vm.IsOutlineDocumentActive)
            return;

        // While editing a Text/Note/group title in-canvas, navigation keys (Tab and
        // arrows) would normally be eaten by WPF focus traversal / directional
        // navigation before they ever reach the canvas HWND. Forward them directly
        // to the canvas's edit handler and mark e.Handled so WPF leaves them alone.
        if (CanvasViewport.IsEditingInCanvas)
        {
            bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            if (!ctrl)
            {
                if (e.Key is Key.Tab or Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End)
                {
                    CanvasViewport.ForwardEditKey(e.Key);
                    e.Handled = true;
                    return;
                }
            }
            else if (e.Key is Key.A or Key.C or Key.X or Key.V or Key.B or Key.I or Key.E
                     || (shift && e.Key == Key.S))
            {
                CanvasViewport.ForwardEditKey(e.Key);
                e.Handled = true;
                return;
            }

            // All other in-edit keystrokes (typed characters, Ctrl+B/I/E for inline
            // markdown, Ctrl+C / Ctrl+X / Ctrl+V for text-selection clipboard, etc.)
            // belong to the canvas's edit handler. Bail out without firing the global
            // tool shortcuts / board-level copy.
            return;
        }

        if (CanvasViewport.ActivateBottomToolShortcut(e.Key, Keyboard.Modifiers))
        {
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.C)
        {
            _vm.CopySelectedBoardItemsCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Delete)
            return;

        // Delete is also part of normal text editing; let the canvas's edit
        // handler delete the char under the cursor.
        if (CanvasViewport.IsEditingInCanvas)
            return;

        CanvasViewport.DeleteSelection();
        e.Handled = true;
    }

    private void OnSessionDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (GetSessionFromSource(source) is not ReviewSession session)
            return;

        BeginSessionRename(session, source);
        e.Handled = true;
    }

    private void OnSessionNameEditKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox editor)
            return;

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CompleteSessionRenameAsync(editor, commit: true);
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CompleteSessionRenameAsync(editor, commit: false);
        }
    }

    private void OnSessionNameEditLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox editor)
            CompleteSessionRenameAsync(editor, commit: true);
    }

    private void BeginSessionRename(ReviewSession session, DependencyObject? source)
    {
        if (_activeSessionNameEditor is not null)
            CompleteSessionRenameAsync(_activeSessionNameEditor, commit: true);

        _vm.SelectedSession = session;
        var editor = FindSessionTemplateChild<TextBox>(source, "SessionNameEditor");
        var label = FindSessionTemplateChild<TextBlock>(source, "SessionNameText");
        if (editor is null || label is null)
            return;

        label.Visibility = Visibility.Collapsed;
        editor.Text = session.Name;
        editor.Visibility = Visibility.Visible;
        _activeSessionNameEditor = editor;
        editor.Dispatcher.BeginInvoke(() =>
        {
            editor.Focus();
            editor.SelectAll();
        });
    }

    private async void CompleteSessionRenameAsync(TextBox editor, bool commit)
    {
        if (_isCompletingSessionRename)
            return;

        _isCompletingSessionRename = true;
        try
        {
            if (GetSessionFromDataContext(editor.DataContext) is not ReviewSession session)
                return;

            var label = FindSessionTemplateChild<TextBlock>(editor, "SessionNameText");
            editor.Visibility = Visibility.Collapsed;
            if (label is not null)
                label.Visibility = Visibility.Visible;

            if (ReferenceEquals(_activeSessionNameEditor, editor))
                _activeSessionNameEditor = null;

            if (commit)
                await _vm.RenameSessionAsync(session, editor.Text);
            else
                editor.Text = session.Name;
        }
        finally
        {
            _isCompletingSessionRename = false;
        }
    }

    // Rename now happens in the Project Navigation tree.
    private static T? FindSessionTemplateChild<T>(DependencyObject? source, string name) where T : FrameworkElement
    {
        var item = FindVisualAncestor<TreeViewItem>(source);
        if (item is null)
            return null;

        item.ApplyTemplate();
        var presenter = FindVisualChild<ContentPresenter>(item);
        presenter?.ApplyTemplate();
        return presenter?.ContentTemplate?.FindName(name, presenter) as T;
    }

    private static ReviewSession? GetSessionFromSource(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement element && GetSessionFromDataContext(element.DataContext) is { } session)
                return session;

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private static ReviewSession? GetSessionFromDataContext(object? dataContext) => dataContext switch
    {
        ReviewSession session => session,
        ProjectBrowserItemViewModel { Session: not null } item => item.Session,
        _ => null
    };

    private static T? FindVisualAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T typed)
                return typed;

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private static bool IsTextInputSource(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is TextBox)
                return true;

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed)
                return typed;

            var nested = FindVisualChild<T>(child);
            if (nested is not null)
                return nested;
        }

        return null;
    }

    private async void OnBoardSearchDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.ListBox { SelectedItem: BoardSearchResultViewModel result })
        {
            await _vm.NavigateBoardSearchResultAsync(result.Key);
            CanvasViewport.Scene = _vm.Scene;
            CanvasViewport.FrameSelection();
        }
    }

    private async void OnPasteScreenshotAtMouse(object sender, RoutedEventArgs e)
    {
        Point world = CanvasViewport.GetLastMouseWorldPoint();
        await _vm.PasteImageFromClipboardAtAsync(world.X, world.Y);
    }

    private async void OnPasteAtMouse(object sender, RoutedEventArgs e)
    {
        if (Clipboard.ContainsImage())
        {
            Point world = CanvasViewport.GetLastMouseWorldPoint();
            await _vm.PasteImageFromClipboardAtAsync(world.X, world.Y);
            return;
        }

        Point pasteWorld = CanvasViewport.GetLastMouseWorldPoint();
        await _vm.PasteBoardItemsAtAsync(pasteWorld.X, pasteWorld.Y);
    }

    private async void OnCanvasPasteRequested(PasteRequestedArgs? args)
    {
        if (args is null) return;
        if (Clipboard.ContainsImage())
        {
            await _vm.PasteImageFromClipboardAtAsync(args.WorldX, args.WorldY);
            return;
        }

        if (Clipboard.ContainsText() && LooksLikeMarkdown(Clipboard.GetText()))
        {
            if (await _vm.PasteClipboardTextIntoSelectedEditableBlockAsync())
                return;

            await _vm.PasteMarkdownDocAtAsync(args.WorldX, args.WorldY);
            return;
        }

        await _vm.PasteBoardItemsAtAsync(args.WorldX, args.WorldY);
    }

    private void OnFrameAll(object sender, RoutedEventArgs e) => CanvasViewport.FrameAll();

    private void OnToggleTheme(object sender, RoutedEventArgs e) => Theming.ThemeManager.Toggle();

    // ----- View settings popover -----
    private bool _syncingViewSettings;

    private void OnViewSettingsOpened(object sender, RoutedEventArgs e)
    {
        // Reflect current state into the switches without re-triggering their handlers.
        _syncingViewSettings = true;
        DarkModeSwitch.IsChecked = Theming.ThemeManager.IsDark;
        LineGridSwitch.IsChecked = _vm.BackgroundMode == CanvasBackgroundMode.Grid;
        SleekShapesSwitch.IsChecked = _vm.GlobalShapeStyle == ShapeRenderStyle.Vector;
        _syncingViewSettings = false;
    }

    private void OnDarkModeToggled(object sender, RoutedEventArgs e)
    {
        if (_syncingViewSettings) return;
        Theming.ThemeManager.Apply(DarkModeSwitch.IsChecked == true);
    }

    private void OnLineGridToggled(object sender, RoutedEventArgs e)
    {
        if (_syncingViewSettings) return;
        _vm.BackgroundMode = LineGridSwitch.IsChecked == true
            ? CanvasBackgroundMode.Grid
            : CanvasBackgroundMode.Dots;
    }

    private void OnSleekShapesToggled(object sender, RoutedEventArgs e)
    {
        if (_syncingViewSettings) return;
        _vm.GlobalShapeStyle = SleekShapesSwitch.IsChecked == true
            ? ShapeRenderStyle.Vector
            : ShapeRenderStyle.Sketch;
    }

    // The header mode toggle derives from the active document and, when clicked, switches to
    // the most-recent document of the chosen mode (creating one if none exists yet).
    private async void OnSelectCanvasMode(object sender, RoutedEventArgs e)
    {
        if (_vm.IsCanvasDocumentActive) return;
        var target = _vm.Sessions.LastOrDefault(s => s.Kind == DocumentKind.Canvas);
        if (target is null) { _vm.CreateNewSessionCommand.Execute(null); return; }
        _vm.SelectedSession = target;
        await _vm.ActivateSelectedSessionAsync();
    }

    private async void OnSelectOutlineMode(object sender, RoutedEventArgs e)
    {
        if (_vm.IsOutlineDocumentActive) return;
        var target = _vm.Sessions.LastOrDefault(s => s.Kind != DocumentKind.Canvas);
        if (target is null) { _vm.OpenTodayJournalCommand.Execute(null); return; }
        _vm.SelectedSession = target;
        await _vm.ActivateSelectedSessionAsync();
    }

    // -----------------------------------------------------------------------
    // Command palette (Ctrl+K) — phase 7
    // -----------------------------------------------------------------------
    private sealed class PaletteItem
    {
        public string Title { get; init; } = "";
        public string Group { get; init; } = "";
        public string Shortcut { get; init; } = "";
        public Action Run { get; init; } = () => { };
    }

    private readonly System.Collections.ObjectModel.ObservableCollection<PaletteItem> _paletteItems = new();
    private List<PaletteItem> _allPaletteItems = new();

    private void OnOpenCommandPalette(object sender, RoutedEventArgs e) => OpenCommandPalette();

    private void ToggleCommandPalette()
    {
        if (CommandPalettePopup.IsOpen) CloseCommandPalette();
        else OpenCommandPalette();
    }

    private void OpenCommandPalette()
    {
        BuildPaletteItems();
        if (PaletteList.ItemsSource is null)
        {
            var cvs = new System.Windows.Data.CollectionViewSource { Source = _paletteItems };
            cvs.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(PaletteItem.Group)));
            PaletteList.ItemsSource = cvs.View;
        }
        PaletteInput.Text = "";
        FilterPalette("");
        CommandPalettePopup.IsOpen = true;
        // Focus the input once the popup HWND is realized.
        Dispatcher.BeginInvoke(new Action(() => { PaletteInput.Focus(); Keyboard.Focus(PaletteInput); }),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    private void CloseCommandPalette() => CommandPalettePopup.IsOpen = false;

    private void Exec(System.Windows.Input.ICommand cmd)
    {
        if (cmd.CanExecute(null)) cmd.Execute(null);
    }

    private void BuildPaletteItems()
    {
        _allPaletteItems = new List<PaletteItem>();

        foreach (var s in _vm.Sessions)
        {
            var doc = s;
            _allPaletteItems.Add(new PaletteItem
            {
                Title = s.Name,
                Group = "Jump to",
                Shortcut = s.Kind.ToString(),
                Run = async () => { _vm.SelectedSession = doc; await _vm.ActivateSelectedSessionAsync(); }
            });
        }

        _allPaletteItems.Add(new PaletteItem { Title = "Switch to Canvas", Group = "Commands", Run = () => OnSelectCanvasMode(this, new RoutedEventArgs()) });
        _allPaletteItems.Add(new PaletteItem { Title = "Switch to Outline", Group = "Commands", Run = () => OnSelectOutlineMode(this, new RoutedEventArgs()) });
        _allPaletteItems.Add(new PaletteItem { Title = "Toggle split view (canvas + writing)", Group = "Commands", Run = () => Exec(_vm.ToggleSplitViewCommand) });
        _allPaletteItems.Add(new PaletteItem { Title = "Toggle dark mode", Group = "Commands", Run = () => Theming.ThemeManager.Toggle() });
        _allPaletteItems.Add(new PaletteItem { Title = "Toggle grid (dots / lines)", Group = "Commands", Run = () => Exec(_vm.ToggleBackgroundCommand) });
        _allPaletteItems.Add(new PaletteItem { Title = "Frame all", Group = "Commands", Run = () => CanvasViewport.FrameAll() });
        _allPaletteItems.Add(new PaletteItem { Title = "Paste Mermaid diagram", Group = "Commands", Shortcut = "Ctrl+V", Run = () => Exec(_vm.PasteMermaidDiagramCommand) });

        _allPaletteItems.Add(new PaletteItem { Title = "New page", Group = "Create", Run = () => Exec(_vm.CreateNewPageCommand) });
        _allPaletteItems.Add(new PaletteItem { Title = "New board", Group = "Create", Run = () => Exec(_vm.CreateNewSessionCommand) });
        _allPaletteItems.Add(new PaletteItem { Title = "Today's journal", Group = "Create", Run = () => Exec(_vm.OpenTodayJournalCommand) });
    }

    private void FilterPalette(string query)
    {
        _paletteItems.Clear();
        foreach (var it in _allPaletteItems)
        {
            if (string.IsNullOrWhiteSpace(query)
                || it.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || it.Group.Contains(query, StringComparison.OrdinalIgnoreCase))
                _paletteItems.Add(it);
        }
        if (PaletteList.Items.Count > 0) PaletteList.SelectedIndex = 0;
    }

    private void OnPaletteInputChanged(object sender, TextChangedEventArgs e) => FilterPalette(PaletteInput.Text);

    private void OnPaletteInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control) { CloseCommandPalette(); e.Handled = true; return; }
        switch (e.Key)
        {
            case Key.Down:
                if (PaletteList.Items.Count > 0)
                    PaletteList.SelectedIndex = Math.Min(PaletteList.SelectedIndex + 1, PaletteList.Items.Count - 1);
                PaletteList.ScrollIntoView(PaletteList.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up:
                if (PaletteList.Items.Count > 0)
                    PaletteList.SelectedIndex = Math.Max(PaletteList.SelectedIndex - 1, 0);
                PaletteList.ScrollIntoView(PaletteList.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter:
                ExecuteSelectedPaletteItem();
                e.Handled = true;
                break;
            case Key.Escape:
                CloseCommandPalette();
                e.Handled = true;
                break;
        }
    }

    private void OnPaletteListClick(object sender, MouseButtonEventArgs e) => ExecuteSelectedPaletteItem();

    private void OnPaletteScrimMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, CommandPaletteOverlay)) CloseCommandPalette();
    }

    private void OnTransclusionScrimMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Click outside the palette card (on the dim scrim) closes it.
        if (DataContext is ViewModels.MainWindowViewModel vm && ReferenceEquals(e.OriginalSource, TransclusionScrim))
            vm.CloseTransclusionPickerCommand.Execute(null);
    }

    private void ExecuteSelectedPaletteItem()
    {
        if (PaletteList.SelectedItem is not PaletteItem item) return;
        CloseCommandPalette();
        item.Run();
    }

    // Canvas drag-drop from explorer (files) or the block-reference picker (transclusions)
    private void OnCanvasDragOver(object sender, DragEventArgs e)
    {
        bool accepts = e.Data.GetDataPresent(DataFormats.FileDrop)
            || e.Data.GetDataPresent(TransclusionDragFormat)
            || e.Data.GetDataPresent(PageDragFormat);
        e.Effects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    // -------- Floating toolbox: toggle + drag --------
    private void OnToggleToolbox(object sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainWindowViewModel vm)
            vm.IsToolboxFloating = !vm.IsToolboxFloating;
    }

    private Point _toolboxDragStart;
    private Thickness _toolboxDragOriginalMargin;
    private bool _toolboxDragging;

    private void OnToolboxHeaderMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement header) return;
        _toolboxDragging = true;
        _toolboxDragStart = e.GetPosition(this);
        _toolboxDragOriginalMargin = FloatingToolbox.Margin;
        header.CaptureMouse();
    }

    private void OnToolboxHeaderMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_toolboxDragging) return;
        var cur = e.GetPosition(this);
        var dx = cur.X - _toolboxDragStart.X;
        var dy = cur.Y - _toolboxDragStart.Y;
        FloatingToolbox.Margin = new Thickness(
            Math.Max(0, _toolboxDragOriginalMargin.Left + dx),
            Math.Max(0, _toolboxDragOriginalMargin.Top + dy),
            0, 0);
    }

    private void OnToolboxHeaderMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement header) header.ReleaseMouseCapture();
        _toolboxDragging = false;
    }

    private void OnShapeToolbarClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainWindowViewModel vm) return;
        if (sender is not FrameworkElement fe) return;
        string? tag = fe.Tag as string;
        string? next = string.IsNullOrEmpty(tag) ? null : tag;
        vm.PendingCanvasItemPlacement = null;
        vm.ActiveCanvasShapeTool = string.Equals(vm.ActiveCanvasShapeTool, next, System.StringComparison.OrdinalIgnoreCase) ? null : next;
    }

    private void OnShapeToolbarAddText(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainWindowViewModel vm) return;
        // Toggle pending placement: click "text" once → next canvas click drops a text card
        vm.ActiveCanvasShapeTool = null;
        vm.PendingCanvasItemPlacement = string.Equals(vm.PendingCanvasItemPlacement, "text", System.StringComparison.OrdinalIgnoreCase) ? null : "text";
    }

    private void OnShapeToolbarAddNote(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainWindowViewModel vm) return;
        vm.ActiveCanvasShapeTool = null;
        vm.PendingCanvasItemPlacement = string.Equals(vm.PendingCanvasItemPlacement, "note", System.StringComparison.OrdinalIgnoreCase) ? null : "note";
    }

    private void OnShapeToolbarAddOutline(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainWindowViewModel vm) return;
        // Toggle pending placement: click once → next canvas click drops an editable bullet block.
        vm.ActiveCanvasShapeTool = null;
        vm.PendingCanvasItemPlacement = string.Equals(vm.PendingCanvasItemPlacement, "outline", System.StringComparison.OrdinalIgnoreCase) ? null : "outline";
    }

    private void OnShapeToolbarSearchBlocks(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.MainWindowViewModel vm) return;
        // Opens the "Search blocks" palette: create a new block/page/board, or drop an existing block.
        vm.ActiveCanvasShapeTool = null;
        vm.PendingCanvasItemPlacement = null;
        if (vm.OpenTransclusionPickerCommand.CanExecute(null))
            vm.OpenTransclusionPickerCommand.Execute(null);
    }

    private async void OnCanvasDrop(object sender, DragEventArgs e)
    {
        Point screen = e.GetPosition(CanvasViewport);
        Point world = CanvasViewport.ScreenPointToWorld(screen);

        // Block reference dragged out of the picker — place a live transclusion at the drop point.
        if (e.Data.GetDataPresent(TransclusionDragFormat))
        {
            if (e.Data.GetData(TransclusionDragFormat) is TransclusionCandidateViewModel candidate)
                await _vm.AddTransclusionAtAsync(candidate, world.X, world.Y);
            return;
        }

        // Page dragged out of the project browser — drop a live page portal at the drop point.
        if (e.Data.GetDataPresent(PageDragFormat))
        {
            if (e.Data.GetData(PageDragFormat) is string pageName && !string.IsNullOrWhiteSpace(pageName))
                await _vm.AddPagePortalAtAsync(pageName, world.X, world.Y);
            return;
        }

        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        string[]? files = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (files is null) return;
        foreach (string file in files.Where(IsSupportedBoardFile))
        {
            if (IsImageFile(file))
                await _vm.AddImageFileToCanvasAsync(file, world.X, world.Y);
            else
                await _vm.AddFileToCanvasAsync(file, world.X, world.Y);
        }
    }

    // -------- Block-reference picker: drag a bullet onto the canvas --------
    // A private clipboard format carrying the dragged candidate in-process.
    private const string TransclusionDragFormat = "ReviewScope.TransclusionCandidate";
    private Point _transclusionDragStart;
    private TransclusionCandidateViewModel? _transclusionDragCandidate;

    private void OnTransclusionCandidateMouseDown(object sender, MouseButtonEventArgs e)
    {
        _transclusionDragStart = e.GetPosition(this);
        _transclusionDragCandidate = (sender as FrameworkElement)?.DataContext as TransclusionCandidateViewModel;
    }

    private void OnTransclusionCandidateMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _transclusionDragCandidate is null) return;

        Point pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _transclusionDragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(pos.Y - _transclusionDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var candidate = _transclusionDragCandidate;
        _transclusionDragCandidate = null;

        // Close the overlay so its dimming layer stops covering the canvas drop target. The
        // candidate is captured in the drag payload, so clearing the list here is safe.
        _vm.CloseTransclusionPickerCommand.Execute(null);

        var data = new DataObject(TransclusionDragFormat, candidate);
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
    }

    private static bool IsSupportedBoardFile(string file)
    {
        string ext = System.IO.Path.GetExtension(file);
        return ext.Equals(".cs", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".xaml", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || IsImageFile(file);
    }

    private static bool IsTextLikeBoardFile(string file)
    {
        string ext = System.IO.Path.GetExtension(file);
        return ext.Equals(".cs", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".xaml", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".json", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".xml", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".props", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".targets", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".config", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".yml", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".yaml", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsImageFile(string file)
    {
        string ext = System.IO.Path.GetExtension(file);
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".gif", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeMarkdown(string text)
    {
        string trimmed = text.TrimStart();
        return trimmed.StartsWith("#", StringComparison.Ordinal)
            || trimmed.StartsWith("- ", StringComparison.Ordinal)
            || trimmed.StartsWith("* ", StringComparison.Ordinal)
            || trimmed.Contains("\n##", StringComparison.Ordinal)
            || trimmed.Contains("```", StringComparison.Ordinal);
    }

    // Canvas event handlers
    private void OnBlockActivated(BlockActivatedArgs? args) { /* double-click on code block */ }

    private async void OnExtractRequested(ExtractRequestedArgs? args)
    {
        if (args is not null) await _vm.HandleExtractRequestAsync(args);
    }

    private async void OnFocusRequested(FocusRequestedArgs? args)
    {
        if (args is not null) await _vm.HandleFocusRequestAsync(args);
    }

    private void OnReviewLinesToggled(ReviewLinesToggledArgs? args)
    {
        if (args is null) return;
        string? key = _vm.CurrentWorkspaceKey;
        if (key is null) return;

        // Hash the current file text so we can later detect drift (Phase 4 staleness). Best-effort:
        // an unreadable file still records the toggle, just with an empty hash.
        string hash = string.Empty;
        try
        {
            if (System.IO.File.Exists(args.FilePath))
                hash = ReviewScope.App.Persistence.ReviewProgressStore.ComputeContentHash(
                    System.IO.File.ReadAllText(args.FilePath));
        }
        catch (System.IO.IOException) { }
        catch (UnauthorizedAccessException) { }

        _vm.ReviewProgress.ToggleRange(key, args.FilePath, args.StartLine, args.EndLine, hash);
        CanvasViewport.Refresh();
    }

    private async void OnRestoreRequested(RestoreRequestedArgs? args)
    {
        if (args is not null) await _vm.HandleRestoreAsync(args);
    }

    private async void OnConnectionDrawn(ConnectionDrawnArgs? args)
    {
        if (args is not null) await _vm.HandleConnectionDrawnAsync(args);
    }

    private async void OnAnnotationRequested(AnnotationRequestedArgs? args)
    {
        if (args is null) return;
        await _vm.AddAnnotationAsync(args);
        CanvasViewport.BeginEditNewNote();
    }

    private async void OnSceneChangedByCanvas(CanvasSceneChangedArgs? args)
    {
        if (args is not null && args.IsContentChange) await _vm.OnSceneChangedByCanvas(args.Before, args.After);
    }
}

// Simple relay command for canvas wiring
internal sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    public RelayCommand(Action<T?> execute) => _execute = execute;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute(parameter is T t ? t : default);
    public event EventHandler? CanExecuteChanged { add { } remove { } }
}
