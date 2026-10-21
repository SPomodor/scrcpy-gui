param([Parameter(Mandatory)][ValidateSet('x64', 'x86', 'arm64')][string]$Architecture)
$ErrorActionPreference = 'Stop'
$Architecture = $Architecture.ToLowerInvariant()
$repoRoot = Split-Path -Parent $PSScriptRoot
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'scrcpy-bundles.json') -Raw | ConvertFrom-Json
$bundle = $manifest.bundles.$Architecture
$cache = Join-Path $repoRoot 'artifacts\tools'
New-Item -ItemType Directory -Path $cache -Force | Out-Null
$archive = Join-Path $cache $bundle.file
if (!(Test-Path -LiteralPath $archive)) {
    $url = "https://github.com/Genymobile/scrcpy/releases/download/v$($manifest.version)/$($bundle.file)"
    Write-Host "Downloading $($bundle.file)..."
    Invoke-WebRequest -Uri $url -OutFile $archive -UseBasicParsing
}
$actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
if ($actual -ne $bundle.sha256) { throw "scrcpy archive SHA-256 mismatch: $archive. Remove this cached archive and retry." }
# A fresh extraction avoids reusing modified cached binaries. Previously prepared
# directories are left in the ignored cache; no recursive deletion is necessary.
$expanded = Join-Path $cache ("scrcpy-$Architecture-" + [guid]::NewGuid().ToString('N'))
Expand-Archive -LiteralPath $archive -DestinationPath $expanded
$executable = @(Get-ChildItem -LiteralPath $expanded -Filter scrcpy.exe -Recurse)
if ($executable.Count -ne 1) { throw 'The scrcpy archive must contain exactly one scrcpy.exe.' }
return $executable[0].Directory.FullName
