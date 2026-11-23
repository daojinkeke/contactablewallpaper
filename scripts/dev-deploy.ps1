#requires -Version 5.1
<#
.SYNOPSIS
    开发期部署脚本：把 UI 与 WebView2 播放器的构建产物装配到 Core 输出目录的 plugins\ 下。

.DESCRIPTION
    unpackaged 模式下 Core 的查找路径（与源码一致）：
      UI      -> <CoreDir>\plugins\UI\Lively.UI.WinUI.exe     (RunnerService.cs)
      播放器  -> <CoreDir>\plugins\webview2\Lively.Player.WebView2.exe (WebWebView2.cs)

    本脚本只做构建产物复制，不改 csproj、不加 post-build，避免干扰其他会话的构建。
    可重复执行，已存在的同名文件会被覆盖（PreserveNewest 语义由 Copy-Item -Force 近似）。

.PARAMETER Configuration
    构建配置，Debug 或 Release，默认 Debug。

.PARAMETER Platform
    播放器/UI 的构建平台，AnyCPU 或 x64，默认 AnyCPU（对应 bin\Debug 与 bin\x64\Debug）。

.PARAMETER Clean
    复制前先清空目标 plugins\UI 与 plugins\webview2。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\dev-deploy.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\dev-deploy.ps1 -Platform x64 -Clean
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [ValidateSet('AnyCPU', 'x64')]
    [string]$Platform = 'AnyCPU',

    [switch]$Clean
)

$ErrorActionPreference = 'Stop'

# 脚本位于 <repo>\scripts\，仓库根为其父目录
$repoRoot = Split-Path -Parent $PSScriptRoot
$srcRoot = Join-Path $repoRoot 'src\Lively'

$coreTfm = 'net9.0-windows10.0.18362.0'
$uiTfm = 'net9.0-windows10.0.22621.0'

function Get-PlatformDir {
    param([string]$ProjectDir)
    if ($Platform -eq 'x64') {
        return (Join-Path $ProjectDir "bin\x64\$Configuration")
    }
    return (Join-Path $ProjectDir "bin\$Configuration")
}

function Resolve-CoreOutput {
    $candidates = @(
        (Join-Path $srcRoot "Lively\bin\x64\$Configuration\$coreTfm"),
        (Join-Path $srcRoot "Lively\bin\$Configuration\$coreTfm")
    )
    foreach ($dir in $candidates) {
        if (Test-Path (Join-Path $dir 'Lively.exe')) { return $dir }
    }
    throw "找不到 Core 构建产物（Lively.exe）。已尝试：`n  $($candidates -join "`n  ")`n请先执行 dotnet build src\Lively\Lively.sln -c $Configuration"
}

function Resolve-UIOutput {
    # UI 工程只声明 x86/x64/arm64，没有 AnyCPU 产物，按 x64 -> x86 兜底
    $projectDir = Join-Path $srcRoot 'Lively.UI.WinUI'
    $candidates = @()
    if ($Platform -eq 'x64') {
        $candidates += (Join-Path $projectDir "bin\x64\$Configuration\$uiTfm")
    }
    else {
        $candidates += (Join-Path $projectDir "bin\$Platform\$Configuration\$uiTfm")
    }
    $candidates += @(
        (Join-Path $projectDir "bin\x64\$Configuration\$uiTfm"),
        (Join-Path $projectDir "bin\x86\$Configuration\$uiTfm")
    )
    foreach ($dir in $candidates) {
        if (Test-Path (Join-Path $dir 'Lively.UI.WinUI.exe')) { return $dir }
    }
    throw "找不到 UI 构建产物（Lively.UI.WinUI.exe）。已尝试：`n  $($candidates -join "`n  ")`n请先执行 dotnet build src\Lively\Lively.sln -c $Configuration"
}

