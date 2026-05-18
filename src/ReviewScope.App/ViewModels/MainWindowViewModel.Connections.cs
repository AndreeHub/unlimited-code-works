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
    // Connections
    // -----------------------------------------------------------------------
    public async Task HandleConnectionDrawnAsync(ConnectionDrawnArgs args)
    {
        string key = $"{args.SourceKey}->{args.TargetKey}";
        if (Scene.Connections.Any(c =>
            c.SourceKey.Equals(args.SourceKey, StringComparison.OrdinalIgnoreCase) &&
            c.TargetKey.Equals(args.TargetKey, StringComparison.OrdinalIgnoreCase)))
            return;

        var conn = new RenderConnection(Guid.NewGuid(), args.SourceKey, args.TargetKey);
        var connections = Scene.Connections.Append(conn).ToList();
        Scene = Scene with { Connections = connections };
        await PersistSessionAsync();
}
}
