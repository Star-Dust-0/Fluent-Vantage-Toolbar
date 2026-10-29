# Registers Fluent Legion Toolbar to start at logon with administrator rights and without a UAC prompt.
# Run this file once. It asks for administrator approval once, then never again.
param([switch]$Silent)
$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`""
    exit
}
$exe = Join-Path $PSScriptRoot 'FluentLegionToolbar.exe'
if (-not (Test-Path $exe)) { throw "FluentLegionToolbar.exe was not found next to this script: $exe" }
$user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew

# 1) Background instance at logon: lives in the tray, hidden until you click the icon.
$background = New-ScheduledTaskAction -Execute $exe -Argument '--background' -WorkingDirectory $PSScriptRoot
$logon = New-ScheduledTaskTrigger -AtLogOn -User $user
Register-ScheduledTask -TaskName 'FluentLegionToolbar' -Action $background -Trigger $logon -Principal $principal -Settings $settings -Force | Out-Null

# 2) Launcher task: opens the window elevated. The app runs it by itself when started normally without elevation.
$open = New-ScheduledTaskAction -Execute $exe -Argument '--show' -WorkingDirectory $PSScriptRoot
Register-ScheduledTask -TaskName 'FluentLegionToolbar-Open' -Action $open -Principal $principal -Settings $settings -Force | Out-Null

Start-ScheduledTask -TaskName 'FluentLegionToolbar'
Write-Host 'Done. Fluent Legion Toolbar now starts at logon with administrator rights and no UAC prompt.'
Write-Host 'Folder used by the tasks:' $PSScriptRoot '(do not move or delete it, or run this script again after moving).'
