# Windows 11 Context Menu

This folder registers a Windows 11 modern context-menu entry for `.pdf` files:

`Open with PDF Reader Pro`

It uses a sparse MSIX identity package with a file type association verb. This is different from old registry verbs under `HKCU\Software\Classes\...\shell`, which Windows 11 often moves under **Show more options**.

Install for the current user:

```powershell
.\Packaging\Win11ContextMenu\Install-Win11ContextMenu.ps1 -RestartExplorer
```

For a company rollout, sign with the current user's trusted code-signing certificate instead of creating a development certificate:

```powershell
.\Packaging\Win11ContextMenu\Install-Win11ContextMenu.ps1 -CertificateThumbprint '<certificate thumbprint>'
```

Uninstall:

```powershell
.\Packaging\Win11ContextMenu\Uninstall-Win11ContextMenu.ps1 -RestartExplorer
```

Notes:

- The install script publishes the WPF app to `bin\win11-context-menu-publish`, builds and signs `XTPdfMergeApp.ContextMenu.msix`, then registers the package with `Add-AppxPackage -ExternalLocation`. Without `-CertificateThumbprint`, it creates and trusts a five-year development certificate for the current user only.
- The app accepts one or multiple PDF paths from Explorer and forwards them into the existing single-instance reader window.
- The installer does **not** force PDF Reader Pro to become the Windows default PDF app. Windows requires the user to make that choice in **Settings > Apps > Default apps**; this package makes the reader available as a registered handler and adds the right-click command.
- For production, replace the self-signed cert with a trusted code-signing certificate and call the same `Add-AppxPackage -ExternalLocation` step from the installer.
