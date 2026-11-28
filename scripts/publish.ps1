#requires -Version 5.1
<#
.SYNOPSIS
    Release 发布脚本（self-contained + WebView2 引导安装器）：产出自带 .NET 的可运行目录。

.DESCRIPTION
    与开发期的 dev-deploy.ps1 完全独立：
      * dev-deploy.ps1 只把构建产物拼到 Core 的 bin 目录，依赖机器上的 .NET 运行时；
      * 本脚本走 dotnet publish，把 .NET 运行时打进发布目录，目标机器不需要安装 .NET。

    WebView2 默认走“引导器模式”（-NoWebView2Runtime 可关闭）：
      * 发布目录带 <Out>\prereq\MicrosoftEdgeWebview2Setup.exe（微软官方引导安装器，约 2 MB）；
      * 安装器检测到系统缺 WebView2 时运行它，由微软组件在线安装；
      * 若确实需要零联网，可显式传 -WebView2RuntimeDirectory <固定版本运行时目录> 随包携带运行时。

    三类工程的发布方式不同，脚本内部分开处理：
      * Core / AI CLI 是 net9.0-windows 控制台/窗口程序，按 win-x64 + SelfContained=true + 单文件发布
        （PublishSingleFile，原生库随包自解压，压缩开启）。这样发布根目录只剩一个 Lively.exe，
        不再平铺 300 个运行时 dll；AI CLI 同理只剩单个 exe；
      * UI 是 WinUI3（WindowsAppSDK 自包含），不支持单文件发布，仍按目录发布；
      * 各 Player（WebView2/CefSharp/Wmf/Vlc）与 Watchdog 是 net472，只做 Release 构建后整目录复制，
        绝不传 -r / --self-contained（.NET Framework 不支持自包含，混用会直接失败）。

    发布目录结构与运行时代码（RunnerService / WebWebView2 / WatchdogProcess）的查找路径一致：
      <Out>\Lively.exe                                              （单文件，内含 .NET 运行时）
      <Out>\plugins\UI\Lively.UI.WinUI.exe
      <Out>\plugins\Ai\Lively.Ai.Cli.exe                            （单文件，新建壁纸页调用的生成 CLI）
      <Out>\plugins\webview2\Lively.Player.WebView2.exe
      <Out>\prereq\MicrosoftEdgeWebview2Setup.exe                   （WebView2 引导安装器，默认模式）
      <Out>\plugins\webview2\webview2-runtime\msedgewebview2.exe   （仅 -WebView2RuntimeDirectory 时随包）
      <Out>\plugins\Watchdog\Lively.Watchdog.exe
      <Out>\plugins\cef|wmf|libvlc\...                              （-IncludeOptionalPlayers 时才有）

    默认裁剪（-NoTrim 可关闭）：
      * 删除所有 .pdb 与 .xml 文档；
      * 删除除 zh-CN / zh-Hans / zh-Hant / en / en-US 之外的卫星资源目录；
      * 随包 WebView2 时额外删除 ResiliencyLinks 与非 en-US/zh-CN 的 Locales。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\publish.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -WebView2RuntimeDirectory D:\WebView2Fixed\158.0.4370.33

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -NoWebView2Runtime -NoTrim
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',

    # 默认 <repo>\artifacts\publish\<Runtime>（artifacts/ 已在 .gitignore 中）
    [string]$OutputDirectory,

    # cef / wmf / libvlc 默认不进包（省约 507 MB），需要时显式打开
    [switch]$IncludeOptionalPlayers,

    # 显式指定固定版本 WebView2 运行时目录：随包携带（默认不随包，走引导器）
    [string]$WebView2RuntimeDirectory,

    # 不携带 WebView2 引导器（仅开发调试用，目标机器必须已装 Evergreen Runtime）
    [switch]$NoWebView2Runtime,

    # 不做体积裁剪（排查用）
    [switch]$NoTrim,

    # 保留 staging 中间目录，便于排查发布问题
    [switch]$KeepStaging
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$srcRoot = Join-Path $repoRoot 'src\Lively'
$configuration = 'Release'
$platform = 'x64'

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot "artifacts\publish\$Runtime"
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot $OutputDirectory
}
$outputDir = [System.IO.Path]::GetFullPath($OutputDirectory)

