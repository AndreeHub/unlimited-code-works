# ReviewScope Logseq-Style Outliner Spec

## Summary

Add a native, high-fidelity, blazingly fast Logseq-style outliner to ReviewScope so the
reviewer can write up review notes/issues **in-app** instead of drafting in Logseq and
manually re-creating them on the canvas. Three end goals:

1. **A dedicated fast editing view** for outline writing.
2. **The writeup stored inside the project** (no separate second-brain folder).
3. **Transclusion** — referencing a specific bullet from the writeup onto the canvas.

This grows into a broader change: the app becomes a **multi-document project** navigated
from a **Project Browser**, where the existing Direct2D canvas is one document type
alongside outline **Pages** and **Journals**.

Out of scope (deliberately NOT copying from Logseq): multi-file/per-page storage, a
DataScript database, `parent`+`left` sibling pointers, real-time collaboration, cloud sync.

## Existing foundation (verified, do not rebuild)

- `src/ReviewScope.Canvas/OutlineDocument.cs` — a working outliner ENGINE: bullet parse,
  indent/outdent, subtree move, collapse, TODO/DONE, inline markdown (`**`/`*`/`~~`/`` ` ``),
  `#tags`, `[[wikilinks]]` with autocomplete, and **persistent per-bullet anchor IDs**
  (`^xxxxxxxx`) already used for bullet→block connection lines. Layout/draw/hit-test are
  pure static methods and are reusable as-is.
- The keyboard/caret EDIT state machine is NOT extracted — it lives as fields on
  `CanvasViewport` (`_editBody`/`_editCursorPos`/`_editSelectionAnchor`) driven by
  `HandleEditModeKey`/`HandleChar`/`InsertOutlineContinuation`/`IndentCurrentOutlineSubtree`/
  `TryHandleOutlineBackspace` in `src/ReviewScope.Canvas/CanvasViewport.Input.cs`.
- `CanvasViewport : HwndHost` — Win32 child window, own `WndProc`, own `ID2D1Factory` +
  `ID2D1HwndRenderTarget`; `IDWriteFactory` is created **`Shared`** (shareable across views).
  Immediate-mode rendering (paints on input/resize only), GPU-accelerated, viewport-culled.
- **The project already contains multiple named sessions** (review boards / canvases):
  `MainWindowViewModel.Sessions` (partial VM) + `Persistence/SessionRepository.cs`, stored as
  `.reviewscope/sessions/*.json` with atomic writes. The Project Browser is an *evolution of
  this session list*, not a brand-new concept.
- Persistence root: per-workspace `.reviewscope/` (`sessions/`, `assets/docs`, `project.json`).
  Block bodies stored inline in `BlockPlacement.Body` (`Domain/Models.cs`). Tag/wikilink vocab
  in `TagIndexStore.cs`. Connections carry `SourceLineId`/`TargetLineId` pointing at bullet
  `^anchorId`s. Atomic write pattern in `SessionRepository` (note the
  `UnauthorizedAccessException` fix, commit `6a33e3a`).

## Key decisions

### Internal model: block-object TREE, file authoritative

- Each bullet is a `Block` object: `{ id, content, children[], collapsed, type?, properties? }`.
  A document is an in-memory **tree** of these. This is what lets a bullet later carry a code
  block, a typed reference, or properties.
- Use an **ordered `children` list**, NOT Logseq's `parent`+`left` pointers. `parent`+`left`
  and a database-as-truth only earn their keep for multi-file partial sync / collaboration,
  which this app does not have. A whole-document load/save makes ordered children correct and
  far simpler.
- The **saved file stays the source of truth**: load → build tree → edit tree → serialize tree
  back. No second source of truth, no per-edit graph bookkeeping (preserves "blazing speed").
- Editing operations (Enter/Tab/Backspace/move) become clean **tree operations** instead of
  string-splicing — which also naturally fixes the fidelity bugs listed below.

### Identity

- The existing `^xxxxxxxx` **anchor ID becomes `Block.id`** (the equivalent of Logseq `id::`),
  allocated lazily the first time a bullet is referenced.
- The `StableId` (FNV hash of `level|index|text` in `OutlineDocument.cs`) is **not** stable
  across edits — demote it to a transient render key only; never persist it as identity.

### Storage: one project file (for now)

- Everything saves into the single project file: a **document registry** plus each document's
  data — canvases as today's `BlockPlacement`s, pages/journals as serialized block-trees.
- A standalone `notes.md` was considered and dropped. Journals accumulate (~365/yr) but stay
  small in JSON; the registry abstraction lets us split to per-file storage later without
  breaking callers.

### Product shape: multi-document project + Project Browser

- A **project** is a collection of documents shown in a left **Project Browser**; the main area
  shows the editor for the selected document.

  | Document type | Editor              | Notes                                            |
  |---------------|---------------------|--------------------------------------------------|
  | **Canvas**    | existing CanvasViewport | the Direct2D board; now one item type        |
  | **Page**      | new OutlineView     | named outline/block-tree document                |
  | **Journal**   | new OutlineView     | date-named, auto-created daily (Logseq-style)    |

- Cross-links span all documents: `[[Page]]` / `[[2026-05-29]]` navigate between documents;
  `((^anchor))` transcludes a single bullet from a page onto a canvas.

### What we copy vs reject from Logseq

- **Copy:** block model (bullets as objects), stable per-block IDs, `[[ ]]` page links,
  `#tags`, `((id))` block refs, transclusion, collapse, TODO/DONE, journals.
- **Reject:** multi-file/per-page storage, DataScript, `parent`+`left` pointers, page-as-block
  entity, document-mode Enter. **Defer:** editable `{{embed}}` portals, `key:: value`
  properties, linked/unlinked-reference panels, queries, namespaces.

## Phases

Phases 0 and 1 are strictly first (everything depends on them). The single-page editor
(Phase 2) is intentionally built **before** the Project Browser shell (Phase 3) to reach a
usable "neat typing" tool early and de-risk the big UX restructure — even though multi-page is
committed.

- **Phase 0 — Identity unification** *(low risk)*
  - Make the `^anchor` ID the canonical `Block.id`; keep the lazy allocation in
    `OutlineDocument.GetOrAllocateBulletAnchorId`.
  - Migrate collapse-state persistence (`BoardItemStyle.OutlineCollapsedItems`, parsed by
    `OutlineDocument.ParseCollapsedSet`) off `StableId` onto the anchor ID. Stale collapse sets
    may drop on migration — cosmetic only.

- **Phase 1 — Block-tree model + serialization** *(foundational, medium-HIGH risk)*
  - Introduce a `Block` tree as the in-memory model for outline documents.
  - Parse markdown → tree and serialize tree → markdown (round-trippable, anchors preserved).
  - Keep projecting the tree to the existing flat line layout so `OutlineDocument` draw/hit-test,
    connections, and autocomplete keep working unchanged.
  - Rebuild edit operations as tree ops and fix the editing-fidelity gaps:
    - Enter at end of a block with **expanded children** → insert as first child (currently
      inserts a sibling).
    - Enter on a **collapsed** block → sibling after the whole subtree.
    - **Backspace at start** of a block → merge into the previous block (currently only outdents).
    - **Tab** with no previous sibling → no-op (currently indents anyway).
    - Enter mid-text → split keeping text after the caret (already correct; preserve).
  - Suggested edit surface: `OutlineEditController { Body/tree; CursorPos; SelectionAnchor;
    HandleKey(); HandleChar(); SetCaretFromHitTest(); event Changed; }`. Title/group editing,
    autocomplete, and `RebuildSnapshot`/`RenderNative` stay on the host (injected via callbacks).

- **Phase 2 — `OutlineView : HwndHost`, single page** ⇒ **Piece 1 (fast editing view)**
  - New `HwndHost`: a stripped `CanvasViewport` — no camera/zoom/pan, no connections, no tools.
  - One document laid out top-to-bottom + vertical scroll. Reuse `OutlineDocument` draw/hit-test
    and the Phase-1 controller. Own `ID2D1Factory`/render target; **share the `Shared`
    `IDWriteFactory`**.
  - Add **skip-rows-above-scroll** culling (`Draw` already stops at the viewport bottom; add a
    symmetric top-skip so huge documents stay O(visible)). Caret-follows-scroll.

- **Phase 3 — Project model + Project Browser + multi-document shell**
  - Generalize the existing session list into a **document registry** (Canvas / Page / Journal),
    each with id, name, type, data.
  - Left **Project Browser** panel; main-area view switching between `CanvasViewport` and
    `OutlineView` based on the selected document. Move existing canvases in as `Canvas` items.
  - **Journals**: date-named pages, auto-created for "today".

- **Phase 4 — Whole-project persistence** ⇒ **Piece 2 (writeup stored in project)**
  - Serialize registry + block-trees + canvases into the single project file; atomic writes via
    the `SessionRepository` pattern. Re-parse trees on load; anchors survive.

- **Phase 5 — Cross-document references** ⇒ **Piece 3 (transclusion)**
  - Derived index (rebuilt on parse): `pageName → bullets`, `anchorId → (document, location)`,
    built on the existing `OutlineDocument.ScanTagsAndRefs` plus a new `((anchorId))` block-ref
    parse (add `InlineKind.BlockRef` to `ParseInlineSpans`).
  - `[[ ]]` resolves to pages/journals (navigation + autocomplete across documents).
  - `((^anchor))` transclusion: render a referenced bullet/subtree onto a canvas, read-only/live
    (re-renders when the source changes), with a recursion guard.

- **Phase 6+ (later):** rich bullet `type`s (code blocks, etc.), `key:: value` properties,
  linked/unlinked-reference panels, queries.

## Implementation notes

- Extract behavior-preserving FIRST in Phase 1 (tree model feeding the existing renderer, green
  build, manual smoke test on the canvas), THEN apply the fidelity fixes as separate commits so
  regressions are bisectable.
- Two D2D `HwndRenderTarget`s coexist fine; the shared `IDWriteFactory` is the only thing that
  must be shared. Keep immediate-mode repaint (input/scroll/resize only).
- Keep the markdown serialization the single canonical text form (anchors as `^id`, collapse as
  today's mechanism or a future `collapsed::` line — decide in Phase 1/4).
- Build: `dotnet build`. Run: `dotnet run --project src/ReviewScope.App/ReviewScope.App.csproj`.
- Work directly on `master` in the main project folder (no worktrees).

## Test plan

- Phase 1: parse a markdown outline → tree → serialize, assert byte-stable round-trip (incl.
  anchors). Enter/Tab/Backspace/move on the tree match the fidelity rules above. Existing canvas
  outline blocks still render/edit identically.
- Phase 2: type a large (10k-line) outline in `OutlineView`; scrolling stays smooth, caret
  follows scroll, collapse/TODO/inline-markdown all work.
- Phase 3: create Page/Journal/Canvas documents, switch between them in the browser, confirm the
  correct editor loads; today's canvases appear as items.
- Phase 4: create documents, close/reopen the app, confirm all documents + trees + canvases
  reload from the single project file.
- Phase 5: `[[Page]]` navigates; autocomplete lists documents; `((^anchor))` renders the source
  bullet on a canvas and updates when the source changes; recursion is guarded.

## Assumptions / open questions

- **Canvas becomes a Project Browser item** (main view switches between canvas and outline).
  If the canvas should instead stay primary with pages in a side-panel, Phase 3's shell design
  changes — confirm before starting Phase 3.
- One project file is acceptable for now even as journals accumulate; revisit if it grows large.
