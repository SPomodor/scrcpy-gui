param(
    [switch]$Publish,
    [ValidateSet('x64', 'x86', 'arm64')][string]$Architecture = 'x64',
    [string]$OutputDirectory,
    [string]$Version,
    [switch]$DownloadTools
)
$ErrorActionPreference = 'Stop'
$Architecture = $Architecture.ToLowerInvariant()
Set-Location $PSScriptRoot
$platform = if ($Architecture -eq 'arm64') { 'ARM64' } else { $Architecture }
$tools = Join-Path $PSScriptRoot 'scrcpy'
if ($Architecture -ne 'x64' -or $DownloadTools) {
    $tools = & (Join-Path $PSScriptRoot 'scripts\Get-Scrcpy.ps1') -Architecture $Architecture
}
$arguments = @('.\AndroidDesk\AndroidDesk.csproj', '-c', $(if ($Publish) { 'Release' } else { 'Debug' }), "-p:Platform=$platform", "-r", "win-$Architecture", "-p:ScrcpyToolsDirectory=$tools")
if ($Version) {
    if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw 'Version must be a semantic version such as 1.0.0 or 1.0.0-rc.1.' }
    $arguments += "-p:Version=$Version"
}
if ($Publish) {
    if (!$OutputDirectory) { $OutputDirectory = if ($Architecture -eq 'x64') { 'dist\AndroidDesk' } else { "dist\AndroidDesk-$Architecture" } }
    $arguments += @('-o', $OutputDirectory)
    & dotnet publish @arguments
} else {
    & dotnet build @arguments
}
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
if ($Publish) { & (Join-Path $PSScriptRoot 'scripts\Test-Package.ps1') -Path $OutputDirectory -Architecture $Architecture }
