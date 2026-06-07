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
    private readonly TagIndexStore _tagIndex;
    private readonly ReviewProgressStore _reviewProgress;
    private readonly ILogger<MainWindowViewModel> _logger;

    /// <summary>Exposed so the canvas autocomplete callback can read the latest tag/wiki-link vocabulary.</summary>
    internal TagIndexStore TagIndex => _tagIndex;

    /// <summary>Reading-progress store backing the reviewed-line tracking feature.</summary>
    internal ReviewProgressStore ReviewProgress => _reviewProgress;

    /// <summary>WorkspaceKey of the currently loaded workspace (branch-encoded), or null if none is open.</summary>
    internal string? CurrentWorkspaceKey => _currentSnapshot?.WorkspaceKey;

    private const double CodeLineHeight = 18.0;
    private const double CodeBlockVerticalChrome = 100.0;
    private const double MinFileBlockHeight = 1200.0;
    private const double MaxUnfocusedFileBlockHeight = 1200.0;
    private const double DefaultFileBlockWidth = 800.0;
    private const double MinScopedBlockHeight = 1200.0;

    // ---- Active canvas document (per-board Scene + undo) ----
    // The shell's Scene/_undoStack/_redoStack/_activeSession members below delegate to this instance,
    // so existing code keeps operating on "the focused board" while a second pane can hold another.
    private CanvasDocumentViewModel _activeCanvas = new(null);

    /// <summary>Live canvas documents keyed by board id. Keeping a document around across tab switches
    /// preserves its Scene + undo history (no reload), and lets a second pane render a different board.</summary>
    private readonly Dictionary<Guid, CanvasDocumentViewModel> _canvasDocs = new();

    /// <summary>The focused board's scene. Delegates to <see cref="_activeCanvas"/>; setting it routes
    /// through the document VM, whose change notification re-raises this property and runs the
    /// scene-changed side effects (selection refresh, board details).</summary>
    public RenderScene Scene
    {
        get => _activeCanvas.Scene;
        set => _activeCanvas.Scene = value;
    }

    private void HookActiveCanvas(CanvasDocumentViewModel doc)
    {
        if (ReferenceEquals(_activeCanvas, doc)) return;
        _activeCanvas.PropertyChanged -= OnActiveCanvasPropertyChanged;
        _activeCanvas = doc;
        _activeCanvas.PropertyChanged += OnActiveCanvasPropertyChanged;
        OnPropertyChanged(nameof(Scene));
        HandleSceneChanged(_activeCanvas.Scene);
        UpdateUndoRedoState();
    }

    private void OnActiveCanvasPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CanvasDocumentViewModel.Scene))
        {
            OnPropertyChanged(nameof(Scene));
            HandleSceneChanged(_activeCanvas.Scene);
        }
    }

    // ---- Observable state ----
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
    [ObservableProperty] private ShapeRenderStyle _globalShapeStyle = ShapeRenderStyle.Sketch;
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
    [ObservableProperty] private bool _isTransclusionPickerOpen;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPaletteQuery))]
    [NotifyPropertyChangedFor(nameof(PaletteNewBlockLabel))]
    [NotifyPropertyChangedFor(nameof(PaletteNewPageLabel))]
    [NotifyPropertyChangedFor(nameof(PaletteNewWhiteboardLabel))]
    private string _transclusionPickerFilter = string.Empty;

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
    [ObservableProperty] private string _ubwProjectName = "Untitled Boards";

    // Fields used by partial classes
    private string? _lastWorkspaceLoadPath;
    private string? _lastBoardFilePath;
    private WorkspaceSnapshot? _currentSnapshot;

    /// <summary>The active <b>canvas</b> board document (the one whose Scene is shown). Delegates to the
    /// active canvas document VM so the board pointer and its Scene/undo always move together. Distinct
    /// from <see cref="_activeOutlineSession"/> so a board and a page can be live at once in split view.</summary>
    private ReviewSession? _activeSession
    {
        get => _activeCanvas.Session;
        set => _activeCanvas.Session = value;
    }

    /// <summary>The active <b>outline</b> document (Page/Journal) whose body is shown. Independent of the
    /// active canvas board so both can be open at once when <see cref="IsSplitViewActive"/> is true.</summary>
    private ReviewSession? _activeOutlineSession;
    private CancellationTokenSource? _saveCts;
    private CancellationTokenSource? _loadCts;
    private int _loadVersion;
    private bool _syncingStyleTarget;
    private bool _isUpdatingSelection;

    [RelayCommand]
    public void ToggleBackground()
    {
        BackgroundMode = BackgroundMode == CanvasBackgroundMode.Dots
            ? CanvasBackgroundMode.Grid
            : CanvasBackgroundMode.Dots;
        StatusMessage = $"Background: {BackgroundMode}";
    }

    /// <summary>Flips the canvas-wide shape look between the hand-drawn ("Drawn") and crisp ("Sleek")
    /// styles. Every shape set to "Auto" follows this; shapes with an explicit per-shape style keep it.</summary>
    [RelayCommand]
    public void ToggleGlobalShapeStyle()
    {
        GlobalShapeStyle = GlobalShapeStyle == ShapeRenderStyle.Sketch
            ? ShapeRenderStyle.Vector
            : ShapeRenderStyle.Sketch;
        StatusMessage = GlobalShapeStyle == ShapeRenderStyle.Vector ? "Shapes: Sleek" : "Shapes: Drawn";
    }

    public ObservableCollection<string> AvailableBranches { get; } = new();
    public ObservableCollection<FileExplorerItemViewModel> ExplorerRoots { get; } = new();
    public ObservableCollection<FileExplorerItemViewModel> FileExplorerRoots { get; } = new();
    public ObservableCollection<ReviewSession> Sessions { get; } = new();
    public ObservableCollection<ProjectBrowserItemViewModel> ProjectBrowserRoots { get; } = new();
    public ObservableCollection<SymbolExplorerItemViewModel> SymbolRoots { get; } = new();
    public ObservableCollection<BoardSearchResultViewModel> BoardSearchResults { get; } = new();
    public ObservableCollection<BoardFileUsageViewModel> BoardFileUsages { get; } = new();
    public ObservableCollection<TransclusionCandidateViewModel> TransclusionCandidates { get; } = new();

    // Undo/redo live on the active canvas document so each board keeps its own history.
    private Stack<RenderScene> _undoStack => _activeCanvas.UndoStack;
    private Stack<RenderScene> _redoStack => _activeCanvas.RedoStack;

    // ---- Swim-lane colors ----
    private static readonly string[] LaneColors = { "#4A90D9", "#63D2A5", "#D49A4A", "#C07AB8", "#E05252", "#7CB8E0" };
    private int _nextLaneColor;

    public MainWindowViewModel(
        WorkspaceManager workspace,
        FileStructureService fileStructure,
        SemanticSpanService semanticSpan,
        SymbolScopeService symbolScope,
        SessionRepository sessions,
        TagIndexStore tagIndex,
        ReviewProgressStore reviewProgress,
        ILogger<MainWindowViewModel> logger)
    {
        _workspace = workspace;
        _fileStructure = fileStructure;
        _semanticSpan = semanticSpan;
        _symbolScope = symbolScope;
        _sessions = sessions;
        _tagIndex = tagIndex;
        _reviewProgress = reviewProgress;
        _logger = logger;
        _activeCanvas.PropertyChanged += OnActiveCanvasPropertyChanged;
        Sessions.CollectionChanged += (_, _) => RefreshProjectBrowser();
        RefreshProjectBrowser();
    }

    partial void OnUbwProjectNameChanged(string value) => RefreshProjectBrowser();

    /// <summary>
    /// Returns the cached autocomplete vocabulary for the current workspace. Falls
    /// back to an empty list before any workspace is open so callers don't have to
    /// branch on initialization order.
    /// </summary>
    internal IReadOnlyList<string> GetAutocompleteSuggestions(TagTokenKind kind, string prefix)
    {
        if (_currentSnapshot is null) return Array.Empty<string>();
        var snapshot = _tagIndex.GetSnapshot(_currentSnapshot.WorkspaceKey);

        // [[ ]] links resolve to documents, so offer real Page/Journal names first (the navigable
        // targets), then fall back to the typed wikilink vocabulary. #tags use the tag vocab only.
        IEnumerable<string> source = kind == TagTokenKind.Tag
            ? snapshot.Tags
            : DocumentLinkNames().Concat(snapshot.WikiLinks);

        var ordered = source.Distinct(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(prefix))
            ordered = ordered.Where(s => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return ordered.Take(8).ToArray();
    }

    /// <summary>Names of all navigable documents (pages + journals), newest-journal-first feel
    /// preserved by the Sessions order. Canvases aren't <c>[[link]]</c> targets.</summary>
    private IEnumerable<string> DocumentLinkNames() =>
        Sessions.Where(s => s.Kind != DocumentKind.Canvas).Select(s => s.Name);

    internal void RecordTagsFromBody(string? body)
    {
        if (_currentSnapshot is null || string.IsNullOrEmpty(body)) return;
        var (tags, links) = TagTokens.Extract(body);
        if (tags.Count == 0 && links.Count == 0) return;
        _tagIndex.Record(_currentSnapshot.WorkspaceKey, tags, links);
    }

    /// <summary>
    /// Called from the inspector when the user types tags manually (not via the
    /// outline body). Feeds the workspace TagIndex so those tags become autocomplete
    /// suggestions everywhere else.
    /// </summary>
    internal void RecordTagsFromInspector(IReadOnlyList<string> tags)
    {
        if (_currentSnapshot is null || tags.Count == 0) return;
        _tagIndex.Record(_currentSnapshot.WorkspaceKey, tags, Array.Empty<string>());
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

    /// <summary>
    /// Applies <paramref name="transform"/> to every selected block in one undo step (multi-edit).
    /// The transform receives each selected block plus an <c>isPrimary</c> flag that is true only for
    /// the block the inspector currently reflects (<see cref="SelectedBlock"/>). Style changes should
    /// be applied to all selected blocks; per-item edits (geometry, title, body) should be gated on
    /// <c>isPrimary</c> so they don't smear across the whole selection. When a single block is
    /// selected this collapses to the previous single-item behavior.
    /// </summary>
    internal void UpdateSelectedBlocks(Func<RenderBlock, bool, RenderBlock> transform, string description)
    {
        if (_isUpdatingSelection) return;
        string? primaryKey = SelectedBlock?.Key;
        var selectedKeys = Scene.Blocks.Where(b => b.IsSelected).Select(b => b.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selectedKeys.Count == 0) return;
        var blocks = Scene.Blocks.Select(b =>
            selectedKeys.Contains(b.Key)
                ? transform(b, primaryKey is not null && b.Key.Equals(primaryKey, StringComparison.OrdinalIgnoreCase))
                : b).ToList();
        SetSceneFromUserAction(Scene with { Blocks = blocks }, description);
        _ = PersistSessionAsync();
    }

    /// <summary>
    /// Applies <paramref name="transform"/> to every selected connection in one undo step (multi-edit).
    /// The transform receives each selected connection plus an <c>isPrimary</c> flag that is true only
    /// for the connector the inspector reflects (<see cref="SelectedConnection"/>). Style/type changes
    /// fan out to all; the label should be gated on <c>isPrimary</c>. Collapses to single-item behavior
    /// when only one connection is selected.
    /// </summary>
    internal void UpdateSelectedConnections(Func<RenderConnection, bool, RenderConnection> transform, string description)
    {
        if (_isUpdatingSelection) return;
        Guid? primaryId = SelectedConnection?.Id;
        var selectedIds = Scene.Connections.Where(c => c.IsSelected).Select(c => c.Id).ToHashSet();
        if (selectedIds.Count == 0) return;
        var connections = Scene.Connections.Select(c =>
            selectedIds.Contains(c.Id)
                ? transform(c, primaryId.HasValue && c.Id == primaryId.Value)
                : c).ToList();
        SetSceneFromUserAction(Scene with { Connections = connections }, description);
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

            Type? targetInspectorType = null;
            if (SelectedBlock is not null)
            {
                targetInspectorType = SelectedBlock.Kind switch
                {
                    BlockKind.Text => typeof(TextBlockInspectorViewModel),
                    BlockKind.Note => typeof(StickyNoteInspectorViewModel),
                    // Image cards reuse the Shape inspector so the Style tab (fill,
                    // stroke, corner radius, opacity, fill-style) is available — the
                    // image bitmap is rendered on top of the card chrome that those
                    // properties drive.
                    BlockKind.Shape or BlockKind.Container or BlockKind.Image => typeof(ShapeInspectorViewModel),
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

    private void HandleSceneChanged(RenderScene value)
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
