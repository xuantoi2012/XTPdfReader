# XTMerge architecture

## Ownership

- `PdfWorkspace` owns source identities, workspace documents and command history.
- `SourcePage` identifies a page in an immutable source PDF.
- `PagePlacement` represents one occurrence of a source page in an output document.
- `WorkspaceDocument` owns only an ordered collection of placements.
- `MainWindow` owns visual concerns: selection controls, viewport, drag adorners and viewer chrome.

## Windows

- `ReaderShellWindow` is the application's main window. It hosts `ReaderWindow`: a single-row ribbon,
  open-file tabs, the left panel (`Controls/ReaderSidePanel`: page thumbnails, bookmarks, layers) and
  the page view.
- `MainWindow` is the "merge multiple files" window. It still owns the `PdfWorkspace`, undo history and
  the thumbnail cache, so it is created at startup but stays hidden until the ribbon button opens it.
  Closing it only hides it; it closes for real when the reader window closes. Both windows show the
  same open documents because they share one workspace.

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

1. Move thumbnail/preview cache ownership out of `MainWindow`.
2. Add bounded LRU eviction without violating native document leases.
3. Capture selection and viewport state with undo transactions.
4. Add versioned `.xtmerge` workspace persistence and source-file fingerprint validation.
5. Add PDF regression fixtures for OCG, outlines, links, mixed page sizes and rotations.
