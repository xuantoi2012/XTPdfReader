# Blank Reader on initial native document binding

Reported file: `03. QUYEN 2.2 - TNM, TNT, HKT, CAY XANH, CHIEU SANG, TCTC.pdf`
on mapped network drive P:, 277 pages, 170,261,791 bytes.

The real Reader reproduced an empty view for 100 seconds even though its session
already contained all 277 pages. This was a binding cancellation race, not a
reproduced native render timeout. While the native selected-page size request was
pending, another selection notification entered `ShowReaderContinuous` with an
unbound view. It cancelled `_readerPageCts`, but the same group's pending flag
prevented starting another binding. The cancelled task then returned without
calling `SetDocument`.

Keep the current binding token when a notification concerns the same pending
group. A genuine new binding still cancels old work. Capture the token per binding,
check it after the layout yield, and only clear the pending flag if that token is
still current; an obsolete completion must not clear a replacement binding.
Hiding the Reader also clears the pending binding identity.

Validation uses a real WPF ReaderWindow, its session open flow, and Dispatcher,
with an isolated test print inbox. `--reader-open-check <pdf>` opens the supplied
file read-only, verifies visible images and resumed rendering, visits first,
middle and last pages, saves screenshots, and repeats selection notifications
during initial binding. `--reader-binding-check` runs the same regression on a
generated fixture. Both use the Balance performance profile.

The reported file rendered sharp 4608-pixel images on pages 1, 139 and 277.
The generated binding regression passed 7 checks; the reader memory regression
passed 71 checks. The separate runnable build is
`Tests/bin/NativeOpenFixPublish/XTPdfMergeApp.exe` (with XT Capture).
No source-PDF edits or multi-hour navigation soak were performed.
