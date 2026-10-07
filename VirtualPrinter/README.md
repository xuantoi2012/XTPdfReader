# XT Reader print service

The owner authorized service implementation after reading phase 0 on 2026-10-07. This implements the service component; it does not claim that the unresolved Windows IPP driver, Word event or CAD tests now pass.

## Components

- **Core**: streaming IPP 1.1/2.0 attributes, durable job store, converter interface, session-agent wire protocol.
- **Service**: .NET 10 Windows Service/console host, Kestrel on IPv4 loopback, Windows Negotiate authentication, conversion queue and local authenticated broker.
- **Agent**: user-session process, downloads only its authenticated SID's jobs, starts Reader in that session when configured, sends one completed absolute PDF path per line to `XTPdfMergeApp_IncomingPdfPipe`.

The agent is needed because a LocalService process in session 0 cannot launch an interactive Reader for the printing user. Its protocol is separate from the existing Reader pipe. XT_PRINT, XTPdfReaderIncomingBridge, .xtset and CAD PC3 behavior are unchanged.

## Build and headless test

```powershell
dotnet build VirtualPrinter\Service
dotnet build VirtualPrinter\Agent
& Tests\VirtualPrinter\Run-ServiceTests.ps1
```

Tests start an actual service executable in console mode, use Windows-authenticated HTTP and an impersonating broker, and deliver to a recording sink. A separate temporary pipe tests the real Reader sender's owner/session checks. No Reader window or desktop is driven. Native Word export supplies the 120-page fixture; it is not a Word add-in test.

Run in a normal developer account without installing a service:

```powershell
dotnet run --project VirtualPrinter\Service -- --SpoolRoot=C:\XTReaderTest\Spool --Python=C:\path\to\MuPdfRuntime\python.exe
dotnet run --project VirtualPrinter\Agent -- --reader=C:\path\to\XTPdfMergeApp.exe
```

Choose a new writable spool folder. It is protected to the service account, SYSTEM and Administrators. Configure `--Port=<port>` (default 18632) or `--BrokerPipe=<name>` for isolated development. The printer endpoint is `http://127.0.0.1:18632/ipp/print`. No HTTP.sys URL reservation is needed because Kestrel binds the socket directly. No LAN interface is bound.

## Deployment preparation

```powershell
dotnet publish VirtualPrinter\Service -c Release -r win-x64 --self-contained true -o bin\VirtualPrinterPackage\Service
dotnet publish VirtualPrinter\Agent -c Release -r win-x64 --self-contained true -o bin\VirtualPrinterPackage\Agent
```

Place the **existing** Reader `MuPdfRuntime` beside the published service (or configure Python explicitly). `ConvertDocument.py` is copied by the Core project. Do not install packages into an end-user Python installation.

The eventual installer must create the SCM service as LocalService, grant that account ownership/write access to its dedicated spool folder, register the agent at each user's logon with the installed Reader path, create the printer queue, and remove those registrations on uninstall. **Those installer operations are not implemented or executed in this change.** No admin action or product-installer behavior was changed without approval. Service registration, LocalService execution, printer queue creation and uninstall require the next elevated integration test.

Reader lookup: explicit `--reader` first, then HKCU App Paths/XTPdfMergeApp.exe, then a sibling executable. No broad filesystem search or arbitrary command from an IPP request is used. Supply the explicit installed path until the installer registers it. Agent remains running in the user's session, polling once a second.

## Job behavior

Implemented IPP operations: Print-Job, Validate-Job, Create-Job, Send-Document (last-document=true only), Cancel-Job, Get-Job-Attributes, Get-Jobs and Get-Printer-Attributes. A job id is persisted before receipt, input streams to an exclusive `.part` file, and only a flushed complete input is queued. PDF output is validated and atomically published. Two conversions and four uploads can proceed concurrently. Job names become sanitized filenames plus a job id; duplicate document names cannot overwrite another job. Copies 1..100 produce distinct files in the agent's user-owned folder.

Supported input MIME types: application/pdf, application/oxps, application/vnd.ms-xpsdocument. PDF keeps its text, vectors and outlines; title/author are set from the authenticated job. XPS/OXPS use MuPDF document conversion, not page screenshots. PWG raster, PCLm and unknown formats are rejected. Unsupported page-ranges, grayscale, duplex and N-up options return an IPP error instead of silently producing the wrong document. An application may already paginate/orient its PDF before submission, but driver behavior remains to be measured.

Ownership comes from Windows authentication, never from `requesting-user-name`. Broker ownership comes from pipe impersonation. Users can query/cancel/receive only their SID's jobs. Network logons are denied on the broker, and its native origin check accepts only local pipes. The Reader sender additionally checks server process SID and session before writing a path. Actual Microsoft IPP Class Driver authentication might originate from a spooler account rather than the user: this is **not yet tested**, and the service deliberately rejects SYSTEM/guest/anonymous identities.

Jobs remain pending when no agent is connected. Broken deliveries release their leases; normal acknowledgements persist completion. Interrupted conversions retry from complete input after restart; interrupted uploads abort and discard partial input. A Windows Job Object kills converters when the service terminates. Limits: 256 MiB input/job, 500 unfinished jobs, two converter processes with 256 MiB each / 384 MiB combined, 55-second conversion deadline, 2-minute request deadline. Unsubmitted Create-Job requests expire after one hour. Exceeding conversion memory/time limits aborts the job with a logged error.

The service retains inputs/PDFs and receipts; the agent retains delivered PDFs for Reader ownership. There is no automatic retention cleanup yet: deployment needs a disk-retention policy. Cancel during agent delivery is refused to avoid deleting a file while ownership is transferring.

## Delivery guarantee and open integration work

Broker acknowledgement means the user agent successfully wrote the completed path to the existing Reader pipe. That pipe is one-way and has no import acknowledgement. Therefore **exactly-once shelf import and Reader-crash recovery are not guaranteed**: a crash between pipe write and the local receipt can duplicate a retry; a crash after receipt but before Reader imports can lose the shelf notification. PDFs remain on disk. Solving that end-to-end gap requires an agreed Reader acknowledgement/deduplication extension; this change preserves the mandated existing protocol. The headless recording sink tests transport, not actual shelf import.

Still not run: installed Windows printer/real Ctrl+P formats per app, Win10, SCM/LocalService deployment, 32-bit Office, Word add-in, CAD PC3, actual two-user interactive sessions, W3 performance, cancel/kill during a live print and installer lifecycle. This is an implementation ready for those integration tests, not a certified IPP/Mopria device.

## Dependencies and licences

New .NET packages are Microsoft.Extensions.Hosting.WindowsServices 10.0.12 and Microsoft.AspNetCore.Authentication.Negotiate 10.0.12 (Microsoft, MIT). Windows named-pipe ACL APIs come from the existing .NET framework, without another package. The converter reuses the existing Reader PyMuPDF/MuPDF runtime (AGPL), isolated behind IDocumentConverter. No new restrictive dependency was introduced; the existing internal-use licence risk remains and must be reconsidered before external distribution.

References: [Windows Service hosting](https://learn.microsoft.com/en-us/dotnet/core/extensions/windows-service), [Windows authentication](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/windowsauth?view=aspnetcore-10.0), [IPP guide](https://www.pwg.org/ipp/ippguide.html), [MuPDF document conversion](https://pymupdf.readthedocs.io/en/latest/converting-files.html).
