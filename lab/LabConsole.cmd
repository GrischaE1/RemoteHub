@echo off
rem RemoteHub Lab console: choose tests and VMs, build, start, follow and read the results (LabConsole.ps1).
start "" powershell.exe -NoProfile -STA -WindowStyle Hidden -File "%~dp0LabConsole.ps1"
