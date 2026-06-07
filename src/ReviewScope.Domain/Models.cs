namespace ReviewScope.Domain;

// --- Enums ---

public enum BlockKind { File, Extract, Note, MarkdownDoc, Shape, Text, Image, Container, Transclusion }
public enum SemanticTokenKind { Plain, Keyword, Type, Function, Property, Field, String, Comment, Number, Preprocessor, Operator }
public enum ConnectorRouteKind { Curved, Straight, Orthogonal }
public enum ConnectorArrowKind { None, Forward, Backward, Both }
public enum BoardLayerKind { Background, Architecture, CodeEvidence, Notes, Risks, Screenshots }
public enum ConnectionControlNodeKind { None, Middle, Source, Target }
public enum ConnectionEndpointKind { None, Source, Target }
public enum NoteResizeCorner { None, TopLeft, TopRight, BottomLeft, BottomRight }
public enum CanvasBackgroundMode { Dots, Grid }

/// <summary>How shape outlines/fills are rendered. <see cref="Sketch"/> is the hand-drawn,
/// perturbed "whiteboard" look; <see cref="Vector"/> is a crisp, precise look.</summary>
public enum ShapeRenderStyle { Sketch, Vector }

/// <summary>
/// What kind of document a <see cref="ReviewSession"/> represents in the project registry.
/// Canvas = the freeform board (blocks/connections); Page = a named Logseq-style outline;
/// Journal = a date-named outline page (one per day).
/// </summary>
public enum DocumentKind { Canvas, Page, Journal }

// --- Workspace / file discovery ---

public sealed record WorkspaceFileSummary(string ProjectName, string FilePath, string RelativePath, string ClusterKey);

public sealed record WorkspaceSnapshot(
    string WorkspacePath,
    string DisplayName,
    IReadOnlyList<WorkspaceFileSummary> Files,
    string? BranchName = null)
{
    public string WorkspaceKey => BranchName is null
        ? WorkspacePath.ToLowerInvariant()
        : $"{WorkspacePath.ToLowerInvariant()}::{BranchName.ToLowerInvariant()}";
}

// --- Code analysis ---

public sealed record SemanticTokenSpan(int Line, int Column, int Length, SemanticTokenKind Kind, bool IsSymbolCandidate, string Text);
public sealed record MethodStructureInfo(string Name, string Kind, string Signature, int StartLine, int StartColumn, int EndLine, int EndColumn);
public sealed record TypeStructureInfo(string Name, string Kind, int StartLine, int StartColumn, int EndLine, int EndColumn, IReadOnlyList<MethodStructureInfo> Methods);
public sealed record FileStructureInfo(string FilePath, IReadOnlyList<TypeStructureInfo> Types);

// --- Reading-progress tracking ---

/// <summary>
/// A contiguous, 1-based <b>inclusive</b> span of source lines the user has marked as read.
/// This is the canonical unit of reading progress; symbol- and file-level marking, the green
/// line tint, and any future coverage metric are all projections of a set of these.
/// </summary>
public sealed record ReviewedRange(int StartLine, int EndLine);

/// <summary>
/// Reviewed state for a single file, scoped to a workspace+branch (the branch is already encoded
/// in the owning <see cref="WorkspaceSnapshot.WorkspaceKey"/>). <see cref="ContentHash"/> is the
/// hash of the file text at the moment ranges were last recorded; a mismatch when the file is next
/// read means the file changed underneath us and the ranges should be treated as <i>stale</i>
/// (rendered differently) rather than silently trusted.
/// </summary>
public sealed record FileReviewProgress(
    string RelativePath,
    string ContentHash,
    IReadOnlyList<ReviewedRange> Ranges,
    DateTimeOffset UpdatedAt);

/// <summary>Immutable view of every reviewed file in a workspace, keyed by normalized relative path.</summary>
public sealed record ReviewProgressSnapshot(IReadOnlyDictionary<string, FileReviewProgress> Files)
{
    public static ReviewProgressSnapshot Empty { get; } = new(new Dictionary<string, FileReviewProgress>(StringComparer.OrdinalIgnoreCase));
}

// --- Session data (persisted) ---

