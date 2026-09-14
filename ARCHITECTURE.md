# XTMerge architecture

## Ownership

- `PdfWorkspace` owns source identities, workspace documents and command history.
- `SourcePage` identifies a page in an immutable source PDF.
- `PagePlacement` represents one occurrence of a source page in an output document.
- `WorkspaceDocument` owns only an ordered collection of placements.
- `MainWindow` owns visual concerns: selection controls, viewport, drag adorners and viewer chrome.

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
