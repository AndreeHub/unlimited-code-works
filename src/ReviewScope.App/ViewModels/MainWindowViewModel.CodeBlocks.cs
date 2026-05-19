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

public sealed partial class MainWindowViewModel
{
    // -----------------------------------------------------------------------
    // File drag onto canvas
    // -----------------------------------------------------------------------
    public async Task AddFileToCanvasAsync(string filePath, double? x = null, double? y = null)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;

        string key = $"file::{filePath.ToLowerInvariant()}";
        if (Scene.Blocks.Any(b => b.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = "File already on canvas.";
            return;
        }

        StatusMessage = "Loading file...";
        string body = await File.ReadAllTextAsync(filePath);
        bool isCSharp = Path.GetExtension(filePath).Equals(".cs", StringComparison.OrdinalIgnoreCase);
        bool isMarkdown = Path.GetExtension(filePath).Equals(".md", StringComparison.OrdinalIgnoreCase);
        var tokens = isCSharp
            ? await _semanticSpan.GetTokenSpansAsync(filePath, 1, body.Split('\n').Length, CancellationToken.None)
            : Array.Empty<SemanticTokenSpan>();

        double width = DefaultFileBlockWidth;
        double lineCount = body.Split('\n').Length;
        double height = MeasureUnfocusedFileBlockHeight((int)lineCount);
        var placement = FindOpenCanvasPlacement(width, height, x, y);

        var block = new RenderBlock(
            Guid.NewGuid(), key, BlockKind.File,
            Path.GetFileName(filePath),
            GetRelativePath(filePath),
            placement.X, placement.Y, width, height,
            FilePath: filePath,
            StartLine: 1,
            EndLine: (int)lineCount,
            Body: body,
            SemanticTokens: tokens,
            LayerKey: isMarkdown ? "layer::architecture" : "layer::code",
            ShapeType: isMarkdown ? "markdown" : Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant(),
            Source: new BoardSourceBinding(SourcePath: filePath, SourceLanguage: isCSharp ? "csharp" : Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant()));

        var blocks = Scene.Blocks.Append(block).ToList();
        SetSceneFromUserAction(Scene with { Blocks = blocks }, $"Added: {Path.GetFileName(filePath)}");
        await PersistSessionAsync();
    }

    public void SetExplorerExpanded(bool expanded)
    {
        foreach (var root in ExplorerRoots)
            SetExpandedRecursive(root, expanded);
    }

    private static void SetExpandedRecursive(FileExplorerItemViewModel item, bool expanded)
    {
        item.IsExpanded = expanded;
        foreach (var child in item.Children)
            SetExpandedRecursive(child, expanded);
    }

