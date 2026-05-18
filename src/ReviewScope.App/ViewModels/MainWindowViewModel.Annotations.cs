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
    // Annotations
    // -----------------------------------------------------------------------
    public async Task AddAnnotationAsync(AnnotationRequestedArgs args)
    {
        var annotation = new RenderAnnotation(
            Guid.NewGuid(), args.AttachedBlockKey, "New noteâ€¦", args.WorldX, args.WorldY);
        var annotations = Scene.Annotations.Append(annotation).ToList();
        Scene = Scene with { Annotations = annotations };
        EditingAnnotationId = annotation.Id;
        SelectedAnnotationContent = annotation.Content;
        await PersistSessionAsync();
    }

    [RelayCommand]
    public async Task SaveAnnotationAsync()
    {
        if (EditingAnnotationId is null) return;
        var annotations = Scene.Annotations.Select(a =>
            a.Id == EditingAnnotationId.Value ? a with { Content = SelectedAnnotationContent } : a).ToList();
        Scene = Scene with { Annotations = annotations };
        EditingAnnotationId = null;
        await PersistSessionAsync();
}
}
