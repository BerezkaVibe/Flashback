param([string]$FfmpegPath = 'D:\ffmpeg\ffmpeg.exe')
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$project = Join-Path $projectRoot 'Flashback/Flashback.csproj'
$destination = Join-Path $projectRoot 'artifacts/Flashback-0.5.3'
if (-not (Test-Path -LiteralPath $FfmpegPath)) { throw 'Provide a compatible FFmpeg executable with -FfmpegPath.' }
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
Compress-Archive -Path $destination -DestinationPath (Join-Path $projectRoot 'dist/Flashback-0.5.3-win-x64.zip') -Force
Write-Output 'Created dist/Flashback-0.5.3-win-x64.zip'












