using ReviewScope.App.ViewModels;
using ReviewScope.Canvas;
using ReviewScope.Domain;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DrawingColor = System.Drawing.Color;
using DrawingColorTranslator = System.Drawing.ColorTranslator;
using Forms = System.Windows.Forms;

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
    }

    private async void OnBranchComboSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        string branch = e.AddedItems[0] as string ?? string.Empty;
        await _vm.LoadFromSelectedBranchAsync(branch);
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete && !(Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.C))
            return;

        if (IsTextInputSource(e.OriginalSource as DependencyObject))
            return;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.C)
        {
            _vm.CopySelectedBoardItemsCommand.Execute(null);
            e.Handled = true;
            return;
        }

        CanvasViewport.DeleteSelection();
        e.Handled = true;
    }

    private void OnFileMenuButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is null)
            return;

        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
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

    private async void OnPickFillColor(object sender, RoutedEventArgs e)
    {
        if (TryPickColor(_vm.SelectedFill, out string hex))
        {
            _vm.SelectedFill = hex;
            await _vm.ApplySelectionPropertiesAsync();
        }
    }

    private async void OnPickStrokeColor(object sender, RoutedEventArgs e)
    {
        if (TryPickColor(_vm.SelectedStroke, out string hex))
        {
            _vm.SelectedStroke = hex;
            await _vm.ApplySelectionPropertiesAsync();
        }
    }

    private async void OnPickTextColor(object sender, RoutedEventArgs e)
    {
        if (TryPickColor(_vm.SelectedTextColor, out string hex))
        {
            _vm.SelectedTextColor = hex;
            await _vm.ApplySelectionPropertiesAsync();
        }
    }

    private static bool TryPickColor(string currentHex, out string hex)
    {
        hex = currentHex;
        using var dialog = new Forms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true
        };

        try
        {
            dialog.Color = DrawingColorTranslator.FromHtml(currentHex);
        }
        catch
        {
            dialog.Color = DrawingColor.White;
        }

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
            return false;

        DrawingColor color = dialog.Color;
        hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        return true;
    }

    // Canvas drag-drop from explorer
    private void OnCanvasDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnCanvasDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        string[]? files = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (files is null) return;
        Point screen = e.GetPosition(CanvasViewport);
        Point world = CanvasViewport.ScreenPointToWorld(screen);
        foreach (string file in files.Where(IsSupportedBoardFile))
        {
            if (IsImageFile(file))
                await _vm.AddImageFileToCanvasAsync(file, world.X, world.Y);
            else
                await _vm.AddFileToCanvasAsync(file, world.X, world.Y);
        }
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

    private async void OnSelectColorPreset(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string type || btn.CommandParameter is not string hex) return;
        if (type == "Fill") _vm.SelectedFill = hex;
        else if (type == "Stroke") _vm.SelectedStroke = hex;
        else if (type == "Text") _vm.SelectedTextColor = hex;
        await _vm.ApplySelectionPropertiesAsync();
    }

    private async void OnSelectStrokeWidth(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.CommandParameter is not string width) return;
        _vm.SelectedStrokeWidth = width;
        await _vm.ApplySelectionPropertiesAsync();
    }

    private async void OnApplySelectionProperties(object sender, RoutedEventArgs e)
    {
        await _vm.ApplySelectionPropertiesAsync();
    }

    private async void OnSelectTextAlignment(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.CommandParameter is not string alignment) return;
        _vm.SelectedTextAlignment = alignment;
        await _vm.ApplySelectionPropertiesAsync();
    }

    private async void OnSelectStrokeStyle(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.CommandParameter is not string style) return;
        _vm.SelectedDashed = style == "dashed";
        await _vm.ApplySelectionPropertiesAsync();
    }

    private async void OnSelectFillStyle(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.CommandParameter is not string style) return;
        _vm.SelectedFillStyle = style;
        await _vm.ApplySelectionPropertiesAsync();
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
