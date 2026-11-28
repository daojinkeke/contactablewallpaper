using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Lively.UI.WinUI.Services.Contactable
{
    /// <summary>
    /// 一次自动识别的结果：命中的服务商、规范化的 BaseUrl 与可用对话模型。
    /// </summary>
    public sealed class AiProviderDetection
    {
        public bool IsSuccess { get; set; }
        public string Provider { get; set; }
        public string DisplayName { get; set; }
        public string BaseUrl { get; set; }
        public IReadOnlyList<string> Models { get; set; } = Array.Empty<string>();
        public string SelectedModel { get; set; }
        public string ErrorMessage { get; set; }

        public static AiProviderDetection Fail(string message) => new AiProviderDetection
        {
            IsSuccess = false,
            ErrorMessage = message,
            Models = Array.Empty<string>(),
        };
    }

    /// <summary>
    /// 只凭 API Key 自动识别 OpenAI 兼容服务商。
    /// 做法：按 Key 前缀给候选服务商排序，再请求各家的 /models 接口（只读、不消耗生成额度），
    /// 第一个返回有效模型列表的服务商即为识别结果；全部失败时由界面回退到手动填写。
    /// </summary>
    public sealed class AiProviderDetector
    {
        private sealed class Provider
        {
            public string Id;
            public string DisplayName;
            public string BaseUrl;
            /// <summary>Key 前缀特征，命中时优先探测；null 表示通用。</summary>
            public string[] KeyPrefixes;
            /// <summary>优先选择的模型名（按顺序匹配完整名，再匹配子串）。</summary>
            public string[] PreferredModels;
            /// <summary>部分服务商的模型列表是公开接口，需要额外的带鉴权探针确认 Key 归属。</summary>
            public string AuthProbePath;
        }

        // 顺序即默认探测优先级：国内常用服务商在前，避免多数用户多等。
        private static readonly Provider[] Providers =
        {
            new Provider
            {
                Id = "deepseek",
                DisplayName = "DeepSeek",
                BaseUrl = "https://api.deepseek.com/v1",
                PreferredModels = new[] { "deepseek-chat", "deepseek-reasoner" },
            },
            new Provider
            {
                Id = "openai",
                DisplayName = "OpenAI",
                BaseUrl = "https://api.openai.com/v1",
                PreferredModels = new[] { "gpt-4o-mini", "gpt-4.1-mini", "gpt-4o", "gpt-4.1" },
            },
            new Provider
            {
                Id = "moonshot",
                DisplayName = "Moonshot / Kimi",
                BaseUrl = "https://api.moonshot.cn/v1",
                PreferredModels = new[] { "moonshot-v1-8k", "moonshot-v1-32k", "kimi-k2-0905-preview" },
            },
            new Provider
            {
                Id = "zhipu",
                DisplayName = "智谱 BigModel",
                BaseUrl = "https://open.bigmodel.cn/api/paas/v4",
                PreferredModels = new[] { "glm-4-flash", "glm-4-air", "glm-4-plus" },
            },
            new Provider
            {
                Id = "siliconflow",
                DisplayName = "硅基流动",
                BaseUrl = "https://api.siliconflow.cn/v1",
                PreferredModels = new[] { "Qwen/Qwen2.5-7B-Instruct", "deepseek-ai/DeepSeek-V3" },
            },
            new Provider
            {
                Id = "dashscope",
                DisplayName = "阿里百炼 DashScope",
                BaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1",
                PreferredModels = new[] { "qwen-plus", "qwen-turbo", "qwen-max" },
            },
            new Provider
            {
                Id = "gemini",
                DisplayName = "Google Gemini",
                BaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai",
                KeyPrefixes = new[] { "AIza" },
                PreferredModels = new[] { "gemini-2.0-flash", "gemini-2.5-flash", "gemini-1.5-flash" },
            },
            new Provider
            {
                Id = "openrouter",
                DisplayName = "OpenRouter",
                BaseUrl = "https://openrouter.ai/api/v1",
                KeyPrefixes = new[] { "sk-or-" },
                PreferredModels = new[] { "openai/gpt-4o-mini", "deepseek/deepseek-chat" },
                // /models 是公开接口，必须用 /key 验证 Key 归属，避免任意 Key 误判为 OpenRouter。
                AuthProbePath = "/key",
            },
            new Provider
            {
                Id = "groq",
                DisplayName = "Groq",
                BaseUrl = "https://api.groq.com/openai/v1",
                KeyPrefixes = new[] { "gsk_" },
                PreferredModels = new[] { "llama-3.3-70b-versatile", "llama-3.1-8b-instant" },
            },
            new Provider
            {
                Id = "mistral",
                DisplayName = "Mistral",
                BaseUrl = "https://api.mistral.ai/v1",
                PreferredModels = new[] { "mistral-small-latest", "open-mistral-nemo", "mistral-large-latest" },
            },
        };

        private static readonly string[] IgnoredModelTokens =
        {
            "embed", "embedding", "rerank", "tts", "whisper", "transcribe", "audio",
            "realtime", "dall-e", "moderation", "stable-diffusion", "sdxl", "flux",
            "ocr", "guard", "bge", "m3e", "gme", "veo", "imagen",
        };

        private static readonly string[] ChatModelTokens =
        {
            "chat", "instruct", "gpt", "deepseek", "kimi", "moonshot", "qwen", "glm",
            "claude", "llama", "mistral", "gemini", "yi-", "baichuan", "step",
            "hunyuan", "doubao", "ernie", "spark", "minimax", "abab",
        };

        private const int MaxModels = 150;

        private static readonly HttpClient HttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8),
        };

        static AiProviderDetector()
        {
            try
            {
                HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ContactableWallpaper/1.0");
            }
            catch (Exception)
            {
                // 自定义 UA 失败不影响探测。
            }
        }

        /// <summary>
        /// 仅凭 API Key 识别服务商。调用方若已有 Key 且不需要更换，可直接传入已保存的 Key。
        /// </summary>
        public async Task<AiProviderDetection> DetectAsync(string apiKey, CancellationToken cancellationToken = default)
        {
            var key = (apiKey ?? string.Empty).Trim();
            if (key.Length == 0)
                return AiProviderDetection.Fail("请先粘贴 API Key。");

            // Anthropic 原生接口不是 OpenAI 兼容协议，直接给出明确提示，避免无意义的批量探测。
            if (key.StartsWith("sk-ant-", StringComparison.OrdinalIgnoreCase))
            {
                return AiProviderDetection.Fail(
                    "检测到 Anthropic（Claude）Key，但当前仅支持 OpenAI 兼容接口。请展开「手动配置」填写兼容网关的 BaseUrl 与模型。");
            }

            var ordered = OrderByKeyHint(key);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            // 整体预算：正常网络下首选服务商 1–2 秒内即可返回。
            linked.CancelAfter(TimeSpan.FromSeconds(10));

            var pending = new List<Task<ProbeResult>>();
            for (var i = 0; i < ordered.Count; i++)
            {
                // 错峰发起：越靠前的服务商越先探测，命中后立即取消其余请求，减少 Key 外发。
                pending.Add(ProbeAsync(ordered[i], key, i * 300, linked.Token));
            }

            var hadNetworkError = false;
            var hadAuthError = false;
            try
            {
                while (pending.Count > 0)
                {
                    var finished = await Task.WhenAny(pending).ConfigureAwait(false);
                    pending.Remove(finished);
                    var result = await finished.ConfigureAwait(false);
                    if (result.Detection != null)
                    {
                        linked.Cancel();
                        return result.Detection;
                    }
                    hadNetworkError |= result.IsNetworkError;
                    hadAuthError |= result.IsAuthError;
                }
            }
            catch (OperationCanceledException)
            {
                // 总预算耗尽，按未识别处理。
            }

            if (hadNetworkError)
                return AiProviderDetection.Fail("未能自动识别服务商：网络无法访问常见 AI 服务（可能是代理或防火墙限制）。请展开「手动配置」填写 BaseUrl 与模型。");
            if (hadAuthError)
                return AiProviderDetection.Fail("未能自动识别服务商：该 API Key 在常见服务商处均鉴权失败，请确认 Key 是否有效。也可展开「手动配置」填写 BaseUrl 与模型。");
            return AiProviderDetection.Fail("未能自动识别该 API Key 对应的服务商。请展开「手动配置」填写 BaseUrl 与模型。");
        }

        /// <summary>把配置里的服务商标识转成可读名称；无法识别时退回域名。</summary>
        public static string DescribeProvider(string providerId, string baseUrl)
        {
            if (!string.IsNullOrWhiteSpace(providerId))
            {
                var match = Providers.FirstOrDefault(p =>
                    string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(p.DisplayName, providerId, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    return match.DisplayName;
            }

            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
                return uri.Host;
            return "自定义服务";
        }

        /// <summary>BaseUrl 是否为空或合法的 http/https 绝对地址（空串视为合法，表示未填写）。</summary>
        public static bool IsValidBaseUrl(string value)
        {
            var text = (value ?? string.Empty).Trim();
            if (text.Length == 0)
                return true;
            return Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        private static List<Provider> OrderByKeyHint(string apiKey)
        {
            var result = new List<Provider>(Providers.Length);
            Provider hinted = null;
            foreach (var provider in Providers)
            {
                if (provider.KeyPrefixes == null)
                    continue;
                foreach (var prefix in provider.KeyPrefixes)
                {
                    if (apiKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        hinted = provider;
                        break;
                    }
                }
                if (hinted != null)
                    break;
            }

            if (hinted != null)
                result.Add(hinted);
            foreach (var provider in Providers)
            {
                if (provider != hinted)
                    result.Add(provider);
            }
            return result;
        }

        private async Task<ProbeResult> ProbeAsync(Provider provider, string apiKey, int delayMs, CancellationToken token)
        {
            try
            {
                if (delayMs > 0)
                    await Task.Delay(delayMs, token).ConfigureAwait(false);

                var baseUrl = provider.BaseUrl.TrimEnd('/');

                if (!string.IsNullOrEmpty(provider.AuthProbePath))
                {
                    var auth = await GetAsync(baseUrl + provider.AuthProbePath, apiKey, token).ConfigureAwait(false);
                    if (!auth.IsSuccess)
                        return ProbeResult.Failed(auth.StatusCode);
                }

                var modelsResponse = await GetAsync(baseUrl + "/models", apiKey, token).ConfigureAwait(false);
                if (!modelsResponse.IsSuccess)
                    return ProbeResult.Failed(modelsResponse.StatusCode);

                var models = FilterChatModels(ParseModels(modelsResponse.Body));
                if (models.Count == 0)
                    return new ProbeResult();

                var selected = PickPreferredModel(provider, models);
                var ordered = new List<string> { selected };
                ordered.AddRange(models.Where(m => !string.Equals(m, selected, StringComparison.OrdinalIgnoreCase)));

                return new ProbeResult
                {
                    Detection = new AiProviderDetection
                    {
                        IsSuccess = true,
                        Provider = provider.Id,
                        DisplayName = provider.DisplayName,
                        BaseUrl = provider.BaseUrl,
                        Models = ordered,
                        SelectedModel = selected,
                    }
                };
            }
            catch (OperationCanceledException)
            {
                return new ProbeResult();
            }
            catch (HttpRequestException)
            {
                return new ProbeResult { IsNetworkError = true };
            }
            catch (Exception)
            {
                return new ProbeResult();
            }
        }

        private static async Task<(bool IsSuccess, int StatusCode, string Body)> GetAsync(string url, string apiKey, CancellationToken token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            return (response.IsSuccessStatusCode, (int)response.StatusCode, body);
        }

        private static List<string> ParseModels(string body)
        {
            var models = new List<string>();
            if (string.IsNullOrWhiteSpace(body))
                return models;

            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.Array)
                    return models;

                foreach (var item in data.EnumerateArray())
                {
                    string id = null;
                    if (item.ValueKind == JsonValueKind.Object &&
                        item.TryGetProperty("id", out var idElement) &&
                        idElement.ValueKind == JsonValueKind.String)
                    {
                        id = idElement.GetString();
                    }
                    else if (item.ValueKind == JsonValueKind.String)
                    {
                        id = item.GetString();
                    }

                    if (!string.IsNullOrWhiteSpace(id))
                        models.Add(id.Trim());
                }
            }
            catch (JsonException)
            {
                // 非 JSON 或结构不符，按未解析到模型处理。
            }

            return models.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<string> FilterChatModels(List<string> models)
        {
            var withoutUtility = models.Where(m => !ContainsAny(m, IgnoredModelTokens)).ToList();
            var chatOnly = withoutUtility.Where(m => ContainsAny(m, ChatModelTokens)).ToList();
            var candidates = chatOnly.Count > 0
                ? chatOnly
                : (withoutUtility.Count > 0 ? withoutUtility : models);

            return candidates
                .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
                .Take(MaxModels)
                .ToList();
        }

        private static string PickPreferredModel(Provider provider, IReadOnlyList<string> models)
        {
            foreach (var preferred in provider.PreferredModels)
            {
                var exact = models.FirstOrDefault(m => string.Equals(m, preferred, StringComparison.OrdinalIgnoreCase));
                if (exact != null)
                    return exact;
            }

            foreach (var preferred in provider.PreferredModels)
            {
                var partial = models.FirstOrDefault(m => m.IndexOf(preferred, StringComparison.OrdinalIgnoreCase) >= 0);
                if (partial != null)
                    return partial;
            }

            // 通用兜底：优先低成本、日常生成够用的型号。
            foreach (var token in new[] { "flash", "mini", "chat", "plus", "pro", "max" })
            {
                var hit = models.FirstOrDefault(m => m.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0);
                if (hit != null)
                    return hit;
            }

            return models[0];
        }

        private static bool ContainsAny(string text, IEnumerable<string> tokens)
        {
            if (string.IsNullOrEmpty(text))
                return false;
            foreach (var token in tokens)
            {
                if (text.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private sealed class ProbeResult
        {
            public AiProviderDetection Detection;
            public bool IsAuthError;
            public bool IsNetworkError;

            public static ProbeResult Failed(int statusCode)
            {
                if (statusCode == 401 || statusCode == 403)
                    return new ProbeResult { IsAuthError = true };
                return new ProbeResult();
            }
        }
    }
}
