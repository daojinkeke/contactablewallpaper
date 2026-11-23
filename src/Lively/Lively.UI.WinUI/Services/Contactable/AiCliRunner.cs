using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Lively.Models.Ai;

namespace Lively.UI.WinUI.Services.Contactable
{
    public sealed class AiGenerationOptions
    {
        public string Prompt { get; set; } = string.Empty;
        public string Style { get; set; } = "auto";
        public string Color { get; set; }
        public int Width { get; set; } = 1920;
        public int Height { get; set; } = 1080;
        public string OutputPath { get; set; }
        /// <summary>节能模式：为 true 时给 CLI 追加 --eco，要求生成结果自带低功耗行为。</summary>
        public bool Eco { get; set; }
    }

    public sealed class AiGenerationProgress
    {
        public int Percent { get; set; }
        public string Status { get; set; } = string.Empty;
    }

    public sealed class AiGenerationResult
    {
        public bool IsSuccess { get; set; }
        public bool IsCanceled { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorMessage { get; set; }
        public string OutputPath { get; set; }
        public string Detail { get; set; }
        /// <summary>生成成功后 CLI 返回的会话标识（wp-…），用于后续对话续改。</summary>
        public string SessionId { get; set; }
        public int SessionVersion { get; set; }
    }

    public sealed class AiRevisionOptions
    {
        public string SessionId { get; set; }
        public string Instruction { get; set; }
        public int Width { get; set; } = 1920;
        public int Height { get; set; } = 1080;
        /// <summary>节能模式：为 true 时给 CLI 追加 --eco。</summary>
        public bool Eco { get; set; }
    }

    public sealed class AiRevisionResult
    {
        public bool IsSuccess { get; set; }
        public bool IsCanceled { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorMessage { get; set; }
        public string Detail { get; set; }
        public int SessionVersion { get; set; }
        public string OutputPath { get; set; }
    }

    public sealed class DepthGenerationOptions
    {
        public string ImagePath { get; set; }
        public string OutputDirectory { get; set; }
        public string Title { get; set; }
    }

    public sealed class DepthGenerationResult
    {
        public bool IsSuccess { get; set; }
        public bool IsCanceled { get; set; }
        public string ErrorMessage { get; set; }
        public string Detail { get; set; }
        public string WallpaperDirectory { get; set; }
        public string HtmlPath { get; set; }
        public string ThumbnailPath { get; set; }
    }

    /// <summary>
    /// 调用 Lively.Ai.Cli generate 的进程封装：进度解析、取消、超时与错误分类。
    /// </summary>
    public sealed class AiCliRunner
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        private static readonly TimeSpan TotalTimeout = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(3);

        public string ResolveCliPath()
        {
            var env = Environment.GetEnvironmentVariable("LIVELY_AI_CLI_PATH");
            if (!string.IsNullOrWhiteSpace(env))
            {
                if (Directory.Exists(env))
                {
                    var inDir = Path.Combine(env, "Lively.Ai.Cli.exe");
                    if (File.Exists(inDir))
                        return inDir;
                }
                else if (File.Exists(env))
                {
                    return env;
                }
            }

            var baseDir = AppContext.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(baseDir, "Lively.Ai.Cli.exe"),
                Path.Combine(baseDir, "..", "Ai", "Lively.Ai.Cli.exe"),
                Path.Combine(baseDir, "..", "..", "Lively.Ai.Cli.exe"),
                Path.Combine(baseDir, "..", "..", "..", "Ai", "Lively.Ai.Cli.exe"),
            };
            foreach (var candidate in candidates)
            {
                try
                {
                    var full = Path.GetFullPath(candidate);
                    if (File.Exists(full))
                        return full;
                }
                catch (Exception)
                {
                    // ignore malformed path
                }
            }

