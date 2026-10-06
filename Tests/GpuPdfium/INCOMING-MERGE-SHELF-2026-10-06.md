# Incoming print jobs into the existing Merge temp shelf

The Merge window's temporary shelf is the shared receiving area. Incoming PDFs
are not copied into a second inbox and are not opened as permanent Reader tabs.
The app creates a temporary `WorkspaceDocument` whose pages point at the received
PDF, shows its thumbnail in the existing shelf, and keeps it out of **Merge all**
until the user promotes it or drags its pages into a real merge window.

Each incoming shelf card has three actions:

- **View in Reader** opens the source PDF as a normal Reader tab and keeps the
  shelf item available for later merge.
- **Open as its own window** promotes the existing temporary group in Merge.
- **Return** remains available when pages came from an already-open source.

The existing named pipe is the hand-off protocol. A producer writes one absolute
PDF path per line and closes the pipe. The running app routes those paths to the
Merge shelf and brings MergeWindow forward. The startup argument path continues
to open normal Reader tabs, so launching a PDF directly does not unexpectedly
enter a merge draft.

For a local smoke test while the app is running:

```powershell
$pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'XTPdfMergeApp_IncomingPdfPipe', [System.IO.Pipes.PipeDirection]::Out)
$pipe.Connect(3000)
$writer = New-Object System.IO.StreamWriter($pipe)
$writer.AutoFlush = $true
$writer.WriteLine('C:\path\to\plot.pdf')
$writer.Dispose(); $pipe.Dispose()
```

The producer may use a uniquely named temporary output path. After plotting is
complete it sends the path, so the receiving app never reads a partially written
PDF. A later cleanup job can remove the source only after the shelf item is
promoted, merged, or closed; the first prototype deliberately leaves ownership
with the producer to avoid deleting a file still used by a printer.

## AutoCAD quality path

The AutoCAD path should keep the existing `XTBatchPlotService`/PlotEngine route
and point its output at a generated temporary path. The current plot settings are
copied from the active layout and allow the AutoCAD PDF device, media, CTB/STB,
rotation, scale, and window to remain authoritative. When the output callback
reports success, the integration sends that path through the named pipe instead
of opening it or asking the user for a destination.

For an AutoCAD High Quality equivalent, use the installed Autodesk PDF device or
the user's selected PC3. Autodesk's High Quality preset is 2400 dpi vector and
600 dpi raster, and its PC3 contains more than a resolution number: media,
transparency, font and layer options also affect output. The first acceptance test
must compare vector line zoom, thin line weights, CTB/STB colours, SHX/TrueType
text, hatch, transparency, A0/A1 paper geometry, layers, and bookmarks against
the same drawing plotted with Autodesk's High Quality preset.

`XTBatchPlotService` already receives an output path and has completion callbacks;
the missing adapter is a small bridge from `OnCompleted(true, outPdf)` to the
named pipe. It should run after the plot has closed its file and must not call
`OpenPdf`. This keeps quality generation in Autodesk and lets Reader only receive,
preview, arrange, and merge the resulting PDF.

Windows 11's Print Support Virtual Printer is a later option for Word, Excel,
browsers, and other applications. It can receive OXPS or PDF through the modern
print workflow and write a target PDF, but it cannot reproduce AutoCAD's PC3
semantics. Therefore it should feed the same pipe, while AutoCAD continues using
the native plot engine. The app-side receiving prototype is now implemented and
builds in `bin/IncomingPdfPrototype`.
