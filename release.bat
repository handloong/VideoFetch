@echo off
rem Thin launcher: the real logic lives in tools\release.ps1.
rem Kept ASCII-only on purpose - cmd.exe reads .bat by bytes, and non-ASCII
rem content breaks parsing under a GBK code page (even with a UTF-8 BOM).
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\release.ps1" %*
set "RC=%ERRORLEVEL%"
if "%~1"=="" pause
exit /b %RC%
