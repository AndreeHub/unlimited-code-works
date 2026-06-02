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
    private Point _sessionDragStartPoint;
    private ReviewSession? _draggedSession;
    private bool _isSessionTabDragging;
    private double _sessionDragPointerOffsetX;

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

        // After a Text/Note is added (toolbox button, canvas placement, etc), drop
        // straight into in-canvas edit so the user can type immediately.
        vm.PostCreateEditRequested += OnPostCreateEditRequested;

        // When edit starts, surface the most useful right-side tab: Text-tab for text
        // cards (typography/spacing/alignment), Inspector-tab for notes (their body
        // + sticky color live there).
        CanvasViewport.EditStarted += OnCanvasEditStarted;

        // Feed inline autocomplete (#tag, [[wiki link]]) from the per-workspace
        // TagIndex held by the view-model.
        CanvasViewport.AutocompleteSuggestionsProvider = vm.GetAutocompleteSuggestions;

        // Project browser: an independent grouped view (by document Kind) over the same
        // Sessions collection, so it doesn't disturb the header strip's default view /
        // drag-reorder logic.
        var projectView = new System.Windows.Data.CollectionViewSource { Source = vm.Sessions };
        projectView.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(ReviewSession.Kind)));
        ProjectDocList.ItemsSource = projectView.View;

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
            || e.PropertyName == nameof(MainWindowViewModel.IsOutlineDocumentActive))
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

        RightPanelCol.Width = canvas ? new GridLength(320) : new GridLength(0);
        RightPanelCol.MinWidth = canvas ? 220 : 0;
        RightSplitterCol.Width = canvas ? new GridLength(5) : new GridLength(0);
        RightSplitter.Visibility = canvas ? Visibility.Visible : Visibility.Collapsed;
        RightSidebar.Visibility = canvas ? Visibility.Visible : Visibility.Collapsed;

        // Keep a visible left tab selected for the current mode.
        var sel = LeftTabs.SelectedItem as TabItem;
        if (canvas)
        {
            if (sel == LeftTabNotes || sel == LeftTabGraph) LeftTabs.SelectedItem = LeftTabExplorer;
        }
        else
        {
            if (sel == LeftTabExplorer || sel == LeftTabSymbols) LeftTabs.SelectedItem = LeftTabNotes;
        }
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

    private async void OnProjectDocSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && e.AddedItems.Count > 0)
            await _vm.ActivateSelectedSessionAsync();
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

    private async void OnBranchComboSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        string branch = e.AddedItems[0] as string ?? string.Empty;
        await _vm.LoadFromSelectedBranchAsync(branch);
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
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

    private async void OnSymbolsDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var tree = (TreeView)sender;
        if (tree.SelectedItem is SymbolExplorerItemViewModel sym && sym.StartLine.HasValue)
            await _vm.AddSymbolToCanvasAsync(sym);
    }

    private void OnCollapseAll(object sender, RoutedEventArgs e) => _vm.SetExplorerExpanded(false);
    private void OnExpandAll(object sender, RoutedEventArgs e) => _vm.SetExplorerExpanded(true);

    private void OnExplorerSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return || e.Key == Key.Enter)
            _vm.ApplyExplorerSearchCommand.Execute(null);
    }

    private async void OnExplorerDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeView tree)
            return;

        if (tree.SelectedItem is FileExplorerItemViewModel item && item.IsFile && item.FilePath is not null)
        {
            if (IsImageFile(item.FilePath))
            {
                await _vm.AddImageFileToCanvasAsync(item.FilePath);
                return;
            }

            if (IsTextLikeBoardFile(item.FilePath))
            {
                await _vm.LoadSymbolsForFileAsync(item.FilePath);
                await _vm.AddFileToCanvasAsync(item.FilePath);
            }
        }
    }

    private void OnSessionDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GetSessionFromSource(e.OriginalSource as DependencyObject) is not ReviewSession session)
            return;

        BeginSessionRename(session);
        e.Handled = true;
    }

    private void OnSessionTabLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBoxItem item || item.DataContext is not ReviewSession session)
            return;

        if (_vm.SessionSpawnAnimationId != session.Id)
            return;

        double startX = 180;
        try
        {
            startX = NewSessionButton.TransformToVisual(item).Transform(new Point(0, 0)).X;
            if (double.IsNaN(startX) || startX < 48)
                startX = 180;
        }
        catch (InvalidOperationException)
        {
            startX = 180;
        }

        var transform = new TranslateTransform(startX, 0);
        item.RenderTransform = transform;
        item.Opacity = 0.35;

        var travelEase = new CubicEase { EasingMode = EasingMode.EaseOut };
        var settleEase = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var bounce = new DoubleAnimationUsingKeyFrames();
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(startX, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(-18, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(280))) { EasingFunction = travelEase });
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(10, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(360))) { EasingFunction = settleEase });
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(-7, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(440))) { EasingFunction = settleEase });
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(515))) { EasingFunction = settleEase });
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(-2, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(585))) { EasingFunction = settleEase });
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(660))) { EasingFunction = settleEase });

        transform.BeginAnimation(TranslateTransform.XProperty, bounce);
        item.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        _vm.ClearSessionSpawnAnimation(session.Id);
    }

    private void OnSessionTabMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _sessionDragStartPoint = e.GetPosition(SessionTabs);
        _draggedSession = IsTextInputSource(e.OriginalSource as DependencyObject)
            ? null
            : GetSessionFromSource(e.OriginalSource as DependencyObject);
    }

    private void OnSessionTabMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedSession is null)
            return;

        var point = e.GetPosition(SessionTabs);
        if (!_isSessionTabDragging)
        {
            if (Math.Abs(point.X - _sessionDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(point.Y - _sessionDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            BeginSessionTabDrag(_draggedSession, point);
        }

        UpdateSessionTabDrag(point);
        e.Handled = true;
    }

    private async void OnSessionTabMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        bool handled = _isSessionTabDragging;
        await CompleteSessionTabDragAsync();
        e.Handled = handled;
    }

    private async void OnSessionTabLostMouseCapture(object sender, MouseEventArgs e)
    {
        await CompleteSessionTabDragAsync();
    }

    private void BeginSessionTabDrag(ReviewSession session, Point pointer)
    {
        if (_activeSessionNameEditor is not null)
            CompleteSessionRenameAsync(_activeSessionNameEditor, commit: true);

        if (SessionTabs.ItemContainerGenerator.ContainerFromItem(session) is not ListBoxItem item)
            return;

        var bounds = item.TransformToVisual(SessionTabs).TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));
        _sessionDragPointerOffsetX = pointer.X - bounds.Left;
        _isSessionTabDragging = true;
        _vm.SelectedSession = session;

        item.BeginAnimation(OpacityProperty, null);
        item.Opacity = 0.82;
        item.RenderTransform = new TranslateTransform();
        Panel.SetZIndex(item, 1000);
        Mouse.Capture(SessionTabs);
    }

    private void UpdateSessionTabDrag(Point pointer)
    {
        if (!_isSessionTabDragging || _draggedSession is null)
            return;

        int targetIndex = GetLiveSessionTargetIndex(pointer, _draggedSession);
        bool moved = _vm.MoveSessionInQueue(_draggedSession, targetIndex);
        if (moved)
            SessionTabs.UpdateLayout();

        if (SessionTabs.ItemContainerGenerator.ContainerFromItem(_draggedSession) is not ListBoxItem item)
            return;

        var bounds = item.TransformToVisual(SessionTabs).TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));
        double targetLeft = pointer.X - _sessionDragPointerOffsetX;
        var transform = item.RenderTransform as TranslateTransform;
        if (transform is null)
        {
            transform = new TranslateTransform();
            item.RenderTransform = transform;
        }

        transform.X = targetLeft - bounds.Left;
        transform.Y = 0;
        item.Opacity = 0.82;
        Panel.SetZIndex(item, 1000);
    }

    private async Task CompleteSessionTabDragAsync()
    {
        if (_draggedSession is null)
            return;

        var completed = _draggedSession;
        bool wasDragging = _isSessionTabDragging;
        _draggedSession = null;
        _isSessionTabDragging = false;

        if (Mouse.Captured == SessionTabs)
            Mouse.Capture(null);

        if (SessionTabs.ItemContainerGenerator.ContainerFromItem(completed) is ListBoxItem item)
        {
            if (item.RenderTransform is TranslateTransform transform)
            {
                var settle = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                transform.BeginAnimation(TranslateTransform.XProperty, settle);
            }

            item.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
            Panel.SetZIndex(item, 0);
        }

        if (wasDragging)
            await _vm.PersistSessionOrderAsync();
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

    private void BeginSessionRename(ReviewSession session)
    {
        if (_activeSessionNameEditor is not null)
            CompleteSessionRenameAsync(_activeSessionNameEditor, commit: true);

        _vm.SelectedSession = session;
        var editor = FindSessionTemplateChild<TextBox>(session, "SessionNameEditor");
        var label = FindSessionTemplateChild<TextBlock>(session, "SessionNameText");
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
            if (editor.DataContext is not ReviewSession session)
                return;

            var label = FindSessionTemplateChild<TextBlock>(session, "SessionNameText");
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

    private T? FindSessionTemplateChild<T>(ReviewSession session, string name) where T : FrameworkElement
    {
        if (SessionTabs.ItemContainerGenerator.ContainerFromItem(session) is not ListBoxItem item)
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
            if (source is FrameworkElement { DataContext: ReviewSession session })
                return session;

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private int GetLiveSessionTargetIndex(Point pointer, ReviewSession dragging)
    {
        for (int i = 0; i < _vm.Sessions.Count; i++)
        {
            var session = _vm.Sessions[i];
            if (session.Id == dragging.Id)
                continue;

            if (SessionTabs.ItemContainerGenerator.ContainerFromItem(session) is not ListBoxItem item)
                continue;

            var bounds = item.TransformToVisual(SessionTabs).TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));
            if (pointer.X < bounds.Left + bounds.Width / 2)
                return i;
        }

        return _vm.Sessions.Count;
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

    private async void OnSessionTabSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (IsLoaded)
            await _vm.ActivateSelectedSessionAsync();
    }

    private void OnFrameAll(object sender, RoutedEventArgs e) => CanvasViewport.FrameAll();

    private void OnToggleTheme(object sender, RoutedEventArgs e) => Theming.ThemeManager.Toggle();

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

    // Canvas drag-drop from explorer (files) or the block-reference picker (transclusions)
    private void OnCanvasDragOver(object sender, DragEventArgs e)
    {
        bool accepts = e.Data.GetDataPresent(DataFormats.FileDrop)
            || e.Data.GetDataPresent(TransclusionDragFormat);
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
