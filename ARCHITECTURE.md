# XTMerge architecture

## Ownership

- `PdfWorkspace` owns source identities, workspace documents and command history.
- `SourcePage` identifies a page in an immutable source PDF.
- `PagePlacement` represents one occurrence of a source page in an output document.
- `WorkspaceDocument` owns only an ordered collection of placements.
- `ReaderWindow` / `MergeWorkspaceWindow` own visual concerns: selection controls, viewport, drag adorners and viewer chrome.

## Windows and ownership

- `ReaderWindow` (an `XTWindow`) is the entry point: `App.OnStartup` creates it as
  `Application.MainWindow`. It contains a single-row ribbon, the open-file tabs, the left panel
  (`Controls/ReaderSidePanel`: page thumbnails, bookmarks, layers) and the page view.
- `ReaderWindow` owns the `DocumentSession`. The session holds the open documents (`PdfWorkspace` and
  undo history), opens files, and implements every page, annotation and layer edit the ribbon invokes
  (`IReaderPageEditHost`).
- `MergeWorkspaceWindow` is the secondary "merge multiple files" window, which drags pages between
  files, merges and splits them, and saves each group. Only `ReaderWindow.OpenMergeWindow()` creates it,
  passing in the session. Closing it really closes it: it unsubscribes from the session and nothing
  else is affected.
- The thumbnail cache and render queue live in `Services/ThumbnailCache`, shared by both windows.

## Layers (optional content)

PDFium has no public API for toggling layers, but it always renders using the default state in
`/OCProperties/D`. `PdfLayerStateStore` keeps the set of hidden layers for each open file. When that set
differs from the file's default, `PdfThumbnailService.LoadDocumentLease()` opens the document with
`FPDF_LoadCustomDocument`. The document it reads is the original file on disk plus an in-memory
incremental update (`PdfLayerService.BuildVisibilityTail`) that rewrites `/D/ON` and `/D/OFF`
(`LayeredDocumentSource`). The file on disk is never modified.
Toggling a layer retires the file's current lease. The thumbnail, reader-page and tile caches all
include the layer-state token in their keys (`RenderCacheKeys`), so switching back to a state that was
already rendered hits the cache.

## Mutation rule

Workspace collections must be changed through `IWorkspaceCommand` implementations. Commands are
transactional and must implement the inverse operation in `Undo()`.

Current commands:

- `MovePagesCommand` (move and copy)
- `MergeDocumentsCommand`
- `RemovePagesCommand`
- `RemoveDocumentCommand`
- `ReorderDocumentsCommand`

Opening a source file is intentionally not part of undo history yet.

## Rendering rule

PDF source identity and placement state must never depend on a WPF control. Thumbnail and preview
bitmaps currently remain transitional visual properties on `PagePlacement`; they will move into
dedicated LRU caches keyed by source page, render bucket, rotation and source revision.

Split layout uses the maintained MIT `VirtualizingWrapPanel` package. Do not fork it into XTStyle:
keeping the upstream package isolated makes fixes and upgrades reviewable. PDFium calls are globally
serialized because the native library is not thread-safe. Cached document handles additionally use
acquisition leases: cache eviction requests disposal, but the handle is closed only after every active
page-count/render operation releases its lease.

## Next milestones

1. ~~Move thumbnail/preview cache ownership out of `MainWindow`.~~ (`Services/ThumbnailCache`)
2. Add bounded LRU eviction without violating native document leases.
3. Capture selection and viewport state with undo transactions.
4. Add versioned `.xtmerge` workspace persistence and source-file fingerprint validation.
5. Add PDF regression fixtures for OCG, outlines, links, mixed page sizes and rotations.
