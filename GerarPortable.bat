@echo off
chcp 65001 >nul
title Compilador GVPN Portable
echo ===================================================
echo   A GERAR APLICACAO PORTABLE GVPN (SINGLE-FILE)
echo ===================================================
echo.

cd /d "%~dp0"
dotnet publish "GVPN.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./GVPN_Portable

echo.
echo ===================================================
echo   CONCLUIDO! O seu executavel portatil esta na pasta:
echo   GVPN_Portable\GVPN.exe
echo ===================================================
pause