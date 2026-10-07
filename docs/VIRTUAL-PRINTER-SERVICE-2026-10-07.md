# Print service implementation and validation - 2026-10-07

The owner explicitly authorized service implementation after the phase 0 report. The earlier findings remain valid; this report is component validation, not a pass for the full virtual-printer acceptance matrix.

Implemented: separate Core, Windows Service/console host and user-session Agent projects under VirtualPrinter. IPP PDF/XPS/OXPS reception, per-job naming, durable streaming spool, conversion behind IDocumentConverter using the existing MuPDF runtime, local Windows authentication, SID-based ownership, cancellation, restart recovery and broker-to-agent delivery. The agent sends completed absolute paths using the existing Reader pipe and can start a configured Reader executable in its own session. No production installer, Word add-in, fallback bookmarks, CAD PC3 or XT_PRINT/.xtset changes.

## Validation

Windows 11 build 26200.9550, .NET SDK 10.0.401. Command:

```powershell
& Tests\VirtualPrinter\Run-ServiceTests.ps1
```

| Check | Result | Evidence |
| --- | --- | --- |
| Service and Agent builds | pass | 0 warnings/errors |
| Reader compatibility build | pass | Main project excludes new service sources; 0 warnings/errors |
| Release publish, service and agent | pass | Framework-dependent publish; converter script included |
| Protocol/lifecycle/transport checks | pass | 32 assertions |
| Local HTTP authentication | pass | Anonymous request gets 401; current Windows user can submit |
| Three simultaneous jobs / two copies | pass | 6 distinct complete PDF paths delivered through actual broker to recording sink |
| Word PDF preservation | pass | All 6 files: 120 pages, 108 identical heading bookmarks, exact normalized text, embedded fonts, no images |
| XPS conversion | pass | WPF-generated fixture; searchable Vietnamese text, embedded font, 1 vector line, 0 images |
| OXPS conversion | pass for synthetic fixture | Namespace/content-type adjusted XPS fixture; searchable text, embedded font, vector line, 0 images; no Windows OXPS stream was captured |
| Cancel / upload limits / SID isolation | pass at component level | Held cancellation, converter token signalling, oversize/partial failure, cross-SID queries and leases |
| Restart | pass at durable-store level | Cancellation persists; interrupted conversion requeues from complete input |
| Delivery retry | pass at broker level | Broken lease can retry; acknowledged jobs do not resend |
| Reader sender | pass against temporary pipe | Verifies server SID/session; writes absolute UTF-8 path |
| Measured processing interval | observation | 1.1260158 seconds for three 120-page PDFs, from submission through broker/recording sink |
| Service private memory | observation | 37,261,312 bytes (35.54 MiB), one snapshot; not a peak and excludes converters |

Retained evidence: Tests/VirtualPrinter/Evidence/2026-10-07/Service/results.json and pdf-verification.json. Original generated run: Tests/bin/virtual-printer/service/348b629cff7a462e86f0857d177774e9 (ignored).

## Not run / limitations

- SCM registration, LocalService account execution, real Windows IPP queue and real Ctrl+P from apps: **not run**; current process is not elevated. Windows spooler authentication and vector format negotiation remain to be measured.
- Live Reader shelf import / auto-start: **not run**; no app window was opened. Recording sink and a temporary pipe validate transport only.
- Windows 10, true two-user interactive sessions, LAN connection attempts, 300-page performance/peak memory, service killed during a real print, installer twice/uninstall: **not run**.
- Word add-in and range/copy preservation from DocumentBeforePrint, full W1 sample, heuristic precision/recall and CAD PC3 comparison: **not run**.
- Unsupported range/color/duplex/N-up options return an error. PWG raster/PCLm are refused. The implementation is not IPP/Mopria-certified.
- Existing Reader pipe is one-way: an agent receipt records a successful pipe write, not confirmed shelf import. Exactly-once import and Reader-crash recovery need an agreed acknowledgement/deduplication extension. Files remain available on disk; duplicate/retry windows are documented in VirtualPrinter/README.md.
- Spool/agent file retention is currently manual; no automatic disk cleanup policy is implemented.

The implementation uses two new Microsoft MIT packages and reuses the existing AGPL MuPDF runtime behind an interface. No new restrictive dependency, desktop automation or product-installer behavior was introduced.
