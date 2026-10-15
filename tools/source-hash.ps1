# Fingerprint of the plugin source code (all files under src/VibelightValley except bin/obj), independent of line endings.
# tools/build.ps1 writes it next to the committed DLL (release/plugin/source.sha256); the release workflow runs
# this with -Check so a release can never ship a DLL that is older than its source.
# Works in Windows PowerShell 5.1 and PowerShell 7 (CI on Linux).
#   pwsh tools/source-hash.ps1            -> prints the hash
#   pwsh tools/source-hash.ps1 -Check     -> exit 1 if release/plugin/source.sha256 does not match
param([switch]$Check)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$src = Join-Path (Join-Path $root 'src') 'VibelightValley'

$paths = @{}
foreach ($f in Get-ChildItem $src -Recurse -File) {
    $rel = $f.FullName.Substring($src.Length + 1).Replace('\', '/')
    if ($rel -notmatch '^(bin|obj)/') { $paths[$rel] = $f.FullName }
}
# Ordinal sort: identical order on every OS/culture.
$keys = [string[]]@($paths.Keys)
[Array]::Sort($keys, [StringComparer]::Ordinal)

$sha = [Security.Cryptography.SHA256]::Create()
$stream = New-Object IO.MemoryStream
foreach ($rel in $keys) {
    $text = [IO.File]::ReadAllText($paths[$rel]).Replace("`r", '')
    $bytes = [Text.Encoding]::UTF8.GetBytes("$rel`n$text`n")
    $stream.Write($bytes, 0, $bytes.Length)
}
$hash = ([BitConverter]::ToString($sha.ComputeHash($stream.ToArray()))).Replace('-', '').ToLowerInvariant()

if (-not $Check) { $hash; exit 0 }

$recordFile = Join-Path (Join-Path (Join-Path $root 'release') 'plugin') 'source.sha256'
$recorded = if (Test-Path $recordFile) { (Get-Content $recordFile -Raw).Trim() } else { '<missing>' }
if ($recorded -ne $hash) {
    Write-Host "release/plugin/VibelightValley.dll is stale: source hash $hash, DLL built from $recorded."
    Write-Host 'Run tools/build.ps1 locally and commit release/plugin.'
    exit 1
}
Write-Host "release/plugin/VibelightValley.dll matches the source ($hash)."
exit 0
