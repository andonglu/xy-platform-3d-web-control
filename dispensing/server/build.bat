@echo off
cd /d "%~dp0"
set DOTNET_ROOT=%ProgramFiles%\dotnet
set PATH=%DOTNET_ROOT%;%PATH%
dotnet build DispenseServer\DispenseServer.csproj -c Release
if %errorlevel% neq 0 (
    echo 编译失败
    pause
    exit /b 1
)
echo.
echo 编译成功。运行: run.bat
