@echo off
powershell -ExecutionPolicy Bypass -File "%~dp0setup-libs.ps1" %*
pause
