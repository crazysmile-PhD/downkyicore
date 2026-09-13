@echo off
pwsh -NoLogo -NoProfile -NonInteractive -File "%~dp0stage-canonical-publish.ps1"
exit /b %ERRORLEVEL%
