@echo off
setlocal enabledelayedexpansion

set PROJECT=VideoFetch\VideoFetch.csproj
set RID=win-x64
set CONFIG=Release
set OUTPUT_BASE=publish-output
set VERSION=1.4.0

echo ============================================================
echo   VideoFetch 发布脚本
echo ============================================================
echo.

:: ── 清理旧的发布产物 ─────────────────────────────────────────────
if exist "%OUTPUT_BASE%" (
    echo 正在清理旧发布文件...
    rmdir /s /q "%OUTPUT_BASE%"
    if exist VideoFetch-framework-dependent-*.zip del /q VideoFetch-framework-dependent-*.zip
    if exist VideoFetch-self-contained-*.zip      del /q VideoFetch-self-contained-*.zip
    echo 已清理
    echo.
)

:: ── 1. 依赖框架版本（不包含 .NET 8 运行时，体积小）──────────────────
set OUT_FD=%OUTPUT_BASE%\framework-dependent
echo [1/4] 正在发布「依赖框架」版本（不含 .NET 8 运行时）...
echo       输出目录: %OUT_FD%
echo.

dotnet publish %PROJECT% -c %CONFIG% -r %RID% --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -o %OUT_FD%

if %errorlevel% neq 0 (
    echo [错误] 依赖框架版本发布失败！
    goto :end
)
echo [完成] 依赖框架版本已生成
echo.

:: ── 2. 自包含版本（包含 .NET 8 运行时，可直接运行）──────────────────
set OUT_SC=%OUTPUT_BASE%\self-contained
echo [2/4] 正在发布「自包含」版本（含 .NET 8 运行时）...
echo       输出目录: %OUT_SC%
echo.

dotnet publish %PROJECT% -c %CONFIG% -r %RID% --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o %OUT_SC%

if %errorlevel% neq 0 (
    echo [错误] 自包含版本发布失败！
    goto :end
)
echo [完成] 自包含版本已生成
echo.

:: ── 3. 压缩依赖框架版本 ──────────────────────────────────────────
set ZIP_FD=VideoFetch-framework-dependent-v%VERSION%-%RID%.zip
echo [3/4] 正在压缩依赖框架版本 → %ZIP_FD% ...
powershell -NoProfile -Command "Compress-Archive -Path '%OUT_FD%\*' -DestinationPath '%ZIP_FD%' -CompressionLevel Optimal -Force"

if %errorlevel% neq 0 (
    echo [错误] 压缩依赖框架版本失败！
    goto :end
)
echo [完成] %ZIP_FD%
echo.

:: ── 4. 压缩自包含版本 ────────────────────────────────────────────
set ZIP_SC=VideoFetch-self-contained-v%VERSION%-%RID%.zip
echo [4/4] 正在压缩自包含版本 → %ZIP_SC% ...
powershell -NoProfile -Command "Compress-Archive -Path '%OUT_SC%\*' -DestinationPath '%ZIP_SC%' -CompressionLevel Optimal -Force"

if %errorlevel% neq 0 (
    echo [错误] 压缩自包含版本失败！
    goto :end
)
echo [完成] %ZIP_SC%
echo.

:: ── 汇总 ────────────────────────────────────────────────────────────
echo ============================================================
echo   全部完成！
echo.
echo   发布目录:
echo     %OUTPUT_BASE%\framework-dependent\
echo     %OUTPUT_BASE%\self-contained\
echo.
echo   压缩包 (可直接分享):
echo     %ZIP_FD%
echo     %ZIP_SC%
echo ============================================================

:end
echo.
pause
endlocal