public sealed record BlockPlacement(
    Guid Id,
    BlockKind Kind,
    string Key,
    string Title,
    string Subtitle,
    string? FilePath,
    int? StartLine,
    int? EndLine,
    double X,
    double Y,
    double Width,
    double Height,
    bool IsCollapsed,
    FocusedRange? Focused = null,
    int ZIndex = 0,
    string? LayerKey = null,
    bool IsLocked = false,
    string? ShapeType = null,
    BoardItemStyle? Style = null,
    BoardSourceBinding? Source = null,
    BoardGroupState? GroupState = null,
    string? Body = null,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyList<string>? WikiLinks = null,
    // For BlockKind.Transclusion: the persistent ^anchor id of the source bullet this block
    // mirrors. The mirrored text itself is NOT persisted (it's re-resolved on load); only this
    // pointer is, so the transclusion stays live when the source bullet changes.
    string? RefAnchorId = null,
    // For a Transclusion that embeds a WHOLE page (Logseq "page portal"): the referenced page's
    // name. When set, the block mirrors that page's entire outline (re-resolved on load = live).
    string? RefPageName = null);

public sealed record FocusedRange(
    int StartLine,
    int EndLine,
    string SymbolName,
    double OriginalWidth,
    double OriginalHeight);

public sealed record BoardItemStyle(
    string Fill = "#FFFFFF",
    string Stroke = "#CBD5E1",
    string Text = "#111827",
    double StrokeWidth = 1.2,
    bool Dashed = false,
    double Opacity = 1,
    double CornerRadius = 8,
    string TextAlign = "Center",
    string FillStyle = "hatch",
    double FontSize = 16,
    // --- Rich text (draw.io parity) ---
    string FontFamily = "Helvetica",
    bool Bold = false,
    bool Italic = false,
    bool Underline = false,
    bool Strikethrough = false,
    string VerticalAlign = "Middle",       // Top | Middle | Bottom
    string Position = "Center",            // Center | Top | Bottom | Left | Right (label position)
    string WritingDirection = "Automatic", // Automatic | LeftToRight | RightToLeft
    bool FontColorEnabled = true,
    bool BackgroundColorEnabled = false,
    bool BorderColorEnabled = false,
    bool ShadowEnabled = false,
    bool WordWrap = true,
    bool FormattedText = true,
    bool ConvertLabelsToSvg = false,
    bool AutoFontSize = false,
    bool OutlineEnabled = false,
    string OutlineCollapsedItems = "",
    double SpacingTop = 4,
    double SpacingRight = 4,
    double SpacingBottom = 4,
    double SpacingLeft = 4,
    double HatchOpacity = 0.6,
    // Per-shape render style override. null = inherit the canvas-wide default; "sketch" / "vector"
    // force that look for this shape regardless of the global setting.
    string? RenderStyle = null);

public sealed record BoardSourceBinding(
    string? AssetPath = null,
    string? SourcePath = null,
    string? SourceSection = null,
    string? SourceLanguage = null);

public sealed record BoardGroupState(
    double ExpandedX,
    double ExpandedY,
    double ExpandedWidth,
    double ExpandedHeight);

public sealed record BoardLayerSnapshot(
    Guid Id,
    string Key,
    string Name,
    BoardLayerKind Kind,
    bool IsVisible = true,
    bool IsLocked = false);

public sealed record ConnectionSnapshot(
    Guid Id,
    string SourceKey,
    string TargetKey,
    string? Label,
    int? SourceAnchorIndex = null,
    int? TargetAnchorIndex = null,
    double? ArrowPosition = null,
    bool ArrowForward = true,
    double? SourceControlX = null,
    double? SourceControlY = null,
    double? TargetControlX = null,
    double? TargetControlY = null,
    double? MidControlX = null,
    double? MidControlY = null,
    bool MidControlBends = false,
    ConnectorRouteKind RouteKind = ConnectorRouteKind.Curved,
    ConnectorArrowKind ArrowKind = ConnectorArrowKind.Forward,
    string Stroke = "#4584CB",
    bool Dashed = false,
    string? SourceLineId = null,
    string? TargetLineId = null);

public sealed record AnnotationSnapshot(
    Guid Id,
    string? AttachedToKey,
    string Content,
    double X,
    double Y,
    DateTimeOffset UpdatedAt);

public sealed record SwimLaneSnapshot(
    Guid Id,
    string Key,
    string Name,
    string Color,
    double X,
    double Y,
    double Width,
    double Height);

