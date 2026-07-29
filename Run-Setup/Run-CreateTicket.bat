@echo off
REM qbiq Render Dispatcher - SENDER setup
REM Instala el boton "Create Ticket" en Revit para mandar renders al servidor.
REM Requiere: Revit 2024 cerrado + .NET SDK / Visual Studio 2022.

setlocal
cd /d "%~dp0"

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Setup-CreateTicket.ps1"

echo.
echo ----------------------------------------------------------------
echo Presiona cualquier tecla para cerrar.
echo ----------------------------------------------------------------
pause >nul
