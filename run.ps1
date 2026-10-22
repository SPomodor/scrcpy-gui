$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$appPath = Join-Path $PSScriptRoot 'dist\ScrcpyDesk\ScrcpyDesk.exe'
if (!(Test-Path -LiteralPath $appPath)) {
    & .\build.ps1 -Publish
}
Start-Process -FilePath $appPath
