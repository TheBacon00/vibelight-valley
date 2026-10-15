# Builds VibelightValley and deploys it into the game's BepInEx/plugins folder.
#   powershell -ExecutionPolicy Bypass -File tools\build.ps1 [-NoDeploy] [-GameDir "..."]
param(
    [switch]$NoDeploy,
    [string]$GameDir = $(if ($env:DDLV_DIR) { $env:DDLV_DIR } else { 'C:\Program Files (x86)\Steam\steamapps\common\Disney Dreamlight Valley' })
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$proj = Join-Path $root 'src\VibelightValley\VibelightValley.csproj'
$out = Join-Path $root 'build'
$sourceHash = & (Join-Path $PSScriptRoot 'source-hash.ps1')
& $dotnet build $proj -c Release -o $out "-p:GameDir=$GameDir" --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)" }

# The release workflow cannot compile the mod (it needs interop assemblies generated from a local game install),
# so the built DLL is committed in release/plugin together with the hash of the source it was built from.
$release = Join-Path $root 'release\plugin'
New-Item -ItemType Directory -Force $release | Out-Null
Copy-Item (Join-Path $out 'VibelightValley.dll') $release -Force
[IO.File]::WriteAllText((Join-Path $release 'source.sha256'), "$sourceHash`n")
Write-Host "Updated release\plugin (source $sourceHash) - commit it together with your source changes."

if ($NoDeploy) { Write-Host "Built to $out (not deployed)"; return }

if (Get-Process ddv -ErrorAction SilentlyContinue) { throw "The game is running - close it before deploying." }
$dest = Join-Path $GameDir 'BepInEx\plugins\VibelightValley'
# The mod used to be called DDLV-VR: never leave the old copy next to the new one (both would load).
$legacy = Join-Path $GameDir 'BepInEx\plugins\DDLV-VR'
if (Test-Path $legacy) { Remove-Item $legacy -Recurse -Force; Write-Host "Removed old $legacy" }
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item (Join-Path $out 'VibelightValley.dll') $dest -Force
$native = Join-Path $root 'lib\native'
Copy-Item (Join-Path $native 'openxr_loader.dll') $dest -Force
# Optional debugging layer (Diagnostics.EnableOpenXRValidationLayer)
if (Test-Path (Join-Path $native 'api_layers')) {
    New-Item -ItemType Directory -Force (Join-Path $dest 'api_layers') | Out-Null
    Copy-Item (Join-Path $native 'api_layers\XrApiLayer_core_validation.*') (Join-Path $dest 'api_layers') -Force
}
Write-Host "Deployed to $dest"
Get-ChildItem $dest -Recurse -File | Select-Object FullName, Length, LastWriteTime | Format-Table -AutoSize
