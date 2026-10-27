# Regenerates the app icons in src/QuailImageSearch/Assets from icon/icon.svg (or icon/logo.svg):
#   logo.png  512 px PNG (in-app logo, Linux launcher icon)
#   app.ico   16-256 px, PNG entries (window icon, exe, installer)
#   app.icns  16-1024 px, PNG entries (macOS app bundle)
# Renders with the installed Microsoft Edge in headless mode; no network use.
# Usage: powershell -ExecutionPolicy Bypass -File tools\make_icons.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$svg = @('icon\icon.svg', 'icon\logo.svg') | ForEach-Object { Join-Path $root $_ } | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $svg) { throw 'icon\icon.svg not found.' }
$assets = Join-Path $root 'src\QuailImageSearch\Assets'
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
          "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw 'Microsoft Edge not found.' }

$work = Join-Path ([IO.Path]::GetTempPath()) ("quail-icons-" + [guid]::NewGuid())
New-Item -ItemType Directory $work | Out-Null
try {
    Copy-Item $svg (Join-Path $work 'icon.svg')
    $png = @{}
    foreach ($n in 16, 24, 32, 48, 64, 128, 256, 512, 1024) {
        $html = Join-Path $work "r$n.html"
        Set-Content -Encoding utf8 $html "<!doctype html><html><body style='margin:0;background:transparent'><img src='icon.svg' style='display:block;width:${n}px;height:${n}px'></body></html>"
        $out = Join-Path $work "$n.png"
        $p = Start-Process $edge -Wait -PassThru -WindowStyle Hidden -ArgumentList @(
            '--headless=new', '--disable-gpu', '--hide-scrollbars', '--no-first-run',
            '--force-device-scale-factor=1', "--user-data-dir=$work\profile",
            '--default-background-color=00000000', "--window-size=$n,$n",
            "--screenshot=$out", ('file:///' + ($html -replace '\\', '/')))
        if (-not (Test-Path $out)) { throw "Rendering $n px failed (exit $($p.ExitCode))." }
        $png[$n] = [IO.File]::ReadAllBytes($out)
    }

    Copy-Item (Join-Path $work '512.png') (Join-Path $assets 'logo.png') -Force

    # ICO: header, one 16-byte directory entry per image, then the PNG data.
    $sizes = 16, 24, 32, 48, 64, 256
    $ms = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter $ms
    $w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    foreach ($n in $sizes) {
        $d = [byte]($n % 256)   # 0 means 256
        $w.Write($d); $w.Write($d); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([UInt16]1); $w.Write([UInt16]32)
        $w.Write([UInt32]$png[$n].Length); $w.Write([UInt32]$offset)
        $offset += $png[$n].Length
    }
    foreach ($n in $sizes) { $w.Write($png[$n]) }
    [IO.File]::WriteAllBytes((Join-Path $assets 'app.ico'), $ms.ToArray())

    # ICNS: 'icns' + big-endian total length, then (type, big-endian length, PNG) per image.
    function BE([int]$v) { [byte[]]@((($v -shr 24) -band 255), (($v -shr 16) -band 255), (($v -shr 8) -band 255), ($v -band 255)) }
    $types = [ordered]@{ icp4 = 16; icp5 = 32; icp6 = 64; ic07 = 128; ic08 = 256; ic09 = 512; ic10 = 1024 }
    $body = New-Object IO.MemoryStream
    foreach ($t in $types.Keys) {
        $data = $png[$types[$t]]
        $body.Write([Text.Encoding]::ASCII.GetBytes($t), 0, 4)
        $body.Write((BE ($data.Length + 8)), 0, 4)
        $body.Write($data, 0, $data.Length)
    }
    $icns = New-Object IO.MemoryStream
    $icns.Write([Text.Encoding]::ASCII.GetBytes('icns'), 0, 4)
    $icns.Write((BE ($body.Length + 8)), 0, 4)
    $body.WriteTo($icns)
    [IO.File]::WriteAllBytes((Join-Path $assets 'app.icns'), $icns.ToArray())

    Write-Host "Wrote logo.png, app.ico and app.icns to $assets"
} finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
