<#
.SYNOPSIS
    One-click build script for AI-Tool-Box (WinUI 3 / .NET 8).
.DESCRIPTION
    Sets up local .NET SDK + MSBuild SDKs path, configures UTF-8 console output,
    and invokes Visual Studio 2022's MSBuild.exe to build the solution.
    Output is mirrored to build.log with minimal verbosity.
.PARAMETER Clean
    Run Clean target before Build (force full rebuild).
.PARAMETER Configuration
    Build configuration: Debug or Release. Default: Debug.
.PARAMETER Platform
    Target platform: x64. Default: x64.
.EXAMPLE
    .\build.ps1
    .\build.ps1 -Clean
    .\build.ps1 -Configuration Release -Clean
#>
[CmdletBinding()]
param(
    [switch]$Clean,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [ValidateSet('x64')]
    [string]$Platform = 'x64'
)

$ErrorActionPreference = 'Stop'

# --- Paths ---
$repoRoot   = 'e:\ITCH\Project\Intergrated AI Codebot'
$solution   = Join-Path $repoRoot 'AI-Tool-Box\AI-Tool-Box.sln'
$dotnetRoot = Join-Path $repoRoot '.dotnet'
$sdkSdks    = Join-Path $dotnetRoot 'sdk\8.0.424\Sdks'
$msbuild    = 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe'
$logFile    = Join-Path $repoRoot 'build.log'
# EXE Win32 图标：通过编译指令 -p:ApplicationIcon 指定（与 csproj 内 <ApplicationIcon> 一致）
$appIcon    = Join-Path $repoRoot 'assets\H+_logo-transparent.ico'

# --- Validate prerequisites ---
if (-not (Test-Path $dotnetRoot)) {
    Write-Host "[ERROR] .NET SDK not found at: $dotnetRoot" -ForegroundColor Red
    exit 1
}
if (-not (Test-Path $sdkSdks)) {
    Write-Host "[ERROR] MSBuild SDKs path not found at: $sdkSdks" -ForegroundColor Red
    exit 1
}
if (-not (Test-Path $msbuild)) {
    Write-Host "[ERROR] VS 2022 MSBuild.exe not found at: $msbuild" -ForegroundColor Red
    exit 1
}
if (-not (Test-Path $solution)) {
    Write-Host "[ERROR] Solution not found at: $solution" -ForegroundColor Red
    exit 1
}
if (-not (Test-Path $appIcon)) {
    Write-Host "[ERROR] App icon not found at: $appIcon" -ForegroundColor Red
    exit 1
}

# --- Environment ---
$env:DOTNET_ROOT       = $dotnetRoot
$env:PATH              = "$dotnetRoot;$env:PATH"
$env:MSBuildSDKsPath   = $sdkSdks
# PowerShell 5.1 default encoding is GBK; force UTF-8 for correct Chinese output
chcp 65001 | Out-Null
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# --- Target ---
$targets = if ($Clean) { 'Clean,Build' } else { 'Build' }

# --- Build ---
Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Solution : AI-Tool-Box.sln"
Write-Host " Config   : $Configuration"
Write-Host " Platform : $Platform"
Write-Host " Target   : $targets"
Write-Host " MSBuild  : $msbuild"
Write-Host " Log      : $logFile"
Write-Host "========================================" -ForegroundColor Cyan

$startTime = Get-Date

# Step 1: Restore NuGet packages (always, ensures packages exist after Clean)
Write-Host "[1/2] Restoring NuGet packages..." -ForegroundColor Yellow
& $msbuild $solution -t:Restore -p:Platform=$Platform -p:Configuration=$Configuration -nologo -verbosity:minimal
$restoreCode = $LASTEXITCODE
if ($restoreCode -ne 0) {
    Write-Host "[FAIL] Restore failed (exit $restoreCode). See build.log." -ForegroundColor Red
    exit $restoreCode
}

# Step 2: Clean + Build（-p:ApplicationIcon 将 H+ 图标嵌入 EXE Win32 资源）
Write-Host "[2/2] Building ($targets)..." -ForegroundColor Yellow
& $msbuild $solution `
    -t:$targets `
    -p:Platform=$Platform `
    -p:Configuration=$Configuration `
    "-p:ApplicationIcon=$appIcon" `
    -nologo `
    -flp1:"logfile=$logFile;verbosity=minimal;encoding=UTF-8"

$exitCode = $LASTEXITCODE
$elapsed  = (Get-Date) - $startTime

Write-Host "----------------------------------------" -ForegroundColor Cyan
Write-Host (" Exit code : {0}" -f $exitCode)
Write-Host (" Elapsed   : {0:mm\:ss}" -f $elapsed)
Write-Host "----------------------------------------" -ForegroundColor Cyan

if ($exitCode -eq 0) {
    Write-Host "[OK] Build succeeded." -ForegroundColor Green
} else {
    Write-Host "[FAIL] Build failed. See build.log for details." -ForegroundColor Red
}

exit $exitCode