$stagingRoot = Join-Path $repoRoot "artifacts\publish\.staging-$Runtime"
$pluginDir = Join-Path $outputDir 'plugins'

# 保留的卫星资源语言（其余语言目录全部裁掉）
$keepCultures = @('zh-CN', 'zh-Hans', 'zh-Hant', 'en', 'en-US')

function Invoke-Dotnet {
    param(
        [Parameter(Mandatory)][string]$Title,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    Write-Host "`n>>> $Title" -ForegroundColor Yellow
    Write-Host "    dotnet $($Arguments -join ' ')" -ForegroundColor DarkGray

    # obj/bin 被其他 dotnet 进程短暂占用时等待重试，不打断其他会话的构建
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        & dotnet @Arguments
        if ($LASTEXITCODE -eq 0) {
            return
        }
        if ($attempt -lt 3) {
            Write-Host "    构建失败（exit $LASTEXITCODE），$($attempt * 6) 秒后重试..." -ForegroundColor DarkYellow
            Start-Sleep -Seconds ($attempt * 6)
        }
    }
    throw "dotnet 执行失败（exit $LASTEXITCODE）：$Title"
}

function Copy-Tree {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    if (-not (Test-Path $Source)) {
        throw "源目录不存在：$Source"
    }

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    $files = Get-ChildItem $Source -Recurse -File
    # 用 robocopy 而不是 Copy-Item：WebView2 固定版本运行时存在超过 260 字符的深路径
    & robocopy $Source $Destination /E /COPY:DAT /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) {
        throw "robocopy 复制失败（exit $LASTEXITCODE）：$Source -> $Destination"
    }

    return [pscustomobject]@{
        Files = $files.Count
        Bytes = ($files | Measure-Object -Property Length -Sum).Sum
    }
}

function Remove-Tree {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path $Path)) {
        return
    }
    # Remove-Item 无法处理 WebView2 运行时那种超过 260 字符的深路径，先用 robocopy 掏空目录
    $emptyDir = Join-Path ([System.IO.Path]::GetTempPath()) ('lively-empty-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $emptyDir | Out-Null
    try {
        & robocopy $emptyDir $Path /MIR /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    }
    finally {
        Remove-Item $emptyDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    Remove-Item $Path -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path $Path) {
        throw "目录清理失败：$Path"
    }
}

function Resolve-RepoPath {
    param([Parameter(Mandatory)][string]$ChildPath)
    return (Join-Path $srcRoot $ChildPath)
}

function Get-DirectorySize {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return 0 }
    return (Get-ChildItem $Path -Recurse -File | Measure-Object -Property Length -Sum).Sum
}

function Format-Size {
    param([double]$Bytes)
    if ($Bytes -ge 1GB) { return ('{0:N2} GB' -f ($Bytes / 1GB)) }
    return ('{0:N1} MB' -f ($Bytes / 1MB))
}

function Find-WebView2RuntimeDirectory {
    $candidates = @()

    $regKeys = @(
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
        'HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
    )
    foreach ($regKey in $regKeys) {
        if (Test-Path $regKey) {
            $pv = (Get-ItemProperty $regKey -ErrorAction SilentlyContinue).pv
            if ($pv) {
                $candidates += (Join-Path ${env:ProgramFiles(x86)} "Microsoft\EdgeWebView\Application\$pv")
            }
        }
    }

    foreach ($baseDir in @(
            (Join-Path ${env:ProgramFiles(x86)} 'Microsoft\EdgeWebView\Application'),
            (Join-Path $env:LOCALAPPDATA 'Microsoft\EdgeWebView\Application'))) {
        if (Test-Path $baseDir) {
            Get-ChildItem $baseDir -Directory -ErrorAction SilentlyContinue |
                Sort-Object Name -Descending |
                ForEach-Object { $candidates += $_.FullName }
        }
    }

    foreach ($candidate in $candidates) {
        if (Test-Path (Join-Path $candidate 'msedgewebview2.exe')) {
            return $candidate
        }
    }
    return $null
}

