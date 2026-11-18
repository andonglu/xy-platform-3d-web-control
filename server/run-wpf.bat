@echo off
setlocal
if not defined ProgramFiles set "ProgramFiles=C:\Program Files"
if not defined ProgramFiles(x86) set "ProgramFiles(x86)=C:\Program Files (x86)"
cd /d %~dp0
if not exist "MotionControl.Wpf\obj\build-temp" mkdir "MotionControl.Wpf\obj\build-temp"
if not exist "MotionControl.Wpf\obj\nuget-scratch" mkdir "MotionControl.Wpf\obj\nuget-scratch"
set "TEMP=%~dp0MotionControl.Wpf\obj\build-temp"
set "TMP=%TEMP%"
set "NUGET_SCRATCH=%~dp0MotionControl.Wpf\obj\nuget-scratch"
"C:\Program Files\dotnet\dotnet.exe" build MotionControl.Wpf\MotionControl.Wpf.csproj --nologo --disable-build-servers
if errorlevel 1 (
  echo WPF 上位机编译失败。
  pause
  exit /b 1
)
start "激光设备上位机" /D "%~dp0MotionControl.Wpf\bin\Debug\net8.0-windows" "%~dp0MotionControl.Wpf\bin\Debug\net8.0-windows\MotionControl.Wpf.exe"
