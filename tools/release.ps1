# 一键发版：同步版本号 -> 提交 -> 打标签 -> 推送
#
#   release.bat                在上一个 v* 标签基础上 minor +1（v1.5 -> v1.6）
#   release.bat 1.8            指定版本（等价于 v1.8）
#   release.bat 1.8.2          三段式版本
#   release.bat --dry-run      只显示计划，不改文件、不打标签、不推送
#   release.bat --help
#
# 推送后 GitHub Actions（.github/workflows/release.yml）会跑测试、打包两个
# 单文件 exe 并创建 Release。
#
# 为什么逻辑放在 ps1 而不是 .bat：cmd.exe 按字节读取 .bat，含中文时在 GBK
# 控制台下会解析错位（UTF-8 无 BOM 会把半行当命令执行，加 BOM 又连 @echo off
# 都认不出来，两种都实测过）。release.bat 只做一行转发。

$ErrorActionPreference = 'Continue'

$root = Split-Path -Parent $PSScriptRoot
$csproj = 'VideoFetch/VideoFetch.csproj'
$bumpScript = Join-Path $PSScriptRoot 'bump-version.ps1'

# ── git 调用封装 ──────────────────────────────────────────────
# git 习惯把进度/提示写到 stderr（"To <repo>" 之类），PS 5.1 会把它包成
# ErrorRecord 冒到错误流上，行为随调用方式而变（有时候直接把脚本打断）。
# 统一在这里捕获 2>&1 并用 Write-Host 输出，只返回退出码和文本。
function Invoke-Git {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$GitArgs)

    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'SilentlyContinue'
    try {
        $lines = @(& git @GitArgs 2>&1)
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $prev
    }

    $text = @()
    foreach ($line in $lines) {
        if ($null -eq $line) { continue }
        $s = $line.ToString()
        if ($s.Length -gt 0) { $text += $s }
    }

    return [pscustomobject]@{ Code = $code; Lines = $text }
}

function Write-Usage {
    Write-Host @'
用法:
  release.bat                自动递增：上一个 v* 标签 minor +1
  release.bat 1.8            指定版本（等价于 v1.8）
  release.bat 1.8.2          三段式版本
  release.bat --dry-run      只显示计划，不做任何修改
'@
}

function Get-CsprojVersion {
    try {
        [xml]$proj = Get-Content $csproj
        return ($proj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
    } catch {
        return $null
    }
}

# ── 参数：--dry-run / --help / 版本号（位置参数） ──
$dryRun = $false
$version = $null
$wantsHelp = $false

foreach ($arg in $args) {
    if ($arg -match '^--?dry-?run$') { $dryRun = $true }
    elseif ($arg -match '^--?h(elp)?$') { $wantsHelp = $true }
    elseif ($arg -match '^\d+(\.\d+){1,3}$') { $version = $arg }
    else {
        Write-Host "[错误] 无法识别的参数: $arg"
        Write-Usage
        exit 1
    }
}

if ($wantsHelp) {
    Write-Usage
    exit 0
}

Push-Location $root
try {
    # ── 1. 决定版本号 ──
    if (-not $version) {
        $tagResult = Invoke-Git tag --list 'v*' --sort=-v:refname
        $last = $tagResult.Lines | Select-Object -First 1
        if (-not $last) {
            Write-Host '[错误] 没有找到任何 v* 标签，请先指定版本，例如: release.bat 1.5'
            exit 1
        }

        $lastParts = ($last -replace '^[vV]', '').Split('.')
        if ($lastParts.Count -lt 2) {
            Write-Host "[错误] 上一个标签 $last 不是 major.minor 形式，无法自动递增"
            exit 1
        }

        $major = [int]$lastParts[0]
        $minor = [int]$lastParts[1] + 1
        $version = "$major.$minor"
        Write-Host "上一个标签: $last  ->  新版本: v$version"
    }

    $tag = 'v' + ($version -replace '^[vV]', '')

    # ── 2. 前置检查 ──
    if ((Invoke-Git rev-parse -q --verify "refs/tags/$tag").Code -eq 0) {
        Write-Host "[错误] 标签 $tag 已存在"
        exit 1
    }

    $status = Invoke-Git status --porcelain
    if ($status.Code -ne 0) {
        Write-Host '[错误] 无法读取 git 状态'
        exit 1
    }
    if ($status.Lines.Count -gt 0) {
        Write-Host '[错误] 有未提交的改动，请先 commit：'
        $status.Lines | ForEach-Object { Write-Host "  $_" }
        exit 1
    }

    # ── 3. dry-run：只报计划 ──
    $current = Get-CsprojVersion
    if ($dryRun) {
        Write-Host ''
        Write-Host '[dry-run] 不会修改任何文件、不会打标签、不会推送'
        Write-Host "  csproj 当前版本 : $current"
        Write-Host "  目标标签        : $tag"
        Write-Host "  版本号将同步为  : $version"
        exit 0
    }

    # ── 4. 同步版本号并提交 ──
    Write-Host ''
    Write-Host "同步 csproj 版本号为 $version ..."
    & $bumpScript $version
    if ($LASTEXITCODE -ne 0) {
        Write-Host '[错误] 版本号同步失败'
        exit 1
    }

    $diff = Invoke-Git diff --quiet -- $csproj
    if ($diff.Code -ne 0) {
        if ((Invoke-Git add $csproj).Code -ne 0) {
            Write-Host '[错误] 暂存版本号变更失败'
            exit 1
        }
        if ((Invoke-Git commit -q -m "chore(release): 版本号 -> $tag").Code -ne 0) {
            Write-Host '[错误] 提交版本号变更失败'
            exit 1
        }
        Write-Host '已提交版本号变更'
    }

    # ── 5. 打标签并推送 ──
    Write-Host ''
    Write-Host "创建标签 $tag ..."
    $tagCreate = Invoke-Git tag -a $tag -m "release $tag"
    foreach ($l in $tagCreate.Lines) { Write-Host $l }
    if ($tagCreate.Code -ne 0) {
        Write-Host '[错误] 创建标签失败'
        exit 1
    }

    Write-Host '推送提交和标签到 origin ...'
    $pushBranch = Invoke-Git push origin HEAD
    foreach ($l in $pushBranch.Lines) { Write-Host $l }
    if ($pushBranch.Code -ne 0) {
        Write-Host '[错误] 推送提交失败，检查网络/代理: git config http.proxy'
        exit 1
    }

    $pushTag = Invoke-Git push origin $tag
    foreach ($l in $pushTag.Lines) { Write-Host $l }
    if ($pushTag.Code -ne 0) {
        Write-Host '[错误] 推送标签失败，检查网络/代理: git config http.proxy'
        exit 1
    }

    Write-Host ''
    Write-Host '============================================================'
    Write-Host " 已推送 $tag"
    Write-Host ' Actions: https://github.com/handloong/VideoFetch/actions'
    Write-Host " 产物: VideoFetch-*-$tag-win-x64.zip"
    Write-Host '============================================================'
    exit 0
}
finally {
    Pop-Location
}
