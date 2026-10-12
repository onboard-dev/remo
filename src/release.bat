@echo off
setlocal EnableDelayedExpansion
cd /d "%~dp0"
rem ============================================================
rem  Remo バージョン番号付きビルド
rem   実行すると新しいバージョン番号を聞かれます。
rem   何も入力せず Enter → 最後の番号が1つ上がります (例 1.0.0 → 1.0.1)
rem   出力: ..\Remo.exe と ..\releases\Remo_v番号.exe
rem ============================================================

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo C#コンパイラ^(csc.exe^)が見つかりません。
  goto :fail
)

set "VER=1.0.0"
if exist version.txt set /p VER=<version.txt
set MA=1& set MI=0& set PA=0
for /f "tokens=1-3 delims=. " %%a in ("%VER%") do (
  set /a MA=%%a
  set /a MI=%%b
  set /a PA=%%c
)
set "OLD=!MA!.!MI!.!PA!"

set /a NP=PA+1
set "NEXT=!MA!.!MI!.!NP!"

echo.
echo   現在のバージョン: v%OLD%
echo.
:ask
set "IN="
set /p "IN=  新しいバージョン番号を入力 (Enterで v%NEXT%): "
if "!IN!"=="" set "IN=%NEXT%"
if /i "!IN:~0,1!"=="v" set "IN=!IN:~1!"
>"%TEMP%\remo_ver.txt" echo(!IN!
findstr /r /x "[0-9][0-9]*\.[0-9][0-9]*\.[0-9][0-9]*" "%TEMP%\remo_ver.txt" >nul
if errorlevel 1 (
  echo   「1.2.3」のように 数字.数字.数字 で入力してください。
  goto :ask
)
for /f "tokens=1-3 delims=." %%a in ("!IN!") do (
  set /a MA=%%a
  set /a MI=%%b
  set /a PA=%%c
)
set "NEW=!MA!.!MI!.!PA!"

echo.
echo   Remo v%OLD%  →  v%NEW%
echo.

if exist Version.cs copy /y Version.cs Version.cs.bak >nul
> Version.cs echo // Remo - simple SSH client for Windows
>>Version.cs echo // Copyright (C) 2026 onboard-dev
>>Version.cs echo // SPDX-License-Identifier: GPL-3.0-or-later
>>Version.cs echo using System.Reflection;
>>Version.cs echo [assembly: AssemblyTitle("Remo")]
>>Version.cs echo [assembly: AssemblyProduct("Remo SSH Client")]
>>Version.cs echo [assembly: AssemblyDescription("Simple SSH client")]
>>Version.cs echo [assembly: AssemblyCopyright("Copyright (C) 2026 onboard-dev / GPL-3.0")]
>>Version.cs echo [assembly: AssemblyVersion("%NEW%.0")]
>>Version.cs echo [assembly: AssemblyFileVersion("%NEW%.0")]
>>Version.cs echo [assembly: AssemblyInformationalVersion("%NEW%")]

set "TMPEXE=%TEMP%\Remo_build_%RANDOM%.exe"
echo ビルド中...
"%CSC%" /nologo /optimize+ /target:winexe /win32icon:app.ico /out:"%TMPEXE%" /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Numerics.dll /r:System.Security.dll *.cs
if errorlevel 1 (
  echo.
  echo ビルドに失敗しました。バージョン番号は変更していません。
  goto :restore
)

copy /y "%TMPEXE%" "..\Remo.exe" >nul
if errorlevel 1 (
  echo.
  echo ..\Remo.exe を上書きできませんでした。Remo を終了してから、もう一度実行してください。
  del "%TMPEXE%" >nul 2>&1
  goto :restore
)
if not exist "..\releases" mkdir "..\releases"
copy /y "%TMPEXE%" "..\releases\Remo_v%NEW%.exe" >nul
del "%TMPEXE%" >nul 2>&1
if exist Version.cs.bak del Version.cs.bak
>version.txt echo %NEW%

echo.
echo 完了しました: Remo v%NEW%
echo   ..\Remo.exe
echo   ..\releases\Remo_v%NEW%.exe
echo.
pause
exit /b 0

:restore
if exist Version.cs.bak move /y Version.cs.bak Version.cs >nul
:fail
echo.
pause
exit /b 1
