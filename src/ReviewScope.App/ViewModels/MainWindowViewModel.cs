using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using ReviewScope.Analysis;
using ReviewScope.App.Persistence;
using ReviewScope.Canvas;
using ReviewScope.Domain;
using System.Collections.ObjectModel;
using System.IO;

namespace ReviewScope.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private const double DefaultFileBlockWidth = 880;
    private const double MaxUnfocusedFileBlockWidth = 880;
    private const double MaxUnfocusedFileBlockHeight = 1280;
    private const double MinFileBlockHeight = 280;
    private const double MinScopedBlockHeight = 180;
    private const double CodeLineHeight = 18;
    private const double CodeBlockVerticalChrome = 108 + 30 + 24;
    private const double NewBlockStartX = 96;
    private const double NewBlockStartY = 72;
    private const double NewBlockGapX = 56;
    private const double NewBlockGapY = 72;
    private const double NewBlockRowWidth = 5200;

    private readonly WorkspaceManager _workspace;
    private readonly FileStructureService _fileStructure;
    private readonly SemanticSpanService _semanticSpan;
    private readonly SymbolScopeService _symbolScope;
    private readonly SessionRepository _sessions;
    private readonly ILogger<MainWindowViewModel> _logger;

    private WorkspaceSnapshot? _currentSnapshot;
    private string? _lastWorkspaceLoadPath;
    private ReviewSession? _activeSession;
    private CancellationTokenSource? _saveCts;

    // ---- Observable state ----
    [ObservableProperty] private RenderScene _scene = RenderScene.Empty;
    [ObservableProperty] private string _statusMessage = "Open a folder, .sln, or .csproj to start.";
    [ObservableProperty] private string _workspacePath = string.Empty;
    [ObservableProperty] private string _workspaceBranchName = string.Empty;
    [ObservableProperty] private ReviewSession? _selectedSession;
    [ObservableProperty] private string _sessionNameDraft = "New Session";
    [ObservableProperty] private Guid? _sessionSpawnAnimationId;
    [ObservableProperty] private string _selectedAnnotationContent = string.Empty;
    [ObservableProperty] private Guid? _editingAnnotationId;
    [ObservableProperty] private CanvasBackgroundMode _backgroundMode = CanvasBackgroundMode.Dots;
    [ObservableProperty] private bool _snapToGrid = true;
    [ObservableProperty] private bool _connectorsEnabled = true;
    [ObservableProperty] private RenderBlock? _selectedBlock;
    [ObservableProperty] private RenderConnection? _selectedConnection;
    [ObservableProperty] private RenderSwimLane? _selectedSwimLane;
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
    [ObservableProperty] private string _selectedTitleDraft = string.Empty;
    [ObservableProperty] private string _selectedBodyDraft = string.Empty;
    [ObservableProperty] private string _selectedFill = "#FFFFFF";
    [ObservableProperty] private string _selectedStroke = "#CBD5E1";
    [ObservableProperty] private string _selectedTextColor = "#111827";
    [ObservableProperty] private string _selectedStrokeWidth = "1.2";
    [ObservableProperty] private string _selectedTextAlignment = "Center";
    [ObservableProperty] private bool _selectedDashed;
    [ObservableProperty] private bool _selectedLocked;
    [ObservableProperty] private string _selectedConnectionLabel = string.Empty;
    [ObservableProperty] private string _selectedRouteKind = ConnectorRouteKind.Curved.ToString();
    [ObservableProperty] private string _selectedArrowKind = ConnectorArrowKind.Forward.ToString();
    [ObservableProperty] private string _selectedSymbolsHeader = "Symbols";
    [ObservableProperty] private bool _isSymbolsPanelVisible = true;
    [ObservableProperty] private string _boardSearchQuery = string.Empty;
    [ObservableProperty] private string _explorerSearchQuery = string.Empty;
    [ObservableProperty] private string _llmExportPreview = string.Empty;
    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private bool _canRedo;
    [ObservableProperty] private string _selectedBranch = string.Empty;

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

    private static double MeasureCodeBlockHeight(int lineCount, double minHeight) =>
        Math.Max(minHeight, lineCount * CodeLineHeight + CodeBlockVerticalChrome);

    [RelayCommand]
    public void CloseSymbolsPanel() => IsSymbolsPanelVisible = false;

    private static double MeasureUnfocusedFileBlockHeight(int lineCount) =>
        Math.Min(MaxUnfocusedFileBlockHeight, MeasureCodeBlockHeight(lineCount, MinFileBlockHeight));

    private int NextBlockZIndex() =>
        Scene.Blocks.Count == 0 ? 0 : Scene.Blocks.Max(b => b.ZIndex) + 1;

    private (double X, double Y) FindOpenCanvasPlacement(double width, double height, double? preferredX = null, double? preferredY = null)
    {
        double originX = preferredX ?? Scene.Blocks.Select(b => b.X).DefaultIfEmpty(NewBlockStartX).Min();
        double originY = preferredY ?? Scene.Blocks.Select(b => b.Y).DefaultIfEmpty(NewBlockStartY).Min();
        int columns = Math.Max(1, (int)Math.Floor(NewBlockRowWidth / Math.Max(1, width + NewBlockGapX)));

        for (int row = 0; row < 200; row++)
        {
            double y = originY + row * (height + NewBlockGapY);
            for (int col = 0; col < columns; col++)
            {
                double x = originX + col * (width + NewBlockGapX);
                if (!Scene.Blocks.Any(b => BlocksOverlapWithGap(x, y, width, height, b)))
                    return (x, y);
            }
        }

        double fallbackY = Scene.Blocks.Select(b => b.Y + b.Height).DefaultIfEmpty(originY).Max() + NewBlockGapY;
        return (originX, fallbackY);
    }

    private static bool BlocksOverlapWithGap(double x, double y, double width, double height, RenderBlock block)
    {
        return x < block.X + block.Width + NewBlockGapX
            && x + width + NewBlockGapX > block.X
            && y < block.Y + block.Height + NewBlockGapY
            && y + height + NewBlockGapY > block.Y;
    }

    private void UpdateSelectedObject(RenderScene scene)
    {
        SelectedBlock = scene.Blocks.FirstOrDefault(b => b.IsSelected);
        SelectedConnection = scene.Connections.FirstOrDefault(c => c.IsSelected);
        SelectedSwimLane = scene.SwimLanes.FirstOrDefault(l => l.IsSelected);
        SelectionIsBlock = SelectedBlock is not null;
        SelectionIsConnection = SelectedConnection is not null;
        HasBoardSelection = SelectionIsBlock || SelectionIsConnection || SelectedSwimLane is not null;

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
            SelectedStroke = style.Stroke;
            SelectedTextColor = style.Text;
            SelectedStrokeWidth = style.StrokeWidth.ToString("0.##");
            SelectedTextAlignment = NormalizeTextAlignment(style.TextAlign);
            SelectedDashed = style.Dashed;
            SelectedLocked = SelectedBlock.IsLocked;
            SelectedConnectionLabel = string.Empty;
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
            SelectedTextAlignment = "Center";
            SelectedDashed = SelectedConnection.Dashed;
            SelectedRouteKind = SelectedConnection.RouteKind.ToString();
            SelectedArrowKind = SelectedConnection.ArrowKind.ToString();
            SelectedTitleDraft = string.Empty;
            SelectedBodyDraft = string.Empty;
            SelectedLocked = false;
            return;
        }

        if (SelectedSwimLane is not null)
        {
            SelectedObjectTitle = SelectedSwimLane.Name;
            SelectedObjectKind = "Architecture frame";
            SelectedObjectPath = string.Empty;
            SelectedObjectLineRange = string.Empty;
            SetSelectedObjectData(SelectedSwimLane.X, SelectedSwimLane.Y, SelectedSwimLane.Width, SelectedSwimLane.Height);
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
        SelectedTextAlignment = "Center";
        SelectedLocked = false;
        ClearSelectedObjectData();
    }

    partial void OnSceneChanged(RenderScene value)
    {
        UpdateSelectedObject(value);
        RefreshBoardDetails();
    }

    private void SetSceneFromUserAction(RenderScene next, string? status = null)
    {
        _undoStack.Push(Scene);
        _redoStack.Clear();
        Scene = next;
        UpdateSelectedObject(Scene);
        RefreshBoardDetails();
        UpdateHistoryState();
        if (!string.IsNullOrWhiteSpace(status)) StatusMessage = status;
    }

    private void ResetHistory()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        UpdateHistoryState();
    }

    private void UpdateHistoryState()
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
        UpdateSelectedObject(Scene);
        RefreshBoardDetails();
        UpdateHistoryState();
        StatusMessage = "Undo";
        await PersistSessionAsync();
    }

    [RelayCommand]
    public async Task RedoAsync()
    {
        if (_redoStack.Count == 0) return;
        _undoStack.Push(Scene);
        Scene = _redoStack.Pop();
        UpdateSelectedObject(Scene);
        RefreshBoardDetails();
        UpdateHistoryState();
        StatusMessage = "Redo";
        await PersistSessionAsync();
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

    private static string NormalizeTextAlignment(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "left" => "Left",
            "right" => "Right",
            _ => "Center"
        };
}
