<#
.SYNOPSIS
    AI-Tool-Box 打包发布脚本（Zip + Bandizip 自解压 exe）。

.DESCRIPTION
    1) 发布（默认：自包含散装，输出到 ...\win-x64\publish）；
    2) 复制到干净的暂存目录，剔除本机私有文件（logs、WebView2 缓存、pdb、运行期配置等）；
    3) 调用 Bandizip 命令行生成 Zip 与自解压安装程序（Setup.exe）。

.PARAMETER Version
    版本号，用于产物命名（默认 1.0.0）。

.PARAMETER SourceDir
    直接指定待打包目录，跳过 publish。留空则使用 publish 输出。
    例：-SourceDir "AI-Tool-Box\src\AI-Tool-Box\bin\x64\Release\net8.0-windows10.0.19041.0"

.PARAMETER SkipPublish
    跳过 publish，直接使用上一次的 publish 输出。

.PARAMETER IncludeVpnConfig
    把 Assets\VPN-config.json 一并打包。该文件含本机 VPN 凭据（uid/MAC/activeCode），
    默认剔除，避免把个人账号信息发给别人。

.PARAMETER NoZip
    不生成 Zip。

.PARAMETER NoSfx
    不生成自解压 exe。

.PARAMETER CompressLevel
    Bandizip 压缩级别 0-9（默认 5）。

.EXAMPLE
    .\release.ps1
    .\release.ps1 -Version 1.0.1
    .\release.ps1 -SourceDir "AI-Tool-Box\src\AI-Tool-Box\bin\x64\Release\net8.0-windows10.0.19041.0" -IncludeVpnConfig
    .\release.ps1 -SkipPublish -NoSfx
#>
[CmdletBinding()]
param(
    [string]$Version = '1.0.0',
    [string]$SourceDir,
    [switch]$SkipPublish,
    [switch]$IncludeVpnConfig,
    [switch]$NoZip,
    [switch]$NoSfx,
    [ValidateRange(0, 9)]
    [int]$CompressLevel = 5
)

$ErrorActionPreference = 'Stop'

# ---------------- 路径 ----------------
$repoRoot   = 'e:\ITCH\Project\Intergrated AI Codebot'
$exeName    = 'AI-Tool-Box'
$ridOut     = Join-Path $repoRoot 'AI-Tool-Box\src\AI-Tool-Box\bin\x64\Release\net8.0-windows10.0.19041.0'
$publishDir = Join-Path $ridOut 'win-x64\publish'
$distDir    = Join-Path $repoRoot 'dist'
$stageDir   = Join-Path $distDir ("{0}-{1}" -f $exeName, $Version)

# Bandizip 命令行（bz.exe）
$bzCandidates = @(
    'C:\Program Files\Bandizip\bz.exe',
    'C:\Program Files (x86)\Bandizip\bz.exe'
)
$bz = $bzCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

chcp 65001 | Out-Null
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Write-Host "========================================" -ForegroundColor Cyan
Write-Host (" AI-Tool-Box 打包发布  v{0}" -f $Version)
Write-Host "========================================" -ForegroundColor Cyan

$start = Get-Date

# ---------------- 1) 发布 ----------------
if (-not $SourceDir) {
    if (-not $SkipPublish) {
        Write-Host "[1/4] 发布（自包含散装）..." -ForegroundColor Yellow
        & powershell -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'publish.ps1')
        if ($LASTEXITCODE -ne 0) {
            Write-Host "[FAIL] publish.ps1 失败" -ForegroundColor Red
            exit $LASTEXITCODE
        }
        # publish.ps1 会裁掉多余语言包，此处重新执行一次保证结果一致
    }
    else {
        Write-Host "[1/4] 跳过发布，复用已有输出" -ForegroundColor DarkGray
    }
    $source = $publishDir
}
else {
    Write-Host "[1/4] 使用指定目录作为源" -ForegroundColor DarkGray
    $source = if ([System.IO.Path]::IsPathRooted($SourceDir)) { $SourceDir } else { Join-Path $repoRoot $SourceDir }
}

if (-not (Test-Path (Join-Path $source "$exeName.exe"))) {
    Write-Host ("[FAIL] 源目录缺少 {0}.exe：{1}" -f $exeName, $source) -ForegroundColor Red
    Write-Host "       请先运行 publish.ps1，或用 -SourceDir 指定正确目录。" -ForegroundColor Yellow
    exit 1
}

# 框架依赖目录没有 .NET 运行时，发给没装 .NET 8 的电脑会启动失败
if (-not (Test-Path (Join-Path $source 'coreclr.dll'))) {
    Write-Host "[WARN] 源目录不含 .NET 运行时（coreclr.dll），属于框架依赖输出。" -ForegroundColor Yellow
    Write-Host "       仅在目标机已安装 .NET 8 Desktop Runtime 时可用；" -ForegroundColor Yellow
    Write-Host "       若要发给任何人都能直接运行，请去掉 -SourceDir 让脚本使用自包含 publish 输出。" -ForegroundColor Yellow
}

# ---------------- 2) 暂存 + 剔除私有文件 ----------------
Write-Host "[2/4] 生成干净副本..." -ForegroundColor Yellow
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

