using Microsoft.Extensions.Logging;
using ReviewScope.Analysis;
using ReviewScope.App.Persistence;
using ReviewScope.App.ViewModels;
using ReviewScope.Domain;
using Xunit;

namespace ReviewScope.Domain.Tests;

public sealed class ViewSwitchPersistenceTests
{
    [Fact]
    public async Task SwitchingBetweenCanvasPageAndJournalPersistsPendingEdits()
    {
        await using var env = await TestWorkspace.CreateAsync();
        var vm = env.ViewModel;

        var board = vm.Sessions.First(s => s.Kind == DocumentKind.Canvas);
        var originalBlock = TextBlock("note::one", "Draft", "before");
        var updatedBlock = originalBlock with { Title = "Canvas persisted", Body = "after canvas edit", X = 42, Y = 84 };

        vm.Scene = vm.Scene with { Blocks = new[] { originalBlock } };
        vm.UpdateSceneBlock(updatedBlock, "test canvas edit");

        await vm.CreateNewPageAsync();
        var page = vm.SelectedSession!;
        const string pageBody = "- page survives\n  - child survives";
        vm.OnOutlineBodyEdited(pageBody);
        vm.OnOutlineCollapsedChanged(new[] { "deadbeef" });

        await vm.OpenTodayJournalAsync();
        var journal = vm.SelectedSession!;
        const string journalBody = "- journal survives\n  - nested journal note";
        vm.OnOutlineBodyEdited(journalBody);

        await vm.ToggleSplitViewAsync();
        await vm.ToggleSplitViewAsync();
        vm.SelectedSession = board;
        await vm.ActivateSelectedSessionAsync();

        var reloaded = await env.ReloadAsync();
        var savedBoard = reloaded.Sessions.Single(s => s.Id == board.Id);
        var savedPage = reloaded.Sessions.Single(s => s.Id == page.Id);
        var savedJournal = reloaded.Sessions.Single(s => s.Id == journal.Id);

        var savedBlock = Assert.Single(savedBoard.Blocks);
        Assert.Equal("Canvas persisted", savedBlock.Title);
        Assert.Equal("after canvas edit", savedBlock.Body);
        Assert.Equal(42, savedBlock.X);
        Assert.Equal(84, savedBlock.Y);
        Assert.Equal(pageBody, savedPage.OutlineBody);
        Assert.Equal("deadbeef", savedPage.OutlineCollapsed);
        Assert.Equal(journalBody, savedJournal.OutlineBody);
    }

    [Fact]
    public async Task ClosingActiveOutlineTabFlushesPendingPageEdits()
    {
        await using var env = await TestWorkspace.CreateAsync();
        var vm = env.ViewModel;

        await vm.CreateNewPageAsync();
        var page = vm.SelectedSession!;
        const string body = "- close tab should save me";
        vm.OnOutlineBodyEdited(body);

        var tab = vm.OutlineTabs.Single(t => t.Id == page.Id);
        await vm.CloseTabAsync(tab);

        var reloaded = await env.ReloadAsync();
        Assert.Equal(body, reloaded.Sessions.Single(s => s.Id == page.Id).OutlineBody);
    }

    [Fact]
    public async Task ClosingActiveCanvasTabFlushesPendingBoardEdits()
    {
        await using var env = await TestWorkspace.CreateAsync();
        var vm = env.ViewModel;

        var board = vm.Sessions.First(s => s.Kind == DocumentKind.Canvas);
        var originalBlock = TextBlock("note::two", "Original", "before");
        var updatedBlock = originalBlock with { Title = "Closed tab saved", Body = "after close" };

        vm.Scene = vm.Scene with { Blocks = new[] { originalBlock } };
        vm.UpdateSceneBlock(updatedBlock, "test close canvas tab");

        var tab = vm.CanvasTabs.Single(t => t.Id == board.Id);
        await vm.CloseTabAsync(tab);

        var reloaded = await env.ReloadAsync();
        var savedBlock = Assert.Single(reloaded.Sessions.Single(s => s.Id == board.Id).Blocks);
        Assert.Equal("Closed tab saved", savedBlock.Title);
        Assert.Equal("after close", savedBlock.Body);
    }

    private static RenderBlock TextBlock(string key, string title, string body) =>
        new(Guid.NewGuid(), key, BlockKind.Text, title, string.Empty, 10, 20, 240, 120, Body: body);

    private sealed class TestWorkspace : IAsyncDisposable
    {
        private TestWorkspace(string path, MainWindowViewModel viewModel)
        {
            Path = path;
            ViewModel = viewModel;
        }

        public string Path { get; }
        public MainWindowViewModel ViewModel { get; }

        public static async Task<TestWorkspace> CreateAsync()
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ReviewScopePersistenceTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            await File.WriteAllTextAsync(System.IO.Path.Combine(path, "README.md"), "# test workspace");

            var vm = CreateViewModel();
            await vm.LoadWorkspaceAsync(path);
            return new TestWorkspace(path, vm);
        }

        public async Task<MainWindowViewModel> ReloadAsync()
        {
            await ViewModel.FlushPendingOutlineSaveAsync();
            await ViewModel.FlushPendingSaveAsync();
            var vm = CreateViewModel();
            await vm.LoadWorkspaceAsync(Path);
            return vm;
        }

        public async ValueTask DisposeAsync()
        {
            await ViewModel.FlushPendingOutlineSaveAsync();
            await ViewModel.FlushPendingSaveAsync();
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static MainWindowViewModel CreateViewModel()
        {
            var repo = new SessionRepository();
            var cache = new SemanticCache();
            var workspace = new WorkspaceManager(new TestLogger<WorkspaceManager>(), cache);
            var fileStructure = new FileStructureService();
            var semanticSpan = new SemanticSpanService(workspace, cache);
            var symbolScope = new SymbolScopeService(workspace);
            var tagIndex = new TagIndexStore(repo);
            var progress = new ReviewProgressStore(repo);

            return new MainWindowViewModel(
                workspace,
                fileStructure,
                semanticSpan,
                symbolScope,
                repo,
                tagIndex,
                progress,
                new TestLogger<MainWindowViewModel>());
        }
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }
}