Write-Host "== ContactableWallpaper publish (self-contained) ==" -ForegroundColor Cyan
Write-Host "配置      : $configuration（发布一律 Release，Debug 播放器不可用）"
Write-Host "运行时    : $Runtime（SelfContained=true, PublishReadyToRun=false）"
Write-Host "打包形态  : Core / AI CLI 单文件（原生库自解压 + 压缩），UI 目录发布（WinUI3 不支持单文件）"
Write-Host "输出目录  : $outputDir"
Write-Host "可选播放器: $(if ($IncludeOptionalPlayers) { 'cef / wmf / libvlc 一并发布' } else { '不打包（-IncludeOptionalPlayers 可开启）' })"
Write-Host "WebView2  : $(if ($WebView2RuntimeDirectory) { '随包携带固定版本' } elseif ($NoWebView2Runtime) { '不携带（依赖系统 Evergreen）' } else { '引导器模式（微软官方引导安装器，约 2 MB）' })"
Write-Host "体积裁剪  : $(if ($NoTrim) { '关闭' } else { '开启' })"

# 每次发布都从干净的目录开始，保证结果可重复
if (Test-Path $outputDir) {
    Write-Host "`n清理旧发布目录：$outputDir" -ForegroundColor DarkYellow
    Remove-Tree $outputDir
}
if (Test-Path $stagingRoot) {
    Remove-Tree $stagingRoot
}
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null

$sw = [System.Diagnostics.Stopwatch]::StartNew()

# ---------------------------------------------------------------------------
# 1. Core（net9.0-windows）：self-contained 单文件发布到发布根目录
# ---------------------------------------------------------------------------
$coreStage = Join-Path $stagingRoot 'core'
Invoke-Dotnet -Title '发布 Core (Lively.csproj, self-contained 单文件)' -Arguments @(
    'publish', (Resolve-RepoPath 'Lively\Lively.csproj'),
    '-c', $configuration,
    '-r', $Runtime,
    '--self-contained', 'true',
    "-p:Platform=$platform",
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:TrimSingleFileBundle=true',
    '-p:PublishTrimmed=false',
    '-p:PublishReadyToRun=false',
    '-o', $coreStage
)
$coreCopy = Copy-Tree -Source $coreStage -Destination $outputDir

# ---------------------------------------------------------------------------
# 2. UI（net9.0-windows, WinUI3 unpackaged）：self-contained 发布到 plugins\UI
# ---------------------------------------------------------------------------
$uiStage = Join-Path $stagingRoot 'ui'
Invoke-Dotnet -Title '发布 UI (Lively.UI.WinUI.csproj, self-contained)' -Arguments @(
    'publish', (Resolve-RepoPath 'Lively.UI.WinUI\Lively.UI.WinUI.csproj'),
    '-c', $configuration,
    '-r', $Runtime,
    '--self-contained', 'true',
    "-p:Platform=$platform",
    '-p:WindowsPackageType=None',
    '-p:WindowsAppSDKSelfContained=true',
    '-p:PublishSingleFile=false',
    '-p:PublishTrimmed=false',
    '-p:PublishReadyToRun=false',
    '-o', $uiStage
)
$uiCopy = Copy-Tree -Source $uiStage -Destination (Join-Path $pluginDir 'UI')

# ---------------------------------------------------------------------------
# 2.6 AI CLI（net9.0-windows）：self-contained 发布到 plugins\Ai
#     UI 的“新建壁纸”页按 <UI>\..\Ai\Lively.Ai.Cli.exe 查找生成器
# ---------------------------------------------------------------------------
$aiStage = Join-Path $stagingRoot 'ai'
Invoke-Dotnet -Title '发布 AI CLI (Lively.Ai.Cli.csproj, self-contained 单文件)' -Arguments @(
    'publish', (Resolve-RepoPath 'Lively.Ai.Cli\Lively.Ai.Cli.csproj'),
    '-c', $configuration,
    '-r', $Runtime,
    '--self-contained', 'true',
    "-p:Platform=$platform",
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:TrimSingleFileBundle=true',
    '-p:PublishTrimmed=false',
    '-p:PublishReadyToRun=false',
    '-o', $aiStage
)
$aiCopy = Copy-Tree -Source $aiStage -Destination (Join-Path $pluginDir 'Ai')