public sealed record ReviewSession(
    Guid Id,
    string Name,
    string WorkspaceKey,
    IReadOnlyList<BlockPlacement> Blocks,
    IReadOnlyList<ConnectionSnapshot> Connections,
    IReadOnlyList<AnnotationSnapshot> Annotations,
    IReadOnlyList<SwimLaneSnapshot> SwimLanes,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<BoardLayerSnapshot>? Layers = null,
    // --- Phase 3 document-registry fields (appended for JSON back-compat) ---
    // Canvas docs ignore the outline fields; Page/Journal docs keep Blocks empty and
    // store their Logseq-style outline as serialized markdown in OutlineBody, with the
    // collapsed-anchor set persisted (semicolon-separated) in OutlineCollapsed.
    DocumentKind Kind = DocumentKind.Canvas,
    string? OutlineBody = null,
    string? OutlineCollapsed = null);

// --- Render model (in-memory, passed to canvas) ---

public sealed record RenderBlock(
    Guid Id,
    string Key,
    BlockKind Kind,
    string Title,
    string Subtitle,
    double X,
    double Y,
    double Width,
    double Height,
    bool IsCollapsed = false,
    bool IsSelected = false,
    bool IsDimmed = false,
    string? Body = null,
    string? FilePath = null,
    int? StartLine = null,
    int? EndLine = null,
    IReadOnlyList<SemanticTokenSpan>? SemanticTokens = null,
    FocusedRange? Focused = null,
    int ZIndex = 0,
    string? LayerKey = null,
    bool IsLocked = false,
    string? ShapeType = null,
    BoardItemStyle? Style = null,
    BoardSourceBinding? Source = null,
    BoardGroupState? GroupState = null,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyList<string>? WikiLinks = null,
    // For BlockKind.Transclusion: the source bullet's persistent ^anchor id. Body carries the
    // resolved (mirrored) markdown for rendering; this pointer is what gets persisted.
    string? RefAnchorId = null,
    // For a page-portal Transclusion: the embedded page's name. Body carries the resolved page
    // outline for rendering; this pointer is what gets persisted.
    string? RefPageName = null);

/// <summary>
/// Reading-progress overlay for a single file, resolved live at render time (not persisted in the
/// scene): the reviewed source-line spans plus whether the file changed since they were recorded.
/// </summary>
public sealed record ReviewedFileState(IReadOnlyList<ReviewedRange> Ranges, bool IsStale)
{
    public static ReviewedFileState None { get; } = new(Array.Empty<ReviewedRange>(), false);
}

public sealed record RenderConnection(
    Guid Id,
    string SourceKey,
    string TargetKey,
    string? Label = null,
    bool IsDimmed = false,
    bool IsSelected = false,
    int? SourceAnchorIndex = null,
    int? TargetAnchorIndex = null,
    double? ArrowPosition = null,
    bool ArrowForward = true,
    double? SourceControlX = null,
    double? SourceControlY = null,
    double? TargetControlX = null,
    double? TargetControlY = null,
    double? MidControlX = null,
    double? MidControlY = null,
    bool MidControlBends = false,
    ConnectorRouteKind RouteKind = ConnectorRouteKind.Curved,
    ConnectorArrowKind ArrowKind = ConnectorArrowKind.Forward,
    string Stroke = "#4584CB",
    bool Dashed = false,
    string? SourceLineId = null,
    string? TargetLineId = null);

public sealed record RenderSwimLane(
    Guid Id,
    string Key,
    string Name,
    string Color,
    double X,
    double Y,
    double Width,
    double Height,
    bool IsSelected = false);

public sealed record RenderAnnotation(
    Guid Id,
    string? AttachedToKey,
    string Content,
    double X,
    double Y);

public sealed record RenderBoardLayer(
    Guid Id,
    string Key,
    string Name,
    BoardLayerKind Kind,
    bool IsVisible = true,
    bool IsLocked = false);

public sealed record RenderScene(
    IReadOnlyList<RenderBlock> Blocks,
    IReadOnlyList<RenderConnection> Connections,
    IReadOnlyList<RenderSwimLane> SwimLanes,
    IReadOnlyList<RenderAnnotation> Annotations,
    IReadOnlyList<RenderBoardLayer>? Layers = null)
{
    public static RenderScene Empty { get; } = new(
        Array.Empty<RenderBlock>(),
        Array.Empty<RenderConnection>(),
        Array.Empty<RenderSwimLane>(),
        Array.Empty<RenderAnnotation>(),
        Array.Empty<RenderBoardLayer>());
}

// --- Workspace explorer ---

public sealed record ExplorerFile(string Name, string FilePath, string RelativePath);
public sealed record ExplorerFolder(string Name, string Path, IReadOnlyList<ExplorerFolder> SubFolders, IReadOnlyList<ExplorerFile> Files);

// --- Block extract request ---

public sealed record ExtractRequest(string FilePath, int StartLine, int EndLine, string FunctionName, string ContainingType);
