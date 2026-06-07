using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using ReviewScope.Analysis;
using ReviewScope.App.Persistence;
using ReviewScope.Canvas;
using ReviewScope.Domain;
using ReviewScope.Domain.Outline;
using System.Collections.ObjectModel;
using System.IO;

namespace ReviewScope.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    // -----------------------------------------------------------------------
    // Board management
    // -----------------------------------------------------------------------
    private async Task LoadSessionsAsync(string workspaceKey, CancellationToken ct)
    {
        UbwProjectName = _currentSnapshot?.DisplayName ?? "Untitled Boards";
        Sessions.Clear();
        var loaded = await _sessions.GetSessionsAsync(workspaceKey, ct);
        foreach (var s in loaded) Sessions.Add(s);

        if (Sessions.Count == 0)
            await CreateNewSessionAsync();
        else
            await ActivateSessionAsync(Sessions[0]);

        await EnsureTodayJournalAsync();
    }

    [RelayCommand]
    public async Task CreateNewSessionAsync()
    {
        var workspace = EnsureBoardWorkspace();
        string name = NextSessionName();
        var session = new ReviewSession(
            Guid.NewGuid(), name, workspace.WorkspaceKey,
            Array.Empty<BlockPlacement>(), Array.Empty<ConnectionSnapshot>(),
            Array.Empty<AnnotationSnapshot>(), Array.Empty<SwimLaneSnapshot>(),
            DateTimeOffset.UtcNow, DefaultBoardLayers());
        SessionSpawnAnimationId = session.Id;
        Sessions.Add(session);
        await _sessions.SaveSessionAsync(session, CancellationToken.None);
        await _sessions.SaveSessionOrderAsync(workspace.WorkspaceKey, Sessions.Select(s => s.Id).ToArray(), CancellationToken.None);
        await ActivateSessionAsync(session, hydrateCode: false);
    }

    [RelayCommand]
    public async Task ActivateSelectedSessionAsync()
    {
        if (SelectedSession is null) return;
        // Both the header strip and the Project browser bind SelectedSession; selecting in one
        // syncs the other, which re-fires SelectionChanged. Skip when the selection is already
        // the active document so we don't reactivate (and reset the outline caret/scroll) twice.
        var active = SelectedSession.Kind == DocumentKind.Canvas ? _activeSession : _activeOutlineSession;
        if (active is not null && active.Id == SelectedSession.Id) return;
        await ActivateSessionAsync(SelectedSession);
    }

    [RelayCommand]
    public async Task RenameSelectedSessionAsync()
    {
        if (SelectedSession is null) return;
        EnsureBoardWorkspace();
        string name = string.IsNullOrWhiteSpace(SessionNameDraft) ? SelectedSession.Name : SessionNameDraft.Trim();
        await RenameSessionAsync(SelectedSession, name);
    }

    public async Task RenameSessionAsync(ReviewSession session, string? requestedName)
    {
        EnsureBoardWorkspace();
        var current = Sessions.FirstOrDefault(s => s.Id == session.Id) ?? session;
        string name = string.IsNullOrWhiteSpace(requestedName) ? current.Name : requestedName.Trim();
        var renamed = current with { Name = name, UpdatedAt = DateTimeOffset.UtcNow };
        await _sessions.SaveSessionAsync(renamed, CancellationToken.None);
        int index = Sessions.IndexOf(current);
        if (index >= 0) Sessions[index] = renamed;
        if (_activeSession?.Id == renamed.Id) _activeSession = renamed;
        if (_activeOutlineSession?.Id == renamed.Id) _activeOutlineSession = renamed;
        RenameTab(renamed.Id, renamed.Name);
        SelectedSession = renamed;
        SessionNameDraft = renamed.Name;
        StatusMessage = $"Renamed board: {name}";
    }

    [RelayCommand]
    public async Task DeleteSelectedSessionAsync()
    {
        if (SelectedSession is null) return;
        var workspace = EnsureBoardWorkspace();
        var deleting = SelectedSession;
        await _sessions.DeleteSessionAsync(deleting.Id, workspace.WorkspaceKey, CancellationToken.None);
        Sessions.Remove(deleting);
        RemoveTabForDocument(deleting.Id);
        _canvasDocs.Remove(deleting.Id);
        if (_activeSession?.Id == deleting.Id) _activeSession = null;
        if (_activeOutlineSession?.Id == deleting.Id) _activeOutlineSession = null;
        await _sessions.SaveSessionOrderAsync(workspace.WorkspaceKey, Sessions.Select(s => s.Id).ToArray(), CancellationToken.None);
        StatusMessage = $"Deleted board: {deleting.Name}";

        if (Sessions.Count == 0)
        {
            await CreateNewSessionAsync();
            return;
        }

        await ActivateSessionAsync(Sessions[0]);
    }

    private async Task ActivateSessionAsync(ReviewSession session, bool hydrateCode = true)
    {
        // Switching documents: flush the outgoing board's and page's pending debounced saves first so a
        // fast switch can't drop the last edits (the active pointers still reference the outgoing docs here).
        await FlushPendingOutlineSaveAsync();
        await FlushPendingSaveAsync();

        SelectedSession = session;
        SessionNameDraft = session.Name;

        if (session.Kind != DocumentKind.Canvas)
        {
            // Outline docs activate into the independent outline pointer (see ActivateOutlineDocument),
            // leaving the active canvas board untouched so both can coexist in split view.
            ActivateOutlineDocument(session);
            return;
        }

        // Make this board's document the focused one. HookActiveCanvas re-points Scene/undo at it
        // and runs the scene-changed side effects, so we don't rebuild the scene or reset history here.
        var doc = GetOrCreateCanvasDoc(session);
        HookActiveCanvas(doc);
        OnDocumentActivated(session);
        IsCanvasDocumentActive = true;
        if (!IsSplitViewActive)
            IsOutlineDocumentActive = false;
        StatusMessage = $"Board: {session.Name}";
        if (hydrateCode)
            await HydrateCodeBlocksAsync();
    }

    public void ClearSessionSpawnAnimation(Guid sessionId)
    {
        if (SessionSpawnAnimationId == sessionId)
            SessionSpawnAnimationId = null;
    }

    public async Task MoveSessionAsync(ReviewSession session, int targetIndex)
    {
        EnsureBoardWorkspace();
        if (!MoveSessionInQueue(session, targetIndex))
            return;

        await PersistSessionOrderAsync();
    }

    public bool MoveSessionInQueue(ReviewSession session, int targetIndex)
    {
        EnsureBoardWorkspace();
        int oldIndex = Sessions.ToList().FindIndex(s => s.Id == session.Id);
        if (oldIndex < 0) return false;

        targetIndex = Math.Clamp(targetIndex, 0, Sessions.Count);
        if (targetIndex > oldIndex)
            targetIndex--;

        if (oldIndex == targetIndex)
            return false;

        Sessions.Move(oldIndex, targetIndex);
        SelectedSession = Sessions[targetIndex];
        StatusMessage = $"Moved board: {SelectedSession.Name}";
        return true;
    }

    public async Task PersistSessionOrderAsync()
    {
        var workspace = EnsureBoardWorkspace();
        await _sessions.SaveSessionOrderAsync(workspace.WorkspaceKey, Sessions.Select(s => s.Id).ToArray(), CancellationToken.None);
    }

    /// <summary>Swaps an existing session instance in the <see cref="Sessions"/> collection for an
    /// updated copy, keeping <see cref="SelectedSession"/>/<c>_activeSession</c> pointing at the
    /// new instance. Use after mutating a non-active document (e.g. allocating an anchor).</summary>
    private void ReplaceSession(ReviewSession original, ReviewSession updated)
    {
        int index = Sessions.ToList().FindIndex(s => s.Id == original.Id);
        if (index >= 0) Sessions[index] = updated;
        if (SelectedSession?.Id == updated.Id) SelectedSession = updated;
        if (_activeSession?.Id == updated.Id) _activeSession = updated;
        if (_activeOutlineSession?.Id == updated.Id) _activeOutlineSession = updated;
    }

    private string NextSessionName()
    {
        const string baseName = "New Board";
        if (!Sessions.Any(s => string.Equals(s.Name, baseName, StringComparison.OrdinalIgnoreCase)))
            return baseName;

        for (int i = 2; ; i++)
        {
            string candidate = $"{baseName} {i}";
            if (!Sessions.Any(s => string.Equals(s.Name, candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
    }

    /// <summary>Fetch the live canvas document for a board, building it (and resolving transclusions)
    /// on first open. On revisit the existing instance — with its in-memory Scene and undo history —
    /// is reused, so switching canvas tabs no longer reloads from disk or drops undo.</summary>
    private CanvasDocumentViewModel GetOrCreateCanvasDoc(ReviewSession session)
    {
        if (_canvasDocs.TryGetValue(session.Id, out var existing))
        {
            existing.Session = session;
            return existing;
        }

        var doc = new CanvasDocumentViewModel(session)
        {
            Scene = ResolveTransclusions(BuildSceneFromSession(session))
        };
        _canvasDocs[session.Id] = doc;
        return doc;
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
            Body: b.Kind == BlockKind.Note ? note?.Content : ResolvePersistedBlockBody(b),
            FilePath: b.FilePath,
            StartLine: b.StartLine,
            EndLine: b.EndLine,
            Focused: b.Focused,
            ZIndex: b.ZIndex,
            LayerKey: b.LayerKey,
            IsLocked: b.IsLocked,
            ShapeType: b.ShapeType,
            Style: b.Style,
            Source: b.Source,
            GroupState: b.GroupState,
            Tags: b.Tags,
            WikiLinks: b.WikiLinks,
            RefAnchorId: b.RefAnchorId,
            RefPageName: b.RefPageName);
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
                MidControlBends: c.MidControlBends,
                RouteKind: c.RouteKind,
                ArrowKind: c.ArrowKind,
                Stroke: c.Stroke,
                Dashed: c.Dashed,
                SourceLineId: c.SourceLineId,
                TargetLineId: c.TargetLineId))
            .ToList();

        var swimLanes = session.SwimLanes
            .Select(l => new RenderSwimLane(l.Id, l.Key, l.Name, l.Color, l.X, l.Y, l.Width, l.Height))
            .ToList();

        var annotations = session.Annotations
            .Select(a => new RenderAnnotation(a.Id, a.AttachedToKey, a.Content, a.X, a.Y))
            .ToList();

        var layers = (session.Layers ?? DefaultBoardLayers())
            .Select(l => new RenderBoardLayer(l.Id, l.Key, l.Name, l.Kind, l.IsVisible, l.IsLocked))
            .ToList();

        return new RenderScene(blocks, connections, swimLanes, annotations, layers);
    }

    /// <summary>
    /// Re-resolves every <see cref="BlockKind.Transclusion"/> block in the scene against the
    /// current set of outline documents, baking the mirrored bullet's subtree markdown into the
    /// block's <c>Body</c> for read-only render. The source-of-truth pointer (<c>RefAnchorId</c>)
    /// is never touched, so a transclusion stays live: edit the source bullet, reopen the canvas,
    /// and the mirror updates. Unresolved anchors render a visible placeholder rather than vanish.
    /// </summary>
    private RenderScene ResolveTransclusions(RenderScene scene)
    {
        if (!scene.Blocks.Any(b => b.Kind == BlockKind.Transclusion))
            return scene;

        var index = BuildReferenceIndex();
        var blocks = scene.Blocks.Select(b =>
        {
            if (b.Kind != BlockKind.Transclusion)
                return b;

            var style = (b.Style ?? new BoardItemStyle()) with { OutlineEnabled = true };

            // Page portal: embed an entire page's outline (Logseq-style page reference dragged
            // onto a whiteboard). The page name becomes a top heading; its bullets nest beneath.
            if (b.RefPageName is { Length: > 0 } pageName)
            {
                var page = Sessions.FirstOrDefault(s =>
                    s.Kind != DocumentKind.Canvas && string.Equals(s.Name, pageName, StringComparison.OrdinalIgnoreCase));
                if (page is not null)
                {
                    // Reserve the card's header/footer via style spacing so the SAME content rect is
                    // used for rendering and caret hit-testing (GetContentRect honors this spacing),
                    // which lets the existing canvas outline editor edit the portal in place.
                    var portalStyle = style with
                    {
                        SpacingLeft = 14, SpacingTop = 64, SpacingRight = 14, SpacingBottom = 40,
                    };
                    return b with
                    {
                        Body = BuildPagePortalBody(page.OutlineBody),
                        Subtitle = page.Name,
                        Style = portalStyle,
                    };
                }
                return b with
                {
                    Body = $"- ⚠ Unresolved page: {pageName}",
                    Subtitle = pageName,
                    Style = style,
                };
            }

            if (b.RefAnchorId is { Length: > 0 } anchor && index.TryResolveBlock(anchor, out var loc))
            {
                return b with
                {
                    Body = OutlineTree.SerializeSubtree(loc.Block),
                    Subtitle = loc.Document.Name,
                    Style = style,
                };
            }

            return b with
            {
                Body = "- ⚠ Unresolved block reference",
                Subtitle = b.RefAnchorId is null ? string.Empty : $"^{b.RefAnchorId}",
                Style = style,
            };
        }).ToList();

        return scene with { Blocks = blocks };
    }

    /// <summary>The page-portal body is just the page's outline as-is; the page name is rendered
    /// in the portal card's header bar (see BlockRenderer), Logseq-style.</summary>
    private static string BuildPagePortalBody(string? pageBody)
    {
        string body = (pageBody ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        return string.IsNullOrWhiteSpace(body) ? "- " : body;
    }

    /// <summary>Builds a reference index over all outline documents (pages + journals) in the
    /// project so <c>[[Page]]</c> and <c>((^anchor))</c> targets can be resolved.</summary>
    private ReferenceIndex BuildReferenceIndex() => ReferenceIndex.Build(
        Sessions
            .Where(s => s.Kind != DocumentKind.Canvas)
            .Select(s => new DocumentSource(s.Id, s.Name, s.Kind, s.OutlineBody)));

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
                bool isCSharp = Path.GetExtension(block.FilePath).Equals(".cs", StringComparison.OrdinalIgnoreCase);
                var tokens = isCSharp
                    ? await _semanticSpan.GetTokenSpansAsync(block.FilePath, 1, allLines.Length, CancellationToken.None)
                    : Array.Empty<SemanticTokenSpan>();
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
        if (_activeSession is null) return;
        EnsureBoardWorkspace();
        _saveCts?.Cancel();
        _saveCts = new CancellationTokenSource();
        var ct = _saveCts.Token;
        try
        {
            await Task.Delay(800, ct);
            await SaveCurrentSessionSnapshotAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Background autosave hit a transient file-system lock (AV/indexer/another
            // in-flight save). Don't tear down the editor over it — the next edit
            // re-triggers a save, and the prior snapshot on disk is still intact.
            _logger.LogWarning(ex, "Autosave failed; will retry on next change.");
        }
    }

    public async Task SaveActiveSessionNowAsync(bool showStatus = true)
    {
        if (_activeSession is null)
        {
            if (showStatus) StatusMessage = "Create a board before saving.";
            return;
        }

        if (_activeSession.Kind != DocumentKind.Canvas)
        {
            await FlushPendingOutlineSaveAsync();
            if (showStatus) StatusMessage = $"Saved {_activeSession.Kind}: {_activeSession.Name}";
            return;
        }

        EnsureBoardWorkspace();
        _saveCts?.Cancel();
        _saveCts = null;
        await SaveCurrentSessionSnapshotAsync(CancellationToken.None);

        if (showStatus)
            StatusMessage = $"Saved board: {_activeSession.Name}";
    }

    public async Task FlushPendingSaveAsync()
    {
        if (_activeSession is null) return;
        await SaveActiveSessionNowAsync(showStatus: false);
    }

    private async Task SaveCurrentSessionSnapshotAsync(CancellationToken ct)
    {
        if (_activeSession is null) return;
        var workspace = EnsureBoardWorkspace();

        var updated = BuildSessionFromScene(_activeSession, Scene);
        _activeSession = updated;
        await _sessions.SaveSessionAsync(updated, ct);

        int index = Sessions.ToList().FindIndex(s => s.Id == updated.Id);
        if (index >= 0)
            Sessions[index] = updated;

        if (SelectedSession?.Id == updated.Id)
            SelectedSession = updated;

        await _sessions.SaveSessionOrderAsync(workspace.WorkspaceKey, Sessions.Select(s => s.Id).ToArray(), ct);
    }

    private WorkspaceSnapshot EnsureBoardWorkspace()
    {
        if (_currentSnapshot is not null) return _currentSnapshot;

        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ReviewScope",
            "UntitledBoards");
        Directory.CreateDirectory(root);

        _currentSnapshot = new WorkspaceSnapshot(
            root,
            "Untitled Boards",
            Array.Empty<WorkspaceFileSummary>());
        WorkspacePath = _currentSnapshot.DisplayName;
        UbwProjectName = _currentSnapshot.DisplayName;
        StatusMessage = "Untitled board workspace ready.";
        return _currentSnapshot;
    }

    private static ReviewSession BuildSessionFromScene(ReviewSession template, RenderScene scene)
    {
        var blocks = scene.Blocks.Select(b => new BlockPlacement(
            b.Id, b.Kind, b.Key, b.Title, b.Subtitle,
            b.FilePath, b.StartLine, b.EndLine,
            b.X, b.Y, b.Width, b.Height, b.IsCollapsed, b.Focused,
            b.ZIndex, b.LayerKey, b.IsLocked, b.ShapeType, b.Style, b.Source, b.GroupState, PersistedBodyFor(b), b.Tags, b.WikiLinks, b.RefAnchorId, b.RefPageName)).ToList();

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
                c.MidControlBends,
                c.RouteKind,
                c.ArrowKind,
                c.Stroke,
                c.Dashed,
                c.SourceLineId,
                c.TargetLineId))
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

        var layers = (scene.Layers ?? Array.Empty<RenderBoardLayer>())
            .Select(l => new BoardLayerSnapshot(l.Id, l.Key, l.Name, l.Kind, l.IsVisible, l.IsLocked))
            .ToList();

        return template with { Blocks = blocks, Connections = connections, Annotations = annotations, SwimLanes = swimLanes, Layers = layers, UpdatedAt = DateTimeOffset.UtcNow };
}

    private static string? ResolvePersistedBlockBody(BlockPlacement block)
    {
        // Transclusion bodies aren't persisted — only RefAnchorId is. The mirrored markdown is
        // re-resolved on canvas activation (ResolveTransclusions) so it stays live.
        if (block.Kind is BlockKind.File or BlockKind.Extract or BlockKind.Transclusion)
            return null;

        if (!string.IsNullOrEmpty(block.Body))
            return block.Body;

        string? assetPath = block.Source?.AssetPath;
        if (string.IsNullOrWhiteSpace(assetPath) || !File.Exists(assetPath))
            return null;

        if (block.Kind == BlockKind.MarkdownDoc || block.Kind == BlockKind.Text || block.Kind == BlockKind.Shape)
        {
            try
            {
                return File.ReadAllText(assetPath);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        return null;
    }

    private static string? PersistedBodyFor(RenderBlock block) =>
        block.Kind is BlockKind.File or BlockKind.Extract or BlockKind.Transclusion ? null : block.Body;

    private static IReadOnlyList<BoardLayerSnapshot> DefaultBoardLayers() => new[]
    {
        new BoardLayerSnapshot(Guid.NewGuid(), "layer::background", "Background", BoardLayerKind.Background),
        new BoardLayerSnapshot(Guid.NewGuid(), "layer::architecture", "Architecture", BoardLayerKind.Architecture),
        new BoardLayerSnapshot(Guid.NewGuid(), "layer::code", "Code evidence", BoardLayerKind.CodeEvidence),
        new BoardLayerSnapshot(Guid.NewGuid(), "layer::notes", "Notes", BoardLayerKind.Notes),
        new BoardLayerSnapshot(Guid.NewGuid(), "layer::risks", "Risks", BoardLayerKind.Risks),
        new BoardLayerSnapshot(Guid.NewGuid(), "layer::screenshots", "Screenshots", BoardLayerKind.Screenshots)
    };
}
