# Installs dist\EldoradoManagerSetup.exe, checks what it left behind, then uninstalls it. It replaces any
# Eldorado Manager on this machine and deletes its settings, so run it on a clean machine: CI does.
# Keep this file pure ASCII: PowerShell 5.1 reads BOM-less scripts as ANSI.

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$home_ = Join-Path $env:LOCALAPPDATA 'EldoradoManager'
$exe = Join-Path $home_ 'EldoradoManager.exe'
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$uninstall = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\EldoradoManager'
$links = [Environment]::GetFolderPath('Programs')

function Wait-Until([scriptblock] $done, [string] $what) {
    foreach ($i in 1..60) {
        if (& $done) { Write-Output "ok: $what"; return }
        Start-Sleep -Milliseconds 500
    }
    throw "timed out: $what"
}

# Not -Wait: that also waits for the app the installer starts, which keeps running.
(Start-Process (Join-Path $here 'dist\EldoradoManagerSetup.exe') -PassThru).WaitForExit()

Wait-Until { Test-Path $exe } 'exe installed'
Wait-Until { (Get-ItemProperty $run).EldoradoManager -like '*--background' } 'starts at sign-in'
Wait-Until { (Get-ItemProperty $uninstall).DisplayVersion } 'listed in Installed apps'
Wait-Until { Test-Path (Join-Path $links 'Eldorado Manager.lnk') } 'dashboard shortcut'
Wait-Until { Test-Path (Join-Path $links 'Eldorado Manager settings.lnk') } 'settings shortcut'
Wait-Until { Get-Process EldoradoManager -ErrorAction SilentlyContinue } 'app running'
Start-Sleep -Seconds 3
if (-not (Get-Process EldoradoManager -ErrorAction SilentlyContinue)) { throw 'the app quit after starting' }
Write-Output 'ok: still running after 3 seconds'

(Start-Process $exe -ArgumentList '--uninstall' -PassThru).WaitForExit()
Wait-Until { -not (Test-Path $exe) } 'exe removed'
Wait-Until { -not (Test-Path $uninstall) } 'removed from Installed apps'
Wait-Until { -not (Test-Path (Join-Path $links 'Eldorado Manager.lnk')) } 'shortcuts removed'
