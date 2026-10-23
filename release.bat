@echo off
rem ===========================================================================
rem  VideoFetch - one-click release
rem
rem  The git tag IS the version. Push tag v1.6 and CI builds with
rem  -p:Version=1.6, then publishes both single-file exes. No source file
rem  needs to be edited or committed for a release.
rem
rem  This file is ASCII-only on purpose: cmd.exe parses .bat by bytes, and
rem  non-ASCII text breaks parsing under a GBK code page (0x5C / 0x7C are
rem  legal GBK trail bytes, so a Chinese comment can eat the next command).
rem
rem  Usage:
rem    release.bat               auto-increment minor: v1.5 -> v1.6
rem    release.bat 1.8           explicit version
rem    release.bat 1.8.2         three-part version
rem    release.bat --dry-run     print the plan, change nothing
rem    release.bat --help
rem ===========================================================================
setlocal enabledelayedexpansion

set "NEWVER="
set "DRYRUN=0"
rem  No arguments at all usually means a double-click: keep the window open.
if "%~1"=="" set "NOARGS=1"

:parse_args
if "%~1"=="" goto args_done
if /i "%~1"=="--dry-run" (
    set "DRYRUN=1"
    shift
    goto parse_args
)
if /i "%~1"=="-n" (
    set "DRYRUN=1"
    shift
    goto parse_args
)
if /i "%~1"=="--help" goto usage
if /i "%~1"=="-h"     goto usage
if /i "%~1"=="/?"     goto usage
set "NEWVER=%~1"
shift
goto parse_args

:args_done
rem ---- must run inside a git work tree ------------------------------------
git rev-parse --is-inside-work-tree >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Not a git repository.
    exit /b 1
)

rem ---- the work tree must be clean, or the tag would not match the code ---
set "DIRTY="
for /f "delims=" %%s in ('git status --porcelain') do set "DIRTY=1"
if defined DIRTY (
    echo [ERROR] Working tree has uncommitted changes:
    git status --short
    echo         Commit or stash them first.
    exit /b 1
)

rem ---- current branch + newest existing tag ------------------------------
set "BRANCH="
for /f "delims=" %%b in ('git rev-parse --abbrev-ref HEAD') do set "BRANCH=%%b"

set "LATEST="
for /f "delims=" %%t in ('git tag --list "v*" --sort=-v:refname') do (
    if not defined LATEST set "LATEST=%%t"
)
set "LATEST_SHOW=%LATEST%"
if not defined LATEST_SHOW set "LATEST_SHOW=(none)"

rem ---- decide the version ------------------------------------------------
if not defined NEWVER goto derive_version
if /i "!NEWVER:~0,1!"=="v" set "NEWVER=!NEWVER:~1!"
goto have_version

:derive_version
if not defined LATEST goto no_prev_tag
rem  v1.5 -> 1.5 -> 1.6   (v1.5.0 also -> 1.6: minor bump drops the patch)
set "BARE=!LATEST:~1!"
for /f "tokens=1,2 delims=." %%a in ("!BARE!") do (
    set "MAJOR=%%a"
    set "MINOR=%%b"
)
if not defined MINOR set "MINOR=0"
set /a MINOR+=1
set "NEWVER=!MAJOR!.!MINOR!"
goto have_version

:no_prev_tag
set "NEWVER=1.0"

:have_version
echo(!NEWVER!| findstr /r "^[0-9][0-9]*\.[0-9]" >nul
if errorlevel 1 (
    echo [ERROR] Invalid version "%NEWVER%". Use forms like 1.6 or 1.6.1.
    exit /b 1
)
set "TAG=v!NEWVER!"

rem ---- the tag must not exist yet ----------------------------------------
git rev-parse -q --verify "refs/tags/!TAG!" >nul 2>&1
if not errorlevel 1 (
    echo [ERROR] Tag !TAG! already exists locally.
    exit /b 1
)
git ls-remote --tags --exit-code origin "refs/tags/!TAG!" >nul 2>&1
if not errorlevel 1 (
    echo [ERROR] Tag !TAG! already exists on origin.
    exit /b 1
)

echo ============================================================
echo   VideoFetch release
echo ============================================================
echo   branch   : !BRANCH!
echo   prev tag : !LATEST_SHOW!
echo   new tag  : !TAG!
echo   version  : !NEWVER!
echo              ^(CI injects this into the assembly version^)
echo ============================================================
echo.

if "%DRYRUN%"=="1" goto dry_run

echo Creating annotated tag !TAG! ...
git tag -a "!TAG!" -m "release !TAG!"
if errorlevel 1 (
    echo [ERROR] git tag failed.
    exit /b 1
)

echo Pushing branch !BRANCH! ...
git push origin HEAD
if errorlevel 1 (
    echo [ERROR] Push failed. Check your network / proxy:
    echo         git config --get http.proxy
    exit /b 1
)

echo Pushing tag !TAG! ...
git push origin "!TAG!"
if errorlevel 1 (
    echo [ERROR] Tag push failed. The local tag was kept; retry with:
    echo         git push origin !TAG!
    exit /b 1
)

echo.
echo ============================================================
echo   Done. GitHub Actions is now building !TAG!
echo   Watch it here:
echo     https://github.com/handloong/VideoFetch/actions
echo   Release (once the run finishes):
echo     https://github.com/handloong/VideoFetch/releases/tag/!TAG!
echo ============================================================
if defined NOARGS pause
exit /b 0

:dry_run
echo [dry-run] Nothing was changed, nothing was pushed.
echo           Next real run would create and push tag !TAG!.
exit /b 0

:usage
echo Usage:
echo   release.bat               auto-increment minor  ^(v1.5 -^> v1.6^)
echo   release.bat 1.8           explicit version      ^(tag v1.8^)
echo   release.bat 1.8.2         three-part version    ^(tag v1.8.2^)
echo   release.bat --dry-run     print the plan only
echo   release.bat --help        this text
echo.
echo The tag name becomes the product version: v1.8 -^> 1.8.
echo CI builds framework-dependent + self-contained single-file exes
echo and attaches both zips to the GitHub Release.
exit /b 0