function Copy-Tree {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    if (-not (Test-Path $Source)) {
        throw "源目录不存在：$Source"
    }
    if ($Clean -and (Test-Path $Destination)) {
        Remove-Item $Destination -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null

    $copied = @()
    foreach ($file in Get-ChildItem $Source -Recurse -File) {
        $relative = $file.FullName.Substring($Source.Length).TrimStart('\')
        $target = Join-Path $Destination $relative
        $parent = Split-Path -Parent $target
        if (-not (Test-Path $parent)) {
            New-Item -ItemType Directory -Force -Path $parent | Out-Null
        }
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
        $copied += [pscustomobject]@{
            Path = $relative
            Bytes = $file.Length
        }
    }
    return $copied
}

Write-Host "== ContactableWallpaper dev-deploy ==" -ForegroundColor Cyan
Write-Host "仓库根   : $repoRoot"
Write-Host "配置     : $Configuration"
Write-Host "平台     : $Platform"

$coreOut = Resolve-CoreOutput
Write-Host "Core 输出: $coreOut`n"

$uiSource = Resolve-UIOutput
$playerSource = Get-PlatformDir (Join-Path $srcRoot 'Lively.Player.WebView2')

$uiDest = Join-Path $coreOut 'plugins\UI'
$playerDest = Join-Path $coreOut 'plugins\webview2'
$watchdogSource = Get-PlatformDir (Join-Path $srcRoot 'Lively.Utility.Watchdog')
$watchdogDest = Join-Path $coreOut 'plugins\Watchdog'

$sw = [System.Diagnostics.Stopwatch]::StartNew()

Write-Host "[1/3] 播放器 $playerSource -> $playerDest" -ForegroundColor Yellow
$playerFiles = Copy-Tree -Source $playerSource -Destination $playerDest

Write-Host "[2/3] UI     $uiSource -> $uiDest" -ForegroundColor Yellow
$uiFiles = Copy-Tree -Source $uiSource -Destination $uiDest

Write-Host "[3/3] 看门狗 $watchdogSource -> $watchdogDest" -ForegroundColor Yellow
$watchdogFiles = Copy-Tree -Source $watchdogSource -Destination $watchdogDest
# Core 固定查找 plugins\Watchdog\Lively.Watchdog(.exe)，产物名带 .Utility 需要改名
foreach ($file in Get-ChildItem $watchdogDest -Filter 'Lively.Utility.Watchdog.*' -File) {
    $newName = $file.Name -replace '^Lively\.Utility\.Watchdog', 'Lively.Watchdog'
    Move-Item -LiteralPath $file.FullName -Destination (Join-Path $watchdogDest $newName) -Force
}

$sw.Stop()

function Write-Manifest {
    param([string]$Prefix, $Files)
    $totalMb = [math]::Round((($Files | Measure-Object -Property Bytes -Sum).Sum) / 1MB, 1)
    Write-Host ("  {0}: {1} 个文件, {2} MB" -f $Prefix, $Files.Count, $totalMb) -ForegroundColor Green
    $Files |
        Sort-Object Path |
        Select-Object -First 12 |
        ForEach-Object { Write-Host ("    {0,-52} {1,10} B" -f $_.Path, $_.Bytes) }
    if ($Files.Count -gt 12) {
        Write-Host ("    ... 其余 {0} 个文件略" -f ($Files.Count - 12))
    }
}

Write-Host "`n== 复制清单 ==" -ForegroundColor Cyan
Write-Manifest -Prefix 'webview2' -Files $playerFiles
Write-Manifest -Prefix 'UI' -Files $uiFiles
Write-Manifest -Prefix 'Watchdog' -Files $watchdogFiles

# 关键入口自检
$checks = @(
    (Join-Path $playerDest 'Lively.Player.WebView2.exe'),
    (Join-Path $uiDest 'Lively.UI.WinUI.exe'),
    (Join-Path $watchdogDest 'Lively.Watchdog.exe')
)
$missing = $checks | Where-Object { -not (Test-Path $_) }
if ($missing) {
    throw "部署后缺少关键文件：`n  $($missing -join "`n  ")"
}

Write-Host "`n完成，用时 $([math]::Round($sw.Elapsed.TotalSeconds,1)) 秒。" -ForegroundColor Cyan
Write-Host "启动命令（需 .NET 9 或 DOTNET_ROLL_FORWARD=LatestMajor）："
Write-Host "  `$env:DOTNET_ROLL_FORWARD='LatestMajor'; & '$coreOut\Lively.exe'"
