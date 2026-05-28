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
    private static readonly HashSet<string> FileExplorerSkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".vs",
        ".reviewscope",
        "bin",
        "obj",
        "node_modules",
        "packages"
    };

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

    internal async Task LoadWorkspaceAsync(string path, string? branchName = null)
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        int loadVersion = ++_loadVersion;
        var ct = _loadCts.Token;
        string? normalizedBranch = string.IsNullOrWhiteSpace(branchName) ? null : branchName.Trim();
        StatusMessage = normalizedBranch is null
            ? "Loading workspace..."
            : $"Loading workspace from {normalizedBranch}...";
        try
        {
            _lastWorkspaceLoadPath = path;
            _currentSnapshot = await _workspace.LoadAsync(path, ct, normalizedBranch);
            if (loadVersion != _loadVersion || ct.IsCancellationRequested) return;
            WorkspacePath = _currentSnapshot.DisplayName;
            StatusMessage = $"Loaded: {_currentSnapshot.DisplayName} - {_currentSnapshot.Files.Count} files";

            await RefreshAvailableBranchesAsync(path, normalizedBranch, ct);
            if (loadVersion != _loadVersion || ct.IsCancellationRequested) return;
            BuildExplorer(_currentSnapshot);
            BuildFileExplorer(_currentSnapshot);
            await _tagIndex.LoadAsync(_currentSnapshot.WorkspaceKey, ct);
            await LoadSessionsAsync(_currentSnapshot.WorkspaceKey, ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (loadVersion != _loadVersion) return;
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

    private void BuildFileExplorer(WorkspaceSnapshot snapshot, string? query = null)
    {
        FileExplorerRoots.Clear();

        string rootPath = ResolveWorkspaceFolder(snapshot.WorkspacePath);
        FileExplorerRootPath = rootPath;
        if (!Directory.Exists(rootPath))
            return;

        string rootName = Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(rootName))
            rootName = rootPath;

        var root = new FileExplorerItemViewModel(rootName, null, isFile: false, isProjectRoot: true) { IsExpanded = true };
        string trimmedQuery = query?.Trim() ?? string.Empty;
        PopulateFileExplorerNode(root, rootPath, trimmedQuery);
        FileExplorerRoots.Add(root);
    }

    [RelayCommand]
    public void ApplyExplorerSearch()
    {
        if (_currentSnapshot is null) return;
        string query = ExplorerSearchQuery?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            BuildExplorer(_currentSnapshot);
            BuildFileExplorer(_currentSnapshot);
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
        BuildFileExplorer(_currentSnapshot, query);
        StatusMessage = $"Explorer search: {filtered.Files.Count} file(s)";
    }

    private static bool PopulateFileExplorerNode(FileExplorerItemViewModel parent, string directoryPath, string query)
    {
        var childDirectories = EnumerateDirectoriesSafe(directoryPath)
            .Where(d => !FileExplorerSkippedDirectories.Contains(Path.GetFileName(d)))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var childFiles = EnumerateFilesSafe(directoryPath)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        bool hasMatch = false;
        foreach (string childDirectory in childDirectories)
        {
            var folder = new FileExplorerItemViewModel(Path.GetFileName(childDirectory), null, isFile: false);
            bool childMatched = PopulateFileExplorerNode(folder, childDirectory, query);
            bool selfMatched = MatchesExplorerQuery(childDirectory, query);
            if (query.Length == 0 || childMatched || selfMatched)
            {
                folder.IsExpanded = query.Length > 0 && (childMatched || selfMatched);
                parent.Children.Add(folder);
                hasMatch = true;
            }
        }

        foreach (string file in childFiles)
        {
            if (query.Length > 0 && !MatchesExplorerQuery(file, query))
                continue;

            parent.Children.Add(new FileExplorerItemViewModel(Path.GetFileName(file), file, isFile: true));
            hasMatch = true;
        }

        return hasMatch;
    }

    private static IEnumerable<string> EnumerateDirectoriesSafe(string directoryPath)
    {
        try
        {
            return Directory.EnumerateDirectories(directoryPath);
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> EnumerateFilesSafe(string directoryPath)
    {
        try
        {
            return Directory.EnumerateFiles(directoryPath);
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static bool MatchesExplorerQuery(string path, string query) =>
        query.Length == 0 ||
        Path.GetFileName(path).Contains(query, StringComparison.OrdinalIgnoreCase) ||
        path.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static string ResolveWorkspaceFolder(string workspacePath)
    {
        string path = Path.GetFullPath(workspacePath);
        if (Directory.Exists(path)) return path;
        return Path.GetDirectoryName(path) ?? path;
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
        SelectedLeftTabIndex = 1;

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