            try
            {
                var dir = new DirectoryInfo(baseDir);
                for (var i = 0; i < 10 && dir != null; i++, dir = dir.Parent)
                {
                    var binDir = Path.Combine(dir.FullName, "src", "Lively", "Lively.Ai.Cli", "bin");
                    if (!Directory.Exists(binDir))
                        continue;
                    foreach (var config in new[] { "Release", "Debug" })
                    {
                        var configDir = Path.Combine(binDir, "x64", config);
                        if (!Directory.Exists(configDir))
                            configDir = Path.Combine(binDir, config);
                        if (!Directory.Exists(configDir))
                            continue;
                        var found = Directory.EnumerateFiles(configDir, "Lively.Ai.Cli.exe", SearchOption.AllDirectories).FirstOrDefault();
                        if (found != null)
                            return found;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "AI CLI 开发态路径探测失败");
            }

            return null;
        }

        public async Task<AiGenerationResult> GenerateAsync(AiGenerationOptions options, IProgress<AiGenerationProgress> progress, CancellationToken cancellationToken)
        {
            var cliPath = ResolveCliPath();
            if (string.IsNullOrWhiteSpace(cliPath))
            {
                Logger.Error("未找到 Lively.Ai.Cli.exe");
                return Fail("ai_cli_missing", "生成组件缺失：未找到 Lively.Ai.Cli.exe，请重新安装最新发布包。");
            }

            if (string.IsNullOrWhiteSpace(options?.OutputPath))
                return Fail("ai_bad_arguments", "生成输出路径为空。");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(options.OutputPath));
            }
            catch (Exception ex)
            {
                return Fail("ai_output_missing", $"无法创建输出目录：{ex.Message}");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = cliPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                WorkingDirectory = Path.GetDirectoryName(cliPath) ?? AppContext.BaseDirectory,
            };
            startInfo.ArgumentList.Add("generate");
            startInfo.ArgumentList.Add("--prompt");
            startInfo.ArgumentList.Add(options.Prompt ?? string.Empty);
            startInfo.ArgumentList.Add("--out");
            startInfo.ArgumentList.Add(options.OutputPath);
            startInfo.ArgumentList.Add("--width");
            startInfo.ArgumentList.Add(options.Width.ToString());
            startInfo.ArgumentList.Add("--height");
            startInfo.ArgumentList.Add(options.Height.ToString());
            startInfo.ArgumentList.Add("--style");
            startInfo.ArgumentList.Add(string.IsNullOrWhiteSpace(options.Style) ? "auto" : options.Style);
            if (!string.IsNullOrWhiteSpace(options.Color))
            {
                startInfo.ArgumentList.Add("--color");
                startInfo.ArgumentList.Add(options.Color);
            }
            if (options.Eco)
                startInfo.ArgumentList.Add("--eco");

            using var process = new Process { StartInfo = startInfo };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var gate = new object();
            var percent = 0;
            var lastOutput = DateTime.UtcNow;

            void Report(int value, string status)
            {
                if (value > percent)
                    percent = value;
                progress?.Report(new AiGenerationProgress
                {
                    Percent = Math.Clamp(percent, 0, 100),
                    Status = status ?? string.Empty,
                });
            }

            try
            {
                if (!process.Start())
                    return Fail("ai_failed", "生成器启动失败。");
            }
            catch (Exception ex)
            {
                return Fail("ai_failed", $"生成器启动失败：{ex.Message}");
            }

            var stdoutTask = Task.Run(async () =>
            {
                string line;
                while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    lock (gate)
                    {
                        stdout.AppendLine(line);
                        if (stdout.Length > 60000)
                            stdout.Remove(0, stdout.Length - 60000);
                    }
                    lastOutput = DateTime.UtcNow;
                    MapLine(line, Report);
                }
            });

