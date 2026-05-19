# ReviewScope Code Review Board Spec

## Summary

Build ReviewScope into a portable code-review board where the reviewer can first model the intended architecture, then place architecture docs, C#/XAML files, extracted functions, notes, screenshots, and review findings onto named sessions.

The core workflow is:

1. Create or open a ReviewScope project.
2. Draw the intended architecture.
3. Import or paste `architecture.md`.
4. Add relevant files and extracted functions.
5. Link discovered implementation details to the intended design.
6. Add review notes and findings.
7. Export an LLM-readable review package.

Out of scope for this phase:

- Realtime collaboration
- Cloud sync
- Full draw.io parity
- Full markdown authoring app

## Key Changes

- Portable project storage: use a workspace-local `.reviewscope/` folder containing `project.json`, `sessions/*.json`, `assets/`, and `exports/`. Migrate existing AppData sessions on first open without deleting old data.
- Board object model: support `File`, `Extract`, `Note`, `MarkdownDoc`, `Shape`, `Text`, `Image`, and `Container` board items. Each item has id, kind, position, size, z-index, layer id, lock state, style, and optional source binding.
- Sessions as review boards: a project contains multiple named sessions. Each session represents one application area or review topic.
- Architecture docs: paste or import markdown as rendered `MarkdownDoc` board cards with clean Google Stitch-like styling, collapsible headings later, source toggle later, and stored source markdown under `.reviewscope/assets/docs/`.
- Code inputs: keep existing C# cards and function extraction, add XAML/WPF cards with syntax-friendly display, and allow markdown files as rendered docs or source cards.
- Diagramming tools: add toolbar actions for pointer, pan, note, text, shape, image, connector, container/layer, frame all, and background controls.
- Review stencils: include service, API, database, queue, cache, UI, file, class, method, dependency, risk, TODO, bug, and test symbols.
- Connectors: persist route type, labels, arrow mode, stroke color, and dashed/solid style. Full orthogonal routing is a follow-up.
- Layers and containers: use visual architecture containers for grouping, plus logical layers for architecture, code evidence, notes, risks, screenshots, and background.
- Editing basics: implement undo/redo, copy/paste/duplicate, delete, z-order, lock/unlock, snap-to-grid, smart alignment guides, align, distribute, and multi-select over time.
- Images and screenshots: store image cards under `.reviewscope/assets/images/`; support import/paste, resize, and later freehand/highlight annotations.
- Search and board details: search explorer files and board items; show which files/functions are used in the current board.
- LLM review export: provide preview/copy plus save-to-markdown. Include global notes, attached notes grouped by file/function, board/session name, architecture docs, diagram elements, and selected code excerpts.

## Implementation Notes

- Refactor persistence and domain types first because portable assets and richer board items depend on them.
- Keep the Direct2D canvas, but dispatch rendering by `BlockKind`; existing file/extract/note renderers become specialized board item renderers.
- Use one scene-change path for board mutations so undo/redo, autosave, board details, and exports stay consistent.
- Treat connectors as first-class persisted objects with source/target item keys, ports, route type, style, label, and arrow mode.
- Keep review exports deterministic and readable markdown. JSON remains internal project/session storage.

## Test Plan

- Open an existing workspace, migrate current sessions, restart the app, and confirm sessions reload from `.reviewscope/`.
- Create sessions, switch between them, add architecture markdown, close/reopen, and confirm rendered state/source markdown persists.
- Add C#, XAML, markdown, notes, shapes, images, containers, and connectors; verify selection, move, resize, copy/paste, duplicate, undo/redo, and autosave.
- Attach notes to files/functions/shapes and verify LLM export groups context correctly.
- Test search across explorer files, board items, notes, docs, and function cards.
- Export review markdown, copy from preview, and save to `.reviewscope/exports/`.
- Verify large boards still pan/zoom smoothly and minimap/details stay accurate.

## Assumptions

- First milestone prioritizes the code-review workflow over a general-purpose draw.io clone.
- First-class file types are C#, XAML/WPF, and Markdown.
- The first stencil library is review/architecture focused, not exhaustive.
- Architecture docs are rendered board cards with source toggle later, not a full markdown editor.
- LLM export provides both preview/copy and file save.
- Collaboration, cloud storage, and realtime cursors are intentionally excluded.

## Implemented Foundation Slice

This repo now includes the first foundation pass:

- Portable `.reviewscope/` session/project storage and migration from AppData.
- Expanded domain model for richer board items, styles, source bindings, layers, and connector metadata.
- C#, XAML, and Markdown file discovery.
- Toolbar commands for markdown docs, image cards, notes, text, review stencils, containers, undo/redo, copy/paste/duplicate, search, and LLM export.
- Basic Direct2D rendering for markdown cards, review shapes, text cards, image placeholders, and containers.
- Board search, file usage details, and LLM export preview/copy/save.

Follow-up work should focus on true bitmap rendering, screenshot ink annotations, editable shape/style panel, orthogonal connector routing, snap/alignment guides, and richer markdown source/preview controls.
