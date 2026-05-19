# Layered Color Groups Spec

## Summary

Add Dynamo/Revit-style color groups to ReviewScope: a reviewer can select several board items, wrap them in one large colored group block, edit the group title, and move items normally inside the group. The group acts as visual organization and lightweight spatial containment, not as a hard layout system.

This feature replaces the vague current "layer" mental model with board-level colored regions that make architecture areas, review scopes, risks, or feature slices obvious at a glance.

## User Workflow

1. Select one or more board items.
2. Run **Group Into Layer**.
3. ReviewScope creates a large colored group behind the selected items.
4. The group gets a default title such as `Layer 1`, `Review area`, or the user-entered name.
5. The user can drag items inside the colored group without moving the group.
6. The user can drag the group title/header or border to move the whole group and all contained items.
7. The user can double-click the group title to rename it.
8. The user can resize the group block to fit more or less content.

## Board Object

Represent the group as a first-class board item:

- `BlockKind.Container`
- `ShapeType = "color-group"`
- Large colored fill with low opacity
- Stronger colored border
- Editable title
- Optional subtitle/count later
- Lower z-index than normal board items
- Lockable like other board items

The existing `RenderBlock` / `BlockPlacement` model can support this with minimal changes. If containment needs to become explicit later, add:

- `ParentContainerKey` on `BlockPlacement`
- or `ContainedItemKeys` on the container source/style metadata

For the first pass, containment can be spatial: items whose bounds are inside/intersecting the group are treated as members when moving the group.

## Visual Design

Color groups should look like Dynamo groups:

- Transparent pastel fill, about 12-22% opacity
- Border in the same hue, about 1.5-2px
- Header/title text in the top-left
- Header area should be easy to grab
- Rounded radius should stay small, around 6-8px
- Group should render behind files, notes, shapes, images, and connectors
- Selected group uses a blue selection outline in addition to its own border

Default colors:

- Architecture: soft blue
- UI: soft green
- Data: soft amber
- Risk: soft red
- Notes/review: soft violet

## Interaction Rules

### Creating A Group

Command: **Group Into Layer**

Input:

- Current selected blocks
- Ignore selected connectors for now
- If no blocks are selected, create an empty group at the canvas center

Behavior:

- Compute bounding box around selected blocks
- Add padding: 48px left/right, 64px top, 48px bottom
- Create a `Container` block behind the selected blocks
- Assign a default color from a rotating palette
- Select the new group after creation
- Persist immediately
- Add operation to undo stack

### Moving Items Inside A Group

Dragging a normal item inside a group should only move that item.

The group does not auto-move while a child item is dragged.

If the item is dragged outside the group, it is allowed. The group is visual organization, not a hard constraint.

### Moving A Group

Dragging the group header or border moves the group and all blocks currently inside/intersecting it.

Dragging inside the empty body of the group may either:

- Select the group, then drag on second gesture, or
- Immediately move the group if the pointer is not over another item

Recommended first pass: header and border move the group; empty body selects only.

### Resizing A Group

Groups use the existing resize handle behavior.

Resizing the group does not resize contained items.

### Renaming A Group

Double-click title/header:

- Opens inline title edit if available
- Otherwise use inspector title field as first pass

Press Enter or focus loss commits.

Escape cancels.

### Selection

Normal item hit testing should prefer foreground items over groups.

Groups should be selectable when:

- The user clicks the header
- The user clicks the border
- The user clicks empty group body where no foreground item exists
- The user selects it from search/results later

Marquee selection should include normal items. It may include groups only when the marquee fully contains the group, to avoid accidentally selecting giant backgrounds.

## Inspector

When a group is selected, inspector should expose:

- Title
- X, Y, width, height
- Fill color
- Stroke color
- Lock
- Z-order
- Optional: "Fit To Contents"
- Optional: "Ungroup"

## Commands

Required first-pass commands:

- **Group Into Layer**: create colored group around selection
- **Ungroup Layer**: delete the group block only, leaving contained items in place
- **Fit Layer To Contents**: resize group to current spatial members with padding
- **Rename**: use existing title editing path or inspector title field

Nice follow-ups:

- Duplicate group with contained items
- Cycle group color
- Send group to back
- Collapse group to header

## Persistence

Persist groups as normal board blocks:

- `Kind = Container`
- `ShapeType = "color-group"`
- `Title = user title`
- `Style.Fill = group color`
- `Style.Stroke = border color`
- `Style.Opacity = 0.16-0.22`
- `LayerKey = "layer::architecture"` or a neutral layer key

No migration is required if existing containers already load safely.

## Export

LLM export should include color groups under **Diagram Elements**:

- Group title
- Approximate contained items by title/kind
- Optional color/style is not important unless it encodes risk

Example:

```md
### Authentication Boundary
Contains: LoginView.xaml, AuthService, Token cache, Risk: refresh token leak
```

## Edge Cases

- Locked group: cannot move/resize/rename, but items inside can still move unless they are locked.
- Locked item inside group: group movement should not move locked contained items.
- Nested groups: allow visually, but first-pass movement should only move direct spatial contents and avoid special nesting rules.
- Connectors: when a group moves with contained blocks, connectors update naturally because endpoints are tied to moved blocks.
- Huge groups: hit testing must keep foreground items easy to select.

## First Implementation Slice

1. Add **Group Into Layer**, **Ungroup Layer**, and **Fit Layer To Contents** commands.
2. Render `Container` with `ShapeType = "color-group"` using Dynamo-style translucent fill and header.
3. Update hit testing so foreground items win over group bodies.
4. Add group drag behavior from header/border that moves spatially contained unlocked items.
5. Reuse inspector fields for title and color edits.
6. Persist through existing session save/load.

