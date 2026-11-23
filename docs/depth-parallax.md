# 照片 → 深度视差壁纸（能力说明）

实现位置：`src/Lively/Lively.Ai/Depth/`，命令行入口：`Lively.Ai.Cli depth`。
推理代码与 ONNX Runtime 随程序内置；模型权重不打包，首次使用时下载，之后完全离线。

## 1. 组成

| 部分 | 是否内置 | 说明 |
| --- | --- | --- |
| MiDaS 推理实现 | 内置 | 直接复用基线 `Lively.ML/DepthEstimate/MiDaS.cs`，未重写 |
| ONNX Runtime | 内置 | 复用 `Lively.ML` 的 `Microsoft.ML.OnnxRuntime` 引用，CPU 推理 |
| `model.onnx` 权重 | 外置 | 首次使用时下载 |
| Three.js 模板 | 外置 | 与模型同包下载，解压到 `Templates/0` |

## 2. 下载

- 地址：`https://github.com/lively-community/lively-ml-models/releases/download/v1.0.0.0/midas_small.zip`
  （与基线 `DepthEstimateWallpaperViewModel` 同一发布仓库与资产名；固定版本号，避免上游发新版后哈希失配）
- 大小：`62,046,014` 字节（约 59.2 MiB）
- SHA-256：`e53fb39c98ea9f9d57fb3856a25829e813d0d9d497c0d447688ec56f56bab4c9`
- 可用环境变量 `LIVELY_MIDAS_URL` 覆盖下载地址（仅限校验通过的包，哈希仍以当前版本常量为准）
- 解压位置：`%LOCALAPPDATA%\<实例数据目录>\ML\Midas`，与基线 `Constants.MachineLearning.MiDaSDir` 一致

下载行为：

- 进度回调：`IMidasModelManager.EnsureAsync(IProgress<MidasModelProgress>, CancellationToken)`
- 断点续传：复用同目录 `midas_small.zip.part`，以 HTTP `Range` 从已有长度续传；服务端不支持 206 时自动从头重下
- 完整性校验：先比大小、再比 SHA-256；失败的文件改名 `*.bad` 保留，不覆盖已解压模型
- 解压：先解到 `.extract-<guid>` 临时目录，确认 `model.onnx` 与 `Templates/0` 齐全后才逐文件写入根目录；
  已存在完整模型时直接跳过，不会把可用文件写坏
- 失败可重试：再次调用 `EnsureAsync` 即从断点继续
- 包内 `midas_small.zip` 会保留在模型目录，作为离线修复源（可随时删除，不影响已解压模型）

## 3. 离线可用性

- 模型解压完成后，推理与壁纸生成全程不发起任何网络请求
- 生成的 `index.html` 只引用同目录下的 `js/`、`media/`，无 `http(s)`、无 CDN、无绝对路径；
  通过 `HtmlWallpaperValidator`（07 契约 V001–V008）离线校验，仅外部脚本场景下的 V008 静态扫描误报
- `index.single.html` 为可选便携版：Three.js、脚本、原图与深度图全部内联，可直接双击预览；
  因其内联了 three.js 库源码（含 `XMLHttpRequest` 字样），不用于 Lively 挂载，也不参与离线校验

## 4. 许可证

| 组件 | 许可证 | 说明 |
| --- | --- | --- |
| MiDaS 模型与算法（Intel ISL） | MIT | `isl-org/MiDaS` |
| ONNX Runtime | MIT | Microsoft |
| Three.js | MIT | 随模型包分发 |
| dat.GUI | Apache-2.0 | 随模型包分发，仅用于模板调试面板 |
| depthmap-wallpaper 模板（rocksdanister） | MIT | 包内 `Templates/0/License.txt`，生成壁纸时原样复制到输出目录 |
| `lively-ml-models` 发布包 | 仓库未声明许可证 | 见下 |

`lively-community/lively-ml-models` 仓库当前没有 LICENSE 文件（GitHub API 返回 `license: null`）。
因此本产品**不随安装包分发**该权重包，而是在首次使用时由用户机器从上述 GitHub 地址下载。
若后续需要把模型打进安装包或做商业再分发，需先向上游确认 ONNX 权重的授权；
如无法确认，可改用 Intel ISL 官方 MIT 许可的 MiDaS 权重自行导出 ONNX，并通过 `LIVELY_MIDAS_URL`
把下载地址切换到自有托管（同时更新常量中的大小与 SHA-256）。

## 5. 命令行验证

```powershell
Lively.Ai.Cli depth --image "C:\photo.jpg" --out "C:\wallpapers\photo-depth"
```

可选参数：`--title`（壁纸标题）、`--model-root`（覆盖模型目录，便于隔离测试）、`--max-image`（内联原图长边，默认 2560）。

输出目录（可直接加入 Lively 壁纸库）：

```
index.html            模板入口（本地引用 js/、media/）
index.single.html     单文件便携预览
LivelyInfo.json       FileName=index.html，Arguments=--pause-event true
thumbnail.jpg         480x270
LivelyProperties.json 模板参数面板（阈值、拉伸、模糊、帧率）
License.txt           模板 MIT 许可
js/three.min.js
js/dat.gui.min.js
js/script.js          模板脚本 + pointermove 兼容 + 静止漂浮补丁
media/image.jpg       规范化原图（去元数据，>3840 先等比缩小）
media/depth.jpg       着色器采样用深度图（≤2048，灰度 JPEG）
media/depth.png       完整分辨率灰度深度图（8-bit 灰度，colorType=0）
```

## 6. 已知限制

- MiDaS small 的模型输入固定 256×256（沿用基线，未改参数），深度图由 256×256 放大到原图尺寸，边缘细节有限
- 静止漂浮为追加行为：鼠标停止 4 秒后按正弦/余弦缓慢漂移，重新移动时重新锚定原点，避免跳变
- 交互不写握手代码：页面同时监听 `pointermove` 并转成模板既有的 `mousemove` 逻辑，适配桥默认合成的 pointer 事件
- 原文管线与基线一致：`MiDaS.Run` 逐像素取值的实现未改动，4K 图单次推理约数秒
