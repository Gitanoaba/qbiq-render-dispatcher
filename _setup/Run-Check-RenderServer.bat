@echo off
REM Lanzador de Check-RenderServer.ps1 - read-only.
REM Correr en la PC render server (NO en la laptop).
setlocal
set "SCRIPT_DIR=%~dp0"
set "PS_SCRIPT=%SCRIPT_DIR%Check-RenderServer.ps1"

if not exist "%PS_SCRIPT%" (
    echo [ERROR] No se encontro Check-RenderServer.ps1 en:
    echo         %PS_SCRIPT%
    pause
    exit /b 1
)

echo.
echo Ejecutando Check-RenderServer.ps1 (read-only)...
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%PS_SCRIPT%"

echo.
echo Reporte guardado en: %USERPROFILE%\Desktop\render_server_inspect.txt
pause
