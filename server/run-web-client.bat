@echo off
setlocal
cd /d %~dp0
dotnet run --project MotionControl\MotionControl.csproj -- --web-client %*
