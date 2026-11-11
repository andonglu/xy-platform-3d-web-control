@echo off
cd /d "%~dp0"
set DOTNET_ROOT=%ProgramFiles%\dotnet
set PATH=%DOTNET_ROOT%;%PATH%
dotnet run --project DispenseHost\DispenseHost.csproj -c Release
