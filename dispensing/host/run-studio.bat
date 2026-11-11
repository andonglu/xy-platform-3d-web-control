@echo off
cd /d "%~dp0"
set "DOTNET_ROOT=%ProgramFiles%\dotnet"
set "PATH=%DOTNET_ROOT%;%PATH%"
set "EXE=%~dp0DispenseStudio\bin\Release\net8.0-windows\DispenseStudio.exe"

if not exist "%EXE%" (
    echo Building first run ...
    dotnet build DispenseStudio\DispenseStudio.csproj -c Release
    if %errorlevel% neq 0 goto :failed
)

echo Starting DispenseStudio ... (server must be running on port 8530)
start "" "%EXE%"
exit /b 0

:failed
echo Build FAILED.
pause
exit /b 1
