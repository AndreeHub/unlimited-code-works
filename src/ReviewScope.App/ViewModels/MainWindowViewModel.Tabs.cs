using CommunityToolkit.Mvvm.Input;
using ReviewScope.Domain;
using System.Collections.ObjectModel;

namespace ReviewScope.App.ViewModels;

/// <summary>
/// Chrome-style open-document tabs. The canvas pane and the writing pane each keep their own strip,
/// matching the two independent active-document pointers (<c>_activeSession</c> / <c>_activeOutlineSession</c>).
/// Tabs are the set of documents the user has <i>opened</i>, a subset of <see cref="Sessions"/>;
/// closing a tab removes it from the strip but never deletes the document.
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>Open boards shown in the canvas pane's tab strip.</summary>
    public ObservableCollection<DocumentTabViewModel> CanvasTabs { get; } = new();

    /// <summary>Open pages/journals shown in the writing pane's tab strip.</summary>
    public ObservableCollection<DocumentTabViewModel> OutlineTabs { get; } = new();

    private ObservableCollection<DocumentTabViewModel> TabsFor(DocumentKind kind) =>
        kind == DocumentKind.Canvas ? CanvasTabs : OutlineTabs;

    /// <summary>Ensure the activated document has an open tab and mark it the active one in its strip.
    /// Called from <c>ActivateSessionAsync</c> / <c>ActivateOutlineDocument</c>.</summary>
    private void OnDocumentActivated(ReviewSession session)
    {
        var tabs = TabsFor(session.Kind);
        var tab = tabs.FirstOrDefault(t => t.Id == session.Id);
        if (tab is null)
        {
            tab = new DocumentTabViewModel(session.Id, session.Name, session.Kind);
            tabs.Add(tab);
        }
        else
        {
            tab.Name = session.Name;
        }
        foreach (var t in tabs) t.IsActive = ReferenceEquals(t, tab);
    }

    /// <summary>Keep a tab's label in sync when its document is renamed.</summary>
    private void RenameTab(Guid id, string name)
    {
        foreach (var tabs in new[] { CanvasTabs, OutlineTabs })
        {
            var tab = tabs.FirstOrDefault(t => t.Id == id);
            if (tab is not null) { tab.Name = name; return; }
        }
    }

    /// <summary>Drop a tab when its document is deleted.</summary>
    private void RemoveTabForDocument(Guid id)
    {
        foreach (var tabs in new[] { CanvasTabs, OutlineTabs })
        {
            var tab = tabs.FirstOrDefault(t => t.Id == id);
            if (tab is not null) { tabs.Remove(tab); return; }
        }
    }

    /// <summary>Click a tab → activate that document into its pane.</summary>
    [RelayCommand]
    public async Task ActivateTabAsync(DocumentTabViewModel? tab)
    {
        if (tab is null) return;
        var session = Sessions.FirstOrDefault(s => s.Id == tab.Id);
        if (session is null) { RemoveTabForDocument(tab.Id); return; }
        await ActivateSessionAsync(session, hydrateCode: session.Kind == DocumentKind.Canvas);
    }

    /// <summary>Close (×) a tab. Removes it from the strip only; the document is untouched on disk.
    /// If the closed tab was active, focus a neighbor of the same kind, or clear the pane if none remain.</summary>
    [RelayCommand]
    public async Task CloseTabAsync(DocumentTabViewModel? tab)
    {
        if (tab is null) return;
        var tabs = TabsFor(tab.Kind);
        int idx = tabs.IndexOf(tab);
        if (idx < 0) return;

        bool wasActive = tab.IsActive;
        tabs.Remove(tab);

        if (!wasActive) return;

        if (tabs.Count > 0)
        {
            var neighbor = tabs[Math.Min(idx, tabs.Count - 1)];
            var session = Sessions.FirstOrDefault(s => s.Id == neighbor.Id);
            if (session is not null)
                await ActivateSessionAsync(session, hydrateCode: neighbor.Kind == DocumentKind.Canvas);
            return;
        }

        // No open tabs of this kind left → clear that pane's active document.
        if (tab.Kind == DocumentKind.Canvas)
        {
            _canvasDocs.Remove(tab.Id);
            HookActiveCanvas(new CanvasDocumentViewModel(null));
        }
        else
        {
            _activeOutlineSession = null;
            OutlineDocumentBody = "- ";
            OutlineDocumentTitle = string.Empty;
            _outlineDocumentCollapsed = string.Empty;
            OutlineDocumentReloadRequested?.Invoke();
        }
    }
}
