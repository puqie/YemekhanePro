@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-task059-acceptance.ps1"
exit /b %ERRORLEVEL%
