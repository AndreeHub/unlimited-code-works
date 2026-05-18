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
    // Swim-lanes
    // -----------------------------------------------------------------------
    [RelayCommand]
    public async Task AddSwimLaneAsync()
    {
        if (_currentSnapshot is null) return;
        string color = LaneColors[_nextLaneColor++ % LaneColors.Length];
        string name = $"Layer {Scene.SwimLanes.Count + 1}";
        string key = $"lane::{Guid.NewGuid():N}";
        var lane = new RenderSwimLane(Guid.NewGuid(), key, name, color, 100, 100, 600, 400);
        var lanes = Scene.SwimLanes.Append(lane).ToList();
        Scene = Scene with { SwimLanes = lanes };
        await PersistSessionAsync();
}
}
