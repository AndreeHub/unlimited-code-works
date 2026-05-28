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
    // Scene changes from canvas
    // -----------------------------------------------------------------------
    public async Task OnSceneChangedByCanvas(RenderScene previousScene, RenderScene newScene)
    {
        var (sceneWithTags, _) = SyncBlockTagsFromBody(previousScene, newScene);
        SetSceneFromUserAction(sceneWithTags, undoBase: previousScene);
        await PersistSessionAsync();
    }

    /// <summary>
    /// For every outline-editable block whose body changed, re-parse #tags and
    /// [[wiki links]] and refresh the block's Tags/WikiLinks fields so the inspector
    /// stays in sync. Also feeds the per-workspace TagIndex so newly typed tokens
    /// become autocomplete suggestions.
    /// </summary>
    private (RenderScene Scene, bool Recorded) SyncBlockTagsFromBody(RenderScene previous, RenderScene next)
    {
        if (next.Blocks.Count == 0) return (next, false);

        var previousByKey = previous.Blocks.ToDictionary(b => b.Key, StringComparer.OrdinalIgnoreCase);
        List<RenderBlock>? updated = null;
        bool anyRecorded = false;

        for (int i = 0; i < next.Blocks.Count; i++)
        {
            var block = next.Blocks[i];
            if (block.Kind is not (BlockKind.Note or BlockKind.Text)) continue;

            previousByKey.TryGetValue(block.Key, out var prevBlock);
            string? newBody = block.Body ?? string.Empty;
            string? oldBody = prevBlock?.Body ?? string.Empty;
            if (ReferenceEquals(prevBlock, null) is false && string.Equals(newBody, oldBody, StringComparison.Ordinal))
                continue;

            var (tags, links) = TagTokens.Extract(newBody);
            if (!TagListEquals(tags, block.Tags) || !TagListEquals(links, block.WikiLinks))
            {
                updated ??= new List<RenderBlock>(next.Blocks);
                updated[i] = block with { Tags = tags.Count == 0 ? null : tags, WikiLinks = links.Count == 0 ? null : links };
            }

            if ((tags.Count > 0 || links.Count > 0) && _currentSnapshot is not null)
            {
                _tagIndex.Record(_currentSnapshot.WorkspaceKey, tags, links);
                anyRecorded = true;
            }
        }

        return updated is null ? (next, anyRecorded) : (next with { Blocks = updated }, anyRecorded);
    }

    private static bool TagListEquals(IReadOnlyList<string> next, IReadOnlyList<string>? existing)
    {
        if (existing is null) return next.Count == 0;
        if (existing.Count != next.Count) return false;
        for (int i = 0; i < next.Count; i++)
            if (!string.Equals(existing[i], next[i], StringComparison.Ordinal))
                return false;
        return true;
    }
}
