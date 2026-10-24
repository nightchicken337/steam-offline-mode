@echo off
title Steam Offline Mode Status
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0steam-offline.ps1" -Action status
