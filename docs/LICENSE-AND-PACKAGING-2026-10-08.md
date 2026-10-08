# License and packaging work, 2026-10-08

## Decisions (owner)
Account per product (own Supabase project for the Reader), 15-day trial then the app locks, 2 PCs per account, virtual printer unlicensed. No commercial AGPL licences yet: nothing is sold, so the AGPL question (`THIRD-PARTY-LICENSES.md`) stays open until the first sale.

## Done
- **Licence client** `Licensing/`: signed tokens (ECDSA P-256, public key in `LicenseConfig.cs`), DPAPI-protected store in `%LOCALAPPDATA%\PDFReaderPro\license.dat`, 7-day offline grace, clock-rollback check, daily renewal, `LicenseManager` as the single entry point. `Controls/LicenseWindow.cs` = sign in / create account / devices / sign out; `App.xaml.cs` gates start-up (`PassLicenseGate`) and re-checks every 30 minutes; About shows the state. 20 checks: `XTPdfMergeApp.PerformanceTests.exe --license-check`.
- **Server** `Licensing/Server/`: `schema.sql`, Edge Function `functions/license/index.ts`, `README.md` (setup, how to sell a year with one SQL line).
- Licensing is OFF while `SupabaseUrl`/`AnonKey` in `LicenseConfig.cs` are empty; `Build-Release.ps1` refuses to pack in that state unless `-AllowUnlicensed`.
- **Size**: `Packaging/Prepare-MuPdfRuntime.ps1` now prunes the embedded Python (99 MB -> 60 MB; OCR checked on the pruned copy). The three worker scripts are compiled to bytecode (same file names) by `Packaging/Release/Protect-Scripts.ps1` during `Build-Release.ps1`.

## Not done / next
1. Owner: create the Supabase project and fill `LicenseConfig.cs` (steps in `Licensing/Server/README.md`). The private key is at `C:\Users\condu\.xt-license\reader-private.pem` (outside the repo; back it up).
2. End-to-end test against the real project (sign up, trial, 3rd PC refused, renewal through `paid_until`). Only the token/state/store logic is tested so far; the HTTP client, the window and the Edge Function have not run.
3. (Done: `bin\MuPdfRuntime` is the pruned 59 MB runtime; the full one is kept as `bin\MuPdfRuntime.full`.)
4. Obfuscate the .NET assemblies (commercial tool) as the last release step; license check also worth a native or second check point.
5. Native worker instead of Python: postponed (see PROGRESS-2026-10-08.md).
6. The release `bin\MuPdfRuntime` is not covered by obfuscation: Python library source stays readable (AGPL anyway).

## Installer (replaces Velopack), 2026-10-08
- **`Setup/`** = own WPF installer (`PdfReaderSetup.csproj`, framework-dependent single file, `requireAdministrator`). `Build-Release.ps1 -Version x.y.z` publishes Reader + XT Capture, compiles the workers to bytecode, zips everything and appends the zip to the installer exe (`Setup/Payload.cs`: `[exe][zip][len int64]["XTPDFRSETUPV1ZIP"]`). Output: `Packaging/Release/_release/PDFReaderPro-Setup.exe` (~34 MB).
- Installs to `C:\Program Files\PDF Reader Pro`: closes the running app, replaces the folder, writes `Uninstall.exe` (the installer stub without the payload), HKLM Uninstall entry, Desktop + Start Menu shortcuts (all users). The screen is the owner's splash design (`AmbientBackdrop`, green bar); the bar shows `min(real progress, elapsed / 15 s)`, so a quick copy still takes ~15 s (`--seconds N` changes it; uninstall 8 s).
- Switches: `--silent`, `--launch` (start the Reader afterwards, through explorer so it is not elevated), `--uninstall`, `--sandbox DIR` (test run in DIR + HKCU, nothing touches the machine; with env `__COMPAT_LAYER=RunAsInvoker` it needs no UAC).
- **Updates**: `AppUpdateService` asks GitHub `xuantoi2012/PDFReaderPro-Releases` for the latest release (tag `vX.Y.Z`, asset `PDFReaderPro-Setup.exe`), downloads it to `%TEMP%\PDFReaderPro-Update`, and `UpdateReadyWindow` offers "Cập nhật ngay" (starts the setup with UAC, the app exits, the setup reopens it). Only an installed copy (under Program Files with `Uninstall.exe`) checks.
- Tested: sandbox install (UI + silent), shortcuts, registry, installed Reader starts, uninstall removes everything. NOT tested: a real install into Program Files with UAC, the update download against a real GitHub release, a machine without .NET 10 Desktop Runtime (the installer needs it too).
- Open: the downloaded setup is not signed or hash-checked (add a code-signing certificate, or publish a SHA-256 beside the asset, before shipping to customers); Authenticode signing would break the appended payload unless the stub is signed first and the payload kept outside the signed part.
