# Releasing

How to build the release packages. For working on the code, see [technical-notes.md](technical-notes.md).

## Building

`scripts\build.ps1` builds a release package for each platform into `artifacts\` (not checked in). All platforms can be built on Windows.

```powershell
.\scripts\build.ps1
```

```powershell
.\scripts\build.ps1 -Platform win-x64,osx-arm64 -Version 1.0.1
```

Options: `-Platform` (default: all), `-Version` (default: `<Version>` in the project file), `-BundleId` (the macOS bundle id), `-ReadyToRun` (faster start-up, roughly 30–50% bigger), `-NoInstaller` (Windows zips only).

From Git Bash, `scripts/build.sh` runs `build.ps1` with the default options. Run it from the `scripts` folder, since it calls `build.ps1` by a relative path, and it doesn't pass options through:

```bash
cd scripts && ./build.sh
```

## Packages

| Platform | Package | Notes |
|---|---|---|
| `win-x64` | `QuailImageSearch-<version>-win-x64-setup.exe` and `.zip` | GPU via DirectML |
| `win-arm64` | `QuailImageSearch-<version>-win-arm64-setup.exe` and `.zip` | GPU via DirectML |
| `osx-arm64` | `QuailImageSearch-<version>-macos-arm64.zip` | Apple Silicon; contains `Quail Image Search.app` (Core ML) |
| `linux-x64` | `QuailImageSearch-<version>-linux-x64.tar.gz` | CPU; includes `install-desktop-entry.sh` to add it to the applications menu |

There's no Intel Mac build: ONNX Runtime, which runs the models, no longer ships a library for Intel Macs.

## What the script does

For each platform:
1. A self-contained Release publish, using `src/QuailImageSearch/Properties/PublishProfiles/<platform>.pubxml`. Users don't need .NET installed.
2. `tools/Packager` then:
   - removes debug symbols and import libraries;
   - checks that the model engine, the program, `LICENSE`, `THIRD-PARTY-NOTICES.md`, `licenses/` and the calibration images are present, and that no Debug-only package slipped in;
   - **runs the privacy scan from [PRIVACY.md](../PRIVACY.md):** it fails if a library mentions telemetry or analytics (except known, handled cases), and warns about unfamiliar web addresses;
   - packages it, setting Unix permissions correctly even when built on Windows.
3. On Windows platforms, it builds the installer from the same checked folder.
4. Finally it writes `artifacts\SHA256SUMS.txt`.

## Windows installer

`packaging/windows/QuailImageSearch.iss`, built with [Inno Setup 6](https://jrsoftware.org/isdl.php). If Inno Setup isn't installed, the script warns and builds only the zips. The installer:
- installs for the current user without admin rights, or for all users if chosen;
- adds a Start menu shortcut, an optional desktop shortcut, and an uninstall entry;
- doesn't use the network;
- on uninstall, asks before deleting the app-data folder and keeps it by default. Silent uninstalls (`/VERYSILENT`) keep it unless `/DELETEDATA` is passed.

## Signing

Not set up yet; `-Sign` stops with an explanation.
- Unsigned Windows packages show a SmartScreen "unknown publisher" warning (More info > Run anyway).
- On macOS, open the unsigned app with right-click > Open the first time.
- Signing needs a Windows code-signing certificate, and an Apple Developer ID with notarization (done on a Mac).
