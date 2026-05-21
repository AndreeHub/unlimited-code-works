# ReviewScope (Unlimited Code Works)

ReviewScope is a specialized code review and architecture visualization tool. It enables developers to map out codebases on a high-performance canvas, tracing symbols, creating connections, and documenting architectural intent visually.

## Project Overview

- **Core Goal**: Transform complex codebases into interactive architectural maps.
- **Main Technologies**:
  - **Runtime**: .NET 9.0
  - **UI Framework**: WPF (Windows Presentation Foundation)
  - **Architecture Pattern**: MVVM (using `CommunityToolkit.Mvvm`)
  - **Code Analysis**: Roslyn (`Microsoft.CodeAnalysis`) for C# parsing and semantic analysis.
  - **Rendering Engine**: Direct2D via `Vortice.Direct2D1` for hardware-accelerated canvas performance.

## File Map & Responsibilities

### `ReviewScope.Analysis`
- **`WorkspaceManager.cs`**: High-level manager for the current Roslyn workspace session.
- **`RoslynWorkspaceLoader.cs`**: Handles the complex logic of loading `.sln`, `.csproj`, and `.shproj` files.
- **`GitBranchWorkspaceResolver.cs`**: Manages Git worktrees to allow reviewing code from different branches.
- **`FileStructureService.cs`**: Parses file contents to build navigation trees.
- **`SemanticSpanService.cs`**: Uses Roslyn to identify semantic tokens (types, methods) in source code.

### `ReviewScope.Canvas`
- **`CanvasViewport.cs`**: The main host control for the rendering canvas.
- **`CanvasViewport.Rendering.cs`**: Orchestrates the high-level frame drawing.
- **`CanvasViewport.Input.cs`**: Handles user input and tool state.
- **`DrawingContext.cs`**: Wraps D2D resources and provides a drawing API to renderers.
- **`CanvasDrawingUtils.cs`**: Shared geometric, color, and layout logic.
- **`BlockRenderer.cs`**: Draws all block types (File, Extract, Note, Shape, etc.).
- **`ConnectionRenderer.cs`**: Draws lines and arrows between blocks.
- **`SelectionTool.cs`, `ConnectionTool.cs`, etc.**: Modular logic for interactive tools.

### `ReviewScope.App`
- **`MainWindowViewModel.cs`**: Primary coordinator for the application state.
- **`ViewModels/MainWindowViewModel.*.cs`**: Partial classes breaking down VM logic (CodeBlocks, Connections, Sessions, etc.).
- **`Persistence/SessionRepository.cs`**: Saves and loads canvas states as `.json`.

### `ReviewScope.Domain`
- **`Models.cs`**: Definition of all domain records (RenderBlock, RenderConnection, RenderScene).
- **`Abstractions.cs`**: Shared interfaces like `IWorkspaceLoader`.

## Building and Running
- **Build**: `dotnet build`
- **Run**: `dotnet run --project src/ReviewScope.App/ReviewScope.App.csproj`
