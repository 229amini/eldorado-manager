# Builds dist\SirenSetup.exe, the one-file Windows installer.
# Run: powershell -ExecutionPolicy Bypass -File desktop\build.ps1
# Keep this file pure ASCII: PowerShell 5.1 reads BOM-less scripts as ANSI.

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $here

if (-not (Test-Path '.build-venv')) { python -m venv .build-venv }
$py = Join-Path $here '.build-venv\Scripts\python.exe'
& $py -m pip install --quiet --disable-pip-version-check pyinstaller
if ($LASTEXITCODE -ne 0) { throw 'pip install pyinstaller failed' }

& $py -m PyInstaller --noconfirm --clean --onefile --windowed --name SirenSetup `
    --distpath dist --workpath build --specpath build siren_desktop.py
if ($LASTEXITCODE -ne 0) { throw 'PyInstaller failed' }
Get-Item dist\SirenSetup.exe | Select-Object Name, Length
