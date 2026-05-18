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
    public async Task AddFileToCanvasAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;

        string key = $"file::{filePath.ToLowerInvariant()}";
        if (Scene.Blocks.Any(b => b.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = "File already on canvas.";
            return;
        }

        StatusMessage = "Loading fileâ€¦";
        string body = await File.ReadAllTextAsync(filePath);
        var tokens = await _semanticSpan.GetTokenSpansAsync(filePath, 1, body.Split('\n').Length, CancellationToken.None);

        double x = 96 + Scene.Blocks.Count * 40;
        double y = 72 + Scene.Blocks.Count * 20;
        double width = DefaultFileBlockWidth;
        double lineCount = body.Split('\n').Length;
        double height = MeasureUnfocusedFileBlockHeight((int)lineCount);

        var block = new RenderBlock(
            Guid.NewGuid(), key, BlockKind.File,
            Path.GetFileName(filePath),
            GetRelativePath(filePath),
            x, y, width, height,
            FilePath: filePath,
            StartLine: 1,
            EndLine: (int)lineCount,
            Body: body,
            SemanticTokens: tokens);

        var blocks = Scene.Blocks.Append(block).ToList();
        Scene = Scene with { Blocks = blocks };
        StatusMessage = $"Added: {Path.GetFileName(filePath)}";
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
        var scope = await _symbolScope.GetSymbolScopeAsync(
            args.SourceBlock.FilePath!, args.Line, args.Column, CancellationToken.None);
        if (scope is null)
        {
            StatusMessage = "No function found at that position.";
            return;
        }

        string body = await File.ReadAllTextAsync(args.SourceBlock.FilePath!);
        string[] allLines = body.Split('\n');
        string extractedBody = string.Join('\n', allLines.Skip(scope.Value.StartLine - 1).Take(scope.Value.EndLine - scope.Value.StartLine + 1));

        var tokens = await _semanticSpan.GetTokenSpansAsync(
            args.SourceBlock.FilePath!, scope.Value.StartLine, scope.Value.EndLine, CancellationToken.None);

        string key = $"extract::{args.SourceBlock.FilePath!.ToLowerInvariant()}::{scope.Value.SymbolName}::{scope.Value.StartLine}";
        if (Scene.Blocks.Any(b => b.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = "This function is already extracted.";
            return;
        }

        double x = args.SourceBlock.X + args.SourceBlock.Width + 80;
        double y = args.SourceBlock.Y;
        double lineCount = scope.Value.EndLine - scope.Value.StartLine + 1;
        double height = MeasureCodeBlockHeight((int)lineCount, MinScopedBlockHeight);

        var extract = new RenderBlock(
            Guid.NewGuid(), key, BlockKind.Extract,
            $"{scope.Value.SymbolName}(â€¦)",
            $"{scope.Value.ContainingType}  â€¢  {Path.GetFileName(args.SourceBlock.FilePath!)}",
            x, y, 640, height,
            FilePath: args.SourceBlock.FilePath,
            StartLine: scope.Value.StartLine,
            EndLine: scope.Value.EndLine,
            Body: extractedBody,
            SemanticTokens: tokens);

        var blocks = Scene.Blocks.Append(extract).ToList();
        // Optionally auto-connect
        var connection = new RenderConnection(Guid.NewGuid(), args.SourceBlock.Key, key);
        var connections = Scene.Connections.Append(connection).ToList();
        Scene = Scene with { Blocks = blocks, Connections = connections };

        StatusMessage = $"Extracted: {scope.Value.SymbolName}";
        await PersistSessionAsync();
    }

    // -----------------------------------------------------------------------
    // Focus mode (Ctrl+click)  â€” shrink the source block to show only the function
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
        Scene = Scene with { Blocks = blocks };
        StatusMessage = $"Focused: {scope.Value.SymbolName}";
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
        Scene = Scene with { Blocks = blocks };
        StatusMessage = $"Restored: {restored.Title}";
        await PersistSessionAsync();
}
}
