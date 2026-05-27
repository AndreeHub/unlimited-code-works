using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ReviewScope.Analysis;
using ReviewScope.App.Persistence;
using ReviewScope.App.ViewModels.Inspectors;
using ReviewScope.Domain;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;

namespace ReviewScope.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly WorkspaceManager _workspace;
    private readonly FileStructureService _fileStructure;
    private readonly SemanticSpanService _semanticSpan;
    private readonly SymbolScopeService _symbolScope;
    private readonly SessionRepository _sessions;
    private readonly ILogger<MainWindowViewModel> _logger;

    private const double CodeLineHeight = 18.0;
    private const double CodeBlockVerticalChrome = 100.0;
    private const double MinFileBlockHeight = 1200.0;
    private const double MaxUnfocusedFileBlockHeight = 1200.0;
    private const double DefaultFileBlockWidth = 800.0;
    private const double MinScopedBlockHeight = 1200.0;

    // ---- Observable state ----
    [ObservableProperty] private RenderScene _scene = RenderScene.Empty;
    [ObservableProperty] private string _statusMessage = "Open a folder, .sln, or .csproj to start.";
    [ObservableProperty] private string _workspacePath = string.Empty;
    [ObservableProperty] private string _workspaceBranchName = string.Empty;
    [ObservableProperty] private ReviewSession? _selectedSession;
    [ObservableProperty] private string _sessionNameDraft = "New Board";
    [ObservableProperty] private int _selectedLeftTabIndex;
    [ObservableProperty] private int _selectedRightTabIndex = 1;
    [ObservableProperty] private string _selectedStyleTarget = "Fill";
    [ObservableProperty] private string _selectedStyleColor = "#FFFFFF";
    [ObservableProperty] private bool _styleTargetFillSelected = true;
    [ObservableProperty] private bool _styleTargetStrokeSelected;
    [ObservableProperty] private bool _styleTargetTextSelected;
    [ObservableProperty] private Guid? _sessionSpawnAnimationId;
    [ObservableProperty] private string _selectedAnnotationContent = string.Empty;
    [ObservableProperty] private Guid? _editingAnnotationId;
    [ObservableProperty] private CanvasBackgroundMode _backgroundMode = CanvasBackgroundMode.Dots;
    [ObservableProperty] private bool _snapToGrid = true;
    [ObservableProperty] private bool _connectorsEnabled = true;
    [ObservableProperty] private RenderBlock? _selectedBlock;
    [ObservableProperty] private RenderConnection? _selectedConnection;
    [ObservableProperty] private RenderSwimLane? _selectedSwimLane;
    [ObservableProperty] private InspectorViewModelBase? _activeInspector;
    [ObservableProperty] private bool _isTextBlockInspectorSelected;
    [ObservableProperty] private bool _isShapeInspectorSelected;
    [ObservableProperty] private bool _isStickyNoteInspectorSelected;
    [ObservableProperty] private bool _isConnectionInspectorSelected;
    [ObservableProperty] private bool _isSwimLaneInspectorSelected;
    [ObservableProperty] private bool _isDefaultBlockInspectorSelected;
    [ObservableProperty] private bool _isToolboxFloating = true;
    [ObservableProperty] private string? _activeCanvasShapeTool;
    [ObservableProperty] private string? _pendingCanvasItemPlacement;

    /// <summary>
    /// Raised after a new Text/Note block is added programmatically (toolbox button path).
    /// MainWindow.xaml.cs uses this to put the canvas into in-canvas edit mode on the new block.
    /// </summary>
    public event Action<BlockKind>? PostCreateEditRequested;
    internal void RequestPostCreateEdit(BlockKind kind) => PostCreateEditRequested?.Invoke(kind);

    partial void OnActiveInspectorChanged(InspectorViewModelBase? value)
    {
        // Use exact-type checks (not `is`) because StickyNoteInspectorViewModel now
        // inherits from TextBlockInspectorViewModel — without this both flags would
        // be true at the same time for sticky notes.
        IsTextBlockInspectorSelected = value?.GetType() == typeof(TextBlockInspectorViewModel);
        IsShapeInspectorSelected = value is ShapeInspectorViewModel;
        IsStickyNoteInspectorSelected = value?.GetType() == typeof(StickyNoteInspectorViewModel);
        IsConnectionInspectorSelected = value is ConnectionInspectorViewModel;
        IsSwimLaneInspectorSelected = value is SwimLaneInspectorViewModel;
        IsDefaultBlockInspectorSelected = value is DefaultBlockInspectorViewModel;
    }

    [ObservableProperty] private string _selectedObjectTitle = "Canvas";
    [ObservableProperty] private string _selectedObjectKind = "Review canvas";
    [ObservableProperty] private string _selectedObjectPath = string.Empty;
    [ObservableProperty] private string _selectedObjectLineRange = string.Empty;
    [ObservableProperty] private string _selectedObjectX = "--";
    [ObservableProperty] private string _selectedObjectY = "--";
    [ObservableProperty] private string _selectedObjectWidth = "--";
    [ObservableProperty] private string _selectedObjectHeight = "--";
    [ObservableProperty] private bool _hasBoardSelection;
    [ObservableProperty] private bool _selectionIsBlock;
    [ObservableProperty] private bool _selectionIsConnection;
    [ObservableProperty] private bool _selectionSupportsStyle;
    [ObservableProperty] private string _selectedTitleDraft = string.Empty;
    [ObservableProperty] private string _selectedBodyDraft = string.Empty;
    [ObservableProperty] private string _selectedFill = "#FFFFFF";
    [ObservableProperty] private string _selectedFillStyle = "hatch";
    [ObservableProperty] private string _selectedStroke = "#CBD5E1";
    [ObservableProperty] private string _selectedTextColor = "#111827";
    [ObservableProperty] private string _selectedStrokeWidth = "1.2";
    [ObservableProperty] private string _selectedFontSize = "16";
    [ObservableProperty] private string _selectedTextAlignment = "Center";
    [ObservableProperty] private bool _selectedDashed;
    [ObservableProperty] private bool _selectedLocked;
    [ObservableProperty] private double _selectedOpacity = 1.0;
    [ObservableProperty] private double _selectedCornerRadius = 8.0;
    [ObservableProperty] private string _selectedConnectionLabel = string.Empty;
    [ObservableProperty] private string _selectedRouteKind = ConnectorRouteKind.Curved.ToString();
    [ObservableProperty] private string _selectedArrowKind = ConnectorArrowKind.Forward.ToString();
    [ObservableProperty] private string _selectedSymbolsHeader = "Symbols";
    [ObservableProperty] private string _boardSearchQuery = string.Empty;
    [ObservableProperty] private string _explorerSearchQuery = string.Empty;
    [ObservableProperty] private string _llmExportPreview = string.Empty;
    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private bool _canRedo;
    [ObservableProperty] private string _selectedBranch = string.Empty;
    [ObservableProperty] private string _fileExplorerRootPath = string.Empty;

    // Fields used by partial classes
    private string? _lastWorkspaceLoadPath;
    private string? _lastBoardFilePath;
    private WorkspaceSnapshot? _currentSnapshot;
    private ReviewSession? _activeSession;
    private CancellationTokenSource? _saveCts;
    private CancellationTokenSource? _loadCts;
    private int _loadVersion;
    private bool _syncingStyleTarget;
    private bool _isUpdatingSelection;
    private string? _lastSelectionKey;

    [RelayCommand]
    public void ToggleBackground()
    {
        BackgroundMode = BackgroundMode == CanvasBackgroundMode.Dots
            ? CanvasBackgroundMode.Grid
            : CanvasBackgroundMode.Dots;
        StatusMessage = $"Background: {BackgroundMode}";
    }

    public ObservableCollection<string> AvailableBranches { get; } = new();
    public ObservableCollection<FileExplorerItemViewModel> ExplorerRoots { get; } = new();
    public ObservableCollection<FileExplorerItemViewModel> FileExplorerRoots { get; } = new();
    public ObservableCollection<ReviewSession> Sessions { get; } = new();
    public ObservableCollection<SymbolExplorerItemViewModel> SymbolRoots { get; } = new();
    public ObservableCollection<BoardSearchResultViewModel> BoardSearchResults { get; } = new();
    public ObservableCollection<BoardFileUsageViewModel> BoardFileUsages { get; } = new();

    private readonly Stack<RenderScene> _undoStack = new();
    private readonly Stack<RenderScene> _redoStack = new();

    // ---- Swim-lane colors ----
    private static readonly string[] LaneColors = { "#4A90D9", "#63D2A5", "#D49A4A", "#C07AB8", "#E05252", "#7CB8E0" };
    private int _nextLaneColor;

    public MainWindowViewModel(
        WorkspaceManager workspace,
        FileStructureService fileStructure,
        SemanticSpanService semanticSpan,
        SymbolScopeService symbolScope,
        SessionRepository sessions,
        ILogger<MainWindowViewModel> logger)
    {
        _workspace = workspace;
        _fileStructure = fileStructure;
        _semanticSpan = semanticSpan;
        _symbolScope = symbolScope;
        _sessions = sessions;
        _logger = logger;
    }

    private void SetSceneFromUserAction(RenderScene newScene, string? description = null, RenderScene? undoBase = null)
    {
        RenderScene previousScene = undoBase ?? Scene;
        if (!ReferenceEquals(previousScene, newScene))
            _undoStack.Push(previousScene);
        _redoStack.Clear();
        Scene = newScene;
        UpdateUndoRedoState();
        if (!string.IsNullOrWhiteSpace(description))
            StatusMessage = description;
    }

    internal void UpdateSceneBlock(RenderBlock updatedBlock, string description)
    {
        if (_isUpdatingSelection) return;
        var blocks = Scene.Blocks.Select(b => b.Key.Equals(updatedBlock.Key, StringComparison.OrdinalIgnoreCase) ? updatedBlock : b).ToList();
        SetSceneFromUserAction(Scene with { Blocks = blocks }, description);
        _ = PersistSessionAsync();
    }

    internal void UpdateSceneConnection(RenderConnection updatedConnection, string description)
    {
        if (_isUpdatingSelection) return;
        var connections = Scene.Connections.Select(c => c.Id == updatedConnection.Id ? updatedConnection : c).ToList();
        SetSceneFromUserAction(Scene with { Connections = connections }, description);
        _ = PersistSessionAsync();
    }

    internal void UpdateSceneSwimLane(RenderSwimLane updatedSwimLane, string description)
    {
        if (_isUpdatingSelection) return;
        var lanes = Scene.SwimLanes.Select(l => l.Key.Equals(updatedSwimLane.Key, StringComparison.OrdinalIgnoreCase) ? updatedSwimLane : l).ToList();
        SetSceneFromUserAction(Scene with { SwimLanes = lanes }, description);
        _ = PersistSessionAsync();
    }

    private void UpdateSelectedObject(RenderScene? scene = null)
    {
        _isUpdatingSelection = true;
        try
        {
            scene ??= Scene;
            SelectedBlock = scene.Blocks.FirstOrDefault(b => b.IsSelected);
            SelectedConnection = scene.Connections.FirstOrDefault(c => c.IsSelected);
            SelectedSwimLane = scene.SwimLanes.FirstOrDefault(l => l.IsSelected);
            SelectionIsBlock = SelectedBlock is not null;
            SelectionIsConnection = SelectedConnection is not null;
            SelectionSupportsStyle = SelectionIsBlock || SelectionIsConnection;
            HasBoardSelection = SelectionIsBlock || SelectionIsConnection || SelectedSwimLane is not null;
            string? currentSelectionKey = SelectedBlock?.Key
                ?? SelectedConnection?.Id.ToString()
                ?? SelectedSwimLane?.Key;
            if (HasBoardSelection && !string.Equals(_lastSelectionKey, currentSelectionKey, StringComparison.Ordinal))
                SelectedRightTabIndex = 0;
            _lastSelectionKey = currentSelectionKey;

            Type? targetInspectorType = null;
            if (SelectedBlock is not null)
            {
                targetInspectorType = SelectedBlock.Kind switch
                {
                    BlockKind.Text => typeof(TextBlockInspectorViewModel),
                    BlockKind.Note => typeof(StickyNoteInspectorViewModel),
                    BlockKind.Shape or BlockKind.Container => typeof(ShapeInspectorViewModel),
                    _ => typeof(DefaultBlockInspectorViewModel)
                };
            }
            else if (SelectedConnection is not null)
            {
                targetInspectorType = typeof(ConnectionInspectorViewModel);
            }
            else if (SelectedSwimLane is not null)
            {
                targetInspectorType = typeof(SwimLaneInspectorViewModel);
            }

            if (targetInspectorType is null)
            {
                ActiveInspector = null;
            }
            else if (ActiveInspector?.GetType() == targetInspectorType)
            {
                ActiveInspector.Refresh();
            }
            else
            {
                ActiveInspector = targetInspectorType switch
                {
                    Type t when t == typeof(TextBlockInspectorViewModel) => new TextBlockInspectorViewModel(this),
                    Type t when t == typeof(StickyNoteInspectorViewModel) => new StickyNoteInspectorViewModel(this),
                    Type t when t == typeof(ShapeInspectorViewModel) => new ShapeInspectorViewModel(this),
                    Type t when t == typeof(ConnectionInspectorViewModel) => new ConnectionInspectorViewModel(this),
                    Type t when t == typeof(SwimLaneInspectorViewModel) => new SwimLaneInspectorViewModel(this),
                    _ => new DefaultBlockInspectorViewModel(this)
                };
            }

            if (SelectedBlock is not null)
            {
                SelectedObjectTitle = SelectedBlock.Focused?.SymbolName ?? SelectedBlock.Title;
                SelectedObjectKind = SelectedBlock.Kind switch
                {
                    BlockKind.File => "File card",
                    BlockKind.Extract => "Symbol card",
                    BlockKind.Note => "Note",
                    BlockKind.MarkdownDoc => "Architecture doc",
                    BlockKind.Shape => "Diagram symbol",
                    BlockKind.Text => "Text",
                    BlockKind.Image => "Image",
                    BlockKind.Container => "Container",
                    _ => "Canvas object"
                };
                SelectedObjectPath = SelectedBlock.FilePath is null ? string.Empty : GetRelativePath(SelectedBlock.FilePath);
                SelectedObjectLineRange = SelectedBlock.StartLine.HasValue && SelectedBlock.EndLine.HasValue
                    ? $"Lines {SelectedBlock.StartLine}-{SelectedBlock.EndLine}"
                    : string.Empty;
                SetSelectedObjectData(SelectedBlock.X, SelectedBlock.Y, SelectedBlock.Width, SelectedBlock.Height);
                var style = SelectedBlock.Style ?? new BoardItemStyle();
                SelectedTitleDraft = SelectedBlock.Title;
                SelectedBodyDraft = SelectedBlock.Body ?? string.Empty;
                SelectedFill = style.Fill;
                SelectedFillStyle = style.FillStyle ?? "hatch";
                SelectedStroke = style.Stroke;
                SelectedTextColor = style.Text;
                SelectedStrokeWidth = style.StrokeWidth.ToString("0.##");
                SelectedFontSize = style.FontSize.ToString("0.#");
                SelectedTextAlignment = NormalizeTextAlignment(style.TextAlign);
                SelectedDashed = style.Dashed;
                SelectedLocked = SelectedBlock.IsLocked;
                SelectedOpacity = style.Opacity;
                SelectedCornerRadius = style.CornerRadius;
                SelectedConnectionLabel = string.Empty;
                RefreshSelectedStyleColor();
                return;
            }

            if (SelectedConnection is not null)
            {
                SelectedObjectTitle = string.IsNullOrWhiteSpace(SelectedConnection.Label) ? "Connector" : SelectedConnection.Label!;
                SelectedObjectKind = "Connector";
                SelectedObjectPath = string.Empty;
                SelectedObjectLineRange = $"{SelectedConnection.SourceKey} -> {SelectedConnection.TargetKey}";
                ClearSelectedObjectData();
                SelectedConnectionLabel = SelectedConnection.Label ?? string.Empty;
                SelectedStroke = SelectedConnection.Stroke;
                SelectedStrokeWidth = "1.6";
                SelectedFontSize = "16";
                SelectedTextAlignment = "Center";
                SelectedDashed = SelectedConnection.Dashed;
                SelectedRouteKind = SelectedConnection.RouteKind.ToString();
                SelectedArrowKind = SelectedConnection.ArrowKind.ToString();
                SelectedTitleDraft = string.Empty;
                SelectedBodyDraft = string.Empty;
                SelectedLocked = false;
                SelectedOpacity = 1.0;
                SelectedCornerRadius = 8.0;
                SelectedStyleTarget = "Stroke";
                RefreshSelectedStyleColor();
                return;
            }

            if (SelectedSwimLane is not null)
            {
                SelectedObjectTitle = SelectedSwimLane.Name;
                SelectedObjectKind = "Architecture frame";
                SelectedObjectPath = string.Empty;
                SelectedObjectLineRange = string.Empty;
                SetSelectedObjectData(SelectedSwimLane.X, SelectedSwimLane.Y, SelectedSwimLane.Width, SelectedSwimLane.Height);
                SelectedOpacity = 1.0;
                SelectedCornerRadius = 8.0;
                return;
            }

            SelectedObjectTitle = "Canvas";
            SelectedObjectKind = "Review canvas";
            SelectedObjectPath = string.Empty;
            SelectedObjectLineRange = string.Empty;
            SelectedTitleDraft = string.Empty;
            SelectedBodyDraft = string.Empty;
            SelectedConnectionLabel = string.Empty;
            SelectedStrokeWidth = "1.2";
            SelectedFontSize = "16";
            SelectedTextAlignment = "Center";
            SelectedLocked = false;
            SelectedOpacity = 1.0;
            SelectedCornerRadius = 8.0;
            SelectionSupportsStyle = false;
            RefreshSelectedStyleColor();
            ClearSelectedObjectData();
        }
        finally
        {
            _isUpdatingSelection = false;
        }
    }

    partial void OnSelectedStyleTargetChanged(string value)
    {
        RefreshSelectedStyleColor();
        _syncingStyleTarget = true;
        StyleTargetFillSelected = value == "Fill";
        StyleTargetStrokeSelected = value == "Stroke";
        StyleTargetTextSelected = value == "Text";
        _syncingStyleTarget = false;
    }

    partial void OnStyleTargetFillSelectedChanged(bool value)
    {
        if (value && !_syncingStyleTarget)
            SelectedStyleTarget = "Fill";
    }

    partial void OnStyleTargetStrokeSelectedChanged(bool value)
    {
        if (value && !_syncingStyleTarget)
            SelectedStyleTarget = "Stroke";
    }

    partial void OnStyleTargetTextSelectedChanged(bool value)
    {
        if (value && !_syncingStyleTarget)
            SelectedStyleTarget = "Text";
    }

    partial void OnSelectedFillChanged(string value) => RefreshSelectedStyleColor();
    partial void OnSelectedStrokeChanged(string value) => RefreshSelectedStyleColor();
    partial void OnSelectedTextColorChanged(string value) => RefreshSelectedStyleColor();
    partial void OnSelectedLockedChanged(bool value) { if (!_isUpdatingSelection) _ = ApplySelectionPropertiesAsync(); }
    partial void OnSelectedDashedChanged(bool value) { if (!_isUpdatingSelection) _ = ApplySelectionPropertiesAsync(); }
    partial void OnSelectedOpacityChanged(double value) { if (!_isUpdatingSelection) _ = ApplySelectionPropertiesAsync(); }
    partial void OnSelectedCornerRadiusChanged(double value) { if (!_isUpdatingSelection) _ = ApplySelectionPropertiesAsync(); }
    partial void OnSelectedFontSizeChanged(string value) { if (!_isUpdatingSelection) _ = ApplySelectionPropertiesAsync(); }
    partial void OnSelectedTextAlignmentChanged(string value) { if (!_isUpdatingSelection) _ = ApplySelectionPropertiesAsync(); }
    partial void OnSelectedStrokeWidthChanged(string value) { if (!_isUpdatingSelection) _ = ApplySelectionPropertiesAsync(); }

    private void RefreshSelectedStyleColor()
    {
        SelectedStyleColor = SelectedStyleTarget switch
        {
            "Stroke" => SelectedStroke,
            "Text" => SelectedTextColor,
            _ => SelectedFill
        };
    }

    private void SetSelectedObjectData(double x, double y, double width, double height)
    {
        SelectedObjectX = x.ToString("0");
        SelectedObjectY = y.ToString("0");
        SelectedObjectWidth = width.ToString("0");
        SelectedObjectHeight = height.ToString("0");
    }

    private void ClearSelectedObjectData()
    {
        SelectedObjectX = "--";
        SelectedObjectY = "--";
        SelectedObjectWidth = "--";
        SelectedObjectHeight = "--";
    }

    partial void OnSceneChanged(RenderScene value)
    {
        UpdateSelectedObject(value);
        RefreshBoardDetails();
    }

    private void ResetHistory()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        UpdateUndoRedoState();
    }

    private void UpdateUndoRedoState()
    {
        CanUndo = _undoStack.Count > 0;
        CanRedo = _redoStack.Count > 0;
    }

    [RelayCommand]
    public async Task UndoAsync()
    {
        if (_undoStack.Count == 0) return;
        _redoStack.Push(Scene);
        Scene = _undoStack.Pop();
        UpdateUndoRedoState();
        StatusMessage = "Undid last board edit.";
        await PersistSessionAsync();
    }

    [RelayCommand]
    public async Task RedoAsync()
    {
        if (_redoStack.Count == 0) return;
        _undoStack.Push(Scene);
        Scene = _redoStack.Pop();
        UpdateUndoRedoState();
        StatusMessage = "Redid board edit.";
        await PersistSessionAsync();
    }
    private static string NormalizeTextAlignment(string? align) => align?.Trim().ToLowerInvariant() switch { "left" => "left", "right" => "right", _ => "center" };

    private static double MeasureCodeBlockHeight(int lineCount, double minHeight) =>
        Math.Max(minHeight, lineCount * CodeLineHeight + CodeBlockVerticalChrome);

    private static double MeasureUnfocusedFileBlockHeight(int lineCount) =>
        Math.Min(MaxUnfocusedFileBlockHeight, MeasureCodeBlockHeight(lineCount, MinFileBlockHeight));

    private int NextBlockZIndex() =>
        Scene.Blocks.Count == 0 ? 0 : Scene.Blocks.Max(b => b.ZIndex) + 1;

    private (double X, double Y) FindOpenCanvasPlacement(double width, double height, double? preferredX = null, double? preferredY = null)
    {
        if (preferredX.HasValue && preferredY.HasValue) return (preferredX.Value, preferredY.Value);
        if (Scene.Blocks.Count == 0) return (100, 100);
        double maxX = Scene.Blocks.Max(b => b.X + b.Width);
        double minY = Scene.Blocks.Min(b => b.Y);
        return (maxX + 60, minY);
    }
}
