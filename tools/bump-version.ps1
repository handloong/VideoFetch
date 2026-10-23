# 同步项目版本号（发版时由 release.bat 自动调用）
#
# 用法:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\bump-version.ps1 1.6
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\bump-version.ps1 1.6.0
#
# 只改 VideoFetch/VideoFetch.csproj 里的 <Version>（它决定 exe 的文件版本/产品版本）。
# 其它地方一律从这里取值，不再各写一份：
#   - CI：从 tag 推导后 -p:Version 覆盖
#   - publish.bat：运行时从 csproj 读取
#
# 两段式版本 1.6 会补成 1.6.0，与 csproj 既有写法一致。
# 除版本号那一行外，文件内容（BOM、换行）原样保留。

param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Version
)

$ErrorActionPreference = 'Stop'

$clean = ($Version.Trim() -replace '^[vV]', '')
if ($clean -notmatch '^\d+(\.\d+){1,3}$') {
    Write-Host "[错误] 版本号格式不对: '$Version'（应形如 1.6 或 1.6.2）"
    exit 1
}

$parts = $clean.Split('.')
while ($parts.Count -lt 3) { $parts += '0' }
$full = ($parts -join '.')

$csproj = Join-Path (Split-Path -Parent $PSScriptRoot) 'VideoFetch\VideoFetch.csproj'
if (-not (Test-Path $csproj)) {
    Write-Host "[错误] 找不到 $csproj"
    exit 1
}

$bytes = [System.IO.File]::ReadAllBytes($csproj)
$hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
$text = [System.IO.File]::ReadAllText($csproj, [System.Text.Encoding]::UTF8)

if ($text -notmatch '<Version>[^<]*</Version>') {
    Write-Host "[错误] csproj 里没有 <Version> 节点"
    exit 1
}

$updated = [regex]::Replace($text, '<Version>[^<]*</Version>', "<Version>$full</Version>")
if ($updated -ceq $text) {
    Write-Host "版本号已是 $full，无需修改"
    exit 0
}

$encoding = if ($hasBom) {
    New-Object System.Text.UTF8Encoding($true)
} else {
    New-Object System.Text.UTF8Encoding($false)
}
[System.IO.File]::WriteAllText($csproj, $updated, $encoding)

Write-Host "版本号 -> $full"
exit 0
