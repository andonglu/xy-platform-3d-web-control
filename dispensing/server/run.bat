@echo off
cd /d "%~dp0"
set DOTNET_ROOT=%ProgramFiles%\dotnet
set PATH=%DOTNET_ROOT%;%PATH%
dotnet run --project DispenseServer\DispenseServer.csproj -c Release
