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
    // Export annotations
    // -----------------------------------------------------------------------
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
            Title = "Export Annotations",
            Filter = "Markdown|*.md|Text|*.txt",
            FileName = $"review-notes-{DateTime.Now:yyyy-MM-dd}"
        };
        if (dlg.ShowDialog() != true) return;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Code Review Notes");
        sb.AppendLine($"Session: {_activeSession?.Name}  â€¢  {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();

        foreach (var annotation in Scene.Annotations)
        {
            string? attachedTitle = annotation.AttachedToKey is not null
                ? Scene.Blocks.FirstOrDefault(b => b.Key == annotation.AttachedToKey)?.Title
                : null;

            if (attachedTitle is not null)
                sb.AppendLine($"## Note on `{attachedTitle}`");
            else
                sb.AppendLine("## Free Note");

            sb.AppendLine(annotation.Content);
            sb.AppendLine();
        }

        foreach (var noteBlock in Scene.Blocks.Where(b => b.Kind == BlockKind.Note))
        {
            sb.AppendLine($"## {noteBlock.Title}");
            if (!string.IsNullOrWhiteSpace(noteBlock.Body))
                sb.AppendLine(noteBlock.Body);
            sb.AppendLine();
        }

        await File.WriteAllTextAsync(dlg.FileName, sb.ToString());
        StatusMessage = $"Exported to {Path.GetFileName(dlg.FileName)}";
}
}