# ---------------------------------------------------------------------------
# 2.5 补齐 VC++ 运行库：.NET 自包含产物里的 vcruntime140_cor3.dll 仍依赖
#     vcruntime140.dll，干净的 Win10/Win11 上可能没有装 VC++ 2015-2022 运行库。
#     随包放在应用目录即可被优先加载，不需要管理员权限安装 Redist。
# ---------------------------------------------------------------------------
$vcRuntimeFiles = @('vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll')
$vcRuntimeCopied = 0
foreach ($targetDir in @($outputDir, (Join-Path $pluginDir 'UI'), (Join-Path $pluginDir 'Ai'))) {
    foreach ($name in $vcRuntimeFiles) {
        $source = Join-Path "$env:SystemRoot\System32" $name
        if (Test-Path $source) {
            Copy-Item $source (Join-Path $targetDir $name) -Force
            $vcRuntimeCopied++
        }
    }
}

# ---------------------------------------------------------------------------
# 3. WebView2 播放器（net472）：Release 构建后整目录复制到 plugins\webview2
# ---------------------------------------------------------------------------
$webView2Stage = Join-Path $stagingRoot 'webview2'
Invoke-Dotnet -Title '发布 WebView2 播放器 (net472, 不自包含)' -Arguments @(
    'publish', (Resolve-RepoPath 'Lively.Player.WebView2\Lively.Player.WebView2.csproj'),
    '-c', $configuration,
    "-p:Platform=$platform",
    '-o', $webView2Stage
)
$webView2Copy = Copy-Tree -Source $webView2Stage -Destination (Join-Path $pluginDir 'webview2')

# ---------------------------------------------------------------------------
# 4. Watchdog（legacy net472）：Release 构建后复制到 plugins\Watchdog
#    Core 固定查找 Lively.Watchdog(.exe)，产物名带 .Utility 需要改名。
# ---------------------------------------------------------------------------
Invoke-Dotnet -Title '构建 Watchdog (net472, 不自包含)' -Arguments @(
    'build', (Resolve-RepoPath 'Lively.Utility.Watchdog\Lively.Utility.Watchdog.csproj'),
    '-c', $configuration,
    "-p:Platform=$platform"
)
$watchdogSource = Resolve-RepoPath "Lively.Utility.Watchdog\bin\$platform\$configuration"
$watchdogDest = Join-Path $pluginDir 'Watchdog'
$watchdogCopy = Copy-Tree -Source $watchdogSource -Destination $watchdogDest
foreach ($file in Get-ChildItem $watchdogDest -Filter 'Lively.Utility.Watchdog.*' -File) {
    $newName = $file.Name -replace '^Lively\.Utility\.Watchdog', 'Lively.Watchdog'
    Move-Item -LiteralPath $file.FullName -Destination (Join-Path $watchdogDest $newName) -Force
}

# ---------------------------------------------------------------------------
# 5. 可选播放器（net472）：cef / wmf / libvlc
# ---------------------------------------------------------------------------
$optionalCopies = @{}

if ($IncludeOptionalPlayers) {
    $cefStage = Join-Path $stagingRoot 'cef'
    Invoke-Dotnet -Title '发布 CefSharp 播放器 (net472, 不自包含)' -Arguments @(
        'publish', (Resolve-RepoPath 'Lively.Player.CefSharp\Lively.Player.CefSharp.csproj'),
        '-c', $configuration,
        "-p:Platform=$platform",
        '-o', $cefStage
    )
    $optionalCopies['cef'] = Copy-Tree -Source $cefStage -Destination (Join-Path $pluginDir 'cef')

    $wmfStage = Join-Path $stagingRoot 'wmf'
    Invoke-Dotnet -Title '发布 Wmf 播放器 (net472, 不自包含)' -Arguments @(
        'publish', (Resolve-RepoPath 'Lively.Player.Wmf\Lively.Player.Wmf.csproj'),
        '-c', $configuration,
        "-p:Platform=$platform",
        '-o', $wmfStage
    )
    $wmfDest = Join-Path $pluginDir 'wmf'
    $optionalCopies['wmf'] = Copy-Tree -Source $wmfStage -Destination $wmfDest
    # Core 期望 plugins\wmf\Lively.PlayerWmf.exe，产物名为 Lively.Player.Wmf.exe，需要改名（含 .exe.config）
    foreach ($file in Get-ChildItem $wmfDest -Filter 'Lively.Player.Wmf.*' -File) {
        $newName = $file.Name -replace '^Lively\.Player\.Wmf', 'Lively.PlayerWmf'
        Move-Item -LiteralPath $file.FullName -Destination (Join-Path $wmfDest $newName) -Force
    }

    $vlcStage = Join-Path $stagingRoot 'libvlc'
    Invoke-Dotnet -Title '发布 LibVlc 播放器 (net472, 不自包含)' -Arguments @(
        'publish', (Resolve-RepoPath 'Lively.Player.Vlc\Lively.Player.Vlc.csproj'),
        '-c', $configuration,
        "-p:Platform=$platform",
        '-o', $vlcStage
    )
    $optionalCopies['libvlc'] = Copy-Tree -Source $vlcStage -Destination (Join-Path $pluginDir 'libvlc')
}

