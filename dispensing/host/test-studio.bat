@echo off
cd /d "%~dp0"
set "DOTNET_ROOT=%ProgramFiles%\dotnet"
set "PATH=%DOTNET_ROOT%;%PATH%"
set "DLL=%~dp0DispenseStudio\bin\Release\net8.0-windows\DispenseStudio.dll"

if not exist "%DLL%" (
    dotnet build DispenseStudio\DispenseStudio.csproj -c Release
    if %errorlevel% neq 0 goto :failed
)

echo Running planar pattern self-test (circle / rectangle / combined) ...
echo.
dotnet "%DLL%" --selftest %*
echo.
echo Exit code: %errorlevel%  (0 = all passed)
pause
exit /b %errorlevel%

:failed
echo Build FAILED.
pause
exit /b 1
