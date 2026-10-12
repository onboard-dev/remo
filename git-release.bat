@echo off
setlocal EnableDelayedExpansion
cd /d "%~dp0"
rem ============================================================
rem  Remo: タグを作成して GitHub Releases に exe を登録
rem   前提: src\release.bat でビルド済み (releases\Remo_v番号.exe がある)
rem         git-push.bat でソースをプッシュ済み
rem   GitHub CLI (gh) を使用します。初回は gh auth login が必要です。
rem ============================================================
set "REPO=onboard-dev/remo"

where gh >nul 2>&1
if errorlevel 1 (
  echo GitHub CLI ^(gh^) が見つかりません。次のコマンドでインストールしてください。
  echo   winget install GitHub.cli
  echo インストール後、コマンドプロンプトを開き直してから再実行してください。
  goto :end
)
gh auth status >nul 2>&1
if errorlevel 1 (
  echo GitHub にログインします。画面の指示に従ってください。
  gh auth login -h github.com -p https -w
  if errorlevel 1 goto :end
)

set "VER=1.0.0"
if exist src\version.txt set /p VER=<src\version.txt
:ask
set "IN="
set /p "IN=リリースするバージョン (Enterで v%VER%): "
if "!IN!"=="" set "IN=%VER%"
if /i "!IN:~0,1!"=="v" set "IN=!IN:~1!"
>"%TEMP%\remo_ver.txt" echo(!IN!
findstr /r /x "[0-9][0-9]*\.[0-9][0-9]*\.[0-9][0-9]*" "%TEMP%\remo_ver.txt" >nul
if errorlevel 1 (
  echo 「1.2.3」のように 数字.数字.数字 で入力してください。
  goto :ask
)
set "TAG=v!IN!"
set "ASSET=releases\Remo_!TAG!.exe"

if not exist "!ASSET!" (
  echo [中止] !ASSET! がありません。先に src\release.bat でビルドしてください。
  goto :end
)

set "DIRTY="
for /f "delims=" %%a in ('git status --porcelain') do set "DIRTY=1"
if defined DIRTY (
  echo [中止] コミットしていない変更があります。先に git-push.bat を実行してください。
  goto :end
)

git rev-parse -q --verify "refs/tags/!TAG!" >nul 2>&1
if not errorlevel 1 (
  echo [中止] タグ !TAG! は既にあります。別のバージョン番号を指定してください。
  goto :end
)

echo.
echo   タグ      : !TAG!
echo   登録ファイル: !ASSET!
echo.
set "OK="
set /p "OK=この内容でリリースしますか? (Y/N): "
if /i not "!OK!"=="Y" goto :end

git push -q origin main
git tag -a "!TAG!" -m "Remo !TAG!"
if errorlevel 1 goto :end
git push origin "!TAG!"
if errorlevel 1 (
  echo [失敗] タグをプッシュできませんでした。
  goto :end
)

gh release create "!TAG!" "!ASSET!" --repo %REPO% --title "Remo !TAG!" --generate-notes
if errorlevel 1 (
  echo [失敗] リリースを作成できませんでした。タグは作成済みです。
  goto :end
)
echo.
echo 完了しました: https://github.com/%REPO%/releases/tag/!TAG!

:end
echo.
pause