# ---------------------------------------------------------------------------
# 6. WebView2：默认携带微软引导安装器；显式指定时才随包固定运行时
# ---------------------------------------------------------------------------
$bundledRuntimeCopy = $null
$bootstrapperCopy = $null
$webView2Mode = 'bootstrapper'
if ($NoWebView2Runtime) {
    $webView2Mode = 'none'
}
elseif ($WebView2RuntimeDirectory) {
    $webView2Mode = 'bundled'
}

if ($webView2Mode -eq 'bundled') {
    $runtimeDir = $WebView2RuntimeDirectory
    if (-not (Test-Path (Join-Path $runtimeDir 'msedgewebview2.exe'))) {
        throw "WebView2 运行时目录无效（缺少 msedgewebview2.exe）：$runtimeDir"
    }
    Write-Host "`n>>> 随包 WebView2 运行时：$runtimeDir" -ForegroundColor Yellow
    $bundledRuntimeCopy = Copy-Tree -Source $runtimeDir -Destination (Join-Path $pluginDir 'webview2\webview2-runtime')
}
elseif ($webView2Mode -eq 'bootstrapper') {
    # 微软官方 Evergreen 引导安装器（约 2 MB），安装/修复系统 WebView2 时由安装器运行。
    $bootstrapperSource = Join-Path $repoRoot 'installer\prereq\MicrosoftEdgeWebview2Setup.exe'
    if (-not (Test-Path $bootstrapperSource)) {
        Write-Host "`n>>> 下载 WebView2 引导安装器（微软官方，约 2 MB）" -ForegroundColor Yellow
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $bootstrapperSource) | Out-Null
        Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $bootstrapperSource -UseBasicParsing
    }
    $prereqDir = Join-Path $outputDir 'prereq'
    New-Item -ItemType Directory -Force -Path $prereqDir | Out-Null
    Copy-Item -LiteralPath $bootstrapperSource -Destination (Join-Path $prereqDir 'MicrosoftEdgeWebview2Setup.exe') -Force
    $bootstrapperCopy = [pscustomobject]@{
        Bytes = (Get-Item (Join-Path $prereqDir 'MicrosoftEdgeWebview2Setup.exe')).Length
        Files = 1
    }
    Write-Host "`n>>> WebView2 引导安装器：$bootstrapperSource（$(Format-Size $bootstrapperCopy.Bytes)）" -ForegroundColor Yellow
}
else {
    Write-Host "`n>>> WebView2：未携带（-NoWebView2Runtime，仅开发调试）" -ForegroundColor DarkYellow
}

$sw.Stop()

# ---------------------------------------------------------------------------
# 7. 体积裁剪 + 逐项统计
# ---------------------------------------------------------------------------
$trimReport = New-Object System.Collections.ArrayList

function Add-TrimReport {
    param([string]$Item, [int]$Files, [double]$Bytes)
    $null = $trimReport.Add([pscustomobject]@{ Item = $Item; Files = $Files; Bytes = $Bytes })
}

function Remove-FilesWithStats {
    param(
        [string]$Root,
        [string]$Filter,
        [string]$ItemName
    )
    if (-not (Test-Path $Root)) { return }
    $items = Get-ChildItem $Root -Recurse -File -Filter $Filter -ErrorAction SilentlyContinue
    if (-not $items) {
        Add-TrimReport -Item $ItemName -Files 0 -Bytes 0
        return
    }
    $bytes = ($items | Measure-Object -Property Length -Sum).Sum
    $count = $items.Count
    foreach ($item in $items) {
        Remove-Item -LiteralPath $item.FullName -Force -ErrorAction SilentlyContinue
    }
    Add-TrimReport -Item $ItemName -Files $count -Bytes $bytes
}

