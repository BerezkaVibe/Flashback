param([string]$FfmpegPath = 'D:\ffmpeg\ffmpeg.exe', [string]$Version = '0.9.3')
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$project = Join-Path $projectRoot 'Flashback/Flashback.csproj'
$destination = Join-Path $projectRoot "artifacts/Flashback-$Version"
if (-not (Test-Path -LiteralPath $FfmpegPath)) { throw 'Provide a compatible FFmpeg executable with -FfmpegPath.' }
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
foreach ($file in @('README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'VALIDATION.md')) { Copy-Item -LiteralPath (Join-Path $projectRoot $file) -Destination $destination }
if (Test-Path (Join-Path $projectRoot 'licenses')) { Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses') -Destination $destination -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $projectRoot 'dist') | Out-Null
Compress-Archive -Path $destination -DestinationPath (Join-Path $projectRoot "dist/Flashback-$Version-win-x64.zip") -Force
Write-Output "Created dist/Flashback-$Version-win-x64.zip"












