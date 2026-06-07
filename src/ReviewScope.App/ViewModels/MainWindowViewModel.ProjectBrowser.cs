using ReviewScope.Domain;

namespace ReviewScope.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    public void RefreshProjectBrowser(string? query = null)
    {
        string trimmedQuery = query?.Trim() ?? string.Empty;

        ProjectBrowserRoots.Clear();

        var root = ProjectBrowserItemViewModel.Project(
            string.IsNullOrWhiteSpace(UbwProjectName) ? "Untitled Boards" : UbwProjectName);
        root.IsExpanded = true;

        AddProjectGroup(root, "Boards", "board", DocumentKind.Canvas, trimmedQuery);
        AddProjectGroup(root, "Pages", "page", DocumentKind.Page, trimmedQuery);
        AddProjectGroup(root, "Journals", "journal", DocumentKind.Journal, trimmedQuery);

        ProjectBrowserRoots.Add(root);
    }

    private void AddProjectGroup(
        ProjectBrowserItemViewModel root,
        string groupName,
        string iconKind,
        DocumentKind kind,
        string query)
    {
        var group = ProjectBrowserItemViewModel.Group(groupName, iconKind);
        group.IsExpanded = true;

        foreach (var session in Sessions
            .Where(s => s.Kind == kind && MatchesProjectBrowserQuery(s, groupName, query))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            group.Children.Add(ProjectBrowserItemViewModel.Document(session));
        }

        if (group.Children.Count > 0 || query.Length == 0)
            root.Children.Add(group);
    }

    private static bool MatchesProjectBrowserQuery(ReviewSession session, string groupName, string query) =>
        query.Length == 0
        || session.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || groupName.Contains(query, StringComparison.OrdinalIgnoreCase)
        || session.Kind.ToString().Contains(query, StringComparison.OrdinalIgnoreCase);
}