if (-not $NoTrim) {
    Write-Host "`n>>> 体积裁剪" -ForegroundColor Yellow

    # 7.1 调试符号
    Remove-FilesWithStats -Root $outputDir -Filter '*.pdb' -ItemName 'pdb 调试符号'

    # 7.2 XML 文档
    Remove-FilesWithStats -Root $outputDir -Filter '*.xml' -ItemName 'xml 文档'

    # 7.2.1 ONNX Runtime 的导入库（*.lib）只在链接期用，运行时不需要
    Remove-FilesWithStats -Root $outputDir -Filter '*.lib' -ItemName 'native 导入库(.lib)'

    # 7.2.2 设计器 / 调试诊断程序集：运行时永不加载（单文件包内由 Directory.Build.targets 剔除，
    #       这里是 UI、webview2 等目录发布形态的对应处理）
    $unusedAssemblies = @(
        'System.Windows.Forms.Design.dll',
        'System.Windows.Forms.Design.Editors.dll',
        'System.Windows.Forms.Design.resources.dll',
        'System.Drawing.Design.dll',
        'System.Design.dll',
        'Microsoft.DiaSymReader.Native.amd64.dll',
        'mscordaccore.dll',
        'mscordbi.dll',
        'createdump.exe')
    $unusedPatterns = @($unusedAssemblies + 'mscordaccore_amd64_amd64_*.dll')
    $unusedFiles = 0
    $unusedBytes = 0
    foreach ($pattern in $unusedPatterns) {
        $items = Get-ChildItem $outputDir -Recurse -File -Filter $pattern -ErrorAction SilentlyContinue
        if (-not $items) { continue }
        $unusedFiles += $items.Count
        $unusedBytes += ($items | Measure-Object -Property Length -Sum).Sum
        foreach ($item in $items) { Remove-Item -LiteralPath $item.FullName -Force -ErrorAction SilentlyContinue }
    }
    Add-TrimReport -Item '设计器/诊断程序集' -Files $unusedFiles -Bytes $unusedBytes

    # 7.3 卫星资源目录（非 zh/en 全部裁掉）
    $allCultureNames = [System.Globalization.CultureInfo]::GetCultures(
        [System.Globalization.CultureTypes]::AllCultures) | ForEach-Object { $_.Name }
    $appRoots = @($outputDir, (Join-Path $pluginDir 'UI'), (Join-Path $pluginDir 'Ai'), (Join-Path $pluginDir 'webview2'), $watchdogDest)
    foreach ($key in $optionalCopies.Keys) { $appRoots += (Join-Path $pluginDir $key) }

    $satelliteDirs = @()
    foreach ($root in $appRoots) {
        if (-not (Test-Path $root)) { continue }
        $satelliteDirs += Get-ChildItem $root -Directory -ErrorAction SilentlyContinue | Where-Object {
            $allCultureNames -contains $_.Name -and $keepCultures -notcontains $_.Name
        }
    }

    $satelliteFiles = 0
    $satelliteBytes = 0
    foreach ($dir in $satelliteDirs) {
        $items = Get-ChildItem $dir.FullName -Recurse -File -ErrorAction SilentlyContinue
        $satelliteFiles += $items.Count
        $satelliteBytes += ($items | Measure-Object -Property Length -Sum).Sum
        Remove-Tree $dir.FullName
    }
    Add-TrimReport -Item '未使用语言资源（卫星程序集）' -Files $satelliteFiles -Bytes $satelliteBytes

    # 7.4 WebView2 运行时裁剪：ResiliencyLinks 整个目录 + 多余 Locales
    if ($bundledRuntimeCopy) {
        $runtimeRoot = Join-Path $pluginDir 'webview2\webview2-runtime'
        $resiliency = Join-Path $runtimeRoot 'ResiliencyLinks'
        $resiliencyBytes = 0
        $resiliencyFiles = 0
        if (Test-Path $resiliency) {
            $items = Get-ChildItem $resiliency -Recurse -File -ErrorAction SilentlyContinue
            $resiliencyFiles = $items.Count
            $resiliencyBytes = ($items | Measure-Object -Property Length -Sum).Sum
            Remove-Tree $resiliency
        }
        Add-TrimReport -Item 'WebView2 ResiliencyLinks' -Files $resiliencyFiles -Bytes $resiliencyBytes

        $localesDir = Join-Path $runtimeRoot 'Locales'
        $localeFiles = 0
        $localeBytes = 0
        if (Test-Path $localesDir) {
            $keepLocaleFiles = @('en-US.pak', 'zh-CN.pak')
            $removable = Get-ChildItem $localesDir -File | Where-Object { $keepLocaleFiles -notcontains $_.Name }
            $localeFiles = $removable.Count
            $localeBytes = ($removable | Measure-Object -Property Length -Sum).Sum
            foreach ($file in $removable) {
                Remove-Item -LiteralPath $file.FullName -Force -ErrorAction SilentlyContinue
            }
        }
        Add-TrimReport -Item 'WebView2 多余 Locales' -Files $localeFiles -Bytes $localeBytes
    }
}

