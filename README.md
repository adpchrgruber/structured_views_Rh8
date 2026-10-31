# Structured Views

<img src="docs/panel.png" width="320" align="right" alt="The Structured Views panel in Rhino 8: top-level views, then folders with their thumbnails, a collapsed subfolder and an empty folder">

Rhino's Named Views panel, with folders.

Structured Views is a Rhino 8 plug-in (Mac and Windows). Its panel does everything the Named Views panel
does: save, restore, update, rename, duplicate, delete, properties, copy and paste, import from a .3dm, list
and thumbnail modes, sorting and animation settings. It also lets you sort views into nested folders.

The views stay ordinary Rhino named views. The `NamedView` command, layouts, render queues and colleagues
without the plug-in still see every view. The folders are extra information stored in the .3dm.

## Layout

The panel is a sectioned gallery rather than a tree:

- Top-level views come first, as a thumbnail grid without a heading.
- Every folder is a full-width header: a chevron, the folder name in bold and the number of views inside it.
  Its views flow underneath in the same evenly spaced grid, followed by its subfolders.
- Nesting is shown by indenting the header and its contents and by a thin guide line, so thumbnails stay
  in clean columns instead of stepping sideways with every level.
- List mode uses the same sections with compact rows.

## Using it

Run `StructuredViews` to open the panel.

The toolbar follows Rhino's Named Views panel: **Save**, **New Folder** | **Restore**, **Delete**,
**Copy**, **Paste**, **Import** | **Duplicate**, **Move Up**, **Move Down**, then on the right the
thumbnail/list switch and an options menu (View Mode, Sort By, Animate Restore, Animation Settings,
Refresh Thumbnails, Expand/Collapse All Folders). Below the views, as in Rhino's panel, are
**Auto-update thumbnails** and **Restore aspect ratio**. On a narrow panel the toolbar wraps onto a
second row instead of hiding buttons.

| Action | How |
|---|---|
| Save the current viewport | Camera button. The view goes into the selected folder |
| New folder | Folder button. It is created inside the selected folder |
| Restore | Double-click, Return, the restore button, or right-click › Restore › To Current Viewport / To New Floating Viewport |
| Update to the current viewport | Right-click › Update to Current Viewport |
| Rename | Right-click › Rename…, or F2 |
| Duplicate | Duplicate button, right-click › Duplicate, or ⌘D / Ctrl+D |
| Delete | ✕ button, Delete or Backspace. A deleted folder's contents move up one level |
| Properties (name, camera, target, lens) | Right-click › Properties… |
| Copy and paste, also between open files | Toolbar, right-click, or ⌘C / ⌘V (Ctrl on Windows) |
| Move into a folder | Drag onto a folder header, or right-click › Move to Folder |
| Reorder (Custom Order) | Drag between thumbnails, or Move Up / Move Down. Drag onto the top or bottom edge of a folder header to put a folder before or after it |
| Copy by dragging | Hold Option (Alt) while dragging |
| Collapse or expand a folder | Click its chevron, double-click its header, or use ← / → |
| Select several | ⌘ or Ctrl click, Shift click, ⌘A / Ctrl+A |
| Import from a .3dm | Import button. Views go into a new folder named after the file, and the file's own folders are kept |

## How it works

- **Folders** are stored in the document's string table, section `StructuredViews`:
  `f:<folder id>` = `parent|order|name` and `v:<named view id>` = `folder|order`.
  Views are matched by `ViewInfo.NamedViewId`, which stays the same when a view is renamed, updated or saved.
  A view without an entry (for example one saved with Rhino's own panel) sits at the end of the top level.
- **Thumbnails** are captured by the plug-in, because Rhino doesn't expose the ones from its own panel.
  - A view is captured from the active viewport when you save or update it.
  - Any view without a thumbnail, or with an outdated one after the model changed, is captured on idle.
    The plug-in borrows a viewport for one capture and puts its camera, target, CPlane, title, display mode
    and wallpaper back before anything is redrawn.
  - Thumbnails are saved in the .3dm as plug-in data.

## Not included

These parts of Rhino's Named Views panel are not part of Structured Views:

- the named view widget,
- Edit by Looking,
- dragging a view onto a viewport or into another file's panel (use copy and paste instead).

## Building

```
dotnet build -c Release
```

This builds `bin/Release/net7.0/StructuredViews.rhp` (Mac and Windows) and `net48` (Windows), and
packs `dist/structuredviews-<version>-rh8_0-any.yak`. Icons are generated with `python3 tools/make_icon.py`.

## Licence

MIT, see [LICENSE](LICENSE).
