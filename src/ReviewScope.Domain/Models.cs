namespace ReviewScope.Domain;

// --- Enums ---

public enum BlockKind { File, Extract, Note }
public enum SemanticTokenKind { Plain, Keyword, Type, Function, Property, Field, String, Comment, Number, Preprocessor, Operator }

// --- Workspace / file discovery ---

public sealed record WorkspaceFileSummary(string ProjectName, string FilePath, string RelativePath, string ClusterKey);

public sealed record WorkspaceSnapshot(string WorkspacePath, string DisplayName, IReadOnlyList<WorkspaceFileSummary> Files)
{
    public string WorkspaceKey => WorkspacePath.ToLowerInvariant();
}

// --- Code analysis ---

public sealed record SemanticTokenSpan(int Line, int Column, int Length, SemanticTokenKind Kind, bool IsSymbolCandidate, string Text);
public sealed record MethodStructureInfo(string Name, string Kind, string Signature, int StartLine, int StartColumn, int EndLine, int EndColumn);
public sealed record TypeStructureInfo(string Name, string Kind, int StartLine, int StartColumn, int EndLine, int EndColumn, IReadOnlyList<MethodStructureInfo> Methods);
public sealed record FileStructureInfo(string FilePath, IReadOnlyList<TypeStructureInfo> Types);

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
    FocusedRange? Focused = null);

public sealed record FocusedRange(
    int StartLine,
    int EndLine,
    string SymbolName,
    double OriginalWidth,
    double OriginalHeight);

public sealed record ConnectionSnapshot(
    Guid Id,
    string SourceKey,
    string TargetKey,
    string? Label);

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
    DateTimeOffset UpdatedAt);

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
    FocusedRange? Focused = null);

public sealed record RenderConnection(
    Guid Id,
    string SourceKey,
    string TargetKey,
    string? Label = null,
    bool IsDimmed = false);

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

public sealed record RenderScene(
    IReadOnlyList<RenderBlock> Blocks,
    IReadOnlyList<RenderConnection> Connections,
    IReadOnlyList<RenderSwimLane> SwimLanes,
    IReadOnlyList<RenderAnnotation> Annotations)
{
    public static RenderScene Empty { get; } = new(
        Array.Empty<RenderBlock>(),
        Array.Empty<RenderConnection>(),
        Array.Empty<RenderSwimLane>(),
        Array.Empty<RenderAnnotation>());
}

// --- Workspace explorer ---

public sealed record ExplorerFile(string Name, string FilePath, string RelativePath);
public sealed record ExplorerFolder(string Name, string Path, IReadOnlyList<ExplorerFolder> SubFolders, IReadOnlyList<ExplorerFile> Files);

// --- Block extract request ---

public sealed record ExtractRequest(string FilePath, int StartLine, int EndLine, string FunctionName, string ContainingType);
