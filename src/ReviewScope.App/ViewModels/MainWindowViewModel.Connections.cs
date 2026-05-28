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
        var conn = new RenderConnection(Guid.NewGuid(), args.SourceKey, args.TargetKey,
            SourceAnchorIndex: args.SourceAnchorIndex,
            TargetAnchorIndex: args.TargetAnchorIndex,
            ArrowPosition: 0.9,
            MidControlX: args.MidControlX,
            MidControlY: args.MidControlY,
            MidControlBends: args.MidControlBends,
            SourceLineId: args.SourceLineId,
            TargetLineId: args.TargetLineId);
        var connections = Scene.Connections.Append(conn).ToList();
        SetSceneFromUserAction(Scene with { Connections = connections });
        await PersistSessionAsync();
}
}
