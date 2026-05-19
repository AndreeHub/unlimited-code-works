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
    // Code reload
    // -----------------------------------------------------------------------
    [RelayCommand]
    public async Task ReloadCodeAsync()
    {
        StatusMessage = "Reloading code blocks...";
        var updatedBlocks = new List<RenderBlock>();
        foreach (var block in Scene.Blocks)
        {
            if (block.FilePath is null || !File.Exists(block.FilePath))
            { updatedBlocks.Add(block); continue; }

            string body = await File.ReadAllTextAsync(block.FilePath);
            string[] allLines = body.Split('\n');

            if (block.Kind == BlockKind.File)
            {
                var tokens = await _semanticSpan.GetTokenSpansAsync(block.FilePath, 1, allLines.Length, CancellationToken.None);
                updatedBlocks.Add(block with { Body = body, SemanticTokens = tokens, EndLine = allLines.Length });
            }
            else if (block.Kind == BlockKind.Extract && block.StartLine.HasValue && block.EndLine.HasValue)
            {
                // Try to find the function by name using structure service
                var structure = await _fileStructure.GetFileStructureAsync(block.FilePath, CancellationToken.None);
                MethodStructureInfo? method = structure?.Types
                    .SelectMany(t => t.Methods)
                    .FirstOrDefault(m => block.Title.StartsWith(m.Name, StringComparison.OrdinalIgnoreCase));

                int newStart = method?.StartLine ?? block.StartLine.Value;
                int newEnd = method?.EndLine ?? block.EndLine.Value;
                string extracted = string.Join('\n', allLines.Skip(newStart - 1).Take(newEnd - newStart + 1));
                var tokens = await _semanticSpan.GetTokenSpansAsync(block.FilePath, newStart, newEnd, CancellationToken.None);
                double newH = MeasureCodeBlockHeight(newEnd - newStart + 1, MinScopedBlockHeight);
                updatedBlocks.Add(block with { Body = extracted, SemanticTokens = tokens, StartLine = newStart, EndLine = newEnd, Height = newH });
            }
            else { updatedBlocks.Add(block); }
        }

        Scene = Scene with { Blocks = updatedBlocks };
        StatusMessage = "Code reloaded. Annotations preserved.";
        await PersistSessionAsync();
}
}
