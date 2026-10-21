@echo off
if not exist "%~dp0dist\AndroidDesk\AndroidDesk.exe" (
  echo Build first: dotnet publish AndroidDesk\AndroidDesk.csproj -c Release -p:Platform=x64 -o dist\AndroidDesk
  pause
  exit /b 1
)
start "" "%~dp0dist\AndroidDesk\AndroidDesk.exe"
