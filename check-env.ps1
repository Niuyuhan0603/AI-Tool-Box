# AI-Tool-Box Dev Environment Dependency Checker
# Run: powershell -ExecutionPolicy Bypass -File .\check-env.ps1

$ErrorActionPreference = 'SilentlyContinue'
$script:pass = 0
$script:fail = 0
$script:warnCount = 0

function Check-Item {
    param([string]$Name, [bool]$Condition, [string]$Detail = "", [string]$FixHint = "")
    if ($Condition) {
        Write-Host "PASS  $Name" -ForegroundColor Green -NoNewline
        $script:pass++
    } else {
        Write-Host "FAIL  $Name" -ForegroundColor Red -NoNewline
        $script:fail++
    }
    if ($Detail) { Write-Host "  ->  $Detail" -ForegroundColor Gray }
    else { Write-Host "" }
    if (-not $Condition -and $FixHint) {
        Write-Host "      FIX: $FixHint" -ForegroundColor Yellow
    }
}

function Check-Warn {
    param([string]$Name, [bool]$Condition, [string]$Detail = "")
    if ($Condition) {
        Write-Host "PASS  $Name" -ForegroundColor Green -NoNewline
    } else {
        Write-Host "WARN  $Name" -ForegroundColor Yellow -NoNewline
        $script:warnCount++
    }
    if ($Detail) { Write-Host "  ->  $Detail" -ForegroundColor Gray }
    else { Write-Host "" }
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " AI-Tool-Box Dev Environment Checker" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# === 1. Operating System ===
Write-Host "[1/7] Operating System" -ForegroundColor Cyan
$osInfo = Get-CimInstance Win32_OperatingSystem
$osVer = [System.Environment]::OSVersion.Version
$minRequired = [Version]"10.0.19041.0"
$osOk = $osVer -ge $minRequired
Check-Item "Windows version" $osOk ($osInfo.Caption + " Build " + $osInfo.BuildNumber) "Need Windows 10 19041 (2004) or later"

# === 2. .NET 8 SDK ===
Write-Host ""
Write-Host "[2/7] .NET 8 SDK" -ForegroundColor Cyan
$dotnetLocal = "e:\ITCH\Project\Intergrated AI Codebot\.dotnet"
$dotnetExe = Join-Path $dotnetLocal "dotnet.exe"
$dotnetInstalled = Test-Path $dotnetExe
Check-Item ".NET SDK (local)" $dotnetInstalled $dotnetLocal ("Run dotnet-install script to " + $dotnetLocal)

if ($dotnetInstalled) {
    $env:DOTNET_ROOT = $dotnetLocal
    $env:PATH = "$dotnetLocal;$env:PATH"
    $sdkVer = & $dotnetExe --version 2>$null
    $sdkOk = ($sdkVer -like "8.0.*")
    Check-Item ".NET SDK version" $sdkOk ("v" + $sdkVer) "Need 8.0.x"

    $sdks = & $dotnetExe --list-sdks 2>$null
    $sdkCount = if ($sdks) { @($sdks).Count } else { 0 }
    Check-Item "Installed SDK list" ($sdkCount -gt 0) ($sdkCount.ToString() + " SDK(s)")
}

# === 3. Visual Studio 2022 ===
Write-Host ""
Write-Host "[3/7] Visual Studio 2022" -ForegroundColor Cyan
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsInstalled = Test-Path $vswhere
Check-Item "vswhere tool" $vsInstalled $vswhere "Install Visual Studio 2022"

if ($vsInstalled) {
    $vsPath = & $vswhere -latest -prerelease -products * -requires Microsoft.Component.MSBuild -property installationPath
    Check-Item "VS 2022 path" ([bool]$vsPath) $vsPath
    if ($vsPath) {
        $msbuild = Join-Path $vsPath 'MSBuild\Current\Bin\MSBuild.exe'
        Check-Item "MSBuild.exe" (Test-Path $msbuild) $msbuild
    }
}

# === 4. Windows App SDK & MSIX Packaging Tools (CRITICAL!) ===
Write-Host ""
Write-Host "[4/7] Windows App SDK & MSIX Packaging (CRITICAL)" -ForegroundColor Cyan

# 4a. Pri.Tasks.dll - most critical
$priTaskPath = "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Microsoft\VisualStudio\v17.0\AppxPackage\Microsoft.Build.Packaging.Pri.Tasks.dll"
$priOk = Test-Path $priTaskPath
$fixHint = "Admin PS: vs_installer modify --installPath 'C:\Program Files\Microsoft Visual Studio\2022\Community' --add Microsoft.VisualStudio.ComponentGroup.MSIX.Packaging --add Microsoft.VisualStudio.ComponentGroup.WindowsAppSDK.Cs --quiet --norestart"
Check-Item "Pri.Tasks.dll" $priOk $priTaskPath $fixHint

# 4b. AppxPackage folder
$appxFolder = "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Microsoft\VisualStudio\v17.0\AppxPackage"
$appxFolderOk = Test-Path $appxFolder
Check-Item "AppxPackage folder" $appxFolderOk $appxFolder "Install MSIX Packaging Tools component"

if ($appxFolderOk) {
    $appxDlls = Get-ChildItem $appxFolder -Filter "*.dll" -ErrorAction SilentlyContinue
    $dllCount = if ($appxDlls) { @($appxDlls).Count } else { 0 }
    Check-Item "AppxPackage task assemblies" ($dllCount -gt 0) ($dllCount.ToString() + " DLL(s)")
}

# === 5. Windows SDK ===
Write-Host ""
Write-Host "[5/7] Windows SDK" -ForegroundColor Cyan
$winSdkRoot = "C:\Program Files (x86)\Windows Kits\10\Include"
$winSdkOk = Test-Path $winSdkRoot
Check-Item "Windows SDK root" $winSdkOk $winSdkRoot "Install Windows 10/11 SDK via VS Installer"

if ($winSdkOk) {
    $sdkVersions = Get-ChildItem $winSdkRoot -Directory | Where-Object { $_.Name -like "10.0.*" } | Select-Object -ExpandProperty Name
    $targetSdk = "10.0.19041.0"
    $hasTarget = $sdkVersions -contains $targetSdk
    $verList = if ($sdkVersions) { ($sdkVersions -join ', ') } else { "none" }
    Check-Item "Target SDK 10.0.19041.0" $hasTarget ("Installed: " + $verList)
    $verCount = if ($sdkVersions) { @($sdkVersions).Count } else { 0 }
    Check-Warn "Any Windows 10+ SDK" ($verCount -gt 0) ($verCount.ToString() + " version(s)")
}

# === 6. NuGet Source ===
Write-Host ""
Write-Host "[6/7] NuGet Source" -ForegroundColor Cyan
if ($dotnetInstalled) {
    $sources = & $dotnetExe nuget list source 2>$null
    $sourcesText = if ($sources) { ($sources | Out-String) } else { "" }
    $hasNugetOrg = $sourcesText.Contains("nuget.org")
    Check-Item "nuget.org source" $hasNugetOrg "configured" "dotnet nuget add source 'https://api.nuget.org/v3/index.json' --name nuget.org --protocol-version 3"
}

# === 7. NuGet Packages Restore ===
Write-Host ""
Write-Host "[7/7] NuGet Packages Restore" -ForegroundColor Cyan
$projPath = "e:\ITCH\Project\Intergrated AI Codebot\AI-Tool-Box\src\AI-Tool-Box\AI-Tool-Box.csproj"
Check-Item "Project file exists" (Test-Path $projPath) $projPath

$nugetCache = Join-Path $env:USERPROFILE ".nuget\packages"
$requiredPkgs = @(
    "microsoft.windowsappsdk",
    "microsoft.windows.sdk.buildtools",
    "microsoft.web.webview2",
    "communitytoolkit.mvvm",
    "microsoft.extensions.hosting",
    "microsoft.extensions.dependencyinjection",
    "serilog",
    "serilog.extensions.hosting",
    "serilog.sinks.file",
    "serilog.sinks.console"
)

if (Test-Path $nugetCache) {
    foreach ($pkg in $requiredPkgs) {
        $pkgPath = Join-Path $nugetCache $pkg
        Check-Item ("NuGet: " + $pkg) (Test-Path $pkgPath)
    }
}

# === Extra: WebView2 Runtime ===
Write-Host ""
Write-Host "[Extra] WebView2 Runtime" -ForegroundColor Cyan
$webview2Reg = Get-ItemProperty "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A5E9C9}" -ErrorAction SilentlyContinue
if (-not $webview2Reg) {
    $webview2Reg = Get-ItemProperty "HKCU:\Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A5E9C9}" -ErrorAction SilentlyContinue
}
$wvVer = if ($webview2Reg) { $webview2Reg.version } else { "not installed" }
Check-Warn "WebView2 Runtime" ([bool]$webview2Reg) $wvVer

# === Summary ===
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Summary" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ("PASS: " + $script:pass) -ForegroundColor Green
$failColor = if ($script:fail -gt 0) { 'Red' } else { 'Gray' }
Write-Host ("FAIL: " + $script:fail) -ForegroundColor $failColor
Write-Host ("WARN: " + $script:warnCount) -ForegroundColor Yellow
Write-Host ""

if ($script:fail -eq 0 -and $script:warnCount -eq 0) {
    Write-Host "OK All dependencies ready! Can build project." -ForegroundColor Green
} elseif ($script:fail -eq 0) {
    Write-Host ("OK Critical deps ready (" + $script:warnCount + " non-critical warnings).") -ForegroundColor Green
} else {
    Write-Host ("! " + $script:fail + " dependency(s) still missing. Follow FAIL hints above.") -ForegroundColor Red
}
Write-Host ""
