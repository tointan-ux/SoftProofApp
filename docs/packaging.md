# Packaging SoftProof for Windows

First release target: **win-x64, self-contained folder + Inno Setup 6 installer**.

## Prerequisites

- .NET 8 SDK on the build machine.
- [Inno Setup 6.3+](https://jrsoftware.org/isdl.php) (`ISCC.exe`) for the installer step.
  `x64compatible` requires 6.3; with an older Inno use `x64` instead.
- Windows 10/11 test VM with **no** Visual Studio, .NET or VC++ runtime installed.

## Build

```powershell
# Publish a self-contained, ReadyToRun folder (no .NET runtime required on the target)
installer\publish-win-x64.ps1 -Version 1.0.0

# Compile the installer (also runs publish unless -SkipPublish is given)
installer\build-installer.ps1 -Version 1.0.0

# Installer-only rebuild after a publish
installer\build-installer.ps1 -Version 1.0.0 -SkipPublish

# Framework-dependent build (requires .NET 8 Desktop Runtime on the target)
installer\publish-win-x64.ps1 -Version 1.0.0 -FrameworkDependent
```

Outputs:

- `publish\win-x64\SoftProof.exe` — self-contained app, `lcms2.dll` next to it.
- `dist\SoftProof-Setup-1.0.0.exe` — per-machine installer.

## Decisions

- **Self-contained** by default: production prepress machines should not need a separate
  .NET install.
- **Folder publish, no single-file**: LittleCMS is a loose native DLL; a folder avoids
  first-run extraction to temp and makes the native dependency visible.
- **No trimming**: WPF is not trim-safe.
- **ReadyToRun** enabled to reduce startup cost; measure the size/startup trade-off if needed.
- **Inno Setup** for the first release; per-machine install under `{autopf}\SoftProof`,
  stable `AppId` for clean upgrades, LZMA2 solid compression.
- Build and ship **win-x64 only** for now. A win-arm64 build needs an arm64 `lcms2.dll`.

## Runtime data locations

| Data | Location |
| --- | --- |
| Settings, recent files, press profiles, sign-off log | `%APPDATA%\SoftProof` |
| Pyramid cache, diagnostics log | `%LOCALAPPDATA%\SoftProof` |

The install folder is never written to. `Help ▸ Clear pyramid cache` deletes the preview
cache; it is capped at 512 MB by `PyramidCache` and rebuilds on demand.

## Clean-VM verification

1. Install on a VM with no SDK/runtime installed; launch from the Start menu.
2. Open a large 1-bit TIFF (e.g. `holobox_background_g4.tif` fixture).
3. Check colour management loads (no `lcms2` BadImageFormat/DllNotFound message).
4. Zoom to 1600%, pan all four corners, run preflight, sign-off gate.
5. Confirm settings/cache appear under the user profile, not under `Program Files`.
6. Uninstall: program files removed; `%APPDATA%`/`%LOCALAPPDATA%` left intact.
7. Optional forensic check: `dumpbin /dependents lcms2.dll` on a VS-equipped machine.
   The bundled 2019 `lcms2.dll` contains no `VCRUNTIME140`/`MSVCP140` strings, suggesting a
   static CRT, but verify on the clean VM before shipping.

## Not done yet

- Code signing (Authenticode) and SmartScreen reputation.
- ARM64 build/publish.
- Automatic update feed.
- Inno `LicenseFile` (no EULA chosen yet).
