@echo off
chcp 65001 >nul
rem Двойной клик — сборка релиза в dist\ (подробности и параметры — в build.ps1)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
if errorlevel 1 (
  echo.
  echo Ошибка сборки. Нужен .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0
  pause
  exit /b 1
)
explorer "%~dp0dist"
