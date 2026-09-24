@echo off
rem Build (Release) and launch MagicCircle.
cd /d "%~dp0"
dotnet build MagicCircle.csproj -c Release --nologo || goto :failed
start "" "bin\Release\net6.0-windows\MagicCircle.exe"
exit /b 0

:failed
echo.
echo Build failed.
pause
exit /b 1
