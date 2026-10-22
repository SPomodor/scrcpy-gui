param(
    [Parameter(Mandatory)][string]$Path,
    [Parameter(Mandatory)][ValidateSet('x64', 'x86', 'arm64')][string]$Architecture
)
$ErrorActionPreference = 'Stop'
$Architecture = $Architecture.ToLowerInvariant()
function Get-PeMachine([string]$File) {
    $stream = [IO.File]::OpenRead($File)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) { throw "Invalid executable: $File" }
        $stream.Position = 0x3C
        $offset = $reader.ReadInt32()
        $stream.Position = $offset
        if ($reader.ReadUInt32() -ne 0x4550) { throw "Invalid PE header: $File" }
        return $reader.ReadUInt16()
    } finally { $reader.Dispose(); $stream.Dispose() }
}
$expected = @{x86 = 0x14C; x64 = 0x8664; arm64 = 0xAA64}[$Architecture]
foreach ($file in @('ScrcpyDesk.exe', 'Microsoft.UI.Xaml.dll', 'scrcpy\scrcpy.exe', 'scrcpy\SDL3.dll', 'scrcpy\avcodec-63.dll', 'scrcpy\avformat-63.dll', 'scrcpy\avutil-61.dll', 'scrcpy\swresample-7.dll', 'scrcpy\libusb-1.0.dll')) {
    $fullPath = Join-Path $Path $file
    if (!(Test-Path -LiteralPath $fullPath)) { throw "Missing package file: $file" }
    $machine = Get-PeMachine $fullPath
    if ($machine -ne $expected) { throw "$file has PE machine $machine; expected $expected for $Architecture." }
}
foreach ($file in @('ScrcpyDesk.dll', 'ScrcpyDesk.pri', 'MainWindow.xbf', 'Assets\app.ico', 'scrcpy\scrcpy-server', 'scrcpy\adb.exe', 'scrcpy\AdbWinApi.dll', 'scrcpy\AdbWinUsbApi.dll', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'licenses\LGPL-2.1.txt', 'scrcpy\LICENSE.txt')) {
    if (!(Test-Path -LiteralPath (Join-Path $Path $file))) { throw "Missing package file: $file" }
}
# ADB is a separate process. Official bundles can include x86 ADB even for x64
# or ARM64 scrcpy; Windows supports that independently of the native GUI process.
$adbMachine = Get-PeMachine (Join-Path $Path 'scrcpy\adb.exe')
if ($adbMachine -ne $expected -and $adbMachine -ne 0x14C) { throw 'Unexpected ADB architecture.' }
foreach ($file in @('AdbWinApi.dll', 'AdbWinUsbApi.dll')) {
    if ((Get-PeMachine (Join-Path $Path "scrcpy\$file")) -ne $adbMachine) { throw "ADB library architecture mismatch: $file" }
}
Write-Host "Validated $Architecture package, resources, native libraries and licenses (ADB PE machine: $adbMachine)."
