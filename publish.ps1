<#
.SYNOPSIS
    AI-Tool-Box publish script (self-contained).

.DESCRIPTION
    Self-contained publish (carries .NET runtime, no install needed).
    Default: loose/folder layout (fast cold start). Add -SingleFile for one-exe mode.
      - EnableMsixTooling=true   : generates embedded resources.pri (required by WinAppSDK)
      - WindowsAppSDKSelfContained=true
      - app.manifest requireAdministrator retained: UAC elevation on launch (shield icon)

.PARAMETER Clean
    dotnet clean before publish (full rebuild).
.PARAMETER SingleFile
    Publish as a single exe (bundle all deps; slower cold start). Default: loose folder.
.PARAMETER Configuration
    Release (default).
.PARAMETER Platform
    x64 (fixed).
.PARAMETER Icon
    App icon path.

.EXAMPLE
    .\publish.ps1                       # loose folder publish (fast cold start)
    .\publish.ps1 -SingleFile           # single exe publish
    .\publish.ps1 -Clean -SingleFile    # clean + single exe
#>
[CmdletBinding()]
param(
    [switch]$Clean,
    [switch]$SingleFile,
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [ValidateSet('x64')]
    [string]$Platform = 'x64',
    [string]$Icon = 'e:\ITCH\Project\Intergrated AI Codebot\assets\H+_logo-transparent.ico'
)

$ErrorActionPreference = 'Stop'

# --- Paths ---
$repoRoot    = 'e:\ITCH\Project\Intergrated AI Codebot'
$project     = Join-Path $repoRoot 'AI-Tool-Box\src\AI-Tool-Box\AI-Tool-Box.csproj'
$dotnet      = Join-Path $repoRoot '.dotnet\dotnet.exe'
$sdkSdks     = Join-Path $repoRoot '.dotnet\sdk\8.0.424\Sdks'

# --- Environment (local private .NET SDK) ---
$env:DOTNET_ROOT     = Join-Path $repoRoot '.dotnet'
$env:PATH            = "$env:DOTNET_ROOT;$env:PATH"
$env:MSBuildSDKsPath = $sdkSdks
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO    = '1'

chcp 65001 | Out-Null
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " AI-Tool-Box self-contained publish"
Write-Host (" Mode   : {0}" -f $(if ($SingleFile) { 'SINGLE FILE (one exe)' } else { 'LOOSE FOLDER (fast cold start)' }))
Write-Host " Config : $Configuration | RID: win-$Platform"
Write-Host "========================================" -ForegroundColor Cyan

$start = Get-Date

if ($Clean) {
    Write-Host "[0/3] clean..." -ForegroundColor Yellow
    & $dotnet clean $project -c $Configuration -p:Platform=$Platform -nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Host "[FAIL] clean failed" -ForegroundColor Red; exit $LASTEXITCODE }
}

# --- Shared publish args ---
$commonArgs = @(
    $project,
    '-c', $Configuration,
    "-p:Platform=$Platform",
    '-r', "win-$Platform",
    '--self-contained', 'true',
    '-p:EnableMsixTooling=true',
    '-p:WindowsAppSDKSelfContained=true',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    "-p:ApplicationIcon=$Icon",
    '-nologo',
    '-v', 'minimal'
)

Write-Host "[1/3] publish..." -ForegroundColor Yellow
if ($SingleFile) {
    & $dotnet publish @commonArgs `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:IncludeAllContentForSelfExtract=true
}
else {
    & $dotnet publish @commonArgs `
        -p:PublishSingleFile=false
}

$exit = $LASTEXITCODE
if ($exit -ne 0) {
    Write-Host "[FAIL] publish failed (exit $exit)" -ForegroundColor Red
    exit $exit
}

# --- Locate publish output ---
# 单文件与散装模式均输出到 <rid>\publish\（<rid>\ 根目录是 MSBuild Build 的常规产物，勿混淆）
$ridRoot = Join-Path $repoRoot "AI-Tool-Box\src\AI-Tool-Box\bin\$Platform\$Configuration\net8.0-windows10.0.19041.0\win-$Platform"
$publishDir = Join-Path $ridRoot 'publish'
$exe = Join-Path $publishDir 'AI-Tool-Box.exe'

Write-Host "[2/3] verify output..." -ForegroundColor Yellow
if (-not (Test-Path $exe)) {
    Write-Host "[FAIL] exe not found: $exe" -ForegroundColor Red
    exit 1
}

# --- Trim satellite language packs (keep zh-Hans/zh-Hant/zh-CN/zh-TW/en/en-us/en-GB only) ---
# These are WinAppSDK MUI resources (Microsoft.ui.xaml.dll.mui), not controlled by SatelliteResourceLanguages.
Write-Host "[2.5/3] trim language packs..." -ForegroundColor Yellow
$langKeep = @('en','en-us','en-GB','en-US','zh','zh-Hans','zh-Hant','zh-CN','zh-TW')
$langDirs = @(Get-ChildItem $publishDir -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^[a-z]{2}(-[A-Za-z]+)?$' -and $langKeep -notcontains $_.Name })
$langRemoved = 0
foreach ($d in $langDirs) {
    try { Remove-Item $d.FullName -Recurse -Force -ErrorAction Stop; $langRemoved++ } catch { }
}

$exeMB = [Math]::Round((Get-Item $exe).Length / 1MB, 1)
$totalMB = [Math]::Round(((Get-ChildItem $publishDir -Recurse -File | Measure-Object Length -Sum).Sum) / 1MB, 1)
$files = Get-ChildItem $publishDir -File | Select-Object -ExpandProperty Name

Write-Host "[3/3] done" -ForegroundColor Green
Write-Host "----------------------------------------" -ForegroundColor Cyan
Write-Host " Output dir : $publishDir"
Write-Host " Main exe   : AI-Tool-Box.exe ($exeMB MB)"
Write-Host " Total size : $totalMB MB | File count : $($files.Count)"
Write-Host (" Lang packs removed : {0} (kept zh/en only)" -f $langRemoved)
if (-not $SingleFile) {
    Write-Host " Top-level files:"
    $files | Select-Object -First 30 | ForEach-Object { Write-Host "    - $_" -ForegroundColor DarkGray }
}
Write-Host "----------------------------------------" -ForegroundColor Cyan
Write-Host (" Elapsed    : {0:mm\:ss}" -f ((Get-Date) - $start))
Write-Host "[OK] Publish succeeded. Double-click AI-Tool-Box.exe -> runs as Administrator (UAC)." -ForegroundColor Green

exit 0
