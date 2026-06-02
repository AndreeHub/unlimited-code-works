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
    [ObservableProperty] private bool _isOutlineDocumentActive;
    [ObservableProperty] private bool _isCanvasDocumentActive = true;

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
        if (_activeSession is null || _activeSession.Kind == DocumentKind.Canvas) return;
        OutlineDocumentBody = body;
        _ = PersistOutlineAsync();
    }

    internal void OnOutlineCollapsedChanged(IReadOnlyCollection<string> ids)
    {
        if (_activeSession is null || _activeSession.Kind == DocumentKind.Canvas) return;
        _outlineDocumentCollapsed = string.Join(';', ids);
        _ = PersistOutlineAsync();
    }

    private async Task PersistOutlineAsync()
    {
        if (_activeSession is null || _activeSession.Kind == DocumentKind.Canvas) return;
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
        if (_activeSession is null || _activeSession.Kind == DocumentKind.Canvas) return;

        // Only update _activeSession (not the Sessions collection / SelectedSession): replacing the
        // bound collection item would bounce the header ListBox selection and reactivate the doc,
        // resetting the caret mid-type. The document name doesn't change while typing, so the
        // browser stays correct without a collection refresh.
        var updated = _activeSession with
        {
            OutlineBody = OutlineDocumentBody,
            OutlineCollapsed = _outlineDocumentCollapsed,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        _activeSession = updated;
        await _sessions.SaveSessionAsync(updated, ct);
    }

    /// <summary>
    /// Commit the title box back to the active document name (Logseq-style page rename).
    /// No-ops when the title is unchanged so editing the body never triggers a rename.
    /// </summary>
    public async Task CommitOutlineTitleAsync()
    {
        if (_activeSession is null || _activeSession.Kind == DocumentKind.Canvas) return;
        string desired = (OutlineDocumentTitle ?? string.Empty).Trim();
        if (desired.Length == 0)
        {
            // Reject an empty title: snap the box back to the current name.
            OutlineDocumentTitle = _activeSession.Name;
            return;
        }
        if (string.Equals(desired, _activeSession.Name, StringComparison.Ordinal)) return;
        await RenameSessionAsync(_activeSession, desired);
        OutlineDocumentTitle = _activeSession.Name;
    }

    /// <summary>Flush any pending debounced outline save (window close / doc switch).</summary>
    public async Task FlushPendingOutlineSaveAsync()
    {
        if (_activeSession is null || _activeSession.Kind == DocumentKind.Canvas) return;
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
        if (_activeSession is null) return;
        int index = Sessions.ToList().FindIndex(s => s.Id == _activeSession.Id);
        if (index >= 0 && !ReferenceEquals(Sessions[index], _activeSession))
            Sessions[index] = _activeSession;
    }

    /// <summary>Configure VM state for the active outline document and signal the host to reload it.</summary>
    private void ActivateOutlineDocument(ReviewSession session)
    {
        Scene = RenderScene.Empty;
        UpdateSelectedObject(Scene);
        RefreshBoardDetails();
        ResetHistory();

        OutlineDocumentBody = string.IsNullOrEmpty(session.OutlineBody) ? "- " : session.OutlineBody!;
        OutlineDocumentTitle = session.Name;
        _outlineDocumentCollapsed = session.OutlineCollapsed ?? string.Empty;
        IsCanvasDocumentActive = false;
        IsOutlineDocumentActive = true;
        StatusMessage = $"{session.Kind}: {session.Name}";
        OutlineDocumentReloadRequested?.Invoke();
    }
}
