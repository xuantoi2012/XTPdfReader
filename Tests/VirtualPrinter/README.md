# Virtual printer phase 0 probes

Disposable probes only: no service, add-in, converter or product installer. Requirements: installed Word/Office 15 PIAs, .NET 10, existing bundled Python/python-docx and Reader MuPDF runtime. No new dependencies. COM controls only a new hidden Word instance and closes it; no mouse/keyboard automation.

Run from the repository root:

```powershell
$probeOut = Join-Path $PWD 'Tests\bin\virtual-printer\phase0'
$probePython = 'C:\Users\Admin\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe'
& $probePython Tests\VirtualPrinter\Generate-WordSpike.py $probeOut
& Tests\VirtualPrinter\Export-WordSpike.ps1 -OutputDirectory $probeOut
dotnet run --project Tests\VirtualPrinter\WordProbe -- word-events $probeOut (Join-Path $probeOut 'word-120-structural.docx')
dotnet run --project Tests\VirtualPrinter\WordProbe -- cad-metadata $probeOut
& .\bin\MuPdfRuntime\python.exe Tests\VirtualPrinter\Inspect-WordSpike.py $probeOut
```

`word-events` issues two PrintOut calls with different page/copy settings and cancels both. The JSON `request` label is instrumentation supplied by the caller, NOT a Word event parameter. `cad-metadata` reads assembly metadata without loading AutoCAD or changing SECURELOAD.

The generated 120-page structural fixture is NOT full acceptance W1: TOC, images, spanning tables, hyperlink and landscape section are absent. Full tests remain not run.

IPP capture, in one terminal:

```powershell
& $probePython Tests\VirtualPrinter\IppCapture.py --output (Join-Path $probeOut 'ipp')
```

In an elevated test session, after confirming the queue name is unused:

```powershell
Add-Printer -Name 'XT Phase0 Probe' -IppURL 'http://127.0.0.1:18631/ipp/print' -ErrorAction Stop
```

Capture actual app PrintOut/PrintTo streams before drawing conclusions. PDF/XPS/OXPS are advertised by default; `--raster` adds PWG raster for a separate negotiation experiment. The endpoint is NOT IPP-conformance-tested and does not implement full job management. Negotiation failures might be caused by the probe itself; confirm against a conforming endpoint before rejecting IPP. Use generated documents only. Stop with Ctrl+C and remove only a queue created by that run:

```powershell
Remove-Printer -Name 'XT Phase0 Probe'
```

The current attempt failed with access denied before any IPP request. No service/queue/PC3 was left installed and no file was sent to the live Reader. Generated output is under ignored Tests/bin/virtual-printer/phase0; selected evidence is retained in Evidence/2026-10-07.
