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
    // Session management
    // -----------------------------------------------------------------------
    private async Task LoadSessionsAsync(string workspaceKey, CancellationToken ct)
    {
        Sessions.Clear();
        var loaded = await _sessions.GetSessionsAsync(workspaceKey, ct);
        foreach (var s in loaded) Sessions.Add(s);

        if (Sessions.Count == 0)
            await CreateNewSessionAsync();
        else
            await ActivateSessionAsync(Sessions[0]);
    }

    [RelayCommand]
    public async Task CreateNewSessionAsync()
    {
        if (_currentSnapshot is null) return;
        var session = new ReviewSession(
            Guid.NewGuid(), SessionNameDraft, _currentSnapshot.WorkspaceKey,
            Array.Empty<BlockPlacement>(), Array.Empty<ConnectionSnapshot>(),
            Array.Empty<AnnotationSnapshot>(), Array.Empty<SwimLaneSnapshot>(),
            DateTimeOffset.UtcNow);
        Sessions.Insert(0, session);
        await _sessions.SaveSessionAsync(session, CancellationToken.None);
        await ActivateSessionAsync(session, hydrateCode: false);
    }

    [RelayCommand]
    public async Task ActivateSelectedSessionAsync()
    {
        if (SelectedSession is null) return;
        await ActivateSessionAsync(SelectedSession);
    }

    private async Task ActivateSessionAsync(ReviewSession session, bool hydrateCode = true)
    {
        _activeSession = session;
        SelectedSession = session;
        Scene = BuildSceneFromSession(session);
        UpdateSelectedObject(Scene);
        StatusMessage = $"Session: {session.Name}";
        if (hydrateCode)
            await HydrateCodeBlocksAsync();
    }

    private static RenderScene BuildSceneFromSession(ReviewSession session)
    {
        var annotationsById = session.Annotations.ToDictionary(a => a.Id);
        var blocks = session.Blocks.Select(b =>
        {
            annotationsById.TryGetValue(b.Id, out var note);
            return new RenderBlock(
            b.Id, b.Key, b.Kind, b.Title, b.Subtitle,
            b.X, b.Y, b.Width, b.Height,
            IsCollapsed: b.IsCollapsed,
            Body: b.Kind == BlockKind.Note ? note?.Content : null,
            FilePath: b.FilePath,
            StartLine: b.StartLine,
            EndLine: b.EndLine,
            Focused: b.Focused);
        }).ToList();

        var connections = session.Connections
            .Select(c => new RenderConnection(c.Id, c.SourceKey, c.TargetKey, c.Label,
                SourceAnchorIndex: c.SourceAnchorIndex,
                TargetAnchorIndex: c.TargetAnchorIndex,
                ArrowPosition: c.ArrowPosition,
                ArrowForward: c.ArrowForward,
                SourceControlX: c.SourceControlX,
                SourceControlY: c.SourceControlY,
                TargetControlX: c.TargetControlX,
                TargetControlY: c.TargetControlY,
                MidControlX: c.MidControlX,
                MidControlY: c.MidControlY,
                MidControlBends: c.MidControlBends))
            .ToList();

        var swimLanes = session.SwimLanes
            .Select(l => new RenderSwimLane(l.Id, l.Key, l.Name, l.Color, l.X, l.Y, l.Width, l.Height))
            .ToList();

        var annotations = session.Annotations
            .Select(a => new RenderAnnotation(a.Id, a.AttachedToKey, a.Content, a.X, a.Y))
            .ToList();

        return new RenderScene(blocks, connections, swimLanes, annotations);
    }

    private async Task HydrateCodeBlocksAsync()
    {
        var updatedBlocks = new List<RenderBlock>();
        var changed = false;

        foreach (var block in Scene.Blocks)
        {
            if (block.FilePath is null || !File.Exists(block.FilePath) || block.Body is not null)
            {
                updatedBlocks.Add(block);
                continue;
            }

            string body = await File.ReadAllTextAsync(block.FilePath);
            string[] allLines = body.Split('\n');

            if (block.Kind == BlockKind.File)
            {
                var tokens = await _semanticSpan.GetTokenSpansAsync(block.FilePath, 1, allLines.Length, CancellationToken.None);
                updatedBlocks.Add(block with
                {
                    Body = body,
                    SemanticTokens = tokens,
                    StartLine = 1,
                    EndLine = allLines.Length
                });
                changed = true;
                continue;
            }

            if (block.Kind == BlockKind.Extract && block.StartLine.HasValue && block.EndLine.HasValue)
            {
                int start = Math.Clamp(block.StartLine.Value, 1, allLines.Length);
                int end = Math.Clamp(block.EndLine.Value, start, allLines.Length);
                string extracted = string.Join('\n', allLines.Skip(start - 1).Take(end - start + 1));
                var tokens = await _semanticSpan.GetTokenSpansAsync(block.FilePath, start, end, CancellationToken.None);
                updatedBlocks.Add(block with { Body = extracted, SemanticTokens = tokens });
                changed = true;
                continue;
            }

            updatedBlocks.Add(block);
        }

        if (!changed) return;
        Scene = Scene with { Blocks = updatedBlocks };
        UpdateSelectedObject(Scene);
        await PersistSessionAsync();
    }

    private async Task PersistSessionAsync()
    {
        if (_activeSession is null || _currentSnapshot is null) return;
        _saveCts?.Cancel();
        _saveCts = new CancellationTokenSource();
        var ct = _saveCts.Token;
        try
        {
            await Task.Delay(800, ct);
            var updated = BuildSessionFromScene(_activeSession, Scene);
            _activeSession = updated;
            await _sessions.SaveSessionAsync(updated, CancellationToken.None);
        }
        catch (OperationCanceledException) { }
    }

    private static ReviewSession BuildSessionFromScene(ReviewSession template, RenderScene scene)
    {
        var blocks = scene.Blocks.Select(b => new BlockPlacement(
            b.Id, b.Kind, b.Key, b.Title, b.Subtitle,
            b.FilePath, b.StartLine, b.EndLine,
            b.X, b.Y, b.Width, b.Height, b.IsCollapsed, b.Focused)).ToList();

        var connections = scene.Connections
            .Select(c => new ConnectionSnapshot(c.Id, c.SourceKey, c.TargetKey, c.Label,
                c.SourceAnchorIndex,
                c.TargetAnchorIndex,
                c.ArrowPosition,
                c.ArrowForward,
                c.SourceControlX,
                c.SourceControlY,
                c.TargetControlX,
                c.TargetControlY,
                c.MidControlX,
                c.MidControlY,
                c.MidControlBends))
            .ToList();

        var annotations = scene.Annotations
            .Select(a =>
            {
                var noteBlock = scene.Blocks.FirstOrDefault(b => b.Id == a.Id && b.Kind == BlockKind.Note);
                return new AnnotationSnapshot(
                    a.Id,
                    a.AttachedToKey,
                    noteBlock?.Body ?? a.Content,
                    noteBlock?.X ?? a.X,
                    noteBlock?.Y ?? a.Y,
                    DateTimeOffset.UtcNow);
            })
            .ToList();

        var swimLanes = scene.SwimLanes
            .Select(l => new SwimLaneSnapshot(l.Id, l.Key, l.Name, l.Color, l.X, l.Y, l.Width, l.Height))
            .ToList();

        return template with { Blocks = blocks, Connections = connections, Annotations = annotations, SwimLanes = swimLanes, UpdatedAt = DateTimeOffset.UtcNow };
}
}
