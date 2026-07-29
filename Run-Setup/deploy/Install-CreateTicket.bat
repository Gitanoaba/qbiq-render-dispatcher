@echo off
REM ================================================================
REM  qbiq Render Dispatcher - Create Ticket installer
REM  Solo necesitas Revit 2024. No requiere Visual Studio ni .NET SDK.
REM
REM  1. Cerrá Revit completamente
REM  2. Doble-click en este archivo
REM  3. Abrí Revit → aparece la tab "Render Server" con "Create Ticket"
REM ================================================================

setlocal

set DLL_SRC=%~dp0QbiqRenderDispatcher.dll
set INSTALL_DIR=%APPDATA%\qbiq\RenderDispatcher
set ADDIN_DIR=%APPDATA%\Autodesk\Revit\Addins\2024

echo.
echo ================================================================
echo  qbiq Render Dispatcher - Create Ticket installer
echo ================================================================
echo.

REM Check Revit is closed
tasklist /FI "IMAGENAME eq Revit.exe" 2>NUL | find /I "Revit.exe" >NUL
if not errorlevel 1 (
    echo [ERROR] Revit esta abierto. Cerrado completamente y volvé a correr este archivo.
    echo.
    pause
    exit /b 1
)

REM Create install folder
if not exist "%INSTALL_DIR%" mkdir "%INSTALL_DIR%"

REM Copy DLL
echo Copiando plugin...
copy /Y "%DLL_SRC%" "%INSTALL_DIR%\QbiqRenderDispatcher.dll" >NUL
if errorlevel 1 (
    echo [ERROR] No se pudo copiar el DLL. Verificá que Revit esté cerrado.
    pause
    exit /b 1
)

REM Create Addins folder if needed
if not exist "%ADDIN_DIR%" mkdir "%ADDIN_DIR%"

REM Write .addin manifest
echo Instalando manifest...
(
echo ^<?xml version="1.0" encoding="utf-8"?^>
echo ^<RevitAddIns^>
echo   ^<AddIn Type="Application"^>
echo     ^<Name^>QbiqRenderDispatcher^</Name^>
echo     ^<Assembly^>%INSTALL_DIR%\QbiqRenderDispatcher.dll^</Assembly^>
echo     ^<AddInId^>a1b2c3d4-e5f6-7890-abcd-ef1234567890^</AddInId^>
echo     ^<FullClassName^>QbiqRenderDispatcher.App^</FullClassName^>
echo     ^<VendorId^>qbiq^</VendorId^>
echo     ^<VendorDescription^>qbiq.ai^</VendorDescription^>
echo   ^</AddIn^>
echo ^</RevitAddIns^>
) > "%ADDIN_DIR%\QbiqRenderDispatcher.addin"

echo.
echo ================================================================
echo  Instalacion completada.
echo  Abrí Revit y buscá la tab "Render Server".
echo ================================================================
echo.
pause
