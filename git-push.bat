@echo off
setlocal EnableDelayedExpansion
cd /d "%~dp0"
rem ============================================================
rem  Remo: 個人情報チェック付きで GitHub へコミット＆プッシュ
rem   ・.gitignore で exe / ログ / 接続先データ / 書き出しファイルを除外
rem   ・privacy-patterns.txt と private-words.txt の語句が含まれていたら中止
rem   ・コミット者のメールは GitHub の noreply アドレスを使用
rem ============================================================
set "REMOTE=https://github.com/onboard-dev/remo.git"
set "GHUSER=onboard-dev"

where git >nul 2>&1
if errorlevel 1 (
  echo Git が見つかりません。Git for Windows をインストールしてください。
  echo   winget install Git.Git
  goto :end
)

if not exist ".git" (
  echo リポジトリを初期化します...
  git init -q -b main
)
git remote get-url origin >nul 2>&1
if errorlevel 1 git remote add origin %REMOTE%

rem --- コミット者情報: 個人のメールアドレスを履歴に残さない ---
set "LE="
for /f "delims=" %%e in ('git config --local user.email 2^>nul') do set "LE=%%e"
if not defined LE (
  git config --local user.name "%GHUSER%"
  git config --local user.email "%GHUSER%@users.noreply.github.com"
)

git add -A
if errorlevel 1 goto :end

rem --- 1. 除外すべきファイルが紛れていないか ---
set "LIST=%TEMP%\remo_files_%RANDOM%.txt"
git diff --cached --name-only > "%LIST%"
findstr /i /r "\.exe$ \.remo$ remo\.dat \.log$ remo-log\.txt$ remo-capture\.bin$ \.bak$ private-words\.txt$" "%LIST%"
if not errorlevel 1 (
  echo.
  echo [中止] コミットしてはいけないファイルが含まれています。上のファイルを確認してください。
  git reset -q
  del "%LIST%" >nul 2>&1
  goto :end
)
del "%LIST%" >nul 2>&1

rem --- 2. 個人情報らしき文字列が含まれていないか ---
set "PAT=%TEMP%\remo_pat_%RANDOM%.txt"
copy /y /b privacy-patterns.txt "%PAT%" >nul
if exist private-words.txt type private-words.txt >> "%PAT%"
set "HIT=%TEMP%\remo_hit_%RANDOM%.txt"
git grep --cached -I -n -i -E -f "%PAT%" -- . ":(exclude)privacy-patterns.txt" > "%HIT%"
rem SSHの方式名 (xxx@openssh.com など) はメールアドレスではないので除外
findstr /v /i /c:"@openssh.com" /c:"@libssh.org" "%HIT%" > "%HIT%.2"
set "FOUND="
for %%A in ("%HIT%.2") do if %%~zA gtr 0 set "FOUND=1"
if defined FOUND (
  type "%HIT%.2"
  echo.
  echo [中止] 個人情報の可能性がある文字列が見つかりました。上の箇所を修正してから再実行してください。
  echo        問題ない語句であれば privacy-patterns.txt / private-words.txt を見直してください。
  git reset -q
  del "%PAT%" "%HIT%" "%HIT%.2" >nul 2>&1
  goto :end
)
del "%HIT%" "%HIT%.2" >nul 2>&1
del "%PAT%" >nul 2>&1
echo 個人情報チェック: OK

rem --- 3. コミット ---
git diff --cached --quiet
if errorlevel 1 (
  echo.
  echo 変更されるファイル:
  git status -s
  echo.
  set "VER="
  if exist src\version.txt set /p VER=<src\version.txt
  set "MSG="
  set /p "MSG=コミットメッセージを入力 (Enterで Update v!VER!): "
  if "!MSG!"=="" set "MSG=Update v!VER!"
  git commit -q -m "!MSG!"
  if errorlevel 1 goto :end
  echo コミットしました。
) else (
  echo 新しい変更はありません。
)

rem --- 4. プッシュ ---
echo.
echo GitHub へプッシュしています...
git push -u origin main
if errorlevel 1 (
  echo リモート側の変更を取り込んで再試行します...
  git fetch origin
  git merge --allow-unrelated-histories -X ours -m "Merge remote main" origin/main
  git push -u origin main
  if errorlevel 1 (
    echo [失敗] プッシュできませんでした。上のメッセージを確認してください。
    goto :end
  )
)
echo.
echo 完了しました: https://github.com/onboard-dev/remo

:end
echo.
pause
