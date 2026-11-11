@echo off
cd /d %~dp0MotionControl
start "" bin\Debug\net8.0\MotionControl.exe
echo 上位机已启动: ws://localhost:8520/ws
