# Windows 11 Context Menu

This folder registers a Windows 11 modern context-menu entry for `.pdf` files:

`Gộp bằng XT PDF Merge`

It uses a sparse MSIX identity package with a file type association verb. This is different from old registry verbs under `HKCU\Software\Classes\...\shell`, which Windows 11 often moves under **Show more options**.

Install for the current user:

```powershell
.\Packaging\Win11ContextMenu\Install-Win11ContextMenu.ps1 -RestartExplorer
```

Uninstall:

```powershell
.\Packaging\Win11ContextMenu\Uninstall-Win11ContextMenu.ps1 -RestartExplorer
```

Notes:

- The install script publishes the WPF app to `bin\win11-context-menu-publish`, builds and signs `XTPdfMergeApp.ContextMenu.msix`, trusts the local dev certificate for the current user, then registers the package with `Add-AppxPackage -ExternalLocation`.
- The app already accepts multiple PDF paths from Explorer and forwards them into the existing single-instance window.
- For production, replace the self-signed cert with a trusted code-signing certificate and call the same `Add-AppxPackage -ExternalLocation` step from the installer.
