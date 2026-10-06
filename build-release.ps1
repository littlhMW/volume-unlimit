$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$appPublish = Join-Path $root 'PerAppVolume\publish'
$payload = Join-Path $root 'VolumeBoostSetup\payload\PerAppVolume.exe'
$nativePayload = Join-Path $root 'VolumeBoostSetup\payload\ApplicationLoopback.dll'
$out = Join-Path $root 'release'

dotnet publish (Join-Path $root 'PerAppVolume\PerAppVolume.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o $appPublish
New-Item -ItemType Directory -Force (Split-Path $payload) | Out-Null
Copy-Item (Join-Path $appPublish 'PerAppVolume.exe') $payload -Force
Copy-Item (Join-Path $root 'PerAppVolume\native\ApplicationLoopback.dll') $nativePayload -Force
New-Item -ItemType Directory -Force $out | Out-Null
dotnet publish (Join-Path $root 'VolumeBoostSetup\VolumeBoostSetup.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o $out
Write-Host "完成：$out\VolumeBoost-Setup.exe"
