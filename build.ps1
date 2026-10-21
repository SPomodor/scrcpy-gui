param([switch]$Publish)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
if ($Publish) {
    dotnet publish .\AndroidDesk\AndroidDesk.csproj -c Release -p:Platform=x64 -o .\dist\AndroidDesk
} else {
    dotnet build .\AndroidDesk\AndroidDesk.csproj -c Debug -p:Platform=x64
}
if ($LASTEXITCODE -ne 0) { throw 'Сборка завершилась с ошибкой.' }
