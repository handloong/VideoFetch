@echo off
setlocal enabledelayedexpansion

set PROJECT=VideoFetch\VideoFetch.csproj
set RID=win-x64
set CONFIG=Release
set OUTPUT_BASE=publish-output
rem 版本号唯一来源: VideoFetch\VideoFetch.csproj 的 <Version>
set "VERSION=0.0.0"
for /f "delims=" %%v in ('findstr /c:"<Version>" "%PROJECT%"') do set "VERLINE=%%v"
if defined VERLINE (
    set "VERSION=!VERLINE:*<Version>=!"
    set "VERSION=!VERSION:</Version>=!"
    for /f "tokens=1 delims= " %%v in ("!VERSION!") do set "VERSION=%%v"
)

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

:: ── 0. 确保 ffmpeg 存在（HLS 下载重封装为 MP4 用）─────────────────
if not exist "VideoFetch\ffmpeg.exe" (
    echo [0/4] 未找到 VideoFetch\ffmpeg.exe，正在下载 ffmpeg（约 160MB，仅首次）...
    powershell -NoProfile -Command "[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12; Invoke-WebRequest -Uri 'https://github.com/BtbN/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip' -OutFile \"$env:TEMP\ffmpeg-win64.zip\""
    if exist "%TEMP%\ffmpeg-win64.zip" (
        powershell -NoProfile -Command "Expand-Archive -Path \"$env:TEMP\ffmpeg-win64.zip\" -DestinationPath \"$env:TEMP\ffmpeg-extract\" -Force; Copy-Item (Get-ChildItem \"$env:TEMP\ffmpeg-extract\" -Recurse -Filter ffmpeg.exe | Select-Object -First 1).FullName -Destination 'VideoFetch\ffmpeg.exe' -Force"
        del /q "%TEMP%\ffmpeg-win64.zip" 2>nul
        echo [完成] ffmpeg 已就位
    ) else (
        echo [警告] ffmpeg 下载失败，HLS 视频将保留 .ts 后缀
    )
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
