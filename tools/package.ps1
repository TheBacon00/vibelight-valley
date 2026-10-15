# Builds the release files in dist/:
#   VibelightValley-Installer.exe     the installer (install / update / on-off / uninstall)
#   VibelightValley-<version>.zip     the mod, laid out like the game folder (BepInEx/plugins/VibelightValley/...)
#   manifest.json             what the installer reads: versions, URLs and SHA-256 of everything it downloads
#
#   Local:  powershell -ExecutionPolicy Bypass -File tools\package.ps1                 (builds the mod first)
#   CI:     pwsh tools/package.ps1 -NoBuild -Version 1.2.3 -Repo owner/name            (uses the committed release/plugin)
#   Test:   tools\package.ps1 -NoBuild -LocalManifest   then   $env:VIBELIGHT_MANIFEST = "<repo>\dist\manifest.json"
#           (the manifest points at the files in dist/ instead of GitHub)
param(
    [string]$Version,
    [switch]$NoBuild,
    [string]$GameDir,
    [string]$Repo = 'TheBacon00/vibelight-valley',
    [switch]$LocalManifest
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
function P([string]$rel) { Join-Path $root $rel }

# BepInEx build the mod is made for. The installer installs exactly this one (verified by SHA-256).
$BepInEx = @{
    version = '6.0.0-be.788'
    url     = 'https://builds.bepinex.dev/projects/bepinex_be/788/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788%2B5b766a3.zip'
    sha256  = 'f4cc496bd098a0df4164b81e3737297707f13a47c2478dba2f60eefab784817a'
    size    = 0
}

if (-not $Version) {
    $Version = ([xml](Get-Content (P 'src/VibelightValley/VibelightValley.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if (-not $NoBuild) {
    $buildArgs = @{ NoDeploy = $true }
    if ($GameDir) { $buildArgs.GameDir = $GameDir }
    & (P 'tools/build.ps1') @buildArgs
}
if (-not (Test-Path (P 'lib/native/openxr_loader.dll'))) { & (P 'tools/fetch-deps.ps1') -NoDotnet }

$dist = P 'dist'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force $dist | Out-Null

function Get-Sha256([string]$path) { (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant() }

# ---------------------------------------------------------------- installer
$dotnet = P '.tools/dotnet/dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'
& $dotnet publish (P 'src/Installer/Installer.csproj') -c Release -o (P 'build/installer-publish') "-p:Version=$Version" --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw "Installer build failed ($LASTEXITCODE)" }
$installerExe = Join-Path $dist 'VibelightValley-Installer.exe'
Copy-Item (P 'build/installer-publish/VibelightValley-Installer.exe') $installerExe

# ---------------------------------------------------------------- mod zip
$name = "VibelightValley-$Version"
$stage = P "build/package/$name"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
$plugin = Join-Path $stage 'BepInEx/plugins/VibelightValley'
New-Item -ItemType Directory -Force $plugin | Out-Null
Copy-Item (P 'release/plugin/VibelightValley.dll') $plugin
Copy-Item (P 'lib/native/openxr_loader.dll') $plugin
foreach ($f in 'README.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md') { Copy-Item (P $f) $stage }

$zip = Join-Path $dist "$name.zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
# Compress-Archive in Windows PowerShell 5.1 writes '\' separators; the installer accepts both.

# ---------------------------------------------------------------- manifest
$base = "https://github.com/$Repo/releases/download/v$Version"
function Url([string]$file) { if ($LocalManifest) { Join-Path $dist $file } else { "$base/$file" } }

$manifest = [ordered]@{
    schema     = 1
    version    = $Version
    releaseUrl = "https://github.com/$Repo/releases/tag/v$Version"
    mod        = [ordered]@{ version = $Version; url = (Url "$name.zip"); sha256 = (Get-Sha256 $zip); size = (Get-Item $zip).Length }
    bepinex    = [ordered]@{ version = $BepInEx.version; url = $BepInEx.url; sha256 = $BepInEx.sha256; size = $BepInEx.size }
    installer  = [ordered]@{ version = $Version; url = (Url 'VibelightValley-Installer.exe'); sha256 = (Get-Sha256 $installerExe); size = (Get-Item $installerExe).Length }
}
$json = $manifest | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText((Join-Path $dist 'manifest.json'), $json, (New-Object Text.UTF8Encoding($false)))

Write-Host "Release files for v$Version in $dist$(if ($LocalManifest) { ' (local test manifest)' }):"
Get-ChildItem $dist -File | ForEach-Object { '  {0,-28} {1,12:N0} bytes' -f $_.Name, $_.Length }