            var stderrTask = Task.Run(async () =>
            {
                string line;
                while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    lock (gate)
                    {
                        stderr.AppendLine(line);
                        if (stderr.Length > 32768)
                            stderr.Remove(0, stderr.Length - 32768);
                    }
                    lastOutput = DateTime.UtcNow;
                }
            });

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(TotalTimeout);

            var exitTask = process.WaitForExitAsync();
            var stopTask = Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
            var finished = await Task.WhenAny(exitTask, stopTask).ConfigureAwait(false);

            if (finished != exitTask)
            {
                var canceledByUser = cancellationToken.IsCancellationRequested;
                var stalled = !canceledByUser && DateTime.UtcNow - lastOutput > StallTimeout;
                TryKill(process);
                await SafeAwait(exitTask).ConfigureAwait(false);
                await Task.WhenAll(SafeAwait(stdoutTask), SafeAwait(stderrTask)).ConfigureAwait(false);

                if (canceledByUser)
                    return new AiGenerationResult { IsCanceled = true };

                return stalled
                    ? Fail("ai_stalled", "生成器长时间没有任何输出，已终止。")
                    : Fail(AiErrorCodes.Timeout, "生成超时，已终止。");
            }

            await Task.WhenAll(SafeAwait(stdoutTask), SafeAwait(stderrTask)).ConfigureAwait(false);

            var exitCode = process.ExitCode;
            var errorText = stderr.ToString();
            var detail = string.IsNullOrWhiteSpace(errorText) ? stdout.ToString() : errorText;

            if (exitCode == 0 && File.Exists(options.OutputPath))
            {
                Report(100, "生成完成");
                var sessionId = ExtractSessionId(stdout.ToString());
                return new AiGenerationResult
                {
                    IsSuccess = true,
                    OutputPath = options.OutputPath,
                    Detail = detail,
                    SessionId = sessionId,
                };
            }

            if (exitCode == 0)
                return Fail("ai_output_missing", "生成器报告成功，但没有找到输出文件。", detail);

            if (errorText.Contains("尚未配置 AI", StringComparison.OrdinalIgnoreCase))
                return Fail(AiErrorCodes.NotConfigured, "尚未配置 AI 服务，请先在设置页填写 BaseUrl / Model / API Key。", detail);

            var match = Regex.Match(errorText, @"生成失败（(?<code>[^）]+)）");
            var errorCode = match.Success ? match.Groups["code"].Value : null;

            if (string.Equals(errorCode, AiErrorCodes.ModelNotFound, StringComparison.OrdinalIgnoreCase) &&
                errorText.Contains("不存在", StringComparison.OrdinalIgnoreCase))
            {
                // already the right code
            }

            return Fail(errorCode ?? "ai_failed", DescribeError(errorCode) ?? FirstErrorLine(errorText) ?? $"生成器异常退出（exit {exitCode}）。", detail);
        }

        /// <summary>
        /// 调用 Lively.Ai.Cli revise，在已有会话上按一句诉求续改并生成新版本。
        /// </summary>
        public async Task<AiRevisionResult> ReviseAsync(AiRevisionOptions options, IProgress<AiGenerationProgress> progress, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(options?.SessionId))
                return ReviseFail("ai_session_missing", "当前结果没有可续接的会话。");
            if (string.IsNullOrWhiteSpace(options.Instruction))
                return ReviseFail("ai_bad_arguments", "请先写一句修改诉求。");

            var cliPath = ResolveCliPath();
            if (string.IsNullOrWhiteSpace(cliPath))
            {
                Logger.Error("未找到 Lively.Ai.Cli.exe");
                return ReviseFail("ai_cli_missing", "生成组件缺失：未找到 Lively.Ai.Cli.exe，请重新安装最新发布包。");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = cliPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                WorkingDirectory = Path.GetDirectoryName(cliPath) ?? AppContext.BaseDirectory,
            };
            startInfo.ArgumentList.Add("revise");
            startInfo.ArgumentList.Add("--session");
            startInfo.ArgumentList.Add(options.SessionId);
            startInfo.ArgumentList.Add("--prompt");
            startInfo.ArgumentList.Add(options.Instruction);
            startInfo.ArgumentList.Add("--width");
            startInfo.ArgumentList.Add(options.Width.ToString());
            startInfo.ArgumentList.Add("--height");
            startInfo.ArgumentList.Add(options.Height.ToString());
            if (options.Eco)
                startInfo.ArgumentList.Add("--eco");

            using var process = new Process { StartInfo = startInfo };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var gate = new object();
            var percent = 0;
            var lastStatus = string.Empty;
            var lastOutput = DateTime.UtcNow;

            void Report(int value, string status)
            {
                if (value > percent)
                    percent = value;
                status ??= string.Empty;
                if (percent == value && string.Equals(status, lastStatus, StringComparison.Ordinal))
                    return;
                lastStatus = status;
                progress?.Report(new AiGenerationProgress
                {
                    Percent = Math.Clamp(percent, 0, 100),
                    Status = status,
                });
            }

            try
            {
                if (!process.Start())
                    return ReviseFail("ai_failed", "修改器启动失败。");
            }
            catch (Exception ex)
            {
                return ReviseFail("ai_failed", $"修改器启动失败：{ex.Message}");
            }

            var stdoutTask = Task.Run(async () =>
            {
                string line;
                while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    lock (gate)
                    {
                        stdout.AppendLine(line);
                        if (stdout.Length > 60000)
                            stdout.Remove(0, stdout.Length - 60000);
                    }
                    lastOutput = DateTime.UtcNow;
                    MapReviseLine(line, Report);
                }
            });

            var stderrTask = Task.Run(async () =>
            {
                string line;
                while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    lock (gate)
                    {
                        stderr.AppendLine(line);
                        if (stderr.Length > 32768)
                            stderr.Remove(0, stderr.Length - 32768);
                    }
                    lastOutput = DateTime.UtcNow;
                }
            });

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(TotalTimeout);

            var exitTask = process.WaitForExitAsync();
            var stopTask = Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
            var finished = await Task.WhenAny(exitTask, stopTask).ConfigureAwait(false);

            if (finished != exitTask)
            {
                var canceledByUser = cancellationToken.IsCancellationRequested;
                var stalled = !canceledByUser && DateTime.UtcNow - lastOutput > StallTimeout;
                TryKill(process);
                await SafeAwait(exitTask).ConfigureAwait(false);
                await Task.WhenAll(SafeAwait(stdoutTask), SafeAwait(stderrTask)).ConfigureAwait(false);

                if (canceledByUser)
                    return new AiRevisionResult { IsCanceled = true };

                return stalled
                    ? ReviseFail("ai_stalled", "修改器长时间没有任何输出，已终止。")
                    : ReviseFail(AiErrorCodes.Timeout, "修改超时，已终止。");
            }

            await Task.WhenAll(SafeAwait(stdoutTask), SafeAwait(stderrTask)).ConfigureAwait(false);

            var exitCode = process.ExitCode;
            var errorText = stderr.ToString();
            var detail = string.IsNullOrWhiteSpace(errorText) ? stdout.ToString() : errorText;

            if (exitCode == 0)
            {
                var match = Regex.Match(stdout.ToString(), @"会话标识：\s*(?<id>wp-[A-Za-z0-9\-]+)（v(?<ver>\d+)）");
                var version = match.Success && int.TryParse(match.Groups["ver"].Value, out var parsed) ? parsed : 0;
                Report(100, "修改完成");
                return new AiRevisionResult
                {
                    IsSuccess = true,
                    Detail = detail,
                    SessionVersion = version,
                };
            }

            if (errorText.Contains("尚未配置 AI", StringComparison.OrdinalIgnoreCase))
                return ReviseFail(AiErrorCodes.NotConfigured, "尚未配置 AI 服务，请先在生成按钮下方填写 BaseUrl / Model / API Key。", detail);
            if (errorText.Contains("找不到会话", StringComparison.OrdinalIgnoreCase))
                return ReviseFail("ai_session_missing", "会话文件已丢失，无法继续修改。", detail);

            var codeMatch = Regex.Match(errorText, @"修改失败（(?<code>[^）]+)）");
            var errorCode = codeMatch.Success ? codeMatch.Groups["code"].Value : null;
            return ReviseFail(errorCode ?? "ai_failed",
                FirstErrorLine(errorText) ?? $"修改器异常退出（exit {exitCode}）。", detail);
        }

        /// <summary>
        /// 调用 Lively.Ai.Cli depth 生成照片视差壁纸。CLI 会把输出目录直接写成可挂载的壁纸目录。
        /// </summary>
        public async Task<DepthGenerationResult> GenerateDepthAsync(DepthGenerationOptions options, IProgress<AiGenerationProgress> progress, CancellationToken cancellationToken)
        {
            var cliPath = ResolveCliPath();
            if (string.IsNullOrWhiteSpace(cliPath))
            {
                Logger.Error("未找到 Lively.Ai.Cli.exe");
                return DepthFail("生成组件缺失：未找到 Lively.Ai.Cli.exe，请重新安装最新发布包。");
            }

            if (options == null || string.IsNullOrWhiteSpace(options.ImagePath) || !File.Exists(options.ImagePath))
                return DepthFail("找不到所选照片，请重新选择。");
            if (string.IsNullOrWhiteSpace(options.OutputDirectory))
                return DepthFail("生成输出目录为空。");

            try
            {
                Directory.CreateDirectory(options.OutputDirectory);
            }
            catch (Exception ex)
            {
                return DepthFail($"无法创建输出目录：{ex.Message}");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = cliPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                WorkingDirectory = Path.GetDirectoryName(cliPath) ?? AppContext.BaseDirectory,
            };
            startInfo.ArgumentList.Add("depth");
            startInfo.ArgumentList.Add("--image");
            startInfo.ArgumentList.Add(options.ImagePath);
            startInfo.ArgumentList.Add("--out");
            startInfo.ArgumentList.Add(options.OutputDirectory);
            if (!string.IsNullOrWhiteSpace(options.Title))
            {
                startInfo.ArgumentList.Add("--title");
                startInfo.ArgumentList.Add(options.Title);
            }

            using var process = new Process { StartInfo = startInfo };
            var stderr = new StringBuilder();
            var gate = new object();
            var percent = 0;
            var lastStatus = string.Empty;
            var lastOutput = DateTime.UtcNow;

            void Report(int value, string status)
            {
                if (value > percent)
                    percent = value;
                status ??= string.Empty;
                if (percent == value && string.Equals(status, lastStatus, StringComparison.Ordinal))
                    return;
                lastStatus = status;
                progress?.Report(new AiGenerationProgress
                {
                    Percent = Math.Clamp(percent, 0, 100),
                    Status = status,
                });
            }

            try
            {
                if (!process.Start())
                    return DepthFail("生成器启动失败。");
            }
            catch (Exception ex)
            {
                return DepthFail($"生成器启动失败：{ex.Message}");
            }

            // CLI 在下载阶段用 \r 刷新同一行，必须按字符切分而不是按行读取。
            async Task PumpAsync(StreamReader reader, bool parse)
            {
                var buffer = new char[2048];
                var pending = new StringBuilder();
                try
                {
                    int read;
                    while ((read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), CancellationToken.None).ConfigureAwait(false)) > 0)
                    {
                        lastOutput = DateTime.UtcNow;
                        for (var i = 0; i < read; i++)
                        {
                            var ch = buffer[i];
                            if (ch != '\r' && ch != '\n')
                            {
                                pending.Append(ch);
                                continue;
                            }

                            if (pending.Length == 0)
                                continue;
                            var line = pending.ToString();
                            pending.Clear();
                            if (parse)
                            {
                                MapDepthLine(line, Report);
                            }
                            else
                            {
                                lock (gate)
                                {
                                    stderr.AppendLine(line);
                                    if (stderr.Length > 32768)
                                        stderr.Remove(0, stderr.Length - 32768);
                                }
                            }
                        }
                    }

                    if (pending.Length > 0)
                    {
                        var line = pending.ToString();
                        if (parse)
                        {
                            MapDepthLine(line, Report);
                        }
                        else
                        {
                            lock (gate)
                                stderr.AppendLine(line);
                        }
                    }
                }
                catch (Exception)
                {
                    // 进程被杀后读取会抛异常，忽略。
                }
            }

            var stdoutTask = PumpAsync(process.StandardOutput, true);
            var stderrTask = PumpAsync(process.StandardError, false);

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(TimeSpan.FromMinutes(15));

            var exitTask = process.WaitForExitAsync();
            var stopTask = Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
            var finished = await Task.WhenAny(exitTask, stopTask).ConfigureAwait(false);

            if (finished != exitTask)
            {
                var canceledByUser = cancellationToken.IsCancellationRequested;
                var stalled = !canceledByUser && DateTime.UtcNow - lastOutput > TimeSpan.FromMinutes(4);
                TryKill(process);
                await SafeAwait(exitTask).ConfigureAwait(false);
                await Task.WhenAll(SafeAwait(stdoutTask), SafeAwait(stderrTask)).ConfigureAwait(false);

                if (canceledByUser)
                    return new DepthGenerationResult { IsCanceled = true };

                return DepthFail(stalled
                    ? "生成器长时间没有任何输出，已终止。模型下载支持断点续传，可稍后重试。"
                    : "生成超时，已终止。模型下载支持断点续传，可稍后重试。");
            }

            await Task.WhenAll(SafeAwait(stdoutTask), SafeAwait(stderrTask)).ConfigureAwait(false);

            var outputDir = Path.GetFullPath(options.OutputDirectory);
            var htmlPath = Path.Combine(outputDir, "index.html");
            var errorText = stderr.ToString();
            if (process.ExitCode == 0 && File.Exists(htmlPath))
            {
                Report(100, "生成完成");
                var thumbnail = Path.Combine(outputDir, "thumbnail.jpg");
                return new DepthGenerationResult
                {
                    IsSuccess = true,
                    WallpaperDirectory = outputDir,
                    HtmlPath = htmlPath,
                    ThumbnailPath = File.Exists(thumbnail) ? thumbnail : null,
                    Detail = errorText,
                };
            }

            if (process.ExitCode == 0)
                return DepthFail("生成器报告成功，但没有找到 index.html。", errorText);

            var firstLine = FirstErrorLine(errorText);
            var message = string.IsNullOrWhiteSpace(firstLine)
                ? $"生成器异常退出（exit {process.ExitCode}）。"
                : (firstLine.StartsWith("生成失败", StringComparison.Ordinal) || firstLine.StartsWith("找不到图片", StringComparison.Ordinal)
                    ? firstLine
                    : $"生成失败：{firstLine}");
            return DepthFail(message, errorText);
        }

        public static string DescribeError(string code)
        {
            switch (code)
            {
                case AiErrorCodes.NotConfigured:
                    return "尚未配置 AI 服务，请先在设置页填写 BaseUrl / Model / API Key。";
                case AiErrorCodes.AuthenticationFailed:
                    return "鉴权失败：API Key 无效或没有访问权限。";
                case AiErrorCodes.QuotaExceeded:
                    return "请求额度或频率超限，请稍后重试。";
                case AiErrorCodes.Timeout:
                    return "AI 服务响应超时，请检查网络后重试。";
                case AiErrorCodes.NetworkError:
                    return "无法连接 AI 服务，请检查 BaseUrl、代理或网络。";
                case AiErrorCodes.ContentRejected:
                    return "描述被服务商拒绝，请调整措辞后重试。";
                case AiErrorCodes.ValidationFailed:
                    return "生成结果未通过本地校验，请重试或换一种描述。";
                case AiErrorCodes.ModelNotFound:
                    return "模型名称不存在，请在设置页检查 Model。";
                case AiErrorCodes.ProtocolError:
                    return "AI 服务返回格式异常，请检查接口是否为 OpenAI 兼容格式。";
                default:
                    return null;
            }
        }

        private static void MapLine(string line, Action<int, string> report)
        {
            if (line.Contains("[1/3] 已提交"))
            {
                report(8, "已提交，等待模型响应…");
            }
            else if (line.Contains("[1/3] 正在生成"))
            {
                report(15, "模型正在生成…");
            }
            else if (line.Contains("已接收"))
            {
                var match = Regex.Match(line, @"已接收\s*([\d,]+)\s*字符");
                if (match.Success && long.TryParse(match.Groups[1].Value.Replace(",", string.Empty), out var chars))
                {
                    var value = (int)Math.Min(70, 15 + 55 * (1 - Math.Exp(-chars / 50000.0)));
                    report(value, $"模型正在生成，已接收 {chars:N0} 字符");
                }
            }
            else if (line.Contains("[2/3] 校验未通过"))
            {
                report(74, "校验未通过，正在自动修复…");
            }
            else if (line.Contains("[2/3] 正在校验"))
            {
                report(78, "正在校验生成结果…");
            }
            else if (line.Contains("[3/3] 完成"))
            {
                report(100, "生成完成");
            }
        }

        private static void MapReviseLine(string line, Action<int, string> report)
        {
            var text = (line ?? string.Empty).Trim();
            if (text.Length == 0)
                return;

            if (text.StartsWith("基于 v", StringComparison.Ordinal))
            {
                report(4, "已找到会话，准备修改…");
                return;
            }
            if (text.StartsWith("修改诉求：", StringComparison.Ordinal))
            {
                report(6, "已提交修改诉求…");
                return;
            }
            if (text.StartsWith("节能模式：", StringComparison.Ordinal))
                return;
            if (text == "完成")
            {
                report(100, "修改完成");
                return;
            }

            var score = Regex.Match(text, @"评分\s*(?<s>\d+(\.\d+)?)/10");
            if (score.Success)
            {
                report(88, "自我精修评分 " + score.Groups["s"].Value + "/10");
                return;
            }
            if (text.Contains("正在按计划改写", StringComparison.Ordinal))
            {
                report(82, "正在按精修计划改写…");
                return;
            }

            MapLine(text, report);
        }

        private static string ExtractSessionId(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            var match = Regex.Match(text, @"会话标识：\s*(?<id>wp-[A-Za-z0-9\-]+)");
            return match.Success ? match.Groups["id"].Value : null;
        }

        private static AiRevisionResult ReviseFail(string code, string message, string detail = null) => new AiRevisionResult
        {
            IsSuccess = false,
            ErrorCode = code,
            ErrorMessage = message,
            Detail = detail,
        };

        private static string FirstErrorLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var meaningful = lines.FirstOrDefault(x => !x.TrimStart().StartsWith("V0", StringComparison.OrdinalIgnoreCase));
            return (meaningful ?? lines.FirstOrDefault())?.Trim();
        }

        private static void MapDepthLine(string line, Action<int, string> report)
        {
            var text = (line ?? string.Empty).Trim();
            if (text.Length == 0)
                return;

            var match = Regex.Match(text, @"^\[\s*(?<pct>\d+)\s*%\]\s*(?<msg>.+)$");
            if (match.Success && int.TryParse(match.Groups["pct"].Value, out var percent))
            {
                report(Math.Clamp(percent, 0, 100), match.Groups["msg"].Value.Trim());
                return;
            }

            if (text.Contains("未找到 MiDaS 模型", StringComparison.OrdinalIgnoreCase))
            {
                report(1, "未找到本地模型，准备下载（约 62 MB）…");
            }
            else if (text.StartsWith("已找到本地模型", StringComparison.Ordinal))
            {
                report(80, "已找到本地模型，离线推理。");
            }
        }

        private static DepthGenerationResult DepthFail(string message, string detail = null) => new DepthGenerationResult
        {
            IsSuccess = false,
            ErrorMessage = message,
            Detail = detail,
        };

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "结束 AI CLI 进程失败");
            }
        }

        private static async Task SafeAwait(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 进程被杀后读取任务可能抛异常，忽略。
            }
        }

        private static AiGenerationResult Fail(string code, string message, string detail = null) => new AiGenerationResult
        {
            IsSuccess = false,
            ErrorCode = code,
            ErrorMessage = message,
            Detail = detail,
        };
    }
}
