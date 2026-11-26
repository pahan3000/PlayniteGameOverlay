param([switch]$NoPause)

$exitCode = 0
$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $ScriptDir

$Configuration = "Release"
$ReleaseDir   = Join-Path $ScriptDir "release"
$BuildLog     = Join-Path $ScriptDir "build.log"
$Toolbox      = Join-Path $env:LOCALAPPDATA "Playnite\Toolbox.exe"

function Pause-OnExit {
    Write-Host ""
    Write-Host "Press Enter to close..." -ForegroundColor DarkGray
    [void](Read-Host)
}

function Find-MSBuild {
    $cmd = Get-Command MSBuild.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $vswhereCandidates = @(
        (Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"),
        (Join-Path $env:ProgramFiles "Microsoft Visual Studio\Installer\vswhere.exe")
    )

    foreach ($vswhere in $vswhereCandidates) {
        if ($vswhere -and (Test-Path $vswhere)) {
            $found = & $vswhere `
                -latest `
                -products * `
                -requires Microsoft.Component.MSBuild `
                -find "MSBuild\**\Bin\MSBuild.exe" 2>$null |
                Select-Object -First 1

            if ($found -and (Test-Path $found)) {
                return $found
            }
        }
    }

    $fallbacks = @(
        "$env:ProgramFiles\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe",
        "$env:ProgramFiles\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
        "$env:ProgramFiles\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
    )

    foreach ($p in $fallbacks) {
        if ($p -and (Test-Path $p)) { return $p }
    }

    return $null
}

try {
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host " PlayniteGameOverlay - Build + Pack" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host ""

    if (Test-Path $BuildLog) {
        Remove-Item $BuildLog -Force
    }

    # IMPORTANT:
    # Build the actual plugin project directly.
    # The .sln has a stale reference to:
    #   C:\playnite_extensions\Test overlay\Test overlay.csproj
    # and therefore must not be used for automated builds.
    $Project = Join-Path $ScriptDir "PlayniteGameOverlay.csproj"

    if (-not (Test-Path $Project)) {
        # Fallback if the project folder/name changes.
        $foundProject = Get-ChildItem -Path $ScriptDir -Filter *.csproj -File -Recurse |
            Where-Object {
                $_.FullName -notmatch '\\(bin|obj|packages)\\' -and
                $_.Name -notmatch '^Test overlay\.csproj$'
            } |
            Select-Object -First 1

        if (-not $foundProject) {
            throw "Could not find PlayniteGameOverlay.csproj."
        }

        $Project = $foundProject.FullName
    }

    $MSBuild = Find-MSBuild
    if (-not $MSBuild) {
        throw "MSBuild.exe was not found. Install Visual Studio / Build Tools with .NET desktop build tools."
    }

    Write-Host "Project : $Project"
    Write-Host "MSBuild : $MSBuild"
    Write-Host ""

    Write-Host "==> Restoring NuGet packages..." -ForegroundColor Yellow
    $NuGet = (Get-Command nuget.exe -ErrorAction SilentlyContinue).Source
    if (-not $NuGet) {
        $NuGet = Join-Path $env:TEMP "nuget.exe"
        if (-not (Test-Path $NuGet)) {
            Write-Host "nuget.exe not found, downloading it..."
            Invoke-WebRequest "https://dist.nuget.org/win-x86-commandline/latest/nuget.exe" -OutFile $NuGet
        }
    }
    & $NuGet restore (Join-Path $ScriptDir "packages.config") -PackagesDirectory (Join-Path $ScriptDir "packages") -NonInteractive
    if ($LASTEXITCODE -ne 0) { throw "NuGet restore failed with exit code $LASTEXITCODE." }
    Write-Host ""

    Write-Host "==> Building $Configuration..." -ForegroundColor Yellow

    & $MSBuild $Project `
        /t:Rebuild `
        /p:Configuration=$Configuration `
        /p:Platform=AnyCPU `
        /m `
        /nologo `
        /v:minimal `
        "/fl" `
        "/flp:logfile=$BuildLog;verbosity=diagnostic"

    if ($LASTEXITCODE -ne 0) {
        Write-Host ""
        Write-Host "BUILD FAILED" -ForegroundColor Red

        if (Test-Path $BuildLog) {
            Write-Host ""
            Write-Host "Last 50 lines:" -ForegroundColor Yellow
            Get-Content $BuildLog -Tail 50 | ForEach-Object { Write-Host $_ }
            Write-Host ""
            Write-Host "Full log: $BuildLog" -ForegroundColor Yellow
        }

        throw "MSBuild exited with code $LASTEXITCODE."
    }

    # Read OutputPath from the project if possible.
    # For this project it is normally bin\Debug\.
    $PackSource = Join-Path $ScriptDir "bin\$Configuration"

    if (-not (Test-Path $PackSource)) {
        throw "Build succeeded, but output folder '$PackSource' does not exist."
    }

    # System.Text.Json requires System.Memory on Playnite's .NET Framework runtime.
    # Fail the package instead of silently distributing an overlay whose FPS actions crash.
    $memoryDll = Join-Path $PackSource "System.Memory.dll"
    if (-not (Test-Path $memoryDll)) {
        $memorySource = Join-Path $ScriptDir "packages\System.Memory.4.5.5\lib\net461\System.Memory.dll"
        if (Test-Path $memorySource) {
            Copy-Item -LiteralPath $memorySource -Destination $memoryDll -Force
        }
    }
    if (-not (Test-Path $memoryDll)) {
        throw "System.Memory.dll missing from build output. Restore NuGet packages before packing."
    }

    # Ensure every transitive runtime dependency is in the extension folder.
    $runtimeDlls = @(
        'Microsoft.Bcl.AsyncInterfaces.dll', 'System.Buffers.dll', 'System.Memory.dll',
        'System.Numerics.Vectors.dll', 'System.Runtime.CompilerServices.Unsafe.dll',
        'System.Text.Encodings.Web.dll', 'System.Text.Json.dll', 'System.Threading.Tasks.Extensions.dll'
    )
    foreach ($dll in $runtimeDlls) {
        $from = Join-Path $ScriptDir (Join-Path 'BundledRuntime' $dll)
        $to = Join-Path $PackSource $dll
        if (-not (Test-Path $from)) { throw "Missing bundled runtime assembly: $dll" }
        Copy-Item -LiteralPath $from -Destination $to -Force
        if (-not (Test-Path $to)) { throw "Cannot package runtime assembly: $dll" }
    }

    if (-not (Test-Path $Toolbox)) {
        throw "Playnite Toolbox.exe was not found at '$Toolbox'."
    }

    New-Item -ItemType Directory -Force -Path $ReleaseDir | Out-Null

    Write-Host ""
    Write-Host "==> Packing extension..." -ForegroundColor Yellow

    $PackStart = Get-Date
    $PackSourceArg = $PackSource.TrimEnd('\')
    $ReleaseDirArg = $ReleaseDir.TrimEnd('\')
    & $Toolbox pack "$PackSourceArg" "$ReleaseDirArg"

    if ($LASTEXITCODE -ne 0) {
        throw "Playnite Toolbox pack failed with exit code $LASTEXITCODE."
    }

    # Validate the final packaged installer, not just the intermediate bin folder.
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $RequiredEntries = @(
        'extension.yaml', 'PlayniteGameOverlay.dll',
        'Microsoft.Bcl.AsyncInterfaces.dll', 'System.Buffers.dll', 'System.Memory.dll',
        'System.Numerics.Vectors.dll', 'System.Runtime.CompilerServices.Unsafe.dll',
        'System.Text.Encodings.Web.dll', 'System.Text.Json.dll',
        'System.Threading.Tasks.Extensions.dll'
    )
    $Packages = @(Get-ChildItem -LiteralPath $ReleaseDir -Filter *.pext -File |
        Where-Object { $_.LastWriteTime -ge $PackStart.AddSeconds(-2) })
    if ($Packages.Count -eq 0) {
        throw "Packaging returned success but no new .pext was created in $ReleaseDir"
    }
    foreach ($Package in $Packages) {
        # Toolbox may omit framework-named assemblies even when they are in bin\Release.
        # Write the actual bundled runtime files into the finished package, not just the build folder.
        $Archive = [System.IO.Compression.ZipFile]::Open($Package.FullName, [System.IO.Compression.ZipArchiveMode]::Update)
        try {
            foreach ($dll in $runtimeDlls) {
                $runtimePath = Join-Path $ScriptDir (Join-Path 'BundledRuntime' $dll)
                if (-not (Test-Path -LiteralPath $runtimePath)) {
                    throw "Cannot finish installer: $dll is missing from BundledRuntime."
                }
                $existing = $Archive.GetEntry($dll)
                if ($null -ne $existing) { $existing.Delete() }
                [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                    $Archive, $runtimePath, $dll, [System.IO.Compression.CompressionLevel]::Optimal)
            }
        }
        finally { $Archive.Dispose() }

        # Verify what was written, including byte-for-byte DLL content.
        $Archive = [System.IO.Compression.ZipFile]::OpenRead($Package.FullName)
        try {
            $Entries = @($Archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
            foreach ($RequiredEntry in $RequiredEntries) {
                if ($Entries -cnotcontains $RequiredEntry) {
                    throw "INVALID installer $($Package.Name): missing $RequiredEntry"
                }
            }
            foreach ($dll in $runtimeDlls) {
                $entry = $Archive.GetEntry($dll)
                $runtimePath = Join-Path $ScriptDir (Join-Path 'BundledRuntime' $dll)
                $fileHash = (Get-FileHash -LiteralPath $runtimePath -Algorithm SHA256).Hash
                $entryStream = $entry.Open()
                try {
                    $hashAlgorithm = [System.Security.Cryptography.SHA256]::Create()
                    try { $archiveHash = ([System.BitConverter]::ToString($hashAlgorithm.ComputeHash($entryStream))).Replace('-', '') }
                    finally { $hashAlgorithm.Dispose() }
                }
                finally { $entryStream.Dispose() }
                if ($archiveHash -ne $fileHash) {
                    throw "INVALID installer $($Package.Name): $dll differs from bundled runtime copy"
                }
            }
            Write-Host "Validated installer and runtime DLL hashes: $($Package.FullName)" -ForegroundColor Green
        }
        finally { $Archive.Dispose() }
    }

    Write-Host ""
    Write-Host "SUCCESS" -ForegroundColor Green
    Write-Host "Built:   $PackSource"
    Write-Host "Package: $ReleaseDir"
}
catch {
    $exitCode = 1
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Red
    Write-Host " ERROR" -ForegroundColor Red
    Write-Host "========================================" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red

    if (Test-Path $BuildLog) {
        Write-Host ""
        Write-Host "Build log: $BuildLog" -ForegroundColor Yellow
    }
}
finally {
    if (-not $NoPause) { Pause-OnExit }
}

exit $exitCode
