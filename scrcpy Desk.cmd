@echo off
if not exist "%~dp0dist\ScrcpyDesk\ScrcpyDesk.exe" (
  echo Build first: dotnet publish AndroidDesk\AndroidDesk.csproj -c Release -p:Platform=x64 -o dist\ScrcpyDesk
  pause
  exit /b 1
)
start "" "%~dp0dist\ScrcpyDesk\ScrcpyDesk.exe"
