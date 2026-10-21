param(
    [Parameter(Mandatory)][ValidateSet('x64', 'x86', 'arm64')][string]$Architecture,
    [string]$Version = 'dev'
)
$ErrorActionPreference = 'Stop'
$Architecture = $Architecture.ToLowerInvariant()
if ($Version -ne 'dev' -and $Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw 'Invalid release version.' }
$repoRoot = Split-Path -Parent $PSScriptRoot
$packageName = "scrcpy-gui-$Version-windows-$Architecture"
$staging = Join-Path $repoRoot ("artifacts\staging\" + [guid]::NewGuid().ToString('N'))
$output = Join-Path $staging $packageName
$parameters = @{ Publish = $true; Architecture = $Architecture; OutputDirectory = $output; DownloadTools = $true }
if ($Version -ne 'dev') { $parameters.Version = $Version }
& (Join-Path $repoRoot 'build.ps1') @parameters
foreach ($document in @('README.md', 'README.ru.md')) { Copy-Item -LiteralPath (Join-Path $repoRoot $document) -Destination $output }
$releaseDirectory = Join-Path $repoRoot 'artifacts\release'
New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
$archive = Join-Path $releaseDirectory "$packageName.zip"
Compress-Archive -LiteralPath $output -DestinationPath $archive -CompressionLevel Optimal -Force
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$archive.sha256", "$hash  $packageName.zip`n", [Text.UTF8Encoding]::new($false))
Write-Host "Created $archive"
