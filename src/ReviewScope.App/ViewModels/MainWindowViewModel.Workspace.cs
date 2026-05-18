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
    // Workspace loading
    // -----------------------------------------------------------------------
    [RelayCommand]
    public async Task OpenWorkspaceAsync()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open Workspace",
            Filter = "Solution / Project|*.sln;*.csproj|All Files|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog() != true) return;
        await LoadWorkspaceAsync(dlg.FileName);
    }

    [RelayCommand]
    public async Task OpenFolderAsync()
    {
        var dlg = new OpenFolderDialog { Title = "Open Folder" };
        if (dlg.ShowDialog() != true) return;
        await LoadWorkspaceAsync(dlg.FolderName);
    }

    private async Task LoadWorkspaceAsync(string path)
    {
        StatusMessage = "Loading workspaceâ€¦";
        try
        {
            var ct = CancellationToken.None;
            _currentSnapshot = await _workspace.LoadAsync(path, ct);
            WorkspacePath = _currentSnapshot.DisplayName;
            StatusMessage = $"Loaded: {_currentSnapshot.DisplayName}  â€¢  {_currentSnapshot.Files.Count} files";

            BuildExplorer(_currentSnapshot);
            await LoadSessionsAsync(_currentSnapshot.WorkspaceKey, ct);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            _logger.LogError(ex, "Failed to load workspace.");
        }
    }

    private void BuildExplorer(WorkspaceSnapshot snapshot)
    {
        ExplorerRoots.Clear();
        var grouped = snapshot.Files.GroupBy(f => f.ClusterKey, StringComparer.OrdinalIgnoreCase);
        foreach (var group in grouped.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var folder = new FileExplorerItemViewModel(group.Key, null, isFile: false) { IsExpanded = true };
            foreach (var file in group.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                var fileItem = new FileExplorerItemViewModel(
                    Path.GetFileName(file.FilePath), file.FilePath, isFile: true);
                folder.Children.Add(fileItem);
            }
            ExplorerRoots.Add(folder);
        }
}
}
