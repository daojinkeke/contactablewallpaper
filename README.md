# Contactable Wallpaper

> 一个面向 Windows 的可交互 AI 桌面壁纸应用：用一句描述生成网页壁纸，用一张照片生成深度视差壁纸，并让壁纸真正响应鼠标与键盘。
>
> *An interactive AI desktop wallpaper app for Windows. Describe a scene to generate an HTML wallpaper, turn a photo into a depth-parallax wallpaper, and let the wallpaper respond to real mouse and keyboard input.*

- 版本 / Version：2.2.1.5（AssemblyVersion）
- 平台 / Platform：Windows 10 1903+ / Windows 11，x64
- 许可证 / License：**GPL-3.0**（沿用上游，见 [LICENSE](./LICENSE)）

**English summary** — Contactable Wallpaper is a **GPL-3.0 derivative of [Lively Wallpaper](https://github.com/rocksdanister/lively)**. It adds AI-generated single-file HTML wallpapers (any OpenAI-compatible endpoint), photo-to-depth parallax rendering with a local MiDaS/ONNX model, a desktop input bridge that forwards pointer/wheel/keyboard events into WebView2, an eco mode, and session-based revision history. Build with the .NET 9 SDK: `dotnet build src/Lively/Lively.sln -c Release -p:Platform=x64`. No API keys are bundled — configure your own endpoint under *Settings → AI 生成*.

---

## 1. 这是什么

Contactable Wallpaper 是一个桌面壁纸宿主程序：

- 把网页（HTML/CSS/JS）、视频、GIF、甚至应用程序渲染到桌面图标层之后（WorkerW）；
- 在此基础上新增了 **AI 生成网页壁纸** 与 **照片转深度视差** 两条「一句话/一张图 → 直接可用的壁纸」链路；
- 自带输入桥接层，让生成的网页壁纸无需额外改造即可接收桌面上的指针、滚轮、按键事件。

界面包含四个主页面：**新建壁纸**、**壁纸库**、**预览**、**设置**；首次运行会把八张内置壁纸（`astral` / `aurora` / `lumen` / `ember` / `prism` / `dune` / `bloom` / `ink`）播种到本地壁纸库。

| 能力 | 说明 |
| --- | --- |
| AI 生成 HTML 壁纸 | 通过任意 OpenAI 兼容接口生成单文件 HTML 壁纸，自动补齐 `LivelyInfo.json` 等运行元数据并直接应用 |
| 照片 → 深度视差 | 本地 MiDaS（ONNX Runtime，CPU）估计深度，生成可交互的 Three.js 视差壁纸；首次使用下载约 62 MB 模型，之后完全离线 |
| 桌面交互 | 指针移动/按下/抬起/离开、滚轮、中键、键盘转发到网页壁纸；总开关与滚轮/中键开关独立，关闭后完全不采集、不注入 |
| 节能模式 | 生成时注入「空闲降帧、交互恢复」约束，减少静止画面下的 GPU/CPU 占用 |
| 会话式修改 | 每次生成带会话标识与版本历史，可用 `revise` 继续对话式增量修改，保留上一版代码便于回退 |
| 多类型壁纸 | 继承上游能力：网页、视频/GIF、应用/游戏、Shadertoy URL、屏幕保护、多显示器、前台应用播放规则等 |

---

## 2. 与上游的关系与许可证（重要）

**本仓库是 [Lively Wallpaper](https://github.com/rocksdanister/lively)（`core-separation` 分支，2.2.1.5）的衍生作品（derivative work）。**

- 许可证依据：仓库根目录的 [LICENSE](./LICENSE) 为上游原样保留的 **GNU General Public License v3.0**（GPL-3.0，全文 35,149 字节，未做任何改写）。
- 因此本仓库同样以 **GPL-3.0** 发布，**不是** MIT/Apache 等宽松协议。
- 上游版权归原作者（rocksdanister 及 Lively 贡献者）所有；本项目对其代码的修改同样以 GPL-3.0 授权。
- 因为上游是 **copyleft**，分发本程序的二进制（含安装包）时需要同时满足：
  1. 提供完整对应的源代码（本仓库即对应源码）；
  2. 保留版权声明与 GPL-3.0 许可证文本；
  3. 衍生部分继续以 GPL-3.0 授权，不得附加额外限制。
- 本仓库相对上游的主要修改：
  - 新增 `Lively.Ai`（AI 生成、校验、自修复、会话）、`Lively.Ai.Cli`（命令行）、`Lively.Packaging`（壁纸打包）；
  - 新增 `Lively/Core/Interaction/*` 与 `Lively.Player.WebView2/Interaction/*`（桌面输入桥接）及对应设置项；
  - 新增 `Lively/Assets/DefaultWallpapers/*` 八张内置壁纸；
  - 界面改为「新建壁纸 / 壁纸库 / 预览 / 设置」四页，替换为自制的黑洞风格图标；
  - 新增 `installer/ContactableWallpaper.iss` 与 `scripts/publish.ps1`、`scripts/package.ps1`；
  - 移除指向上游官网/社区/赞助的链接与在线更新跳转，更新页改为静态说明。
- 名称与标识：英文显示名统一为 **Contactable Wallpaper**；可执行文件名（`Lively.exe`）、数据目录名（`%LOCALAPPDATA%\Lively Wallpaper`）、管道名与注册表项为兼容历史数据而保持不变。Lively 名称与上游图标归上游作者所有，本项目仅作衍生说明之用，与上游官方无隶属关系。

---

## 3. 目录结构

```
.
├─ src/
│  ├─ Lively/                     # 应用解决方案（Lively.sln）
│  │  ├─ Lively/                  # Core 进程：WorkerW 挂载、显示器、壁纸生命周期、gRPC 服务端、交互路由
│  │  ├─ Lively.UI.WinUI/         # 界面进程（WinUI 3）：新建壁纸 / 壁纸库 / 预览 / 设置
│  │  ├─ Lively.UI.Shared/        # UI 共享的 ViewModel 与样式（shared project）
│  │  ├─ Lively.Models/           # 跨进程共享模型、IPC 消息、设置模型
│  │  ├─ Lively.Common/           # P/Invoke、输入、路径常量、存储、日志等通用能力
│  │  ├─ Lively.Common.Services/  # 硬件占用、音频可视化、下载、更新检查等公共服务
│  │  ├─ Lively.Gallery.Client/   # 壁纸库客户端（可选在线壁纸服务）
│  │  ├─ Lively.Grpc.Common/      # proto 契约与生成代码
│  │  ├─ Lively.Grpc.Client/      # UI 侧 gRPC 客户端
│  │  ├─ Lively.Ai/               # AI 生成、校验修复、Self-Refine、会话存储、深度视差
│  │  ├─ Lively.Ai.Cli/           # 命令行入口（不在 sln 内，单独构建）
│  │  ├─ Lively.Packaging/        # 壁纸目录打包与入库
│  │  ├─ Lively.ML/               # MiDaS 深度估计（ONNX Runtime）
│  │  ├─ Lively.Player.WebView2/  # 网页壁纸播放器（含交互桥，net472）
│  │  ├─ Lively.Player.CefSharp/  # 可选网页播放器（net472）
│  │  ├─ Lively.Player.Vlc/       # 视频播放器（net472）
│  │  ├─ Lively.Player.Wmf/       # 系统视频播放器（net472）
│  │  ├─ Lively.Utility.Watchdog/ # 看门狗
│  │  ├─ Lively.Utility.Screensaver/ # 屏幕保护
│  │  ├─ Lively.Utility.Commandline/ # 命令行解析封装
│  │  └─ Lively.Utility.ConsoleDemo/ # 控制台示例
│  └─ installer/                  # 上游遗留的 Inno Setup 脚本（供参考）
├─ installer/                     # 本项目安装脚本 ContactableWallpaper.iss + 中文语言文件
├─ scripts/                       # dev-deploy.ps1 / publish.ps1 / package.ps1
├─ schemas/                       # LivelyProperties.json 的 JSON Schema
├─ docs/depth-parallax.md         # 深度视差能力说明与依赖许可
└─ LICENSE                        # GPL-3.0（上游原文件）
```

---

## 4. 架构概览

四个进程、四层通信，边界严格：

```
Lively.UI.WinUI.exe  ──gRPC(named pipe)──▶  Lively.exe (Core)
                                               │
                                               ├─stdin/stdout NDJSON──▶ Lively.Player.WebView2.exe
                                               │                          │
                                               │                          └─JS 桥──▶ 壁纸页面
                                               │
                                               └─Raw Input / PostMessage──▶ WebView2 的 Chrome_WidgetWin_1
```

- **UI** 只做展示与操作，一切壁纸动作经 gRPC 交给 Core，不直接持有壁纸窗口句柄。
- **Core** 负责 WorkerW 挂载、显示器管理、设置持久化、壁纸生命周期与输入采集（Raw Input）。
- **Player** 只负责渲染；不直接操作 WorkerW。
- **坐标换算只在 Player 内执行一次**：Core 只投递「显示器局部物理像素」，DIP→CSS 的换算在 Player 侧依据页面实测 `innerWidth/innerHeight` 完成。
- 输入注入目标是 WebView2 运行时进程内的 `Chrome_WidgetWin_1` 窗口，并做跨进程归属判定，避免与 Chromium 原生事件双发。
- 交互默认开启（老配置会自动迁移），瞬时开关改动会持久化；壁纸切换、锁屏、Explorer 重启时调用 `InteractionRouter.Reset()` 清理按键状态。

> 说明：部分源码注释引用了内部设计文档编号（如 `docs/contracts/07-...`），该文档属于开发过程中的内部契约，未随本仓库发布；接口事实以源码为准。

---

## 5. 从源码构建

### 5.1 环境要求

| 依赖 | 版本 / 说明 |
| --- | --- |
| Windows | 10 1903（10.0.18362）或更高 / Windows 11，x64 |
| .NET SDK | **9.0**（Core / UI / AI CLI）；Player 与 Watchdog 为 .NET Framework 4.7.2（通过 `Microsoft.NETFramework.ReferenceAssemblies` 免装开发包） |
| Visual Studio | 2022 17.10+（工作负载：.NET 桌面开发），可选；推荐用于调试 WinUI |
| Inno Setup | 6.x（仅打安装包时需要） |
| WebView2 Runtime | 运行网页壁纸需要；安装包会检测并引导安装微软官方运行时 |
| 网络 | NuGet 还原；深度模型首次下载（约 62 MB） |

### 5.2 构建

```powershell
# 还原
dotnet restore src\Lively\Lively.sln

# 构建整个解决方案（Release / x64）
dotnet build src\Lively\Lively.sln -c Release -p:Platform=x64

# AI 命令行不在 sln 内，需要单独构建
dotnet build src\Lively\Lively.Ai.Cli\Lively.Ai.Cli.csproj -c Release -p:Platform=x64
```

也可以直接用 Visual Studio 2022 打开 `src\Lively\Lively.sln`，把 `Lively` 设为启动项目后构建运行。

### 5.3 开发态运行

开发态采用 unpackaged 模式：Core 会到自身输出目录的 `plugins\` 下查找 UI 与播放器。

```powershell
# 把 UI 与 WebView2 播放器的构建产物装配到 Core 的 bin 目录
powershell -ExecutionPolicy Bypass -File scripts\dev-deploy.ps1 -Configuration Release -Platform x64

# 启动
src\Lively\Lively\bin\x64\Release\net9.0-windows10.0.18362.0\Lively.exe
```

`dev-deploy.ps1` 只复制构建产物，不修改 csproj；可重复执行。

### 5.4 发布与打包

```powershell
# 自包含发布（目标机器无需安装 .NET）：artifacts\publish\win-x64
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1

# 生成安装包（需要 Inno Setup 6 的 ISCC.exe）
powershell -ExecutionPolicy Bypass -File scripts\package.ps1
```

说明：

- `publish.ps1` 会发布 Core、UI、AI CLI、WebView2 播放器与 Watchdog，并自动下载微软官方 WebView2 引导安装器（约 2 MB）放入 `prereq\`。若需要完全离线，可显式传 `-WebView2RuntimeDirectory <固定版本运行时目录>` 随包携带运行时。
- `package.ps1` 按原始工作区布局编写，安装包输出到「仓库上两级目录」的 `outputs\`。若你的目录层级不同，可用 `-SkipPublish` 并直接调用 `ISCC`，或修改脚本中的输出路径。
- 可选播放器（CefSharp / WMF / libvlc）默认不进包，`publish.ps1 -IncludeOptionalPlayers` 可开启（会显著增加包体积）。

---

## 6. 配置你自己的 AI 服务

程序**不内置任何服务方密钥**，仓库中也不包含任何真实密钥。你需要准备一个 OpenAI 兼容接口。

### 6.1 界面配置（推荐）

1. 启动应用 → **设置** → **AI 生成**；
2. 填写：

| 字段 | 示例 | 说明 |
| --- | --- | --- |
| BaseUrl | `https://api.deepseek.com/v1` | OpenAI 兼容基础地址；程序会自动补 `/chat/completions` |
| Model | `deepseek-chat` | 与 BaseUrl 匹配的模型名 |
| API Key | `sk-...` | 你的密钥 |

3. 保存后，密钥使用 Windows DPAPI（当前用户）加密，仅以密文形式写入
   `%LOCALAPPDATA%\Lively Wallpaper\AiSettings.json` 的 `ApiKeyProtected` 字段，**不会明文落盘**。

### 6.2 环境变量覆盖（便于联调 / CI）

| 变量 | 作用 |
| --- | --- |
| `LIVELY_AI_BASE_URL` | 覆盖 BaseUrl |
| `LIVELY_AI_MODEL` | 覆盖 Model |
| `LIVELY_AI_API_KEY` | 覆盖 API Key |

环境变量优先级高于配置文件，适合临时切换端点，不建议长期写入系统环境变量。

### 6.3 深度视差（无需 API Key）

深度链路完全本地推理，不访问模型服务；仅首次下载 MiDaS 权重。详见 [docs/depth-parallax.md](./docs/depth-parallax.md)。

---

## 7. 命令行（Lively.Ai.Cli）

```powershell
# 配置
Lively.Ai.Cli config set --base-url https://api.deepseek.com/v1 --model deepseek-chat --api-key sk-xxx
Lively.Ai.Cli config show
Lively.Ai.Cli config clear-key

# 生成
Lively.Ai.Cli generate --prompt "黑洞吸积盘，粒子随指针流动" --out wallpaper.html `
    --width 1920 --height 1080 --style cyber --interaction hover,click --eco

# 会话式续改（增量修改，保留上一版）
Lively.Ai.Cli revise --session <session-id> --prompt "把亮度降低 20%，保留结构"
Lively.Ai.Cli history --session <session-id>

# 照片 → 深度视差壁纸
Lively.Ai.Cli depth --image "C:\photo.jpg" --out "C:\wallpapers\photo-depth"
```

要点：

- 生成结果必须是**单文件、无外部资源**的 HTML，并通过内置结构校验（含交互事件监听检查）；
- 校验失败会自动修复（默认最多 2 次），Self-Refine 默认 2 轮、阈值 7.5（可配置）；不通过的候选不会落盘；
- `--reference <html/js>` 可注入一份本地结构参考（限 32768 字符，不参与校验）。

---

## 8. 数据、日志与隐私

| 内容 | 位置 |
| --- | --- |
| 设置 | `%LOCALAPPDATA%\Lively Wallpaper\Settings.json` |
| AI 配置（密钥为 DPAPI 密文） | `%LOCALAPPDATA%\Lively Wallpaper\AiSettings.json` |
| 壁纸库、会话历史、深度模型 | `%LOCALAPPDATA%\Lively Wallpaper\` 下对应子目录 |
| Core 日志 | `%LOCALAPPDATA%\Lively Wallpaper\logs\*.txt` |
| UI 日志 | `%LOCALAPPDATA%\Lively Wallpaper\UI\*.txt` |

- 程序不收集、不上传个人信息；窗口标题/进程名等仅本地用于播放规则判断。
- 日志与本地配置已被 `.gitignore` 排除，不会被提交。
- 开发调试可用环境变量 `LIVELY_INSTANCE_SUFFIX` 隔离出独立实例（数据目录、互斥体、管道名追加后缀），不影响默认安装。

---

## 9. 第三方组件与素材来源

本仓库自身代码以 GPL-3.0 发布；下列第三方组件/素材遵循各自许可，分发时请一并遵守。

| 组件 / 素材 | 来源 | 许可 |
| --- | --- | --- |
| Lively Wallpaper（上游基线） | https://github.com/rocksdanister/lively | GPL-3.0 |
| Microsoft Windows App SDK / WinUI 3 | Microsoft（NuGet） | MIT |
| CommunityToolkit.WinUI.* / WinUIEx / Microsoft.Xaml.Behaviors.WinUI | Community Toolkit / 社区（NuGet） | MIT |
| WebView2 SDK 与 Evergreen Runtime | Microsoft | Microsoft 专有许可（允许随应用分发/引导安装） |
| CefSharp + Chromium | The CefSharp Authors / Chromium | BSD-3-Clause / BSD 风格及第三方许可 |
| LibVLCSharp + libvlc（VideoLAN） | VideoLAN（NuGet） | LGPL-2.1 |
| Grpc / Grpc.Core / Google.Protobuf | gRPC Authors（NuGet） | Apache-2.0 |
| NLog | NLog Project（NuGet） | BSD-3-Clause |
| Newtonsoft.Json | James Newton-King（NuGet） | MIT |
| Magick.NET（ImageMagick） | ImageMagick（NuGet） | Apache-2.0 |
| Microsoft.ML.OnnxRuntime | Microsoft（NuGet） | MIT |
| NAudio / MathNet.Numerics / Octokit / SharpZipLib / CommandLineParser / H.Hooks / RawInput.Sharp 等 | 各自 NuGet 项目 | MIT / Apache-2.0 等，见各自仓库 |
| MiDaS（深度模型与算法） | Intel ISL `isl-org/MiDaS` | MIT |
| MiDaS ONNX 权重包 | `lively-community/lively-ml-models` 发布资产 | 上游仓库未声明许可证；**不随仓库/安装包分发**，首次使用时由用户机器下载 |
| Three.js（深度模板，随模型包分发） | three.js | MIT |
| depthmap-wallpaper 模板 | rocksdanister | MIT（生成产物中随附 `License.txt`） |
| icons8 图标素材 | https://icons8.com | icons8 免费许可（要求保留来源链接） |
| 内置壁纸 `astral` / `aurora` / `lumen` | 本项目自制 | GPL-3.0 |
| 界面图标（黑洞系列）、`CustomIcons.ttf` | 本项目自制 | GPL-3.0 |

未包含在仓库中的内容（需自行获取）：MiDaS 权重、WebView2 运行时、Cef/libvlc 原生库（NuGet 还原）、安装包构建工具。

---

## 10. 已知限制

- 桌面图标遮挡区域无法接收壁纸点击（系统层级限制）。
- 网页壁纸的交互由 Core 采集 + 消息投递实现，某些以管理员权限运行的前台程序可能阻断 Raw Input。
- MiDaS small 会先把输入缩放到模型要求的较低分辨率，深度边缘细节有限；4K 图在 CPU 上单次推理需要数秒。
- 交互坐标换算依赖页面实测尺寸，页面若在加载后大幅改变视口且未更新尺寸，首次输入可能被放弃（不会猜测比例）。
- 上游遗留的 `src/installer/` 脚本与本项目的 `installer/ContactableWallpaper.iss` 并存，出包请使用后者。

---

## 11. 贡献

欢迎提交 Issue 与 Pull Request。提交前请：

1. 保持 `Lively.Models` / `Lively.Grpc.Common` 中的跨进程契约只做**追加**，不改既有字段编号与消息编号；
2. 新增设置字段必须提供默认值，保证旧版 `Settings.json` 可反序列化；
3. 交互相关改动需同时验证「总开关关闭时完全不注入输入」；
4. 不要提交本地配置、密钥、日志与构建产物（`.gitignore` 已覆盖）。

---

## 12. 免责声明

本软件按 GPL-3.0 的「无担保」条款提供。AI 生成内容由你所配置的第三方模型服务产生，其质量、合规性与费用由你与服务方约定；请勿生成侵犯他人权利的内容。本项目与 Lively Wallpaper 官方、Microsoft、VideoLAN、Intel 均无隶属或背书关系。