# ---------------------------------------------------------------------------
# 8. 校验：运行时代码依赖的关键入口
# ---------------------------------------------------------------------------
$requiredEntryPoints = [ordered]@{
    'Core'            = Join-Path $outputDir 'Lively.exe'
    'UI'              = Join-Path $pluginDir 'UI\Lively.UI.WinUI.exe'
    'AI CLI'          = Join-Path $pluginDir 'Ai\Lively.Ai.Cli.exe'
    'WebView2 播放器' = Join-Path $pluginDir 'webview2\Lively.Player.WebView2.exe'
    'Watchdog'        = Join-Path $pluginDir 'Watchdog\Lively.Watchdog.exe'
}
if ($webView2Mode -eq 'bundled') {
    $requiredEntryPoints['WebView2 运行时'] = Join-Path $pluginDir 'webview2\webview2-runtime\msedgewebview2.exe'
}
elseif ($webView2Mode -eq 'bootstrapper') {
    $requiredEntryPoints['WebView2 引导安装器'] = Join-Path $outputDir 'prereq\MicrosoftEdgeWebview2Setup.exe'
}
if ($IncludeOptionalPlayers) {
    $requiredEntryPoints['CefSharp 播放器'] = Join-Path $pluginDir 'cef\Lively.Player.CefSharp.exe'
    $requiredEntryPoints['Wmf 播放器'] = Join-Path $pluginDir 'wmf\Lively.PlayerWmf.exe'
    $requiredEntryPoints['LibVlc 播放器'] = Join-Path $pluginDir 'libvlc\Lively.Player.Vlc.exe'
}

