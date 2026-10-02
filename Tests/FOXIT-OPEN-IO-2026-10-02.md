# Open-time file I/O: lazy local buffer + small-read window, 2026-10-02

## What was measured

Same file (`03. QUYEN 2.2 ...pdf`, 165.7 MiB), same keystrokes (about 80 PageDown, End, Home, 60 s idle,
`SendKeys` in the foreground for both apps). Foxit PhantomPDF 10.0.0.35798 (x86) versus this Reader (Release).

| At open (first 10-28 s) | Foxit | Reader before | Reader after |
|---|---:|---:|---:|
| Bytes read | 6 MB | 176 MB | 69-70 MB |
| Read operations | ~2,900 | ~798,000 | **~3,180** |
| Bytes written (temp file) | - | 166 MB | 42-43 MB |
| Private memory | 84 MB | 216-225 MB | 187-191 MB |

Foxit's own mechanics, from the same capture and from RTTI class names (no code was copied):

- Reads are lazy `ReadFile` ranges (about 2.5 KB average, ~47k operations over the whole scroll), not a mapping of
  the file: its `MEM_MAPPED` stayed constant at 52 MB. Its `CFX_MMapedFile` is a write buffer.
- Its private memory is **not** ~200 MB under a real scroll: 84 MB at open, 643 MB after the scroll, and it did not
  release anything during 60 s idle. The earlier 200 MB figure came from a lighter workflow.
- Reader after idle drops to ~540-560 MB, i.e. already below Foxit's 615 MB.

## Causes found in this Reader

1. `PdfBlockCache` copied the whole file to a temp file in the background (176 MB read, 166 MB written per open).
2. About 800,000 read operations at open, average ~65 bytes: PDFium and iText (`BlockCacheSource.Get` reads one byte
   at a time) each issued one `RandomAccess.Read` system call per small read.

## Changes (`Services/PdfFileBuffer.cs`)

- **Lazy local mode**: fixed local drives no longer start the background copy. Blocks enter the temp file when PDFium
  or iText reads them (plus the existing read-ahead). The temp file is marked sparse so a far jump does not make NTFS
  zero-fill everything before it. `LoadedFraction` reports 1 so the "Loading into memory" text and thumbnail warm-up
  do not wait forever. `XTPDF_LAZY_LOCAL=0` restores the old background copy (network drives always use it).
- **Per-thread 64 KB read window** in `PdfBlockCache.CopyTo` for reads up to 4 KB. The window stays inside one
  already-present block (present blocks never change), so it cannot return unloaded sparse data.

Peak private memory during scroll (~890 MB) and the scroll-time behaviour are unchanged by this work.

## Verification

Release build 0 warnings/errors; `--background-regression` PASS (391 checks). The first launch of a freshly built exe
can be slow (one run sat idle for about a minute); measure from the second launch.

## Not done / next

- Scroll peak (~890 MB versus Foxit 643 MB) and the 187 MB empty baseline versus Foxit 84 MB.
- Render latency was measured afterwards (next section); PageDown distances still differ between the two apps.
- A scripted check that lazy and old modes render identical page hashes was not added.

## Render latency and deep zoom (GUI, same PDF, both apps in the foreground)

`Tests/Measure-GuiLatency.ps1` sends the same keys and Ctrl+wheel to each app and captures the content area
continuously for 2.5 s after every action; the last frame is the finished image. "Ready" is the first moment the
frame equals the finished image (grey tolerance 12, 8 samples). This is on-screen readiness, not GPU FPS; both apps
were foreground, the machine was otherwise idle, one run each, so treat differences under ~30 ms as noise.
`Tests/Measure-GuiProcessIo.ps1` is the process I/O and memory sampler used above. Both take over mouse and keyboard
for minutes and must be closed gracefully (killing Reader makes the next launch show the recovery dialog).

| Median ready, ms | Foxit | Reader |
|---|---:|---:|
| New adjacent page (PageDown; Reader prefetches 4 pages) | 386 | 49 |
| Jump to a far, unseen page (End) | 152 | 168 |
| Return to a seen page | 50 / 118 | 41 / 32 |
| Zoom in, normal range | 36 | 28 |
| Zoom out | 50 | 18 |
| Pan while zoomed | 35 | 18 |
| Deep zoom in, 10 notches | ~350 | ~400 |
| Deep zoom out, 20 notches | 850 | 650-700 |
| **Return to a seen page after deep zoom** | **2,700-3,400** | **29-169** |
| Private memory peak in this run | 952 MB (deep zoom) | 507 MB |

Foxit stopped changing on several deep-zoom-in steps (it reached its own maximum), so those rows are not comparable
zoom-for-zoom. The far-page jump is the only real cold read and the two apps are level there.

## Zoom limit

`MaxZoom` was 400% and the sharp region was capped at a 16,000 px page width (about 1,450% on a 1,100 px page).
Both are raised: `MaxZoom` 32 (3,200%; Reader 100% = ~1,100 px across the paper, so this is roughly Foxit's 2,200%)
and `MaxRegionFullWidth` 65,536. Regions only rasterize the visible viewport, so memory does not grow with zoom.
The zoom slider is now on a square-root scale so it stays usable at low zoom. Checked visually at 1,166%: vector text
edges are crisp. Not checked: 3,200% itself, other page sizes (A0 layouts exceed the 65,536 px cap earlier and then
upscale), and rotated pages at extreme zoom.
