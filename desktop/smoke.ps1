# Installs dist\SirenSetup.exe, checks what it left behind, then uninstalls it. It replaces any
# Siren on this machine and deletes its settings, so run it on a clean machine: CI does.
# Keep this file pure ASCII: PowerShell 5.1 reads BOM-less scripts as ANSI.

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$home_ = Join-Path $env:LOCALAPPDATA 'Siren'
$exe = Join-Path $home_ 'Siren.exe'
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$uninstall = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Siren'
$links = [Environment]::GetFolderPath('Programs')

function Wait-Until([scriptblock] $done, [string] $what) {
    foreach ($i in 1..60) {
        if (& $done) { Write-Output "ok: $what"; return }
        Start-Sleep -Milliseconds 500
    }
    throw "timed out: $what"
}

Start-Process (Join-Path $here 'dist\SirenSetup.exe') -Wait

Wait-Until { Test-Path $exe } 'exe installed'
Wait-Until { (Get-ItemProperty $run).Siren -like '*--background' } 'starts at sign-in'
Wait-Until { (Get-ItemProperty $uninstall).DisplayVersion } 'listed in Installed apps'
Wait-Until { Test-Path (Join-Path $links 'Siren dashboard.lnk') } 'dashboard shortcut'
Wait-Until { Test-Path (Join-Path $links 'Siren settings.lnk') } 'settings shortcut'
Wait-Until { Get-Process Siren -ErrorAction SilentlyContinue } 'app running'
Start-Sleep -Seconds 3
if (-not (Get-Process Siren -ErrorAction SilentlyContinue)) { throw 'the app quit after starting' }
Write-Output 'ok: still running after 3 seconds'

Start-Process $exe -ArgumentList '--uninstall' -Wait
Wait-Until { -not (Test-Path $exe) } 'exe removed'
Wait-Until { -not (Test-Path $uninstall) } 'removed from Installed apps'
Wait-Until { -not (Test-Path (Join-Path $links 'Siren dashboard.lnk')) } 'shortcuts removed'
