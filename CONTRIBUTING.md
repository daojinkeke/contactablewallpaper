# 贡献指南

感谢你愿意参与 Contactable Wallpaper。

## 基本约定

- 本项目是 [Lively Wallpaper](https://github.com/rocksdanister/lively) 的 GPL-3.0 衍生物，提交代码即表示你同意以 GPL-3.0 授权你的贡献。
- 请先开 Issue 描述问题或方案，较大的改动建议先对齐设计再动手。
- 一次 PR 只做一件事，避免混入无关的格式化或重命名。

## 代码约束

1. **跨进程契约只做追加**：`Lively.Models` 与 `Lively.Grpc.Common`（`*.proto`）中的字段编号、IPC 消息编号（0–19 已冻结）不得重排或复用，新消息只能追加在末尾。
2. **设置向后兼容**：`SettingsModel` 新增字段必须在构造函数中显式赋默认值，保证旧版 `Settings.json` 可正常反序列化。
3. **进程边界**：UI 不直接持有 WebView2 / 播放器对象，所有壁纸动作经 gRPC 交给 Core。
4. **坐标换算唯一职责**：Core 只输出显示器局部物理像素，DIP→CSS 换算只在 Player 内做一次，禁止两端重复换算。
5. **交互开关双层生效**：`InteractionEnabled` 关闭时，输入处理入口与策略链都不得采集、注入或补发任何输入。
6. **WebView2 检测**：保持「随包运行时 → HKCU → HKLM64 → HKLM32」多路兜底，不要退回单一检测路径。
7. **不要提交**：本地配置、密钥、日志、构建产物、模型权重、用户数据（`.gitignore` 已覆盖，提交前请确认 `git status`）。

## 本地验证

```powershell
dotnet build src\Lively\Lively.sln -c Release -p:Platform=x64
dotnet build src\Lively\Lively.Ai.Cli\Lively.Ai.Cli.csproj -c Release -p:Platform=x64
```

涉及交互的改动，请在真实桌面（前台可见、非远程桌面）验证一次，并在 PR 中说明：投递目标窗口、页面实际收到的事件类型、以及关闭总开关后的行为。

## 提交信息

使用简短的中文或英文祈使句，例如：

```
修复：网页壁纸在双屏下 pointerleave 未复位
feat: add revise session history to AI CLI
```
