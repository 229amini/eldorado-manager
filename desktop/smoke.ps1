# Installs dist\SellerDeskSetup.exe, checks what it left behind, then uninstalls it. It replaces any
# SellerDesk on this machine and deletes its settings, so run it on a clean machine: CI does.
# Keep this file pure ASCII: PowerShell 5.1 reads BOM-less scripts as ANSI.

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$home_ = Join-Path $env:LOCALAPPDATA 'SellerDesk'
$exe = Join-Path $home_ 'SellerDesk.exe'
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$uninstall = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\SellerDesk'
$links = [Environment]::GetFolderPath('Programs')

function Wait-Until([scriptblock] $done, [string] $what) {
    foreach ($i in 1..60) {
        if (& $done) { Write-Output "ok: $what"; return }
        Start-Sleep -Milliseconds 500
    }
    throw "timed out: $what"
}

# Not -Wait: that also waits for the app the installer starts, which keeps running.
(Start-Process (Join-Path $here 'dist\SellerDeskSetup.exe') -PassThru).WaitForExit()

Wait-Until { Test-Path $exe } 'exe installed'
Wait-Until { (Get-ItemProperty $run).SellerDesk -like '*--background' } 'starts at sign-in'
Wait-Until { (Get-ItemProperty $uninstall).DisplayVersion } 'listed in Installed apps'
Wait-Until { Test-Path (Join-Path $links 'SellerDesk.lnk') } 'dashboard shortcut'
Wait-Until { Test-Path (Join-Path $links 'SellerDesk settings.lnk') } 'settings shortcut'
Wait-Until { Get-Process SellerDesk -ErrorAction SilentlyContinue } 'app running'
Start-Sleep -Seconds 3
if (-not (Get-Process SellerDesk -ErrorAction SilentlyContinue)) { throw 'the app quit after starting' }
Write-Output 'ok: still running after 3 seconds'

(Start-Process $exe -ArgumentList '--uninstall' -PassThru).WaitForExit()
Wait-Until { -not (Test-Path $exe) } 'exe removed'
Wait-Until { -not (Test-Path $uninstall) } 'removed from Installed apps'
Wait-Until { -not (Test-Path (Join-Path $links 'SellerDesk.lnk')) } 'shortcuts removed'
