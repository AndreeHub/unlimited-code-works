using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReviewScope.Analysis;
using ReviewScope.App.Persistence;
using ReviewScope.App.ViewModels;
using ReviewScope.Domain;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace ReviewScope.App;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Apply the persisted (or default-light) theme before any window is shown so the
        // shared brush resources carry the right colors from the first frame.
        Theming.ThemeManager.Initialize();

        if (!MSBuildLocator.IsRegistered)
            MSBuildLocator.RegisterDefaults();

        _host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => { logging.ClearProviders(); logging.AddConsole(); })
            .ConfigureServices(services =>
            {
                services.AddSingleton<SemanticCache>();
                services.AddSingleton<WorkspaceManager>();
                services.AddSingleton<FileStructureService>();
                services.AddSingleton<SemanticSpanService>();
                services.AddSingleton<SymbolScopeService>();
                services.AddSingleton<SessionRepository>();
                services.AddSingleton<TagIndexStore>();
                services.AddSingleton<ITagIndex>(sp => sp.GetRequiredService<TagIndexStore>());
                services.AddSingleton<MainWindowViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        await _host.StartAsync();

        string? screenshotPath = null;
        string workspacePath = Directory.GetCurrentDirectory();

        for (int i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i].Equals("--screenshot", StringComparison.OrdinalIgnoreCase) && i + 1 < e.Args.Length)
            {
                screenshotPath = e.Args[i + 1];
            }
            else if (e.Args[i].Equals("--workspace", StringComparison.OrdinalIgnoreCase) && i + 1 < e.Args.Length)
            {
                workspacePath = e.Args[i + 1];
            }
        }

        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();

        if (screenshotPath is not null)
        {
            try
            {
                var vm = _host.Services.GetRequiredService<MainWindowViewModel>();
                await vm.LoadWorkspaceAsync(workspacePath);
                
                // Populate a comprehensive demo session for visual verification of Excalidraw style
                var noteId = Guid.NewGuid();
                var note = new RenderBlock(
                    noteId,
                    $"note::{noteId:N}",
                    BlockKind.Note,
                    "Refactor Plan",
                    "",
                    350, 150, 220, 140,
                    IsSelected: true,
                    Body: "1. Migrate to Direct2D sketchy rendering.\n2. Overhaul sidebar into a beautiful floating panel.\n3. Verify everything with headless screenshots.",
                    ZIndex: 1,
                    LayerKey: "layer::notes",
                    Style: new BoardItemStyle(Fill: "#FFFBEB", Stroke: "#D97706", Text: "#172033", StrokeWidth: 1.5)
                );

                var dbId = Guid.NewGuid();
                var db = new RenderBlock(
                    dbId,
                    $"shape::{dbId:N}",
                    BlockKind.Shape,
                    "Users Database",
                    "",
                    150, 320, 180, 120,
                    ShapeType: "database",
                    ZIndex: 2,
                    LayerKey: "layer::architecture",
                    Style: new BoardItemStyle(Fill: "#DBEAFE", Stroke: "#2E7DD7", Text: "#172033", StrokeWidth: 1.5)
                );

                var decId = Guid.NewGuid();
                var decision = new RenderBlock(
                    decId,
                    $"shape::{decId:N}",
                    BlockKind.Shape,
                    "Authorized?",
                    "",
                    400, 320, 150, 120,
                    ShapeType: "decision",
                    ZIndex: 3,
                    LayerKey: "layer::architecture",
                    Style: new BoardItemStyle(Fill: "#FEE2E2", Stroke: "#DC2626", Text: "#172033", StrokeWidth: 1.5)
                );

                var containerId = Guid.NewGuid();
                var container = new RenderBlock(
                    containerId,
                    $"container::{containerId:N}",
                    BlockKind.Container,
                    "Secure API Area",
                    "",
                    100, 80, 520, 420,
                    ZIndex: 0,
                    LayerKey: "layer::architecture",
                    ShapeType: "container",
                    Style: new BoardItemStyle(Fill: "#F8FAFC", Stroke: "#64748B", Text: "#334155", StrokeWidth: 1.5, Dashed: true, Opacity: 0.85)
                );

                var codeId = Guid.NewGuid();
                var codeCard = new RenderBlock(
                    codeId,
                    $"extract::{codeId:N}",
                    BlockKind.Extract,
                    "App.xaml.cs",
                    "Startup Handler",
                    650, 100, 480, 300,
                    ZIndex: 4,
                    Body: "protected override async void OnStartup(StartupEventArgs e)\n{\n    base.OnStartup(e);\n    if (!MSBuildLocator.IsRegistered)\n        MSBuildLocator.RegisterDefaults();\n    // ...\n}",
                    LayerKey: "layer::code",
                    Style: new BoardItemStyle(Fill: "#FFFFFF", Stroke: "#CBD5E1", Text: "#111827", StrokeWidth: 1.2)
                );

                var conn1 = new RenderConnection(
                    Guid.NewGuid(),
                    db.Key,
                    decision.Key,
                    "Query",
                    RouteKind: ConnectorRouteKind.Curved,
                    ArrowKind: ConnectorArrowKind.Forward,
                    Stroke: "#2E7DD7",
                    Dashed: false
                );

                var conn2 = new RenderConnection(
                    Guid.NewGuid(),
                    decision.Key,
                    note.Key,
                    "Alert",
                    RouteKind: ConnectorRouteKind.Orthogonal,
                    ArrowKind: ConnectorArrowKind.Forward,
                    Stroke: "#DC2626",
                    Dashed: true
                );

                var blocks = new System.Collections.Generic.List<RenderBlock> { container, db, decision, note, codeCard };
                var connections = new System.Collections.Generic.List<RenderConnection> { conn1, conn2 };
                vm.Scene = vm.Scene with { Blocks = blocks, Connections = connections };

                Console.WriteLine($"[DEBUG SCREENSHOT] HasBoardSelection: {vm.HasBoardSelection}");
                Console.WriteLine($"[DEBUG SCREENSHOT] SelectedBlock: {vm.SelectedBlock?.Title ?? "None"}");
                Console.WriteLine($"[DEBUG SCREENSHOT] SelectionIsBlock: {vm.SelectionIsBlock}");

                // Wait for layout passes and Direct2D frame completion
                await Task.Delay(3500);

                var handle = new WindowInteropHelper(window).Handle;
                WindowCaptureHelper.CaptureWindow(handle, screenshotPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Screenshot failure: {ex}");
            }
            finally
            {
                Shutdown(0);
            }
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            try
            {
                var vm = _host.Services.GetService<MainWindowViewModel>();
                if (vm is not null)
                    await vm.FlushPendingSaveAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Final save failure: {ex}");
            }

            await _host.StopAsync(TimeSpan.FromSeconds(3));
            _host.Dispose();
        }
        base.OnExit(e);
    }
}

