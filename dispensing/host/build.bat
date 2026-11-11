@echo off
cd /d "%~dp0"
set "DOTNET_ROOT=%ProgramFiles%\dotnet"
set "PATH=%DOTNET_ROOT%;%PATH%"

echo [1/2] Building DispenseHost.Core + DispenseHost (CLI) ...
dotnet build DispenseHost\DispenseHost.csproj -c Release
if %errorlevel% neq 0 goto :failed

echo [2/2] Building DispenseStudio (WinForms GUI) ...
dotnet build DispenseStudio\DispenseStudio.csproj -c Release
if %errorlevel% neq 0 goto :failed

echo.
echo Build OK.
echo   GUI        : run-studio.bat
echo   Self test  : test-studio.bat
echo   CLI host   : run.bat
exit /b 0

:failed
echo Build FAILED.
pause
exit /b 1
