# Downloads build dependencies that are not committed:
#   - Khronos OpenXR loader 1.1.63 (x64) + core_validation API layer  -> lib/native
#   - .NET 8 SDK (local, no admin, Windows only)                        -> .tools/dotnet   (skip with -NoDotnet)
#   powershell -ExecutionPolicy Bypass -File tools\fetch-deps.ps1
# Works in Windows PowerShell 5.1 and PowerShell 7 on Linux (the release workflow uses -NoDotnet).
param([switch]$NoDotnet)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$root = Split-Path $PSScriptRoot -Parent
function P([string]$rel) { Join-Path $root $rel }
$downloads = P '.tools/downloads'
New-Item -ItemType Directory -Force $downloads | Out-Null

# --- OpenXR loader
$native = P 'lib/native'
if (Test-Path (Join-Path $native 'openxr_loader.dll')) {
    Write-Host 'OpenXR loader already present'
}
else {
    $zip = Join-Path $downloads 'openxr_loader_windows-1.1.63.zip'
    Invoke-WebRequest 'https://github.com/KhronosGroup/OpenXR-SDK-Source/releases/download/release-1.1.63/openxr_loader_windows-1.1.63.zip' -OutFile $zip -UseBasicParsing
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        foreach ($e in $archive.Entries) {
            $keep = $e.FullName -eq 'x64/bin/openxr_loader.dll' -or $e.FullName -like 'x64/bin/api_layers/XrApiLayer_core_validation.*'
            if (-not $keep) { continue }
            $target = Join-Path $native $e.FullName.Substring('x64/bin/'.Length)
            New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($e, $target, $true)
        }
    }
    finally { $archive.Dispose() }
    Write-Host "OpenXR loader extracted to $native"
}

# --- .NET 8 SDK
if ($NoDotnet -or (Test-Path (P '.tools/dotnet/dotnet.exe'))) { return }
$installer = Join-Path $downloads 'dotnet-install.ps1'
Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer -UseBasicParsing
& $installer -Channel 8.0 -InstallDir (P '.tools/dotnet') -NoPath