    public async Task AddSymbolToCanvasAsync(SymbolExplorerItemViewModel symbol)
    {
        string? filePath = CurrentSymbolFilePath;
        if (filePath is null || !symbol.StartLine.HasValue || !symbol.EndLine.HasValue) return;
        if (!File.Exists(filePath)) return;

        int start = symbol.StartLine.Value;
        int end = symbol.EndLine.Value;

        // Use a file key so the block merges with an existing file card if present.
        string fileKey = $"file::{filePath.ToLowerInvariant()}";
        if (Scene.Blocks.Any(b => b.Key.Equals(fileKey, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = "File already on canvas - use Ctrl+click inside it to focus the symbol.";
            return;
        }

        StatusMessage = "Loading...";
        string body = await File.ReadAllTextAsync(filePath);
        int totalLines = body.Split('\n').Length;

        bool isCSharp = Path.GetExtension(filePath).Equals(".cs", StringComparison.OrdinalIgnoreCase);
        var tokens = isCSharp
            ? await _semanticSpan.GetTokenSpansAsync(filePath, 1, totalLines, CancellationToken.None)
            : Array.Empty<SemanticTokenSpan>();

        double fullWidth = DefaultFileBlockWidth;
        double fullHeight = MeasureUnfocusedFileBlockHeight(totalLines);
        double focusedHeight = MeasureCodeBlockHeight(end - start + 1, MinScopedBlockHeight);
        var placement = FindOpenCanvasPlacement(fullWidth, focusedHeight);

        // Create a full file block pre-focused on the symbol range.
        // The user can click the restore button (↗) to expand back to the full file.
        var focused = new FocusedRange(start, end, symbol.Name, fullWidth, fullHeight);
        var block = new RenderBlock(
            Guid.NewGuid(), fileKey, BlockKind.File,
            Path.GetFileName(filePath),
            GetRelativePath(filePath),
            placement.X, placement.Y, fullWidth, focusedHeight,
            FilePath: filePath,
            StartLine: 1,
            EndLine: totalLines,
            Body: body,
            SemanticTokens: tokens,
            Focused: focused,
            ShapeType: isCSharp ? "csharp" : Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant(),
            Source: new BoardSourceBinding(SourcePath: filePath, SourceLanguage: isCSharp ? "csharp" : "text"));

        var blocks = Scene.Blocks.Append(block).ToList();
        SetSceneFromUserAction(Scene with { Blocks = blocks }, $"Added: {symbol.Name}");
        await PersistSessionAsync();
    }

    private string GetRelativePath(string filePath)
    {
        if (_currentSnapshot is null) return filePath;
        string root = Path.GetDirectoryName(_workspace.CurrentSession?.ResolvedPath ?? filePath) ?? filePath;
        return Path.GetRelativePath(root, filePath).Replace('\\', '/');
    }

    // -----------------------------------------------------------------------
    // Function extraction
    // -----------------------------------------------------------------------
    public async Task HandleExtractRequestAsync(ExtractRequestedArgs args)
    {
        var definitionScope = await _symbolScope.GetFunctionDefinitionScopeAsync(
            args.SourceBlock.FilePath!, args.Line, args.Column, CancellationToken.None);

        string sourceFilePath;
        int startLine;
        int endLine;
        string symbolName;
        string containingType;

        if (definitionScope is not null)
        {
            sourceFilePath = definitionScope.Value.FilePath;
            startLine = definitionScope.Value.StartLine;
            endLine = definitionScope.Value.EndLine;
            symbolName = definitionScope.Value.SymbolName;
            containingType = definitionScope.Value.ContainingType;
        }
        else
        {
            var scope = await _symbolScope.GetSymbolScopeAsync(
                args.SourceBlock.FilePath!, args.Line, args.Column, CancellationToken.None);
            if (scope is null)
            {
                StatusMessage = "No function found at that position.";
                return;
            }

            sourceFilePath = args.SourceBlock.FilePath!;
            startLine = scope.Value.StartLine;
            endLine = scope.Value.EndLine;
            symbolName = scope.Value.SymbolName;
            containingType = scope.Value.ContainingType;
        }

        if (!File.Exists(sourceFilePath))
        {
            StatusMessage = "Function source file was not found.";
            return;
        }

        string body = await File.ReadAllTextAsync(sourceFilePath);
        string[] allLines = body.Split('\n');
        string extractedBody = string.Join('\n', allLines.Skip(startLine - 1).Take(endLine - startLine + 1));

        var tokens = await _semanticSpan.GetTokenSpansAsync(
            sourceFilePath, startLine, endLine, CancellationToken.None);

        string key = $"extract::{sourceFilePath.ToLowerInvariant()}::{symbolName}::{startLine}";
        if (Scene.Blocks.Any(b => b.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = "This function is already extracted.";
            return;
        }

        double x = args.SourceBlock.X + args.SourceBlock.Width + 80;
        double y = args.SourceBlock.Y;
        double lineCount = endLine - startLine + 1;
        double height = MeasureCodeBlockHeight((int)lineCount, MinScopedBlockHeight);

        var extract = new RenderBlock(
            Guid.NewGuid(), key, BlockKind.Extract,
            $"{symbolName}(...)",
            $"{containingType}  *  {Path.GetFileName(sourceFilePath)}",
            x, y, 640, height,
            FilePath: sourceFilePath,
            StartLine: startLine,
            EndLine: endLine,
            Body: extractedBody,
            SemanticTokens: tokens);

        var blocks = Scene.Blocks.Append(extract).ToList();
        var connection = CreateCurvedExtractConnection(args.SourceBlock, extract);
        var connections = Scene.Connections.Append(connection).ToList();
        SetSceneFromUserAction(Scene with { Blocks = blocks, Connections = connections }, $"Extracted: {symbolName}");
        await PersistSessionAsync();
    }

    private static RenderConnection CreateCurvedExtractConnection(RenderBlock source, RenderBlock target)
    {
        var (sourceAnchor, targetAnchor) = ChooseCurvedConnectionAnchors(source, target);
        return new RenderConnection(
            Guid.NewGuid(),
            source.Key,
            target.Key,
            SourceAnchorIndex: sourceAnchor,
            TargetAnchorIndex: targetAnchor,
            RouteKind: ConnectorRouteKind.Curved,
            ArrowKind: ConnectorArrowKind.Forward);
    }

    private static (int SourceAnchor, int TargetAnchor) ChooseCurvedConnectionAnchors(RenderBlock source, RenderBlock target)
    {
        double sourceCx = source.X + source.Width / 2;
        double sourceCy = source.Y + source.Height / 2;
        double targetCx = target.X + target.Width / 2;
        double targetCy = target.Y + target.Height / 2;
        double dx = targetCx - sourceCx;
        double dy = targetCy - sourceCy;

        if (Math.Abs(dy) > Math.Abs(dx) * 0.75)
            return dy < 0 ? (2, 9) : (9, 2);

        return dx >= 0 ? (5, 14) : (14, 5);
    }

    // -----------------------------------------------------------------------
    // Focus mode (Ctrl+click) - shrink the source block to show only the function
    // -----------------------------------------------------------------------
    public async Task HandleFocusRequestAsync(FocusRequestedArgs args)
    {
        var scope = await _symbolScope.GetSymbolScopeAsync(
            args.SourceBlock.FilePath!, args.Line, args.Column, CancellationToken.None);
        if (scope is null)
        {
            StatusMessage = "No function found at that position.";
            return;
        }

        // Remember the *un-focused* size so the restore button can return to it.
        double originalWidth = args.SourceBlock.Focused?.OriginalWidth ?? args.SourceBlock.Width;
        double originalHeight = args.SourceBlock.Focused?.OriginalHeight ?? args.SourceBlock.Height;

        int lineCount = scope.Value.EndLine - scope.Value.StartLine + 1;
        const double maxFocusedW = 720;
        double focusedW = Math.Min(originalWidth, maxFocusedW);
        double focusedH = MeasureCodeBlockHeight(lineCount, 220);

        var focused = new FocusedRange(
            scope.Value.StartLine, scope.Value.EndLine, scope.Value.SymbolName,
            originalWidth, originalHeight);

        var updated = args.SourceBlock with
        {
            Focused = focused,
            Width = focusedW,
            Height = focusedH
        };

        var blocks = Scene.Blocks.Select(b => b.Key.Equals(updated.Key, StringComparison.OrdinalIgnoreCase) ? updated : b).ToList();
        SetSceneFromUserAction(Scene with { Blocks = blocks }, $"Focused: {scope.Value.SymbolName}");
        await PersistSessionAsync();
    }

    public async Task HandleRestoreAsync(RestoreRequestedArgs args)
    {
        if (args.Block.Focused is null) return;
        var restored = args.Block with
        {
            Focused = null,
            Width = args.Block.Focused.OriginalWidth,
            Height = args.Block.Focused.OriginalHeight
        };
        var blocks = Scene.Blocks.Select(b => b.Key.Equals(restored.Key, StringComparison.OrdinalIgnoreCase) ? restored : b).ToList();
        SetSceneFromUserAction(Scene with { Blocks = blocks }, $"Restored: {restored.Title}");
        await PersistSessionAsync();
}
}
