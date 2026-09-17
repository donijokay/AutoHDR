@echo off
setlocal
cd /d "%~dp0"

echo Publishing AutoHDR (win-x64, self-contained, single-file)...
dotnet publish AutoHDR\AutoHDR.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish\win-x64

if errorlevel 1 (
  echo Publish failed.
  exit /b 1
)

echo.
echo Done. Output: %~dp0publish\win-x64\AutoHDR.exe
echo Config will be created at %%AppData%%\AutoHDR\config.json on first run.
endlocal
