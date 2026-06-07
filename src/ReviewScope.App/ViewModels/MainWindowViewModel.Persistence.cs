using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ReviewScope.Domain;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace ReviewScope.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private static readonly JsonSerializerOptions BoardFileJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    [RelayCommand]
    public async Task OpenProjectAsync() => await LoadWorkAsync();

    [RelayCommand]
    public async Task SaveWorkAsync()
    {
        await EnsureActiveBoardAsync();
        if (_lastBoardFilePath is null)
        {
            await SaveWorkAsAsync();
            return;
        }

        await SaveActiveSessionNowAsync(showStatus: false);
        await WriteBoardsFileAsync(_lastBoardFilePath);
        StatusMessage = $"Saved {Sessions.Count} board(s): {Path.GetFileName(_lastBoardFilePath)}";
    }

    [RelayCommand]
    public async Task SaveWorkAsAsync()
    {
        await EnsureActiveBoardAsync();

        await SaveActiveSessionNowAsync(showStatus: false);

        string workspaceName = string.IsNullOrWhiteSpace(UbwProjectName)
            ? _currentSnapshot?.DisplayName ?? "Untitled Boards"
            : UbwProjectName;
        var dlg = new SaveFileDialog
        {
            Title = "Save UBW Project",
            Filter = "UBW Projects|*.reviewscope.json|JSON|*.json",
            DefaultExt = ".reviewscope.json",
            AddExtension = true,
            FileName = SanitizeFileName($"{workspaceName}-boards")
        };

        if (dlg.ShowDialog() != true) return;

        _lastBoardFilePath = dlg.FileName;
        UbwProjectName = ProjectNameFromFile(_lastBoardFilePath);
        await WriteBoardsFileAsync(_lastBoardFilePath);
        StatusMessage = $"Saved {Sessions.Count} board(s): {Path.GetFileName(dlg.FileName)}";
    }

    [RelayCommand]
    public async Task LoadWorkAsync()
    {
        var picked = await PickBoardsFileAsync("Open UBW Project");
        if (picked is null) return;

        var (fileName, loadedFile) = picked.Value;

        if (loadedFile.Boards.Count == 0)
        {
            StatusMessage = "Could not open UBW project.";
            return;
        }

        var workspaceReference = ResolveWorkspaceReference(loadedFile);
        if (workspaceReference is not null)
            await LoadWorkspaceAsync(workspaceReference.Value.Path, workspaceReference.Value.BranchName);

        var workspace = EnsureBoardWorkspace();
        foreach (var existing in Sessions.ToArray())
            await _sessions.DeleteSessionAsync(existing.Id, workspace.WorkspaceKey, CancellationToken.None);

        Sessions.Clear();
        var boards = new List<ReviewSession>();
        foreach (var loaded in loadedFile.Boards)
        {
            bool duplicateId = boards.Any(s => s.Id == loaded.Id);
            var board = loaded with
            {
                Id = duplicateId ? Guid.NewGuid() : loaded.Id,
                Name = UniqueImportedBoardName(loaded.Name, boards),
                WorkspaceKey = workspace.WorkspaceKey,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            boards.Add(board);
            Sessions.Add(board);
            await _sessions.SaveSessionAsync(board, CancellationToken.None);
        }

        await _sessions.SaveSessionOrderAsync(workspace.WorkspaceKey, Sessions.Select(s => s.Id).ToArray(), CancellationToken.None);
        var firstBoard = boards[0];
        SessionSpawnAnimationId = firstBoard.Id;
        _lastBoardFilePath = fileName;
        UbwProjectName = ProjectNameFromFile(fileName);
        await ActivateSessionAsync(firstBoard);

        StatusMessage = $"Opened UBW project: {Path.GetFileName(fileName)}";
    }

    [RelayCommand]
    public async Task ImportProjectAsync()
    {
        var picked = await PickBoardsFileAsync("Import From UBW Project");
        if (picked is null) return;

        var (fileName, loadedFile) = picked.Value;
        if (loadedFile.Boards.Count == 0)
        {
            StatusMessage = "Could not import UBW project.";
            return;
        }

        var workspace = EnsureBoardWorkspace();
        var imported = new List<ReviewSession>();
        foreach (var loaded in loadedFile.Boards)
        {
            var copy = loaded with
            {
                Id = Guid.NewGuid(),
                Name = UniqueImportedBoardName(loaded.Name, imported),
                WorkspaceKey = workspace.WorkspaceKey,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            imported.Add(copy);
            Sessions.Add(copy);
            await _sessions.SaveSessionAsync(copy, CancellationToken.None);
        }

        await _sessions.SaveSessionOrderAsync(workspace.WorkspaceKey, Sessions.Select(s => s.Id).ToArray(), CancellationToken.None);
        if (imported.Count > 0)
            await ActivateSessionAsync(imported[0], hydrateCode: false);

        StatusMessage = $"Imported {imported.Count} document(s) from {Path.GetFileName(fileName)}";
    }

    [RelayCommand]
    public void OpenSettings()
    {
        string saveLocation = _currentSnapshot is null
            ? "Open a workspace to create a .reviewscope project folder."
            : _sessions.GetReviewScopeDir(_currentSnapshot.WorkspaceKey);

        MessageBox.Show(
            $"ReviewScope saves boards as JSON.\n\nAutosave: on\nSave location:\n{saveLocation}\n\nUse Save As to write a portable file containing all boards. You can create, save, and load boards without opening a code project.",
            "ReviewScope Settings",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        StatusMessage = "Settings opened.";
    }

    private async Task EnsureActiveBoardAsync()
    {
        EnsureBoardWorkspace();
        if (_activeSession is null)
            await CreateNewSessionAsync();
    }

    private async Task WriteBoardsFileAsync(string path)
    {
        string workspaceName = string.IsNullOrWhiteSpace(UbwProjectName)
            ? _currentSnapshot?.DisplayName ?? "Untitled Boards"
            : UbwProjectName;
        var boardFile = new ReviewScopeBoardsFile
        {
            SchemaVersion = 1,
            AppName = "ReviewScope",
            WorkspaceName = workspaceName,
            WorkspacePath = _lastWorkspaceLoadPath ?? _currentSnapshot?.WorkspacePath,
            WorkspaceBranchName = _currentSnapshot?.BranchName,
            SavedAt = DateTimeOffset.UtcNow,
            BoardOrder = Sessions.Select(s => s.Id).ToArray(),
            Boards = Sessions.ToArray()
        };

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, boardFile, BoardFileJsonOptions);
    }

    private async Task<(string FileName, ReviewScopeBoardsFile Boards)?> PickBoardsFileAsync(string title)
    {
        var dlg = new OpenFileDialog
        {
            Title = title,
            Filter = "UBW Projects|*.reviewscope.json;*.json|JSON|*.json|All Files|*.*",
            CheckFileExists = true
        };

        if (dlg.ShowDialog() != true) return null;

        try
        {
            return (dlg.FileName, await ReadBoardsFileAsync(dlg.FileName));
        }
        catch (JsonException ex)
        {
            StatusMessage = $"Could not load JSON: {ex.Message}";
            return null;
        }
        catch (IOException ex)
        {
            StatusMessage = $"Could not read file: {ex.Message}";
            return null;
        }
    }

    private static async Task<ReviewScopeBoardsFile> ReadBoardsFileAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        using var doc = await JsonDocument.ParseAsync(stream);
        if (doc.RootElement.ValueKind == JsonValueKind.Object &&
            doc.RootElement.TryGetProperty("boards", out var boardsElement) &&
            boardsElement.ValueKind == JsonValueKind.Array)
        {
            var boardFile = doc.RootElement.Deserialize<ReviewScopeBoardsFile>(BoardFileJsonOptions);
            return boardFile ?? new ReviewScopeBoardsFile();
        }

        var singleBoard = doc.RootElement.Deserialize<ReviewSession>(BoardFileJsonOptions);
        return new ReviewScopeBoardsFile
        {
            Boards = singleBoard is null ? Array.Empty<ReviewSession>() : new[] { singleBoard }
        };
    }

    private static (string Path, string? BranchName)? ResolveWorkspaceReference(ReviewScopeBoardsFile boardFile)
    {
        if (!string.IsNullOrWhiteSpace(boardFile.WorkspacePath) &&
            (File.Exists(boardFile.WorkspacePath) || Directory.Exists(boardFile.WorkspacePath)))
            return (boardFile.WorkspacePath, string.IsNullOrWhiteSpace(boardFile.WorkspaceBranchName) ? null : boardFile.WorkspaceBranchName);

        string? workspaceKey = boardFile.Boards
            .Select(b => b.WorkspaceKey)
            .FirstOrDefault(k => !string.IsNullOrWhiteSpace(k));
        if (string.IsNullOrWhiteSpace(workspaceKey))
            return null;

        string path = workspaceKey;
        string? branchName = null;
        int branchSeparator = workspaceKey.IndexOf("::", StringComparison.Ordinal);
        if (branchSeparator >= 0)
        {
            path = workspaceKey[..branchSeparator];
            branchName = workspaceKey[(branchSeparator + 2)..];
        }

        return File.Exists(path) || Directory.Exists(path)
            ? (path, string.IsNullOrWhiteSpace(branchName) ? null : branchName)
            : null;
    }

    private string UniqueImportedBoardName(string? requestedName, IReadOnlyList<ReviewSession> pendingImports)
    {
        string baseName = string.IsNullOrWhiteSpace(requestedName) ? "Imported Board" : requestedName.Trim();
        if (!BoardNameExists(baseName, pendingImports))
            return baseName;

        for (int i = 2; ; i++)
        {
            string candidate = $"{baseName} {i}";
            if (!BoardNameExists(candidate, pendingImports))
                return candidate;
        }
    }

    private bool BoardNameExists(string name, IReadOnlyList<ReviewSession> pendingImports) =>
        Sessions.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) ||
        pendingImports.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    private static string SanitizeFileName(string name)
    {
        string safe = string.IsNullOrWhiteSpace(name) ? "boards" : name.Trim();
        foreach (char c in Path.GetInvalidFileNameChars())
            safe = safe.Replace(c, '-');
        return safe;
    }

    private static string ProjectNameFromFile(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        const string suffix = ".reviewscope";
        if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            name = name[..^suffix.Length];
        return string.IsNullOrWhiteSpace(name) ? "Untitled Boards" : name;
    }

    private sealed class ReviewScopeBoardsFile
    {
        public int SchemaVersion { get; init; } = 1;
        public string AppName { get; init; } = "ReviewScope";
        public string? WorkspaceName { get; init; }
        public string? WorkspacePath { get; init; }
        public string? WorkspaceBranchName { get; init; }
        public DateTimeOffset SavedAt { get; init; }
        public IReadOnlyList<Guid> BoardOrder { get; init; } = Array.Empty<Guid>();
        public IReadOnlyList<ReviewSession> Boards { get; init; } = Array.Empty<ReviewSession>();
    }
}
