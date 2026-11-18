@echo off
setlocal
title ddc.exe / new-ddc.exe build
cd /d "%~dp0"

echo [1/2] Locating csc.exe ...
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo [ERROR] csc.exe not found. Install .NET Framework 4.x.
  exit /b 1
)
echo     Using: %CSC%

echo [2/2] Compiling ...
"%CSC%" /nologo /target:exe /platform:anycpu /optimize+ /codepage:65001 /out:ddc.exe ddc.cs
if errorlevel 1 exit /b 1
"%CSC%" /nologo /target:exe /platform:anycpu /optimize+ /codepage:65001 /out:new-ddc.exe new-ddc.cs
if errorlevel 1 exit /b 1
copy /y new-ddc.exe new_ddc.exe >nul

:: aoc-switch 必须 x86（DDCHelperLib.dll 为 32 位），并嵌入 icon.ico
set "CSC32=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC32%" set "CSC32=%CSC%"
if exist icon.ico (
  "%CSC32%" /nologo /target:winexe /platform:x86 /optimize+ /codepage:65001 /win32icon:icon.ico /out:aoc-switch.exe aoc-switch.cs
) else (
  "%CSC32%" /nologo /target:winexe /platform:x86 /optimize+ /codepage:65001 /out:aoc-switch.exe aoc-switch.cs
)
if errorlevel 1 exit /b 1

echo.
echo [OK] built: ddc.exe, new-ddc.exe (alias new_ddc.exe), aoc-switch.exe
echo      ddc.exe test
echo      new-ddc.exe help
echo      aoc-switch.exe diag   (DDCHelperLib.dll 需与 aoc-switch.exe 同目录)
endlocal