# 目录级剔除
$excludeDirs = @('logs', "$exeName.exe.WebView2", 'EBWebView', '.screenshots', 'Crashpad')
# 文件级剔除（本机日志 / 调试符号 / 运行期用户配置）
$excludeFiles = @(
    '*.pdb', '*.log', '*.binlog', '*.bak', '*.tmp', '*.user', '*.suo',
    'app_settings.json', 'ai_assist_settings.json', 'music_favorites.json',
    'novel_settings.json', 'wrongbook.json', 'usage.json', 'answer_share.json'
)
if (-not $IncludeVpnConfig) { $excludeFiles += 'VPN-config.json' }

$roboArgs = @(
    $source, $stageDir,
    '/E', '/R:1', '/W:1', '/NFL', '/NDL', '/NJH', '/NJS', '/NP',
    '/XD'
) + $excludeDirs + @('/XF') + $excludeFiles

& robocopy @roboArgs | Out-Null
$roboExit = $LASTEXITCODE
if ($roboExit -ge 8) {
    Write-Host ("[FAIL] 复制失败（robocopy exit {0}）" -f $roboExit) -ForegroundColor Red
    exit 1
}

# 统计被剔除的本机私有内容，便于确认
$dropped = @()
foreach ($d in $excludeDirs) {
    $candidate = Join-Path $source $d
    if (Test-Path $candidate) {
        $mb = [Math]::Round(((Get-ChildItem $candidate -Recurse -File -ErrorAction SilentlyContinue |
            Measure-Object Length -Sum).Sum) / 1MB, 1)
        $dropped += ("{0}\  ({1} MB)" -f $d, $mb)
    }
}
$pdbCount = @(Get-ChildItem $source -Filter '*.pdb' -File -ErrorAction SilentlyContinue).Count
if ($pdbCount -gt 0) { $dropped += ("*.pdb  x{0}" -f $pdbCount) }
if (-not $IncludeVpnConfig -and (Test-Path (Join-Path $source 'Assets\VPN-config.json'))) {
    $dropped += 'Assets\VPN-config.json  (本机 VPN 凭据)'
}

# ---------------- 3) 打包 ----------------
Write-Host "[3/4] 打包..." -ForegroundColor Yellow
if (-not $bz) {
    Write-Host "[FAIL] 未找到 Bandizip（bz.exe）。请安装 Bandizip 或用 -NoZip -NoSfx 手动打包。" -ForegroundColor Red
    exit 1
}

New-Item -ItemType Directory -Path $distDir -Force | Out-Null
$outZip = Join-Path $distDir ("{0}-{1}.zip" -f $exeName, $Version)
$outSfx = Join-Path $distDir ("{0}-{1}-Setup.exe" -f $exeName, $Version)

if (-not $NoZip) {
    if (Test-Path $outZip) { Remove-Item $outZip -Force }
    & $bz c -y -aoa -r "-l:$CompressLevel" -fmt:zip $outZip $stageDir | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $outZip)) {
        Write-Host "[FAIL] 生成 Zip 失败" -ForegroundColor Red
        exit 1
    }
}

if (-not $NoSfx) {
    if (Test-Path $outSfx) { Remove-Item $outSfx -Force }
    # Bandizip 的 -fmt:exe 即自解压格式（内嵌 bdzsfx 模块）；不可再叠加 -sfx 开关，否则参数报错。
    & $bz c -y -aoa -r "-l:$CompressLevel" -fmt:exe $outSfx $stageDir | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $outSfx)) {
        Write-Host "[FAIL] 生成自解压 exe 失败" -ForegroundColor Red
        exit 1
    }
}

# ---------------- 4) 汇总 ----------------
$stageMB = [Math]::Round(((Get-ChildItem $stageDir -Recurse -File |
    Measure-Object Length -Sum).Sum) / 1MB, 1)
$fileCount = @(Get-ChildItem $stageDir -Recurse -File).Count

Write-Host "[4/4] 完成" -ForegroundColor Green
Write-Host "----------------------------------------" -ForegroundColor Cyan
Write-Host (" 暂存目录 : {0}" -f $stageDir)
Write-Host (" 内容     : {0} 个文件 / {1} MB" -f $fileCount, $stageMB)
if (-not $NoZip) {
    Write-Host (" Zip      : {0}  ({1} MB)" -f $outZip, [Math]::Round((Get-Item $outZip).Length / 1MB, 1))
}
if (-not $NoSfx) {
    Write-Host (" 自解压   : {0}  ({1} MB)" -f $outSfx, [Math]::Round((Get-Item $outSfx).Length / 1MB, 1))
}
if ($dropped.Count -gt 0) {
    Write-Host " 已剔除本机私有内容：" -ForegroundColor DarkGray
    $dropped | ForEach-Object { Write-Host ("   - {0}" -f $_) -ForegroundColor DarkGray }
}
Write-Host "----------------------------------------" -ForegroundColor Cyan
Write-Host (" 耗时     : {0:mm\:ss}" -f ((Get-Date) - $start))
Write-Host "[OK] 发布包已生成，可分发。" -ForegroundColor Green

exit 0
