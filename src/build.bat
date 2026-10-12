@echo off
rem 番号を変えずにビルドだけ行います (バージョン番号付きは release.bat)
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
"%CSC%" /nologo /optimize+ /target:winexe /win32icon:app.ico /out:Remo.exe /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Numerics.dll /r:System.Security.dll *.cs
pause
