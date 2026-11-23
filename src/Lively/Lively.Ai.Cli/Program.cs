using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lively.Ai;
using Lively.Ai.Depth;
using Lively.Models.Ai;

namespace Lively.Ai.Cli
{
    /// <summary>
    /// 独立的 AI 壁纸生成命令行入口，便于脱离界面单测生成链路。
    /// </summary>
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);

            if (args.Length == 0 || IsHelp(args[0]))
            {
                PrintUsage();
                return 0;
            }

            var command = args[0].ToLowerInvariant();
            var rest = args.Length > 1 ? args[1..] : Array.Empty<string>();

            try
            {
                switch (command)
                {
                    case "generate":
                    case "gen":
                        return await GenerateAsync(rest);
                    case "revise":
                    case "edit":
                        return await ReviseAsync(rest);
                    case "history":
                        return RunHistory(rest);
                    case "config":
                        return RunConfig(rest);
                    case "depth":
                    case "d":
                        return await DepthAsync(rest);
                    default:
                        PrintUsage();
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("未预期的错误：" + ex.Message);
                return 1;
            }
        }

        private static int RunConfig(string[] args)
        {
            var store = new AiSettingsStore();
            var action = args.Length > 0 ? args[0].ToLowerInvariant() : "show";

            if (action == "show")
            {
                var settings = store.Load(false);
                var apiKey = store.GetApiKey(settings);
                Console.WriteLine("配置文件：" + store.SettingsPath);
                Console.WriteLine("BaseUrl  ：" + (string.IsNullOrWhiteSpace(settings.BaseUrl) ? "（未填写）" : settings.BaseUrl));
                Console.WriteLine("Model    ：" + (string.IsNullOrWhiteSpace(settings.Model) ? "（未填写）" : settings.Model));
                Console.WriteLine("Provider ：" + (string.IsNullOrWhiteSpace(settings.Provider) ? "（未填写，可忽略）" : settings.Provider));
                Console.WriteLine("Timeout  ：" + settings.TimeoutSeconds + " 秒（无增量超时）");
                Console.WriteLine("自动修复 ：最多 " + settings.MaxRepairAttempts + " 次");
                Console.WriteLine("默认交互 ：" + DescribeInteraction(settings.DefaultInteraction) +
                                  "（generate 未传 --interaction 时使用）");
                Console.WriteLine("自我精修 ：最多 " + settings.MaxRefineRounds + " 轮，低于 " +
                                  settings.RefineScoreThreshold.ToString("0.0") + "/10 时重写" +
                                  (settings.MaxRefineRounds <= 0 ? "（已关闭）" : string.Empty));
                Console.WriteLine("API Key  ：" + DescribeKey(settings, apiKey));
                if (HasEnvOverride())
                    Console.WriteLine("提示：检测到 LIVELY_AI_* 环境变量，环境变量优先级高于配置文件。");
                return 0;
            }

            if (action == "set")
            {
                var settings = store.Load(false);
                var baseUrl = GetArg(args, "--base-url");
                var model = GetArg(args, "--model");
                var apiKey = GetArg(args, "--api-key");
                var provider = GetArg(args, "--provider");
                var timeout = GetArg(args, "--timeout");
                var maxRepairs = GetArg(args, "--max-repairs");
                var interaction = GetArg(args, "--interaction");
                var refineRounds = GetArg(args, "--refine-rounds");
                var refineThreshold = GetArg(args, "--refine-threshold");

                if (baseUrl != null)
                {
                    if (!baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                        !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.Error.WriteLine("--base-url 必须以 http:// 或 https:// 开头。");
                        return 2;
                    }
                    settings.BaseUrl = baseUrl.Trim();
                }
                if (model != null)
                    settings.Model = model.Trim();
                if (provider != null)
                    settings.Provider = provider.Trim();
                if (timeout != null)
                {
                    if (!int.TryParse(timeout, out var seconds) || seconds < 5 || seconds > 600)
                    {
                        Console.Error.WriteLine("--timeout 需要是 5–600 之间的秒数。");
                        return 2;
                    }
                    settings.TimeoutSeconds = seconds;
                }
                if (maxRepairs != null)
                {
                    if (!int.TryParse(maxRepairs, out var repairs) || repairs < 0 || repairs > 2)
                    {
                        Console.Error.WriteLine("--max-repairs 需要是 0–2 之间的整数。");
                        return 2;
                    }
                    settings.MaxRepairAttempts = repairs;
                }
                if (interaction != null)
                {
                    if (!TryParseInteraction(interaction, out var capability))
                    {
                        Console.Error.WriteLine("--interaction 只接受 none|click|hover|drag|keyboard 的逗号组合，例如 hover,click。");
                        return 2;
                    }
                    settings.DefaultInteraction = capability;
                }
                if (refineRounds != null)
                {
                    if (!int.TryParse(refineRounds, out var rounds) || rounds < 0 || rounds > 3)
                    {
                        Console.Error.WriteLine("--refine-rounds 需要是 0–3 之间的整数（0 表示关闭自我精修）。");
                        return 2;
                    }
                    settings.MaxRefineRounds = rounds;
                }
                if (refineThreshold != null)
                {
                    if (!double.TryParse(refineThreshold, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold) ||
                        threshold < 0 || threshold > 10)
                    {
                        Console.Error.WriteLine("--refine-threshold 需要是 0–10 之间的数字。");
                        return 2;
                    }
                    settings.RefineScoreThreshold = threshold;
                }
                if (apiKey != null)
                    store.SetApiKey(settings, apiKey.Trim());

                store.Save(settings);
                Console.WriteLine("已保存：" + store.SettingsPath);
                Console.WriteLine("BaseUrl  ：" + (string.IsNullOrWhiteSpace(settings.BaseUrl) ? "（未填写）" : settings.BaseUrl));
                Console.WriteLine("Model    ：" + (string.IsNullOrWhiteSpace(settings.Model) ? "（未填写）" : settings.Model));
                Console.WriteLine("默认交互 ：" + DescribeInteraction(settings.DefaultInteraction));
                Console.WriteLine("API Key  ：" + DescribeKey(settings, store.GetApiKey(settings)));
                Console.WriteLine("提示：API Key 使用 Windows DPAPI(CurrentUser) 加密，仅当前用户可解密，不会写入日志。");
                return 0;
            }

            if (action == "clear-key")
            {
                var settings = store.Load(false);
                store.SetApiKey(settings, string.Empty);
                store.Save(settings);
                Console.WriteLine("已清除保存的 API Key：" + store.SettingsPath);
                return 0;
            }

            Console.Error.WriteLine("未知的 config 子命令：" + action);
            return 2;
        }

        private static async Task<int> ReviseAsync(string[] args)
        {
            var sessionId = GetArg(args, "--session") ?? GetArg(args, "-s");
            var instruction = GetArg(args, "--prompt") ?? GetArg(args, "-p");
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                Console.Error.WriteLine("缺少 --session。示例：revise --session wp-xxxx --prompt \"把光晕调暗一点\"");
                return 2;
            }
            if (string.IsNullOrWhiteSpace(instruction))
            {
                Console.Error.WriteLine("缺少 --prompt（本次修改诉求）。");
                return 2;
            }

            var sessions = new WallpaperSessionStore();
            if (!sessions.TryLoad(sessionId, out var session) || session.Versions.Count == 0)
            {
                Console.Error.WriteLine("找不到会话：" + sessionId);
                Console.Error.WriteLine("项目目录：" + Path.Combine(sessions.RootDir, sessionId));
                return 2;
            }

            var store = new AiSettingsStore();
            var settings = store.Load();
            if (!store.IsConfigured(settings))
            {
                Console.Error.WriteLine("尚未配置 AI，无法修改壁纸。请先 config set。");
                return 2;
            }

            var width = ParseInt(GetArg(args, "--width"), 1920);
            var height = ParseInt(GetArg(args, "--height"), 1080);
            var interaction = settings.DefaultInteraction;
            var interactionArg = GetArg(args, "--interaction");
            if (interactionArg != null && !TryParseInteraction(interactionArg, out interaction))
            {
                Console.Error.WriteLine("--interaction 只接受 none|click|hover|drag|keyboard 的逗号组合，例如 hover,click。");
                return 2;
            }
            if (!TryGetRoundsOverride(args, out var roundsOverride))
            {
                Console.Error.WriteLine("--refine-rounds 需要是 0–3 之间的整数（修改至少执行 1 轮）。");
                return 2;
            }

            // 显式 --eco / --no-eco 优先；未传参时沿用会话创建时的节能设置。
            var ecoFlag = GetEcoFlag(args);
            var eco = ecoFlag ?? session.Eco;

            var request = new GenerationRequest
            {
                Prompt = session.Prompt,
                TargetWidth = width,
                TargetHeight = height,
                Interaction = interaction,
                Style = ParseStyle(GetArg(args, "--style")),
                PrimaryColor = GetArg(args, "--color"),
                OfflineRequired = true,
                EcoMode = eco
            };

            Console.WriteLine($"会话 {sessionId}：基于 v{session.Versions[^1].Version} 修改");
            Console.WriteLine($"修改诉求：{instruction}");
            Console.WriteLine("节能模式：" + (eco ? "开" : "关") +
                              (ecoFlag.HasValue ? "（本次显式指定）" : "（沿用会话设置）"));

            var service = new AiGenerationService(store);
            var stopwatch = Stopwatch.StartNew();
            GenerationEvent terminal = null;
            await foreach (var item in service.ReviseAsync(sessionId, instruction, request, roundsOverride))
            {
                if (string.IsNullOrEmpty(item.ContentDelta))
                    PrintProgressEvent(item);

                if (item.State == GenerationState.ready ||
                    item.State == GenerationState.failed ||
                    item.State == GenerationState.canceled)
                {
                    terminal = item;
                    break;
                }
            }
            stopwatch.Stop();

            if (terminal == null)
            {
                Console.Error.WriteLine("修改中断：没有收到最终状态。");
                return 1;
            }

            PrintMetrics(terminal, stopwatch.Elapsed);
            if (terminal.State != GenerationState.ready)
            {
                if (terminal.State == GenerationState.canceled)
                    Console.WriteLine("已取消，会话未改动。");
                else
                {
                    Console.Error.WriteLine("修改失败：" + terminal.ErrorMessage);
                    if (terminal.Issues?.Issues != null)
                    {
                        foreach (var issue in terminal.Issues.Issues)
                            Console.Error.WriteLine($"  {issue.Code}: {issue.Message}");
                    }
                }
                return 1;
            }

            var versionPath = sessions.GetVersionPath(sessionId, terminal.SessionVersion);
            Console.WriteLine("完成");
            if (terminal.RefineScore.HasValue)
                Console.WriteLine($"本轮评分：{terminal.RefineScore.Value:0.0}/10");
            Console.WriteLine($"会话标识：{sessionId}（v{terminal.SessionVersion}）");
            Console.WriteLine("会话版本：" + versionPath);
            if (!string.IsNullOrEmpty(terminal.RefinePlan))
                Console.WriteLine("本轮计划：" + terminal.RefinePlan.Replace("\n", "；"));
            if (!string.IsNullOrEmpty(terminal.RefineSummary))
                Console.WriteLine("问题摘要：" + terminal.RefineSummary);

            var output = GetArg(args, "--out") ?? GetArg(args, "-o");
            if (!string.IsNullOrWhiteSpace(output))
            {
                output = Path.GetFullPath(output);
                Directory.CreateDirectory(Path.GetDirectoryName(output) ?? ".");
                File.Copy(versionPath, output, true);
                Console.WriteLine("已写入：" + output);
            }
            Console.WriteLine("继续修改：Lively.Ai.Cli revise --session " + sessionId + " --prompt \"<修改诉求>\"");
            return 0;
        }

        private static int RunHistory(string[] args)
        {
            var sessionId = GetArg(args, "--session") ?? GetArg(args, "-s");
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                Console.Error.WriteLine("缺少 --session。示例：history --session wp-xxxx");
                return 2;
            }

            var store = new WallpaperSessionStore();
            if (!store.TryLoad(sessionId, out var session))
            {
                Console.Error.WriteLine("找不到会话：" + sessionId);
                return 2;
            }

            Console.WriteLine("会话标识：" + session.SessionId);
            Console.WriteLine("原始提示词：" + session.Prompt);
            Console.WriteLine("节能模式：" + (session.Eco ? "开" : "关"));
            Console.WriteLine("创建时间：" + session.CreatedAt);
            Console.WriteLine("项目目录：" + Path.Combine(store.RootDir, session.SessionId));
            Console.WriteLine();
            foreach (var version in session.Versions)
            {
                var score = version.Score.HasValue ? version.Score.Value.ToString("0.0") + "/10" : "未评分";
                Console.WriteLine($"v{version.Version}  {version.CreatedAt}  {score}  " +
                                  $"{version.ElapsedMs / 1000.0:0.0}s  {version.TotalTokens:N0} tok");
                Console.WriteLine("    改了什么：" + version.Summary);
                if (!string.IsNullOrEmpty(version.Instruction))
                    Console.WriteLine("    用户诉求：" + version.Instruction);
                Console.WriteLine("    文件：" + store.GetVersionPath(session.SessionId, version.Version));
            }
            return 0;
        }

        private static void PrintProgressEvent(GenerationEvent item)
        {
            switch (item.State)
            {
                case GenerationState.queued:
                    Console.WriteLine("已提交…");
                    break;
                case GenerationState.generating:
                    if (item.Attempt <= 1)
                        Console.WriteLine("正在生成…");
                    break;
                case GenerationState.repairing:
                    Console.WriteLine($"校验未通过，正在自动修复（第 {item.Attempt} 次）…");
                    break;
                case GenerationState.validating:
                    Console.WriteLine("正在校验…");
                    break;
                case GenerationState.critiquing:
                    if (item.RefineScore.HasValue)
                    {
                        Console.WriteLine($"[精修 {item.RefineRound}] 评分 {item.RefineScore.Value:0.0}/10" +
                                          (string.IsNullOrEmpty(item.RefineSummary) ? string.Empty : "：" + item.RefineSummary));
                        if (!string.IsNullOrEmpty(item.RefinePlan))
                            Console.WriteLine("  计划：" + item.RefinePlan.Replace("\n", "；"));
                    }
                    else
                    {
                        Console.WriteLine($"[精修 {item.RefineRound}] 正在评分并制定修改计划…");
                    }
                    break;
                case GenerationState.refining:
                    if (!string.IsNullOrEmpty(item.RefineSummary) &&
                        (item.RefineSummary.Contains("未通过校验") ||
                         item.RefineSummary.Contains("重写失败") ||
                         item.RefineSummary.Contains("返回为空")))
                        Console.WriteLine($"[精修 {item.RefineRound}] {item.RefineSummary}");
                    else
                        Console.WriteLine($"[精修 {item.RefineRound}] 正在按计划改写…");
                    break;
            }
        }

        private static bool TryGetRoundsOverride(string[] args, out int? rounds)
        {
            rounds = null;
            var raw = GetArg(args, "--refine-rounds");
            if (raw == null)
                return true;
            if (!int.TryParse(raw, out var value) || value < 0 || value > 3)
                return false;
            rounds = value;
            return true;
        }

        /// <summary>
        /// 解析节能开关：--eco 开、--no-eco 关；都没传返回 null（revise 时表示沿用会话设置）。
        /// </summary>
        private static bool? GetEcoFlag(string[] args)
        {
            if (Array.Exists(args, a => string.Equals(a, "--no-eco", StringComparison.OrdinalIgnoreCase)))
                return false;
            if (Array.Exists(args, a => string.Equals(a, "--eco", StringComparison.OrdinalIgnoreCase)))
                return true;
            return null;
        }

        private static async Task<int> GenerateAsync(string[] args)
        {
            var prompt = GetArg(args, "--prompt") ?? GetArg(args, "-p");
            if (string.IsNullOrWhiteSpace(prompt))
            {
                Console.Error.WriteLine("缺少 --prompt。示例：generate --prompt \"星空下的吸积盘\" --out C:\\temp\\wallpaper.html");
                return 2;
            }

            var store = new AiSettingsStore();
            var settings = store.Load();
            if (!store.IsConfigured(settings))
            {
                Console.Error.WriteLine("尚未配置 AI。请先填写 BaseUrl / Model / API Key：");
                Console.Error.WriteLine("  Lively.Ai.Cli config set --base-url https://api.openai.com/v1 --model gpt-4o-mini --api-key sk-...");
                Console.Error.WriteLine("或用环境变量临时提供：LIVELY_AI_BASE_URL / LIVELY_AI_MODEL / LIVELY_AI_API_KEY");
                Console.Error.WriteLine("配置文件位置：" + store.SettingsPath);
                return 2;
            }

            var outputPath = GetArg(args, "--out") ?? GetArg(args, "-o") ??
                             Path.Combine(Environment.CurrentDirectory,
                                 "wallpaper-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".html");
            outputPath = Path.GetFullPath(outputPath);

            var width = ParseInt(GetArg(args, "--width"), 1920);
            var height = ParseInt(GetArg(args, "--height"), 1080);
            if (width < 320 || height < 240 || width > 16384 || height > 16384)
            {
                Console.Error.WriteLine("--width / --height 超出合理范围。");
                return 2;
            }

            var interaction = settings.DefaultInteraction;
            var interactionArg = GetArg(args, "--interaction");
            if (interactionArg != null)
            {
                if (!TryParseInteraction(interactionArg, out var requested))
                {
                    Console.Error.WriteLine("--interaction 只接受 none|click|hover|drag|keyboard 的逗号组合，例如 hover,click。");
                    return 2;
                }
                interaction = requested;
            }

            // --reference 只作为提示词里的结构参考，不写盘、不参与校验。
            const int maxReferenceChars = 32 * 1024;
            string? referenceText = null;
            var referencePath = GetArg(args, "--reference");
            if (referencePath != null)
            {
                var referenceFullPath = Path.GetFullPath(referencePath);
                if (!File.Exists(referenceFullPath))
                {
                    Console.Error.WriteLine("找不到参考文件：" + referenceFullPath);
                    return 2;
                }
                referenceText = File.ReadAllText(referenceFullPath);
                if (referenceText.Length > maxReferenceChars)
                {
                    referenceText = referenceText.Substring(0, maxReferenceChars);
                    Console.Error.WriteLine($"警告：参考文件超过 {maxReferenceChars / 1024} KB，已截断到前 {maxReferenceChars:N0} 个字符。");
                }
            }

            if (!TryGetRoundsOverride(args, out var roundsOverride))
            {
                Console.Error.WriteLine("--refine-rounds 需要是 0–3 之间的整数。");
                return 2;
            }

            var request = new GenerationRequest
            {
                Prompt = prompt,
                TargetWidth = width,
                TargetHeight = height,
                Style = ParseStyle(GetArg(args, "--style")),
                PrimaryColor = GetArg(args, "--color"),
                Interaction = interaction,
                ReferenceText = referenceText ?? string.Empty,
                OfflineRequired = true,
                EcoMode = GetEcoFlag(args) ?? false
            };

            Console.WriteLine($"使用 {settings.Model} @ {settings.BaseUrl}");
            Console.WriteLine($"目标分辨率 {width}x{height}，输出 {outputPath}");
            Console.WriteLine("交互能力：" + DescribeInteraction(request.Interaction) +
                              (interactionArg == null ? "（来自配置默认值）" : string.Empty));
            Console.WriteLine("节能模式：" + (request.EcoMode ? "开（注入低功耗行为约束）" : "关"));
            var referenceParts = new List<string>();
            if (request.Interaction != InteractionCapability.none)
                referenceParts.Add("内置交互骨架");
            if (!string.IsNullOrEmpty(referenceText))
                referenceParts.Add("外部参考（" + Path.GetFileName(referencePath) + "）");
            Console.WriteLine("参考代码：" +
                              (referenceParts.Count == 0 ? "未使用（交互已关闭）" : string.Join(" + ", referenceParts)));

            var service = new AiGenerationService(store);
            var stopwatch = Stopwatch.StartNew();
            var content = new StringBuilder();
            long lastProgressMs = 0;
            GenerationEvent terminal = null;

            await foreach (var item in service.GenerateAsync(request, CancellationToken.None, roundsOverride))
            {
                if (string.IsNullOrEmpty(item.ContentDelta))
                    PrintProgressEvent(item);

                if (!string.IsNullOrEmpty(item.ContentDelta))
                {
                    content.Append(item.ContentDelta);
                    if (stopwatch.ElapsedMilliseconds - lastProgressMs > 2000)
                    {
                        lastProgressMs = stopwatch.ElapsedMilliseconds;
                        Console.WriteLine($"      已接收 {content.Length:N0} 字符，{stopwatch.Elapsed.TotalSeconds:0.0}s");
                    }
                }

                if (item.State == GenerationState.ready ||
                    item.State == GenerationState.failed ||
                    item.State == GenerationState.canceled)
                {
                    terminal = item;
                    break;
                }
            }

            stopwatch.Stop();

            if (terminal == null)
            {
                Console.Error.WriteLine("生成中断：没有收到最终状态。");
                return 1;
            }

            if (terminal.State != GenerationState.ready)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine($"生成失败（{terminal.ErrorCode ?? "unknown"}）：{terminal.ErrorMessage}");
                if (terminal.Issues?.Issues != null && terminal.Issues.Issues.Count > 0)
                {
                    foreach (var issue in terminal.Issues.Issues)
                        Console.Error.WriteLine($"  {issue.Code} {issue.Severity}: {issue.Message}");
                }
                var snapshot = service.GetJob(terminal.JobId);
                if (!string.IsNullOrEmpty(snapshot?.DraftPath) && File.Exists(snapshot.DraftPath) &&
                    new FileInfo(snapshot.DraftPath).Length > 0)
                    Console.Error.WriteLine("未采用的草稿保留在：" + snapshot.DraftPath + "（未写入 --out）");
                PrintMetrics(terminal, stopwatch.Elapsed);
                return 1;
            }

            // 只写服务端标记为 ready 的草稿：它已通过 V001–V009（含精修轮次）。
            var html = content.Length > 0
                ? AiGenerationService.ExtractHtml(content.ToString())
                : string.Empty;
            if (!string.IsNullOrEmpty(terminal.DraftPath) && File.Exists(terminal.DraftPath))
                html = File.ReadAllText(terminal.DraftPath);
            if (string.IsNullOrWhiteSpace(html))
            {
                Console.Error.WriteLine("生成失败：返回内容为空。");
                return 1;
            }

            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(outputPath, html, new UTF8Encoding(false));

            Console.WriteLine("[3/3] 完成");
            if (terminal.RefineScore.HasValue)
                Console.WriteLine($"最终评分：{terminal.RefineScore.Value:0.0}/10（第 {terminal.RefineRound} 轮" +
                                  (string.IsNullOrEmpty(terminal.RefineSummary) ? string.Empty : "：" + terminal.RefineSummary) + "）");
            Console.WriteLine("已写入：" + outputPath);
            Console.WriteLine("文件大小：" + new FileInfo(outputPath).Length.ToString("N0") + " 字节");
            if (!string.IsNullOrEmpty(terminal.SessionId))
            {
                var sessionVersionPath = new WallpaperSessionStore()
                    .GetVersionPath(terminal.SessionId, terminal.SessionVersion);
                Console.WriteLine($"会话标识：{terminal.SessionId}（v{terminal.SessionVersion}）");
                Console.WriteLine("会话版本：" + sessionVersionPath);
                Console.WriteLine("继续修改：Lively.Ai.Cli revise --session " + terminal.SessionId + " --prompt \"<修改诉求>\"");
            }
            if (terminal.Issues?.Issues != null)
            {
                foreach (var issue in terminal.Issues.Issues)
                    Console.WriteLine($"  提示 {issue.Code}: {issue.Message}");
            }
            PrintMetrics(terminal, stopwatch.Elapsed);
            return 0;
        }

        private static async Task<int> DepthAsync(string[] args)
        {
            var image = GetArg(args, "--image") ?? GetArg(args, "-i");
            if (string.IsNullOrWhiteSpace(image))
            {
                Console.Error.WriteLine("缺少 --image。示例：depth --image C:\\photo.jpg --out C:\\wallpaper");
                return 2;
            }
            if (!File.Exists(image))
            {
                Console.Error.WriteLine("找不到图片：" + image);
                return 2;
            }

            var output = GetArg(args, "--out") ?? GetArg(args, "-o") ??
                         Path.Combine(Environment.CurrentDirectory,
                             "depth-wallpaper-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            output = Path.GetFullPath(output);

            var request = new DepthWallpaperRequest
            {
                ImagePath = Path.GetFullPath(image),
                OutputDirectory = output,
                Title = GetArg(args, "--title"),
                ModelRoot = GetArg(args, "--model-root"),
                MaxImageLongSide = ParseInt(GetArg(args, "--max-image"), 2560),
            };

            Console.WriteLine("输入：" + request.ImagePath);
            Console.WriteLine("输出：" + request.OutputDirectory);
            Console.WriteLine("模型：内置 MiDaS 推理 + ONNX Runtime，权重首次使用时下载（不打包）");

            var service = new DepthWallpaperService();
            var lastLine = string.Empty;
            var downloading = false;
            var progress = new Progress<DepthGenerationProgress>(p =>
            {
                var isDownload = p.Stage == DepthGenerationStage.downloading_model;

                // 下载阶段用单行覆盖刷新，避免刷屏。
                if (isDownload)
                {
                    var line = $"[{p.Fraction,6:P0}] {p.Message}";
                    if (line == lastLine)
                        return;
                    lastLine = line;
                    downloading = true;
                    Console.Write("\r" + line + "   ");
                    return;
                }

                if (downloading)
                {
                    Console.WriteLine();
                    downloading = false;
                    lastLine = string.Empty;
                }

                var text = $"[{p.Fraction,6:P0}] {p.Message}";
                if (text == lastLine)
                    return;
                lastLine = text;
                Console.WriteLine(text);
            });

            try
            {
                var result = await service.GenerateAsync(request, progress, CancellationToken.None);

                Console.WriteLine();
                Console.WriteLine("完成：");
                Console.WriteLine("  壁纸目录：" + result.WallpaperDirectory);
                Console.WriteLine("  入口 HTML：" + result.HtmlPath + $"（{result.HtmlBytes / 1024d:0.0} KB，本地引用，可直接挂载）");
                Console.WriteLine("  单文件预览：" + result.SingleFileHtmlPath);
                Console.WriteLine("  深度图  ：" + result.DepthPngPath);
                Console.WriteLine("  缩略图  ：" + result.ThumbnailPath);
                Console.WriteLine("  元数据  ：" + result.LivelyInfoPath);
                Console.WriteLine($"  原图尺寸：{result.Width}x{result.Height}");

                if (result.Issues != null && result.Issues.Count > 0)
                {
                    foreach (var issue in result.Issues)
                        Console.WriteLine($"  校验 {issue.Code} {issue.Severity}: {issue.Message}");
                }
                else
                {
                    Console.WriteLine("  校验：V001–V008 全部通过（离线、无外链、无禁用 API）。");
                }

                Console.WriteLine();
                Console.WriteLine("直接将该目录加入 Lively 壁纸库即可挂载（LivelyInfo.json + index.html）。");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("生成失败：" + ex.Message);
                Console.Error.WriteLine("可重试：已下载的部分会保留并断点续传，已存在的完整模型不会被覆盖。");
                return 1;
            }
        }

        private static void PrintMetrics(GenerationEvent terminal, TimeSpan elapsed)
        {
            var parts = new System.Collections.Generic.List<string>
            {
                $"耗时 {elapsed.TotalSeconds:0.0} 秒"
            };
            if (terminal.TotalTokens.HasValue || terminal.PromptTokens.HasValue || terminal.CompletionTokens.HasValue)
            {
                parts.Add($"tokens: prompt={terminal.PromptTokens?.ToString() ?? "-"} " +
                          $"completion={terminal.CompletionTokens?.ToString() ?? "-"} " +
                          $"total={terminal.TotalTokens?.ToString() ?? "-"}");
            }
            else
            {
                parts.Add("tokens：服务端未返回用量");
            }
            Console.WriteLine(string.Join(" · ", parts));
        }

        private static string DescribeKey(AiSettings settings, string apiKey)
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LIVELY_AI_API_KEY")))
                return "已配置（来源：环境变量 LIVELY_AI_API_KEY，不显示）";
            if (!string.IsNullOrEmpty(apiKey))
                return "已配置（DPAPI 加密保存，不显示）";
            if (!string.IsNullOrEmpty(settings.ApiKeyProtected))
                return "已保存但当前用户无法解密（换机器或换用户后需重新填写）";
            return "未配置";
        }

        private static bool HasEnvOverride()
        {
            return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LIVELY_AI_BASE_URL")) ||
                   !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LIVELY_AI_MODEL")) ||
                   !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LIVELY_AI_API_KEY"));
        }

        private static bool IsHelp(string value) =>
            value == "-h" || value == "--help" || value == "help" || value == "/?";

        private static string? GetArg(string[] args, string name)
        {
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }
            return null;
        }

        private static int ParseInt(string? value, int fallback) =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

        private static StylePreset ParseStyle(string? value) =>
            Enum.TryParse<StylePreset>(value, true, out var style) ? style : StylePreset.auto;

        /// <summary>解析 --interaction：none / all / 逗号组合（click,hover,drag,keyboard）。</summary>
        private static bool TryParseInteraction(string? value, out InteractionCapability interaction)
        {
            interaction = InteractionCapability.none;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var text = value.Trim();
            if (string.Equals(text, "all", StringComparison.OrdinalIgnoreCase))
            {
                interaction = InteractionCapability.click | InteractionCapability.hover |
                              InteractionCapability.drag | InteractionCapability.keyboard;
                return true;
            }

            var parts = text.Split(new[] { ',', ';', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return false;

            foreach (var part in parts)
            {
                if (!Enum.TryParse<InteractionCapability>(part, true, out var capability))
                    return false;
                interaction |= capability;
            }
            return true;
        }

        private static string DescribeInteraction(InteractionCapability interaction) =>
            interaction == InteractionCapability.none ? "none" : interaction.ToString();

        private static void PrintUsage()
        {
            Console.WriteLine("Lively.Ai.Cli — AI 壁纸生成命令行");
            Console.WriteLine();
            Console.WriteLine("配置：");
            Console.WriteLine("  Lively.Ai.Cli config set --base-url <https://.../v1> --model <模型名> --api-key <密钥> [--timeout 120] [--max-repairs 1] [--interaction hover,click] [--refine-rounds 2] [--refine-threshold 7.5]");
            Console.WriteLine("  Lively.Ai.Cli config show");
            Console.WriteLine("  Lively.Ai.Cli config clear-key");
            Console.WriteLine();
            Console.WriteLine("生成：");
            Console.WriteLine("  Lively.Ai.Cli generate --prompt \"描述\" [--out 文件.html] [--width 1920] [--height 1080]");
            Console.WriteLine("                          [--style auto|minimal|cyber|nature|pixel|ink|custom] [--color \"#RRGGBB\"]");
            Console.WriteLine("                          [--interaction hover,click]（none|click|hover|drag|keyboard，逗号组合；默认取配置）");
            Console.WriteLine("                          [--reference <html/js 文件>]（结构参考，限 32 KB，超限截断；不参与校验）");
            Console.WriteLine("                          [--eco]（节能模式：注入低功耗空闲降帧/交互恢复约束）");
            Console.WriteLine();
            Console.WriteLine("继续对话修改：");
            Console.WriteLine("  Lively.Ai.Cli revise --session <会话标识> --prompt \"修改诉求\" [--out 文件.html]");
            Console.WriteLine("                          [--refine-rounds 0-3] [--eco|--no-eco]（不传则沿用会话设置）");
            Console.WriteLine("  Lively.Ai.Cli history --session <会话标识>");
            Console.WriteLine();
            Console.WriteLine("照片 → 深度视差壁纸（本地 MiDaS 推理，模型首次使用时下载）：");
            Console.WriteLine("  Lively.Ai.Cli depth --image <照片> [--out 壁纸目录] [--title 标题] [--model-root 覆盖模型目录] [--max-image 2560]");
            Console.WriteLine();
            Console.WriteLine("配置文件：%LOCALAPPDATA%\\Lively Wallpaper\\AiSettings.json（设置实例后缀时使用对应实例目录）");
            Console.WriteLine("环境变量：LIVELY_AI_BASE_URL / LIVELY_AI_MODEL / LIVELY_AI_API_KEY（优先级高于配置文件）");
        }
    }
}
