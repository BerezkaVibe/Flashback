param([string]$Version='0.9.1')
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$published=Join-Path $root "artifacts/Flashback-$Version"
$work=Join-Path $root "work/installer-$Version"
$compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if(-not (Test-Path -LiteralPath (Join-Path $published 'tools/ffmpeg.exe'))) { throw 'Publish Flashback and copy tools/ffmpeg.exe first.' }
New-Item -ItemType Directory -Force $work,(Join-Path $root 'dist') | Out-Null
foreach($file in @('README.md','LICENSE','THIRD-PARTY-NOTICES.md','VALIDATION.md')) { Copy-Item -LiteralPath (Join-Path $root $file) -Destination $published -Force }
Copy-Item -LiteralPath (Join-Path $root 'licenses') -Destination $published -Recurse -Force
$compileArgs=@('/nologo','/target:winexe','/platform:x64','/optimize+',('/win32icon:'+(Join-Path $root 'Flashback/Assets/Flashback.ico')),('/win32manifest:'+(Join-Path $root 'installer/setup.manifest')),'/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.IO.Compression.dll','/reference:System.IO.Compression.FileSystem.dll','/reference:Microsoft.CSharp.dll')
$source=Join-Path $root 'installer/Setup.cs'
& $compiler @compileArgs '/define:UNINSTALL' (('/out:')+(Join-Path $published 'Uninstall.exe')) $source
if($LASTEXITCODE -ne 0) {throw 'Uninstaller compilation failed'}
$payload=Join-Path $work 'payload.zip'
Compress-Archive -Path (Join-Path $published '*') -DestinationPath $payload -Force
$hash=Join-Path $work 'payload.sha256'
Set-Content -LiteralPath $hash -Value (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash -Encoding ASCII
$output=Join-Path $root "dist/Flashback-$Version-Setup.exe"
& $compiler @compileArgs (('/resource:')+$payload+',payload.zip') (('/resource:')+$hash+',payload.sha256') (('/out:')+$output) $source
if($LASTEXITCODE -ne 0) {throw 'Installer compilation failed'}
Get-Item -LiteralPath $output | Select-Object FullName,Length

