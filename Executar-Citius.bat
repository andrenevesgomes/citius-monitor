@echo off
rem ============================================================
rem  Citius Monitor (.NET) - one-click launcher for end users.
rem  Runs the single-file executable, asks for dates, opens Excel.
rem  (User-facing messages are in Portuguese on purpose.)
rem ============================================================
setlocal
cd /d "%~dp0"

if exist "publish\Citius.exe" (
    "publish\Citius.exe" --open
) else (
    echo O executavel ainda nao foi criado.
    echo Peca ao programador para correr:  dotnet publish -c Release -o publish
    pause
    exit /b 1
)

echo.
pause
