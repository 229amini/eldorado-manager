# Builds dist\SirenSetup.exe, the one-file Windows installer. It is the app itself: run from
# anywhere, the exe installs itself. Needs the .NET SDK, and the .NET Framework 4.8 targeting pack.
# Run: powershell -ExecutionPolicy Bypass -File desktop\build.ps1
# Keep this file pure ASCII: PowerShell 5.1 reads BOM-less scripts as ANSI.

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $here

dotnet build Siren\Siren.csproj -c Release -o build
if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed' }

New-Item -ItemType Directory -Force dist | Out-Null
Copy-Item build\Siren.exe dist\SirenSetup.exe -Force
Get-Item dist\SirenSetup.exe | Select-Object Name, Length
