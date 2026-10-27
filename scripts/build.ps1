<#
.SYNOPSIS
    Builds release packages of Quail Image Search, one per platform, into artifacts\.

.DESCRIPTION
    For each platform: a self-contained Release publish (users don't need .NET installed) with only
    that platform's libraries, then tools\Packager removes debug symbols, checks the build (model
    engine, license and calibration files, no Debug-only packages), runs the PRIVACY.md library scan
    and packages it:
      win-x64, win-arm64   -> QuailImageSearch-<version>-win-x64.zip
                              QuailImageSearch-<version>-win-x64-setup.exe (installer, Inno Setup 6)
      osx-arm64            -> QuailImageSearch-<version>-macos-arm64.zip ("Quail Image Search.app")
      linux-x64            -> QuailImageSearch-<version>-linux-x64.tar.gz
    Finally writes artifacts\SHA256SUMS.txt. Every platform can be built on Windows.

    The Windows installers need Inno Setup 6 (https://jrsoftware.org/isdl.php). Without it they're
    skipped with a warning; -NoInstaller skips them on purpose.

    There's no Intel Mac (osx-x64) build: ONNX Runtime, which runs the models, no longer ships a
    library for Intel Macs.

.EXAMPLE
    .\scripts\build.ps1                               # all platforms
    .\scripts\build.ps1 -Platform win-x64,osx-arm64   # just these
    .\scripts\build.ps1 -Version 1.0.1 -ReadyToRun
#>
param(
    [ValidateSet('win-x64', 'win-arm64', 'osx-arm64', 'linux-x64')]
    [string[]] $Platform = @('win-x64', 'win-arm64', 'osx-arm64', 'linux-x64'),
    # Defaults to <Version> in the project file.
    [string] $Version,
    [string] $BundleId = 'com.chasegrozdina.quailimagesearch',
    # Pre-compiles the code for faster start-up; the download gets roughly 30-50% bigger.
    [switch] $ReadyToRun,
    # Only the Windows zips, no installers.
    [switch] $NoInstaller,
    # Code signing isn't set up yet (Windows certificate / Apple Developer ID + notarization on a Mac).
    [switch] $Sign
)

$ErrorActionPreference = 'Stop'
if ($Sign) {
    throw "Signing isn't set up yet. Windows needs a code-signing certificate; macOS needs an Apple Developer ID and has to be signed and notarized on a Mac. Build without -Sign for unsigned packages."
}

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\QuailImageSearch\QuailImageSearch.csproj'
$artifacts = Join-Path $root 'artifacts'
if (-not $Version) {
    $Version = ([xml](Get-Content $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $Version) { throw "No <Version> in $project; pass -Version." }
}
Write-Host "Quail Image Search ${Version}: $($Platform -join ', ')" -ForegroundColor Cyan

# The packaging helper.
$packagerOut = Join-Path $artifacts '.packager'
dotnet build (Join-Path $root 'tools\Packager\Packager.csproj') -c Release -o $packagerOut -v quiet -nologo
if ($LASTEXITCODE) { throw 'Building tools\Packager failed.' }

# Inno Setup's compiler, for the Windows installers (on PATH, or in its per-user or all-users install folder).
$iscc = $null
if (-not $NoInstaller -and ($Platform | Where-Object { $_ -like 'win-*' })) {
    $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
    if (-not $iscc) {
        $iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6", "${env:ProgramFiles(x86)}\Inno Setup 6", "$env:ProgramFiles\Inno Setup 6") |
            ForEach-Object { Join-Path $_ 'ISCC.exe' } | Where-Object { Test-Path $_ } | Select-Object -First 1
    }
    if (-not $iscc) { Write-Warning 'Inno Setup 6 not found, so no Windows installers (only zips). Get it from https://jrsoftware.org/isdl.php.' }
}
# The installer's file properties need a plain number: 1.0.1-beta -> 1.0.1.0.
$fileVersion = (([regex]::Match($Version, '^\d+(\.\d+){0,3}').Value -split '\.') + @('0', '0', '0'))[0..3] -join '.'

$results = @()
foreach ($rid in $Platform) {
    Write-Host "`n== $rid" -ForegroundColor Cyan
    $publishDir = Join-Path $artifacts "publish\$rid"
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    # -r on the command line (not only in the profile) so the project picks the right ONNX Runtime package.
    dotnet publish $project -c Release -r $rid --self-contained true -p:PublishProfile=$rid `
        -p:Version=$Version -p:PublishReadyToRun=$($ReadyToRun.IsPresent) -o $publishDir -v quiet -nologo
    if ($LASTEXITCODE) { throw "Publishing $rid failed." }
    dotnet (Join-Path $packagerOut 'Packager.dll') package --rid $rid --publish $publishDir --out $artifacts `
        --version $Version --bundle-id $BundleId --root $root
    if ($LASTEXITCODE) { throw "Packaging $rid failed (see the errors above)." }
    if ($iscc -and $rid -like 'win-*') {
        # Built from the same checked folder as the zip.
        & $iscc /Q "/DAppVersion=$Version" "/DFileVersion=$fileVersion" "/DArch=$($rid.Substring(4))" "/DSourceDir=$publishDir" `
            "/DOutputDir=$artifacts" "/DRepoRoot=$root" (Join-Path $root 'packaging\windows\QuailImageSearch.iss')
        if ($LASTEXITCODE) { throw "Building the $rid installer failed." }
        Write-Host "  installer: QuailImageSearch-$Version-$rid-setup.exe"
    }
    $results += $rid
}

# Checksums for every package in artifacts\.
$packages = Get-ChildItem $artifacts -File | Where-Object { $_.Name -like "QuailImageSearch-$Version-*" }
$sums = $packages | Sort-Object Name | ForEach-Object { "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name }
Set-Content -Path (Join-Path $artifacts 'SHA256SUMS.txt') -Value $sums -Encoding ascii

Write-Host "`nPackages in $artifacts" -ForegroundColor Green
$packages | Sort-Object Name | ForEach-Object { "  {0,-45} {1,8:N1} MB" -f $_.Name, ($_.Length / 1e6) }
Write-Host '  SHA256SUMS.txt'
