@echo off
rem 本机 shell 环境缺少 ProgramFiles(x86) 会导致 dotnet CLI 的 NuGet restore 报
rem "Value cannot be null. (Parameter 'path1')" —— 这里补齐后再编译。
if not defined ProgramFiles set "ProgramFiles=C:\Program Files"
if not defined ProgramFiles(x86) set "ProgramFiles(x86)=C:\Program Files (x86)"
cd /d %~dp0MotionControl
"C:\Program Files\dotnet\dotnet.exe" build %*
