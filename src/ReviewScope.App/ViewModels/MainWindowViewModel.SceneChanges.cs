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
    // Scene changes from canvas
    // -----------------------------------------------------------------------
    public async Task OnSceneChangedByCanvas(RenderScene newScene)
    {
        SetSceneFromUserAction(newScene);
        await PersistSessionAsync();
    }
}
