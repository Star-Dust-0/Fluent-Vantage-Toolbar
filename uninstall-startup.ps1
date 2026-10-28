# Removes the scheduled tasks created by install-startup.ps1 and closes the running app.
param([switch]$Silent)
$ErrorActionPreference = 'SilentlyContinue'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`""
    exit
}
Stop-ScheduledTask -TaskName 'FluentLegionToolbar'
Unregister-ScheduledTask -TaskName 'FluentLegionToolbar' -Confirm:$false
Unregister-ScheduledTask -TaskName 'FluentLegionToolbar-Open' -Confirm:$false
Get-Process FluentLegionToolbar | Stop-Process -Force
Write-Host 'Removed. The app no longer starts at logon.'
