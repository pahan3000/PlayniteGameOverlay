# Building Playnite Overlay 3000

## Requirements (Windows only)

- Visual Studio 2022 (or Build Tools) with the **.NET desktop development** workload
- The **.NET Framework 4.6.2 targeting pack**
- [Playnite](https://playnite.link) installed, so `Toolbox.exe` exists at `%LOCALAPPDATA%\Playnite\Toolbox.exe` (used to create the `.pext`)
- Internet access on the first build (NuGet packages are downloaded)

## Build the installer

Run `build-overlay.bat` (or `pack.ps1` in PowerShell) from the project folder. It will:

1. Restore the NuGet packages from `packages.config` into `packages\`
2. Build `PlayniteGameOverlay.csproj` in Release
3. Copy the DLLs from `BundledRuntime\` into the output
4. Pack everything into `release\*.pext` and check the result

Drag the `.pext` onto Playnite and restart Playnite to install it.

## Build in Visual Studio instead

Open `PlayniteGameOverlay.csproj` (or the `.sln`), let Visual Studio restore the NuGet packages, then build **Release**.
