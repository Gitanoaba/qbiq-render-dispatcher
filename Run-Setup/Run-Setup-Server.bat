@echo off
REM qbiq Render Dispatcher - SERVER build launcher
REM Correr en el RENDER PC (Eitan Abaud) para instalar el auto-poll.
REM No requiere permisos de administrador.

setlocal
cd /d "%~dp0"

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Setup-RenderDispatcher-Server.ps1"

echo.
echo ----------------------------------------------------------------
echo Presioná cualquier tecla para cerrar.
echo ----------------------------------------------------------------
pause >nul
