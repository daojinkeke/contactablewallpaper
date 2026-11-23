#requires -Version 5.1
<#
.SYNOPSIS
    一键出安装包：self-contained 发布 → 体积裁剪 → Inno Setup 编译 → 安装包落在 <workspace>\outputs。

.DESCRIPTION
    依赖：
      * .NET SDK（构建）
      * Inno Setup 6（ISCC.exe；默认查找 %LOCALAPPDATA%\Programs\Inno Setup 6）

    产出：
      * <workspace>\outputs\ContactableWallpaper-Setup-<版本>-x64.exe
      * 安装后体积 = artifacts\publish\win-x64 的体积

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\package.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\package.ps1 -SkipPublish
#>
[CmdletBinding()]
param(
    # 不传时从 Lively.csproj 的 AssemblyVersion 读取
    [string]$Version,

    # 跳过发布，直接用现有的 artifacts\publish\win-x64 编译安装包
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$workspaceRoot = Split-Path -Parent (Split-Path -Parent $repoRoot)
$publishDir = Join-Path $repoRoot 'artifacts\publish\win-x64'
$outputsDir = Join-Path $workspaceRoot 'outputs'
$issPath = Join-Path $repoRoot 'installer\ContactableWallpaper.iss'
$iconPath = Join-Path $repoRoot 'src\Lively\Lively\Resources\appicon.ico'
$langPath = Join-Path $repoRoot 'installer\lang\ChineseSimplified.isl'

function Format-Size {
    param([double]$Bytes)
    if ($Bytes -ge 1GB) { return ('{0:N2} GB' -f ($Bytes / 1GB)) }
    return ('{0:N1} MB' -f ($Bytes / 1MB))
}

function Find-Iscc {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )
    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) { return $candidate }
    }
    return $null
}

if (-not $Version) {
    $versionMatch = Select-String -Path (Join-Path $repoRoot 'src\Lively\Lively\Lively.csproj') `
        -Pattern '<AssemblyVersion>([^<]+)</AssemblyVersion>' | Select-Object -First 1
    if ($versionMatch) {
        $Version = $versionMatch.Matches[0].Groups[1].Value
    }
    else {
        $Version = '2.2.1.5'
    }
}

Write-Host "== ContactableWallpaper 打包 ==" -ForegroundColor Cyan
Write-Host "版本      : $Version"
Write-Host "发布目录  : $publishDir"
Write-Host "输出目录  : $outputsDir"

# ---------------------------------------------------------------------------
# 1. 发布（self-contained + WebView2 引导安装器 + 默认裁剪）
# ---------------------------------------------------------------------------
if (-not $SkipPublish) {
    # publish.ps1 内部用 throw 报错（$LASTEXITCODE 会被 robocopy 等原生命令污染，不能用来判断）
    & (Join-Path $PSScriptRoot 'publish.ps1')
}

$coreExe = Join-Path $publishDir 'Lively.exe'
$webView2Bootstrapper = Join-Path $publishDir 'prereq\MicrosoftEdgeWebview2Setup.exe'
if (-not (Test-Path $coreExe)) {
    throw "发布目录不完整（缺少 Lively.exe）：$publishDir"
}
if (-not (Test-Path $webView2Bootstrapper)) {
    throw "发布目录缺少 WebView2 引导安装器：$webView2Bootstrapper"
}

# ---------------------------------------------------------------------------
# 2. 定位 Inno Setup
# ---------------------------------------------------------------------------
$iscc = Find-Iscc
if (-not $iscc) {
    throw @"
没有找到 Inno Setup 6 的 ISCC.exe。
安装方式（任选其一）：
  * 下载 https://jrsoftware.org/isdl.php 的 Inno Setup 6，安装时选择“仅为我安装”；
  * 或设置 PATH 后重试。
已查找：%LOCALAPPDATA%\Programs\Inno Setup 6、Program Files (x86)、Program Files。
"@
}
if (-not (Test-Path $langPath)) {
    throw "缺少简体中文语言文件：$langPath"
}
Write-Host "ISCC      : $iscc"

New-Item -ItemType Directory -Force -Path $outputsDir | Out-Null
Get-ChildItem $outputsDir -Filter 'ContactableWallpaper-Setup-*.exe' -File -ErrorAction SilentlyContinue |
    Remove-Item -Force

# ---------------------------------------------------------------------------
# 3. 编译安装包
# ---------------------------------------------------------------------------
Write-Host "`n>>> 编译安装包（LZMA2 solid 压缩，需要几分钟）" -ForegroundColor Yellow
$sw = [System.Diagnostics.Stopwatch]::StartNew()
& $iscc `
    "/DSourceDir=$publishDir" `
    "/DOutputDir=$outputsDir" `
    "/DMyAppVersion=$Version" `
    "/DIconFile=$iconPath" `
    $issPath
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup 编译失败（exit $LASTEXITCODE）"
}
$sw.Stop()

$setup = Get-ChildItem $outputsDir -Filter "ContactableWallpaper-Setup-$Version-x64.exe" -File |
    Select-Object -First 1
if (-not $setup) {
    throw "安装包未生成：$outputsDir"
}

$installedBytes = (Get-ChildItem $publishDir -Recurse -File | Measure-Object -Property Length -Sum).Sum
$ratio = if ($installedBytes -gt 0) { $setup.Length / $installedBytes * 100 } else { 0 }

Write-Host "`n== 打包结果 ==" -ForegroundColor Cyan
Write-Host ("  安装包      : {0}" -f $setup.FullName)
Write-Host ("  压缩后体积  : {0}（{1:N0} 字节）" -f (Format-Size $setup.Length), $setup.Length)
Write-Host ("  安装后体积  : {0}（{1:N0} 个文件）" -f (Format-Size $installedBytes), (Get-ChildItem $publishDir -Recurse -File).Count)
Write-Host ("  压缩比      : {0:N1}%（相对安装后体积）" -f $ratio)
Write-Host ("  编译耗时    : {0:N1} 秒" -f $sw.Elapsed.TotalSeconds) -ForegroundColor DarkGray
Write-Host "`n双击安装包即可：一路“下一步”完成，无需管理员权限。" -ForegroundColor Green