internal static class WindowCaptureHelper
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hDC, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hDestDC, int x, int y, int nWidth, int nHeight, IntPtr hSrcDC, int xSrc, int ySrc, int dwRop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private const int SRCCOPY = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public static void CaptureWindow(IntPtr hWnd, string filename)
    {
        GetWindowRect(hWnd, out RECT rect);
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;

        if (width <= 0 || height <= 0) return;

        IntPtr hWindowDC = GetWindowDC(hWnd);
        IntPtr hMemoryDC = CreateCompatibleDC(hWindowDC);
        IntPtr hBitmap = CreateCompatibleBitmap(hWindowDC, width, height);
        IntPtr hOldBitmap = SelectObject(hMemoryDC, hBitmap);

        BitBlt(hMemoryDC, 0, 0, width, height, hWindowDC, 0, 0, SRCCOPY);

        BitmapSource bmpSource = Imaging.CreateBitmapSourceFromHBitmap(
            hBitmap,
            IntPtr.Zero,
            Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());

        SelectObject(hMemoryDC, hOldBitmap);
        DeleteDC(hMemoryDC);
        ReleaseDC(hWnd, hWindowDC);
        DeleteObject(hBitmap);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmpSource));

        string? dir = Path.GetDirectoryName(filename);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var fileStream = new FileStream(filename, FileMode.Create);
        encoder.Save(fileStream);
    }
}
