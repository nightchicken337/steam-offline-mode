@echo off
title Steam Offline Mode Toggle
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0steam-offline.ps1" -Action toggle
