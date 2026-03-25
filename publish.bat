@echo off
echo ============================================
echo   EleFEL - Build Single-File EXE
echo ============================================
echo.

cd /d "%~dp0"

echo Limpiando build anterior...
dotnet clean EleFEL.App\EleFEL.App.csproj -c Release >nul 2>&1

echo Publicando EleFEL...
dotnet publish EleFEL.App\EleFEL.App.csproj -c Release -r win-x86 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo ERROR: La publicacion fallo. Revise los errores arriba.
    pause
    exit /b 1
)

echo.
echo ============================================
echo   BUILD EXITOSO!
echo ============================================
echo.
echo El archivo ejecutable esta en:
echo   %~dp0publish\EleFEL.App.exe
echo.
echo Copie la carpeta "publish" completa al
echo computador del cliente.
echo.
pause
