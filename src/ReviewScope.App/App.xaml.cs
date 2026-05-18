using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReviewScope.Analysis;
using ReviewScope.App.Persistence;
using ReviewScope.App.ViewModels;
using System.Windows;

namespace ReviewScope.App;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
                services.AddSingleton<MainWindowViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        await _host.StartAsync();

        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(3));
            _host.Dispose();
        }
        base.OnExit(e);
    }
}
