# -FfmpegLibs: the bin folder of an FFmpeg 9.0 LGPL shared build (avcodec-63.dll and friends) for the FFmpeg
# preview player. Without it the editor keeps using the Windows player.
param([string]$FfmpegPath = 'D:\ffmpeg\ffmpeg.exe', [string]$Version = '0.9.3', [string]$FfmpegLibs = '')
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$project = Join-Path $projectRoot 'Flashback/Flashback.csproj'
$destination = Join-Path $projectRoot "artifacts/Flashback-$Version"
if (-not (Test-Path -LiteralPath $FfmpegPath)) { throw 'Provide a compatible FFmpeg executable with -FfmpegPath.' }
# The app's ffmpeg.exe must encode H.264 in software (libx264: recording without a graphics-card encoder,
# joining clips, some exports, the tests). LGPL builds leave it out, so an LGPL ffmpeg.exe (such as the one
# beside the -FfmpegLibs DLLs) can't be used here; use a full (GPL) build.
$encoders = (& $FfmpegPath -hide_banner -encoders 2>$null) -join "`n"
if ($encoders -notmatch 'libx264') { throw "The ffmpeg.exe given ($FfmpegPath) can't encode H.264 (no libx264), which Flashback needs. Pass a full build's ffmpeg.exe as -FfmpegPath (for example your usual D:\ffmpeg\ffmpeg.exe); keep the LGPL shared folder for -FfmpegLibs." }
# Publish into a clean folder so files dropped from the build do not linger in the zip.
if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Recurse -Force }
dotnet publish $project -c Release -r win-x64 --self-contained true -p:NuGetAudit=false -o $destination
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
New-Item -ItemType Directory -Force (Join-Path $projectRoot 'licenses') | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot '.packages/microsoft.netcore.app.runtime.win-x64/8.0.31/LICENSE.TXT') -Destination (Join-Path $projectRoot 'licenses/DotNet-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $projectRoot '.packages/microsoft.netcore.app.runtime.win-x64/8.0.31/THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $projectRoot 'licenses/DotNet-THIRD-PARTY-NOTICES.txt')
Copy-Item -LiteralPath (Join-Path $projectRoot '.packages/microsoft.windowsdesktop.app.runtime.win-x64/8.0.31/LICENSE') -Destination (Join-Path $projectRoot 'licenses/WindowsDesktop-LICENSE.txt')
New-Item -ItemType Directory -Force (Join-Path $destination 'tools') | Out-Null
Copy-Item -LiteralPath $FfmpegPath -Destination (Join-Path $destination 'tools/ffmpeg.exe')
# A "shared" FFmpeg build's ffmpeg.exe only runs with its DLLs beside it (a static build has none), so they
# go into tools too; without them it fails on start with "avcodec-63.dll was not found".
$ffmpegDlls = Get-ChildItem -LiteralPath (Split-Path -Parent $FfmpegPath) -Filter '*.dll' -ErrorAction SilentlyContinue
if ($ffmpegDlls) {
    Write-Warning 'The ffmpeg.exe given is a shared build; its DLLs are copied beside it. A static ffmpeg.exe keeps the app smaller.'
    $ffmpegDlls | Copy-Item -Destination (Join-Path $destination 'tools')
}
if ($FfmpegLibs -ne '') {
    if (-not (Test-Path -LiteralPath (Join-Path $FfmpegLibs 'avcodec-63.dll'))) { throw 'FfmpegLibs must be the bin folder of an FFmpeg 9.0 LGPL shared build (avcodec-63.dll), such as ffmpeg-n9.0-latest-win64-lgpl-shared-9.0 from github.com/BtbN/FFmpeg-Builds.' }
    New-Item -ItemType Directory -Force (Join-Path $destination 'ffmpeg') | Out-Null
    Copy-Item -Path (Join-Path $FfmpegLibs '*.dll') -Destination (Join-Path $destination 'ffmpeg')
} else { Write-Warning 'No -FfmpegLibs: the FFmpeg preview player is left out and the editor uses the Windows player.' }
foreach ($file in @('README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'VALIDATION.md')) { Copy-Item -LiteralPath (Join-Path $projectRoot $file) -Destination $destination }
# Double-click to run the FFmpeg preview player's tests and open the results (only with the player's libraries).
if ($FfmpegLibs -ne '') { Copy-Item -LiteralPath (Join-Path $projectRoot 'Test-FFmpeg-Player.cmd') -Destination $destination }
if (Test-Path (Join-Path $projectRoot 'licenses')) { Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses') -Destination $destination -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $projectRoot 'dist') | Out-Null
Compress-Archive -Path $destination -DestinationPath (Join-Path $projectRoot "dist/Flashback-$Version-win-x64.zip") -Force
Write-Output "Created dist/Flashback-$Version-win-x64.zip"












