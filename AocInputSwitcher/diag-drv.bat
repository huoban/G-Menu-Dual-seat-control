@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0diag-drv.ps1"
echo.
echo ==== DONE. Please copy ALL output above and send it back. ====
echo ==== If section 1 reports access denied, right-click this .bat ====
echo ==== and choose "Run as administrator", then re-run. ====
pause