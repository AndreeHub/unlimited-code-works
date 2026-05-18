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
    [ObservableProperty] private bool _isEditingNote;

    public async Task SaveNoteEditAsync(string noteKey, string title, string body)
    {
        var noteBlock = Scene.Blocks.FirstOrDefault(b => b.Key.Equals(noteKey, StringComparison.OrdinalIgnoreCase));
        if (noteBlock is null) return;
        string safeTitle = string.IsNullOrWhiteSpace(title) ? "Note" : title.Trim();
        var blocks = Scene.Blocks.Select(b =>
            b.Key.Equals(noteKey, StringComparison.OrdinalIgnoreCase)
                ? b with { Title = safeTitle, Body = body }
                : b).ToList();
        var annotations = Scene.Annotations.Select(a =>
            a.Id == noteBlock.Id ? a with { Content = body } : a).ToList();
        Scene = Scene with { Blocks = blocks, Annotations = annotations };
        await PersistSessionAsync();
    }

    [RelayCommand]
    public void DiscardAnnotationEdit()
    {
        EditingAnnotationId = null;
        IsEditingNote = false;
    }

    public async Task AddAnnotationAsync(AnnotationRequestedArgs args)
    {
        var noteId = Guid.NewGuid();
        string key = $"note::{noteId:N}";
        var note = new RenderBlock(
            noteId,
            key,
            BlockKind.Note,
            "New note",
            string.Empty,
            args.WorldX,
            args.WorldY,
            280,
            130,
            Body: "New note...");

        var blocks = Scene.Blocks.Append(note).ToList();
        var annotations = Scene.Annotations
            .Append(new RenderAnnotation(noteId, args.AttachedBlockKey ?? key, note.Body!, args.WorldX, args.WorldY))
            .ToList();

        var connections = args.AttachedBlockKey is null
            ? Scene.Connections
            : Scene.Connections.Append(new RenderConnection(Guid.NewGuid(), args.AttachedBlockKey, key, "__note")).ToList();

        Scene = Scene with { Blocks = blocks, Annotations = annotations, Connections = connections };
        EditingAnnotationId = noteId;
        SelectedAnnotationContent = note.Body!;
        await PersistSessionAsync();
    }

    [RelayCommand]
    public async Task SaveAnnotationAsync()
    {
        if (EditingAnnotationId is null) return;

        var annotations = Scene.Annotations.Select(a =>
            a.Id == EditingAnnotationId.Value ? a with { Content = SelectedAnnotationContent } : a).ToList();
        var blocks = Scene.Blocks.Select(b =>
            b.Id == EditingAnnotationId.Value ? b with { Title = FirstNoteLine(SelectedAnnotationContent), Body = SelectedAnnotationContent } : b).ToList();

        Scene = Scene with { Blocks = blocks, Annotations = annotations };
        EditingAnnotationId = null;
        IsEditingNote = false;
        await PersistSessionAsync();
    }

    private static string FirstNoteLine(string content)
    {
        string first = content.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "Note";
        return first.Length <= 32 ? first : first[..32] + "...";
    }
}
