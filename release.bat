@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion

rem ============================================================
rem  VideoFetch 一键发版
rem
rem  用法:
rem    release.bat           在上一个 v* 标签基础上 minor +1（v1.5 -> v1.6）
rem    release.bat 1.8       指定版本（等价于 v1.8）
rem    release.bat 1.8.2     三段式版本也可以
rem    release.bat --dry-run 只显示将要打的版本，不改文件/不打标签/不推送
rem
rem  做的事: 检查工作区干净 -> 同步 csproj 版本号并提交 -> 打标签 -> 推送
rem  推送后 GitHub Actions（.github/workflows/release.yml）会自动
rem  打包 framework-dependent / self-contained 两个单文件 exe 并发布 Release。
rem ============================================================

pushd "%~dp0"

set "DRYRUN="
if /i "%~1"=="--dry-run" set "DRYRUN=1"
set "NEWVER="
if not defined DRYRUN set "NEWVER=%~1"
if not "%NEWVER%"=="" goto have_version

set "LAST="
for /f "delims=" %%t in ('git tag --list "v*" --sort=-v:refname') do (
    if not defined LAST set "LAST=%%t"
)
if not defined LAST (
    echo [错误] 没有找到任何 v* 标签，请先指定版本，例如: release.bat 1.5
    goto end
)

set "V=!LAST:v=!"
for /f "tokens=1,2 delims=." %%a in ("!V!") do (
    set /a MAJOR=%%a
    set /a MINOR=%%b+1
)
set "NEWVER=!MAJOR!.!MINOR!"
echo 上一个标签: !LAST!  ^-^>  新版本: v!NEWVER!

:have_version
set "TAG=v%NEWVER%"

rem 已存在同名标签则拒绝，避免覆盖
git rev-parse -q --verify "refs/tags/%TAG%" >nul && (
    echo [错误] 标签 %TAG% 已存在
    goto end
)

rem 工作区必须干净，否则打出来的包和源码对不上
git diff --quiet
if errorlevel 1 (
    echo [错误] 有未提交的改动，请先 commit
    goto end
)
git diff --cached --quiet
if errorlevel 1 (
    echo [错误] 有已暂存未提交的改动，请先 commit
    goto end
)

rem --dry-run：只显示计划，不改文件、不打标签、不推送
if defined DRYRUN (
    set "CURVER=?"
    for /f "delims=" %%v in ('findstr /c:"<Version>" "VideoFetch\VideoFetch.csproj"') do set "VERLINE=%%v"
    set "CURVER=!VERLINE:*<Version>=!"
    set "CURVER=!CURVER:</Version>=!"
    for /f "tokens=1 delims= " %%v in ("!CURVER!") do set "CURVER=%%v"
    echo.
    echo [dry-run] 不会修改任何文件、不会打标签、不会推送
    echo   csproj 当前版本 : !CURVER!
    echo   目标标签        : %TAG%
    echo   版本号将同步为  : %NEWVER%
    goto end
)

echo.
echo 同步 csproj 版本号为 %NEWVER% ...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\bump-version.ps1" %NEWVER%
if errorlevel 1 (
    echo [错误] 版本号同步失败
    goto end
)

rem 版本号文件已改 → 提交，标签才能和源码对上
git diff --quiet -- VideoFetch/VideoFetch.csproj
if errorlevel 1 (
    git add VideoFetch/VideoFetch.csproj
    git commit -q -m "chore(release): 版本号 -> %TAG%"
    if errorlevel 1 (
        echo [错误] 版本号提交失败
        goto end
    )
    echo 已提交版本号变更
)

echo.
echo 创建标签 %TAG% ...
git tag -a "%TAG%" -m "release %TAG%"
if errorlevel 1 goto end

echo 推送提交和标签到 origin ...
git push origin HEAD
if errorlevel 1 (
    echo [错误] 推送提交失败，检查网络/代理: git config http.proxy
    goto end
)
git push origin "%TAG%"
if errorlevel 1 (
    echo [错误] 推送标签失败，检查网络/代理: git config http.proxy
    goto end
)

echo.
echo ============================================================
echo  已推送 %TAG%
echo  Actions: https://github.com/handloong/VideoFetch/actions
echo  产物: VideoFetch-*-%TAG%-win-x64.zip
echo ============================================================

:end
echo.
popd
if "%~1"=="" pause
endlocal
