using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ReviewScope.Domain;
using System.Globalization;
using System.IO;

namespace ReviewScope.App.ViewModels;

/// <summary>
/// Phase 3 — the multi-document project shell. A <see cref="ReviewSession"/> is the universal
/// persisted unit; its <see cref="ReviewSession.Kind"/> selects which editor the main area shows:
/// Canvas → the freeform board (CanvasViewport); Page/Journal → the Logseq-style outline
/// (OutlineView), whose body/collapsed-state round-trip through OutlineBody/OutlineCollapsed.
/// </summary>
public sealed partial class MainWindowViewModel
{
    // ---- Main-area view switching (bound by MainWindow.xaml visibility) ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOutlinePaneVisible))]
    private bool _isOutlineDocumentActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCanvasPaneVisible))]
    private bool _isCanvasDocumentActive = true;

    /// <summary>
    /// When true the center area shows the active board's canvas and the active page's outline
    /// side-by-side (canvas left, writing right) instead of one-at-a-time. Both
    /// <see cref="IsCanvasDocumentActive"/> and <see cref="IsOutlineDocumentActive"/> are forced on
    /// while split is active so the existing per-mode guards keep working for whichever pane is focused.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCanvasPaneVisible))]
    [NotifyPropertyChangedFor(nameof(IsOutlinePaneVisible))]
    private bool _isSplitViewActive;

    /// <summary>Canvas pane is shown in canvas mode or whenever split view is active.</summary>
    public bool IsCanvasPaneVisible => IsCanvasDocumentActive || IsSplitViewActive;

    /// <summary>Outline pane is shown in outline mode or whenever split view is active.</summary>
    public bool IsOutlinePaneVisible => IsOutlineDocumentActive || IsSplitViewActive;

    /// <summary>Toggle the side-by-side split layout. Turning it on ensures both a board and a page
    /// are live; turning it off collapses back to the last single-document mode.</summary>
    [RelayCommand]
    public async Task ToggleSplitViewAsync()
    {
        IsSplitViewActive = !IsSplitViewActive;
        if (IsSplitViewActive)
        {
            // Make sure both panes have something to show: a board for the canvas and a page for the outline.
            await EnsureSplitDocumentsAsync();
            StatusMessage = "Split view: canvas + writing";
        }
        else
        {
            // Collapse back to a single pane. Land on the canvas (the common primary) so the layout
            // is predictable; the writing pane is one click away on the Outline toggle.
            IsCanvasDocumentActive = true;
            IsOutlineDocumentActive = false;
            StatusMessage = "Single view";
        }
    }

    /// <summary>Guarantees a live canvas board and a live outline page so split view has content on both
    /// sides. Activates the first existing document of each kind, creating a page if none exists.</summary>
    private async Task EnsureSplitDocumentsAsync()
    {
        if (_activeSession is null)
        {
            var board = Sessions.FirstOrDefault(s => s.Kind == DocumentKind.Canvas);
            if (board is not null) await ActivateSessionAsync(board);
            else await CreateNewSessionAsync();
        }

        if (_activeOutlineSession is null)
        {
            var page = Sessions.FirstOrDefault(s => s.Kind != DocumentKind.Canvas);
            if (page is not null) await ActivateSessionAsync(page, hydrateCode: false);
            else await OpenTodayJournalAsync();
        }
    }

    // ---- Active outline document state (pushed into OutlineView by the code-behind) ----
    [ObservableProperty] private string _outlineDocumentBody = "- ";

    /// <summary>The active Page/Journal title, shown as the centered heading above the outline.
    /// Two-way bound to the title box; committed (renames the document) on Enter/blur.</summary>
    [ObservableProperty] private string _outlineDocumentTitle = string.Empty;

    private string _outlineDocumentCollapsed = string.Empty;

    /// <summary>Semicolon-separated collapsed-anchor set for the active outline document.</summary>
    public string OutlineDocumentCollapsed => _outlineDocumentCollapsed;

    /// <summary>
    /// Raised when a Page/Journal becomes active so the host can (re)load OutlineView.Document and
    /// its collapsed set — needed even when two documents share the same body text (equality would
    /// otherwise suppress a plain property-change push).
    /// </summary>
    public event Action? OutlineDocumentReloadRequested;

    private CancellationTokenSource? _outlineSaveCts;

    // -----------------------------------------------------------------------
    // Document creation
    // -----------------------------------------------------------------------
    [RelayCommand]
    public async Task CreateNewPageAsync()
    {
        var workspace = EnsureBoardWorkspace();
        string name = NextDocumentName("Untitled Page", DocumentKind.Page);
        var doc = NewOutlineSession(name, workspace.WorkspaceKey, DocumentKind.Page);
        SessionSpawnAnimationId = doc.Id;
        Sessions.Add(doc);
        await _sessions.SaveSessionAsync(doc, CancellationToken.None);
        await _sessions.SaveSessionOrderAsync(workspace.WorkspaceKey, Sessions.Select(s => s.Id).ToArray(), CancellationToken.None);
        await ActivateSessionAsync(doc, hydrateCode: false);
    }

    [RelayCommand]
    public async Task OpenTodayJournalAsync() => await OpenOrCreateJournalAsync(DateTimeOffset.Now.Date);

    /// <summary>
    /// Follow a <c>[[link]]</c>: activate the named Page/Journal, creating it on demand
    /// (Logseq-style click-to-create). A name that parses as <c>yyyy-MM-dd</c> becomes a Journal;
    /// anything else a Page.
    /// </summary>
    public async Task NavigateToDocumentAsync(string name)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0) return;

        var existing = Sessions.FirstOrDefault(s =>
            s.Kind != DocumentKind.Canvas && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            await ActivateSessionAsync(existing, hydrateCode: false);
            return;
        }

        if (DateTime.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            await OpenOrCreateJournalAsync(date.Date);
            return;
        }

        var workspace = EnsureBoardWorkspace();
        var doc = NewOutlineSession(name, workspace.WorkspaceKey, DocumentKind.Page);
        SessionSpawnAnimationId = doc.Id;
        Sessions.Add(doc);
        await _sessions.SaveSessionAsync(doc, CancellationToken.None);
        await _sessions.SaveSessionOrderAsync(workspace.WorkspaceKey, Sessions.Select(s => s.Id).ToArray(), CancellationToken.None);
        await ActivateSessionAsync(doc, hydrateCode: false);
    }

    private async Task OpenOrCreateJournalAsync(DateTime date)
    {
        string name = date.ToString("yyyy-MM-dd");
        var existing = Sessions.FirstOrDefault(s =>
            s.Kind == DocumentKind.Journal && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            await ActivateSessionAsync(existing, hydrateCode: false);
            return;
        }

        var workspace = EnsureBoardWorkspace();
        var doc = NewOutlineSession(name, workspace.WorkspaceKey, DocumentKind.Journal);
        SessionSpawnAnimationId = doc.Id;
        Sessions.Add(doc);
        await _sessions.SaveSessionAsync(doc, CancellationToken.None);
        await _sessions.SaveSessionOrderAsync(workspace.WorkspaceKey, Sessions.Select(s => s.Id).ToArray(), CancellationToken.None);
        await ActivateSessionAsync(doc, hydrateCode: false);
    }

    /// <summary>Auto-create today's journal page on workspace load (does not change the active doc).</summary>
    private async Task EnsureTodayJournalAsync()
    {
        string name = DateTimeOffset.Now.ToString("yyyy-MM-dd");
        if (Sessions.Any(s => s.Kind == DocumentKind.Journal && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
            return;

        var workspace = EnsureBoardWorkspace();
        var doc = NewOutlineSession(name, workspace.WorkspaceKey, DocumentKind.Journal);
        Sessions.Add(doc);
        await _sessions.SaveSessionAsync(doc, CancellationToken.None);
        await _sessions.SaveSessionOrderAsync(workspace.WorkspaceKey, Sessions.Select(s => s.Id).ToArray(), CancellationToken.None);
    }

    private static ReviewSession NewOutlineSession(string name, string workspaceKey, DocumentKind kind) =>
        new(Guid.NewGuid(), name, workspaceKey,
            Array.Empty<BlockPlacement>(), Array.Empty<ConnectionSnapshot>(),
            Array.Empty<AnnotationSnapshot>(), Array.Empty<SwimLaneSnapshot>(),
            DateTimeOffset.UtcNow, DefaultBoardLayers(),
            Kind: kind, OutlineBody: "- ", OutlineCollapsed: string.Empty);

    private string NextDocumentName(string baseName, DocumentKind kind)
    {
        bool Taken(string candidate) => Sessions.Any(s =>
            s.Kind == kind && string.Equals(s.Name, candidate, StringComparison.OrdinalIgnoreCase));

        if (!Taken(baseName)) return baseName;
        for (int i = 2; ; i++)
        {
            string candidate = $"{baseName} {i}";
            if (!Taken(candidate)) return candidate;
        }
    }

    // -----------------------------------------------------------------------
    // Live editing → debounced persistence (called from the OutlineView host)
    // -----------------------------------------------------------------------
    internal void OnOutlineBodyEdited(string body)
    {
        if (_activeOutlineSession is null || _activeOutlineSession.Kind == DocumentKind.Canvas) return;
        OutlineDocumentBody = body;
        _ = PersistOutlineAsync();
    }

    internal void OnOutlineCollapsedChanged(IReadOnlyCollection<string> ids)
    {
        if (_activeOutlineSession is null || _activeOutlineSession.Kind == DocumentKind.Canvas) return;
        _outlineDocumentCollapsed = string.Join(';', ids);
        _ = PersistOutlineAsync();
    }

    private async Task PersistOutlineAsync()
    {
        if (_activeOutlineSession is null || _activeOutlineSession.Kind == DocumentKind.Canvas) return;
        EnsureBoardWorkspace();
        _outlineSaveCts?.Cancel();
        _outlineSaveCts = new CancellationTokenSource();
        var ct = _outlineSaveCts.Token;
        try
        {
            await Task.Delay(600, ct);
            await SaveActiveOutlineSnapshotAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Outline autosave failed; will retry on next change.");
        }
    }

    private async Task SaveActiveOutlineSnapshotAsync(CancellationToken ct)
    {
        if (_activeOutlineSession is null || _activeOutlineSession.Kind == DocumentKind.Canvas) return;

        // Only update _activeOutlineSession (not the Sessions collection / SelectedSession): replacing the
        // bound collection item would bounce the header ListBox selection and reactivate the doc,
        // resetting the caret mid-type. The document name doesn't change while typing, so the
        // browser stays correct without a collection refresh.
        var updated = _activeOutlineSession with
        {
            OutlineBody = OutlineDocumentBody,
            OutlineCollapsed = _outlineDocumentCollapsed,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        _activeOutlineSession = updated;
        await _sessions.SaveSessionAsync(updated, ct);
    }

    /// <summary>
    /// Commit the title box back to the active document name (Logseq-style page rename).
    /// No-ops when the title is unchanged so editing the body never triggers a rename.
    /// </summary>
    public async Task CommitOutlineTitleAsync()
    {
        if (_activeOutlineSession is null || _activeOutlineSession.Kind == DocumentKind.Canvas) return;
        string desired = (OutlineDocumentTitle ?? string.Empty).Trim();
        if (desired.Length == 0)
        {
            // Reject an empty title: snap the box back to the current name.
            OutlineDocumentTitle = _activeOutlineSession.Name;
            return;
        }
        if (string.Equals(desired, _activeOutlineSession.Name, StringComparison.Ordinal)) return;
        await RenameSessionAsync(_activeOutlineSession, desired);
        OutlineDocumentTitle = _activeOutlineSession.Name;
    }

    /// <summary>Flush any pending debounced outline save (window close / doc switch).</summary>
    public async Task FlushPendingOutlineSaveAsync()
    {
        if (_activeOutlineSession is null || _activeOutlineSession.Kind == DocumentKind.Canvas) return;
        _outlineSaveCts?.Cancel();
        _outlineSaveCts = null;
        await SaveActiveOutlineSnapshotAsync(CancellationToken.None);
        SyncActiveOutlineIntoCollection();
    }

    /// <summary>
    /// Push the freshly-saved outline body/collapsed-state back into the bound
    /// <see cref="Sessions"/> item, which is the source of truth when a document is reactivated.
    /// Without this the mid-type debounced save updates only <c>_activeSession</c> + disk, so
    /// switching page→board→page reloads the stale collection item and the body looks empty.
    /// Done only on flush (doc switch / close), never on the mid-type save, so the header
    /// ListBox selection isn't bounced while editing.
    /// </summary>
    private void SyncActiveOutlineIntoCollection()
    {
        if (_activeOutlineSession is null) return;
        int index = Sessions.ToList().FindIndex(s => s.Id == _activeOutlineSession.Id);
        if (index >= 0 && !ReferenceEquals(Sessions[index], _activeOutlineSession))
            Sessions[index] = _activeOutlineSession;
    }

    /// <summary>Configure VM state for the active outline document and signal the host to reload it.
    /// In split view the canvas board stays live, so we never clear the Scene or flip canvas off.</summary>
    private void ActivateOutlineDocument(ReviewSession session)
    {
        _activeOutlineSession = session;
        OnDocumentActivated(session);

        if (!IsSplitViewActive)
        {
            Scene = RenderScene.Empty;
            UpdateSelectedObject(Scene);
            RefreshBoardDetails();
            ResetHistory();
            IsCanvasDocumentActive = false;
        }

        OutlineDocumentBody = string.IsNullOrEmpty(session.OutlineBody) ? "- " : session.OutlineBody!;
        OutlineDocumentTitle = session.Name;
        _outlineDocumentCollapsed = session.OutlineCollapsed ?? string.Empty;
        IsOutlineDocumentActive = true;
        StatusMessage = $"{session.Kind}: {session.Name}";
        OutlineDocumentReloadRequested?.Invoke();
    }
}