$missing = @()
foreach ($entry in $requiredEntryPoints.GetEnumerator()) {
    if (-not (Test-Path $entry.Value)) {
        $missing += "$($entry.Key) -> $($entry.Value)"
    }
}
if ($missing.Count -gt 0) {
    throw "发布产物缺少关键入口：`n  $($missing -join "`n  ")"
}

# 单文件校验：Core / AI CLI 的 .NET 运行时已打进 exe，目录里不应再出现主程序 dll 或运行时 dll
$singleFileRoots = [ordered]@{
    'Core'   = @{ Dir = $outputDir; HostDll = 'Lively.dll' }
    'AI CLI' = @{ Dir = (Join-Path $pluginDir 'Ai'); HostDll = 'Lively.Ai.Cli.dll' }
}
foreach ($entry in $singleFileRoots.GetEnumerator()) {
    $leftovers = @($entry.Value.HostDll, 'coreclr.dll', 'hostfxr.dll') |
        Where-Object { Test-Path (Join-Path $entry.Value.Dir $_) }
    if ($leftovers) {
        throw "$($entry.Key) 不是单文件发布（仍存在 $($leftovers -join ', ')）：$($entry.Value.Dir)"
    }
}

# 自包含校验：UI 仍为目录发布，必须带 hostfxr/hostpolicy 与 coreclr
$selfContainedMarkers = @('hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll')
$uiRoot = Join-Path $pluginDir 'UI'
$missingMarkers = $selfContainedMarkers | Where-Object { -not (Test-Path (Join-Path $uiRoot $_)) }
if ($missingMarkers) {
    throw "UI 不是 self-contained（缺少 $($missingMarkers -join ', ')）：$uiRoot"
}

# ---------------------------------------------------------------------------
# 9. 清单与体积报告
# ---------------------------------------------------------------------------
Write-Host "`n== 发布目录结构 ==" -ForegroundColor Cyan
Write-Host ("  {0,-16} {1,10}  {2,7} 个文件  {3}" -f 'Core', (Format-Size $coreCopy.Bytes), $coreCopy.Files, "$outputDir\Lively.exe")
Write-Host ("  {0,-16} {1,10}  {2,7} 个文件  {3}" -f 'UI', (Format-Size $uiCopy.Bytes), $uiCopy.Files, 'plugins\UI\Lively.UI.WinUI.exe')
Write-Host ("  {0,-16} {1,10}  {2,7} 个文件  {3}" -f 'AI CLI', (Format-Size $aiCopy.Bytes), $aiCopy.Files, 'plugins\Ai\Lively.Ai.Cli.exe')
Write-Host ("  {0,-16} {1,10}  {2,7} 个文件  {3}" -f 'webview2', (Format-Size $webView2Copy.Bytes), $webView2Copy.Files, 'plugins\webview2\Lively.Player.WebView2.exe')
Write-Host ("  {0,-16} {1,10}  {2,7} 个文件  {3}" -f 'Watchdog', (Format-Size $watchdogCopy.Bytes), $watchdogCopy.Files, 'plugins\Watchdog\Lively.Watchdog.exe')
foreach ($key in $optionalCopies.Keys) {
    $copy = $optionalCopies[$key]
    Write-Host ("  {0,-16} {1,10}  {2,7} 个文件  {3}" -f $key, (Format-Size $copy.Bytes), $copy.Files, "plugins\$key")
}
if ($bundledRuntimeCopy) {
    Write-Host ("  {0,-16} {1,10}  {2,7} 个文件  {3}" -f 'webview2-runtime', (Format-Size $bundledRuntimeCopy.Bytes), $bundledRuntimeCopy.Files, 'plugins\webview2\webview2-runtime')
}
elseif ($bootstrapperCopy) {
    Write-Host ("  {0,-16} {1,10}  {2,7} 个文件  {3}" -f 'WebView2 引导器', (Format-Size $bootstrapperCopy.Bytes), $bootstrapperCopy.Files, 'prereq\MicrosoftEdgeWebview2Setup.exe')
}
else {
    Write-Host "  WebView2                   未携带（使用系统 Evergreen Runtime）" -ForegroundColor DarkGray
}

if ($trimReport.Count -gt 0) {
    Write-Host "`n== 裁剪明细（已从上面的体积中扣除）==" -ForegroundColor Cyan
    foreach ($row in $trimReport) {
        Write-Host ("  {0,-28} {1,6} 个文件  -{2,9}" -f $row.Item, $row.Files, (Format-Size $row.Bytes))
    }
}
Write-Host ("  {0,-28} {1,6}        {2}" -f 'ReadyToRun', '关闭', '-0.0 MB（原本未启用，仅显式锁定）') -ForegroundColor DarkGray

if ($vcRuntimeCopied -gt 0) {
    Write-Host "  VC++ 运行库（vcruntime140/msvcp140）已随包 $vcRuntimeCopied 个文件，约 1.4 MB（防干净机器缺 VCRT）" -ForegroundColor DarkGray
}

$totalBytes = Get-DirectorySize $outputDir
Write-Host ("`n合计：{0}（{1:N0} 个文件）" -f (Format-Size $totalBytes), (Get-ChildItem $outputDir -Recurse -File).Count) -ForegroundColor Green

if (-not $IncludeOptionalPlayers) {
    Write-Host "可选播放器未进包：cef 约 401 MB / libvlc 约 106 MB / wmf 约 4.6 MB（-IncludeOptionalPlayers 可纳入）" -ForegroundColor DarkGray
}

if (-not $KeepStaging) {
    Remove-Tree $stagingRoot
}
else {
    Write-Host "staging 保留在：$stagingRoot"
}

Write-Host "`n完成，用时 $([math]::Round($sw.Elapsed.TotalSeconds,1)) 秒。" -ForegroundColor Cyan
if ($webView2Mode -eq 'bootstrapper') {
    Write-Host "直接运行（无需安装 .NET；缺失 WebView2 时安装器会引导安装）："
}
else {
    Write-Host "直接运行（无需安装 .NET 与 WebView2）："
}
Write-Host "  & '$outputDir\Lively.exe'`n"
