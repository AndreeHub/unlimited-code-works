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
    private bool _suppressBranchReload;
    internal string? CurrentSymbolFilePath { get; private set; }

    // -----------------------------------------------------------------------
    // Workspace loading
    // -----------------------------------------------------------------------
    [RelayCommand]
    public async Task OpenWorkspaceAsync()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open Workspace",
            Filter = "Solution / Project|*.sln;*.csproj;*.shproj|All Files|*.*",
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

    [RelayCommand]
    public async Task OpenBranchWorkspaceAsync()
    {
        string branchName = WorkspaceBranchName?.Trim() ?? string.Empty;
        if (branchName.Length == 0)
        {
            StatusMessage = "Enter a branch name first.";
            return;
        }

        string? path = _lastWorkspaceLoadPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            var dlg = new OpenFileDialog
            {
                Title = "Open Workspace From Branch",
                Filter = "Solution / Project|*.sln;*.csproj;*.shproj|All Files|*.*",
                CheckFileExists = true
            };
            if (dlg.ShowDialog() != true) return;
            path = dlg.FileName;
        }

        await LoadWorkspaceAsync(path, branchName);
    }

    public async Task LoadFromSelectedBranchAsync(string branch)
    {
        if (_suppressBranchReload || _lastWorkspaceLoadPath is null) return;
        string? b = string.IsNullOrWhiteSpace(branch) ? null : branch.Trim();
        await LoadWorkspaceAsync(_lastWorkspaceLoadPath, b);
    }

    private async Task LoadWorkspaceAsync(string path, string? branchName = null)
    {
        string? normalizedBranch = string.IsNullOrWhiteSpace(branchName) ? null : branchName.Trim();
        StatusMessage = normalizedBranch is null
            ? "Loading workspace..."
            : $"Loading workspace from {normalizedBranch}...";
        try
        {
            var ct = CancellationToken.None;
            _lastWorkspaceLoadPath = path;
            _currentSnapshot = await _workspace.LoadAsync(path, ct, normalizedBranch);
            WorkspacePath = _currentSnapshot.DisplayName;
            StatusMessage = $"Loaded: {_currentSnapshot.DisplayName} - {_currentSnapshot.Files.Count} files";

            await RefreshAvailableBranchesAsync(path, normalizedBranch, ct);
            BuildExplorer(_currentSnapshot);
            await LoadSessionsAsync(_currentSnapshot.WorkspaceKey, ct);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            _logger.LogError(ex, "Failed to load workspace.");
        }
    }

    private async Task RefreshAvailableBranchesAsync(string path, string? activeBranch, CancellationToken ct)
    {
        var branches = await WorkspaceManager.GetBranchNamesAsync(path, ct);
        _suppressBranchReload = true;
        try
        {
            AvailableBranches.Clear();
            AvailableBranches.Add(string.Empty); // represents "current checkout / no branch override"
            foreach (var b in branches)
                AvailableBranches.Add(b);

            SelectedBranch = activeBranch ?? string.Empty;
            WorkspaceBranchName = activeBranch ?? string.Empty;
        }
        finally
        {
            _suppressBranchReload = false;
        }
    }

    private void BuildExplorer(WorkspaceSnapshot snapshot)
    {
        ExplorerRoots.Clear();
        var grouped = snapshot.Files.GroupBy(f => f.ClusterKey, StringComparer.OrdinalIgnoreCase);
        foreach (var group in grouped.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var folder = new FileExplorerItemViewModel(group.Key, null, isFile: false, isProjectRoot: true) { IsExpanded = true };
            foreach (var file in group.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                AddFileToExplorer(folder, file);
            }
            ExplorerRoots.Add(folder);
        }

        SymbolRoots.Clear();
        SelectedSymbolsHeader = "Symbols";
    }

    [RelayCommand]
    public void ApplyExplorerSearch()
    {
        if (_currentSnapshot is null) return;
        string query = ExplorerSearchQuery?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            BuildExplorer(_currentSnapshot);
            return;
        }

        var filtered = _currentSnapshot with
        {
            Files = _currentSnapshot.Files
                .Where(f => f.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(f.FilePath).Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList()
        };
        BuildExplorer(filtered);
        StatusMessage = $"Explorer search: {filtered.Files.Count} file(s)";
    }

    private static void AddFileToExplorer(FileExplorerItemViewModel projectRoot, WorkspaceFileSummary file)
    {
        string rel = file.RelativePath;
        string projectMarker = file.ProjectName.Replace('\\', '/');
        int markerIndex = rel.IndexOf(projectMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
            rel = rel[(markerIndex + projectMarker.Length)..].TrimStart('/');

        string[] segments = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            projectRoot.Children.Add(new FileExplorerItemViewModel(Path.GetFileName(file.FilePath), file.FilePath, isFile: true));
            return;
        }

        var parent = projectRoot;
        foreach (string segment in segments.Take(segments.Length - 1))
        {
            var folder = parent.Children.FirstOrDefault(c => c.IsFolder && c.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
            if (folder is null)
            {
                folder = new FileExplorerItemViewModel(segment, null, isFile: false);
                parent.Children.Add(folder);
            }
            parent = folder;
        }

        parent.Children.Add(new FileExplorerItemViewModel(segments[^1], file.FilePath, isFile: true));
    }

    public async Task LoadSymbolsForFileAsync(string filePath)
    {
        CurrentSymbolFilePath = filePath;
        SymbolRoots.Clear();
        SelectedSymbolsHeader = $"Symbols ({Path.GetFileName(filePath)})";
        IsSymbolsPanelVisible = true;

        var structure = await _fileStructure.GetFileStructureAsync(filePath, CancellationToken.None);
        if (structure is null || structure.Types.Count == 0)
        {
            SymbolRoots.Add(new SymbolExplorerItemViewModel("No symbols found", "Try another source file", iconKind: "empty"));
            return;
        }

        foreach (var type in structure.Types)
        {
            var typeItem = new SymbolExplorerItemViewModel($"{type.Kind} {type.Name}", $"Lines {type.StartLine}-{type.EndLine}", type.StartLine, type.EndLine, type.Kind);
            foreach (var method in type.Methods)
                typeItem.Children.Add(new SymbolExplorerItemViewModel(method.Signature, method.Kind, method.StartLine, method.EndLine, method.Kind));
            typeItem.IsExpanded = true;
            SymbolRoots.Add(typeItem);
        }
    }
}
