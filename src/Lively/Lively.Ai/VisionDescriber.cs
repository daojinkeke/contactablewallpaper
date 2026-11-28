using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lively.Models.Ai;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Lively.Ai
{
    public sealed class VisionDescribeResult
    {
        public bool Success { get; set; }

        /// <summary>面向生成模型的结构化视觉描述；失败时为空。</summary>
        public string Description { get; set; }

        public AiGenerationException Failure { get; set; }
    }

    /// <summary>
    /// 用读图模型把用户附图转成结构化文字，再交给主生成模型。
    /// 默认 DeepSeek 使用 deepseek-flash；BaseUrl/Model/Key 均可独立覆盖。
    /// </summary>
    public sealed class VisionDescriber
    {
        private const int MaxImageBytes = 10 * 1024 * 1024;
        private const int MaxDescriptionLength = 4000;

        private static readonly HttpClient SharedHttpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        private static readonly string[] AllowedExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".gif" };

        private const string SystemPrompt =
            "You are a visual analyst for a desktop live-wallpaper generator.\n" +
            "Describe the attached image precisely so another model can recreate its look as code.\n" +
            "Return compact plain text in this structure (no markdown, no code fences):\n" +
            "1) Scene and subject\n" +
            "2) Composition and layout\n" +
            "3) Palette (approximate hex colors, dominant to accent)\n" +
            "4) Lighting, depth and mood\n" +
            "5) Style references\n" +
            "6) Motion or interaction ideas suitable for a wallpaper\n" +
            "7) Visible text (quote it exactly, or write none)\n" +
            "Only describe what is actually visible. Do not invent details. Keep it under 350 words.";

        private readonly HttpClient httpClient;

        public VisionDescriber(HttpClient httpClient = null)
        {
            this.httpClient = httpClient ?? SharedHttpClient;
        }

        /// <summary>
        /// 读取本地图片并请求视觉模型转述；失败时返回可读的 <see cref="AiGenerationException"/>。
        /// </summary>
        public async Task<VisionDescribeResult> DescribeAsync(
            AiSettings settings,
            AiVisionEndpoint endpoint,
            string imagePath,
            string focus,
            CancellationToken token)
        {
            if (endpoint == null ||
                string.IsNullOrWhiteSpace(endpoint.BaseUrl) ||
                string.IsNullOrWhiteSpace(endpoint.Model) ||
                string.IsNullOrWhiteSpace(endpoint.ApiKey))
            {
                return Fail(AiFailureKind.Protocol,
                    "尚未配置读图模型：请在 AI 配置里填写视觉模型（DeepSeek 请使用 deepseek-flash）。");
            }

            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            {
                return Fail(AiFailureKind.Protocol, "找不到选择的图片，请重新选择。");
            }

            var extension = Path.GetExtension(imagePath).ToLowerInvariant();
            if (Array.IndexOf(AllowedExtensions, extension) < 0)
            {
                return Fail(AiFailureKind.Protocol,
                    "暂不支持该图片格式，请使用 JPG、PNG、WebP 或 GIF。");
            }

            FileInfo info;
            try
            {
                info = new FileInfo(imagePath);
            }
            catch (Exception ex)
            {
                return Fail(AiFailureKind.Protocol, "无法读取图片信息：" + ex.Message);
            }

            if (info.Length <= 0)
                return Fail(AiFailureKind.Protocol, "图片文件为空，请重新选择。");
            if (info.Length > MaxImageBytes)
                return Fail(AiFailureKind.Protocol,
                    $"图片过大（{info.Length / 1024d / 1024d:0.0} MB），请压缩到 {MaxImageBytes / 1024 / 1024} MB 以内。");

            string dataUrl;
            try
            {
                var bytes = await File.ReadAllBytesAsync(imagePath, token).ConfigureAwait(false);
                dataUrl = "data:" + GetMimeType(extension) + ";base64," + Convert.ToBase64String(bytes);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return Fail(AiFailureKind.Timeout, "已取消读取图片。");
            }
            catch (Exception ex)
            {
                return Fail(AiFailureKind.Protocol, "读取图片失败：" + ex.Message);
            }

            var timeoutSeconds = Math.Clamp(settings?.TimeoutSeconds ?? 120, 30, 180);
            using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            idleCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            var payload = new JObject
            {
                ["model"] = endpoint.Model,
                ["stream"] = false,
                ["temperature"] = 0.2,
                ["max_tokens"] = 900,
                ["messages"] = new JArray
                {
                    new JObject { ["role"] = "system", ["content"] = SystemPrompt },
                    new JObject
                    {
                        ["role"] = "user",
                        ["content"] = new JArray
                        {
                            new JObject { ["type"] = "text", ["text"] = BuildUserText(focus) },
                            new JObject
                            {
                                ["type"] = "image_url",
                                ["image_url"] = new JObject
                                {
                                    ["url"] = dataUrl,
                                    ["detail"] = "high"
                                }
                            }
                        }
                    }
                }
            };

            // 读图同样关闭思考模式：只需要结构化描述，思维链只会增加延迟与费用。
            AiRequestModes.DisableThinking(payload, endpoint.BaseUrl, endpoint.Model);

            var endpointUrl = BuildEndpoint(endpoint.BaseUrl);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpointUrl)
            {
                Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);

            HttpResponseMessage response;
            try
            {
                response = await httpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idleCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return Fail(AiFailureKind.Timeout,
                    $"读图模型在 {timeoutSeconds} 秒内没有响应，请稍后重试。");
            }
            catch (HttpRequestException ex)
            {
                return Fail(AiFailureKind.Network,
                    $"无法连接读图模型服务：{SafeHost(endpointUrl)}，请检查视觉模型 BaseUrl 或网络。", ex);
            }

            using (response)
            {
                var body = await ReadBodySafeAsync(response, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return new VisionDescribeResult
                    {
                        Success = false,
                        Failure = ClassifyHttpFailure((int)response.StatusCode, body)
                    };
                }

                var description = ExtractContent(body);
                if (string.IsNullOrWhiteSpace(description))
                    return Fail(AiFailureKind.Protocol, "读图模型没有返回可用的图片描述，请重试或更换视觉模型。");

                return new VisionDescribeResult
                {
                    Success = true,
                    Description = Truncate(description.Trim(), MaxDescriptionLength)
                };
            }
        }

        private static string BuildUserText(string focus)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Describe this reference image for a wallpaper revision.");
            if (!string.IsNullOrWhiteSpace(focus))
                sb.AppendLine("The user's revision request: " + focus.Trim());
            sb.AppendLine("Focus on visual details that can be recreated in HTML/CSS/Canvas.");
            return sb.ToString();
        }

        private static string ExtractContent(string body)
        {
            try
            {
                var obj = JObject.Parse(body);
                var choice = obj["choices"] is JArray choices && choices.Count > 0 ? choices[0] : null;
                if (choice is JObject choiceObject && choiceObject["message"] is JObject message)
                {
                    var content = message["content"];
                    if (content is JArray parts)
                    {
                        var sb = new StringBuilder();
                        foreach (var part in parts)
                        {
                            if (part is JObject partObject && partObject["type"]?.ToString() == "text")
                                sb.Append(partObject["text"]?.ToString());
                        }
                        return sb.ToString();
                    }
                    return content?.ToString();
                }
            }
            catch (JsonException)
            {
                return null;
            }
            return null;
        }

        private static AiGenerationException ClassifyHttpFailure(int statusCode, string body)
        {
            var text = (body ?? string.Empty).Trim();
            var lower = text.ToLowerInvariant();

            if (statusCode == 401 || statusCode == 403)
                return new AiGenerationException(AiFailureKind.Authentication,
                    "读图模型鉴权失败：请检查视觉模型的 API Key 是否有权限。");
            if (statusCode == 429)
                return new AiGenerationException(AiFailureKind.Quota,
                    "读图模型额度不足或请求过于频繁，请稍后重试。");
            if (statusCode == 404 ||
                lower.Contains("model_not_found") ||
                (lower.Contains("model") && (lower.Contains("not found") || lower.Contains("does not exist"))))
            {
                return new AiGenerationException(AiFailureKind.ModelNotFound,
                    "读图模型不存在或无权限访问，请检查视觉模型名称与 BaseUrl 是否匹配。");
            }
            if (statusCode == 400 &&
                (lower.Contains("image") || lower.Contains("vision") || lower.Contains("multimodal") ||
                 lower.Contains("content type")))
            {
                return new AiGenerationException(AiFailureKind.Protocol,
                    "当前模型不支持图片输入，请把视觉模型改成支持读图的模型（DeepSeek 使用 deepseek-flash）。");
            }

            return new AiGenerationException(AiFailureKind.Protocol,
                $"读图模型返回 HTTP {statusCode}：{Truncate(text, 200)}");
        }

        private static async Task<string> ReadBodySafeAsync(HttpResponseMessage response, CancellationToken token)
        {
            try
            {
                return await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static string BuildEndpoint(string baseUrl)
        {
            var trimmed = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                return trimmed;
            return trimmed + "/chat/completions";
        }

        private static string GetMimeType(string extension)
        {
            switch (extension)
            {
                case ".png":
                    return "image/png";
                case ".webp":
                    return "image/webp";
                case ".gif":
                    return "image/gif";
                default:
                    return "image/jpeg";
            }
        }

        private static string SafeHost(string endpoint) =>
            Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Host : "读图模型服务";

        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }

        private static VisionDescribeResult Fail(AiFailureKind kind, string message, Exception inner = null) =>
            new VisionDescribeResult
            {
                Success = false,
                Failure = new AiGenerationException(kind, message, inner)
            };
    }
}
