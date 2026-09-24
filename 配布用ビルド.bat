@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

echo MagicCircle の配布用自己完結版を作成します...
dotnet publish MagicCircle.csproj ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:PublishTrimmed=false ^
  -p:DebugType=None ^
  -p:DebugSymbols=false ^
  -o "配布用\MagicCircle-win-x64"

if errorlevel 1 goto :failed

echo 配布用 ZIP を作成します...
powershell -NoProfile -Command "Compress-Archive -Path '.\配布用\MagicCircle-win-x64\*' -DestinationPath '.\配布用\MagicCircle-win-x64.zip' -Force"
if errorlevel 1 goto :failed

echo.
echo 完了: 配布用\MagicCircle-win-x64.zip
echo この ZIP を別 PC にコピーし、展開してから MagicCircle.exe を起動してください。
pause
exit /b 0

:failed
echo.
echo 配布用ビルドまたは ZIP の作成に失敗しました。
pause
exit /b 1
