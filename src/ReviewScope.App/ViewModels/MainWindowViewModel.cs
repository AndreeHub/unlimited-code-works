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
    private const double MaxUnfocusedFileBlockHeight = 640;
    private const double MinFileBlockHeight = 280;
    private const double MinScopedBlockHeight = 180;
    private const double CodeLineHeight = 18;
    private const double CodeBlockVerticalChrome = 108 + 30 + 24;

    private readonly WorkspaceManager _workspace;
    private readonly FileStructureService _fileStructure;
    private readonly SemanticSpanService _semanticSpan;
    private readonly SymbolScopeService _symbolScope;
    private readonly SessionRepository _sessions;
    private readonly ILogger<MainWindowViewModel> _logger;

    private WorkspaceSnapshot? _currentSnapshot;
    private ReviewSession? _activeSession;
    private CancellationTokenSource? _saveCts;

    // ---- Observable state ----
    [ObservableProperty] private RenderScene _scene = RenderScene.Empty;
    [ObservableProperty] private string _statusMessage = "Open a folder, .sln, or .csproj to start.";
    [ObservableProperty] private string _workspacePath = string.Empty;
    [ObservableProperty] private ReviewSession? _selectedSession;
    [ObservableProperty] private string _sessionNameDraft = "New Session";
    [ObservableProperty] private string _selectedAnnotationContent = string.Empty;
    [ObservableProperty] private Guid? _editingAnnotationId;
    [ObservableProperty] private CanvasBackgroundMode _backgroundMode = CanvasBackgroundMode.Dots;

    [RelayCommand]
    public void ToggleBackground()
    {
        BackgroundMode = BackgroundMode == CanvasBackgroundMode.Dots
            ? CanvasBackgroundMode.Grid
            : CanvasBackgroundMode.Dots;
        StatusMessage = $"Background: {BackgroundMode}";
    }

    public ObservableCollection<FileExplorerItemViewModel> ExplorerRoots { get; } = new();
    public ObservableCollection<ReviewSession> Sessions { get; } = new();

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

    private static double MeasureUnfocusedFileBlockHeight(int lineCount) =>
        Math.Min(MaxUnfocusedFileBlockHeight, MeasureCodeBlockHeight(lineCount, MinFileBlockHeight));
}
