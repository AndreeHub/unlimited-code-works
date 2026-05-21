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
    [RelayCommand]
    public async Task ExportAnnotationsAsync()
    {
        if (!Scene.Annotations.Any() && !Scene.Blocks.Any(b => b.Kind == BlockKind.Note))
        {
            StatusMessage = "No annotations to export.";
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title = "Export Review Notes",
            Filter = "Markdown|*.md|Text|*.txt",
            FileName = $"unlimited-code-works-{DateTime.Now:yyyy-MM-dd}"
        };
        if (dlg.ShowDialog() != true) return;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# unlimited-code-works Review Export");
        sb.AppendLine();
        sb.AppendLine($"Project: {_currentSnapshot?.DisplayName ?? "Unknown"}");
        sb.AppendLine("Branch: unknown");
        sb.AppendLine($"Board: {_activeSession?.Name ?? "New Board"}");
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        int index = 1;
        foreach (var annotation in Scene.Annotations)
        {
            var attachedBlock = annotation.AttachedToKey is not null
                ? Scene.Blocks.FirstOrDefault(b => b.Key.Equals(annotation.AttachedToKey, StringComparison.OrdinalIgnoreCase))
                : null;

            sb.AppendLine(attachedBlock is not null
                ? $"## {index}. {attachedBlock.Title}"
                : $"## {index}. Free Note");
            sb.AppendLine();
            AppendBlockContext(sb, attachedBlock);
            sb.AppendLine("### Review Notes");
            sb.AppendLine();
            sb.AppendLine(annotation.Content);
            sb.AppendLine();
            AppendRelatedBlocks(sb, attachedBlock);
            sb.AppendLine("---");
            sb.AppendLine();
            index++;
        }

        foreach (var noteBlock in Scene.Blocks.Where(b => b.Kind == BlockKind.Note))
        {
            sb.AppendLine($"## {index}. {noteBlock.Title}");
            sb.AppendLine();
            sb.AppendLine("### Review Notes");
            sb.AppendLine();
            sb.AppendLine(string.IsNullOrWhiteSpace(noteBlock.Body) ? noteBlock.Title : noteBlock.Body);
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            index++;
        }

        await File.WriteAllTextAsync(dlg.FileName, sb.ToString());
        StatusMessage = $"Exported to {Path.GetFileName(dlg.FileName)}";
    }

    private void AppendBlockContext(System.Text.StringBuilder sb, RenderBlock? block)
    {
        if (block is null) return;

        sb.AppendLine($"File: `{(block.FilePath is null ? "unknown" : GetRelativePath(block.FilePath))}`");
        sb.AppendLine($"Object: `{block.Title}`");
        sb.AppendLine($"Type: {block.Kind}");
        if (block.StartLine.HasValue && block.EndLine.HasValue)
            sb.AppendLine($"Lines: {block.StartLine}-{block.EndLine}");
        sb.AppendLine();

        if (string.IsNullOrWhiteSpace(block.Body)) return;

        sb.AppendLine("### Current Code");
        sb.AppendLine();
        sb.AppendLine("```csharp");
        sb.AppendLine(block.Body.TrimEnd());
        sb.AppendLine("```");
        sb.AppendLine();
    }

    private void AppendRelatedBlocks(System.Text.StringBuilder sb, RenderBlock? block)
    {
        if (block is null) return;

        var incoming = Scene.Connections
            .Where(c => c.TargetKey.Equals(block.Key, StringComparison.OrdinalIgnoreCase))
            .Select(c => Scene.Blocks.FirstOrDefault(b => b.Key.Equals(c.SourceKey, StringComparison.OrdinalIgnoreCase))?.Title)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();
        var outgoing = Scene.Connections
            .Where(c => c.SourceKey.Equals(block.Key, StringComparison.OrdinalIgnoreCase))
            .Select(c => Scene.Blocks.FirstOrDefault(b => b.Key.Equals(c.TargetKey, StringComparison.OrdinalIgnoreCase))?.Title)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        if (incoming.Count == 0 && outgoing.Count == 0) return;

        sb.AppendLine("### Related Blocks");
        sb.AppendLine();
        foreach (var title in incoming)
            sb.AppendLine($"- Called from: {title}");
        foreach (var title in outgoing)
            sb.AppendLine($"- Connects to: {title}");
        sb.AppendLine();
    }
}
