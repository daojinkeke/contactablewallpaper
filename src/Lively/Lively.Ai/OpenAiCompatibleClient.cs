using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lively.Models.Ai;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Lively.Ai
{
    public enum AiFailureKind
    {
        Network = 0,
        Authentication = 1,
        Quota = 2,
        ContentRejected = 3,
        Timeout = 4,
        Protocol = 5,
        ModelNotFound = 6
    }

    /// <summary>
    /// 生成失败的分类异常。消息面向用户，必须是一句话可读文本（07 契约 §10.3 约束）。
    /// </summary>
    public class AiGenerationException : Exception
    {
        public AiGenerationException(AiFailureKind kind, string message, Exception inner = null)
            : base(message, inner)
        {
            Kind = kind;
        }

        public AiFailureKind Kind { get; }

        public string ErrorCode
        {
            get
            {
                switch (Kind)
                {
                    case AiFailureKind.Authentication:
                        return AiErrorCodes.AuthenticationFailed;
                    case AiFailureKind.Quota:
                        return AiErrorCodes.QuotaExceeded;
                    case AiFailureKind.ContentRejected:
                        return AiErrorCodes.ContentRejected;
                    case AiFailureKind.Timeout:
                        return AiErrorCodes.Timeout;
                    case AiFailureKind.Network:
                        return AiErrorCodes.NetworkError;
                    case AiFailureKind.ModelNotFound:
                        return AiErrorCodes.ModelNotFound;
                    case AiFailureKind.Protocol:
                        return AiErrorCodes.ProtocolError;
                    default:
                        return AiErrorCodes.ValidationFailed;
                }
            }
        }
    }

    /// <summary>
    /// 流式响应的单个增量，可能只带内容、只带用量，或两者都有。
    /// </summary>
    public sealed class CompletionChunk
    {
        public string Content { get; set; }
        public int? PromptTokens { get; set; }
        public int? CompletionTokens { get; set; }
        public int? TotalTokens { get; set; }
    }

    /// <summary>
    /// OpenAI 兼容 Chat Completions 客户端（流式优先）。不绑定任何具体厂商。
    /// </summary>
    public class OpenAiCompatibleClient
    {
        private readonly HttpClient httpClient;

        public OpenAiCompatibleClient(HttpClient httpClient = null)
        {
            this.httpClient = httpClient ?? new HttpClient
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
        }

        public async IAsyncEnumerable<CompletionChunk> StreamCompletionAsync(
            AiSettings settings,
            string apiKey,
            string systemPrompt,
            string userPrompt,
            [EnumeratorCancellation] CancellationToken token)
        {
            var endpoint = BuildEndpoint(settings.BaseUrl);

            using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            idleCts.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));

            HttpResponseMessage response = null;
            try
            {
                response = await SendWithUsageFallbackAsync(settings, apiKey, endpoint, systemPrompt, userPrompt, idleCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new AiGenerationException(AiFailureKind.Timeout,
                    $"AI 服务在 {settings.TimeoutSeconds} 秒内没有响应，已判定超时。");
            }
            catch (HttpRequestException ex)
            {
                throw new AiGenerationException(AiFailureKind.Network,
                    $"网络连接失败：无法访问 {SafeHost(endpoint)}，请检查网络或代理设置。", ex);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await ReadBodySafeAsync(response, token).ConfigureAwait(false);
                    throw ClassifyHttpFailure((int)response.StatusCode, errorBody);
                }

                var isEventStream = response.Content.Headers.ContentType?.MediaType != null &&
                                    response.Content.Headers.ContentType.MediaType.IndexOf("event-stream", StringComparison.OrdinalIgnoreCase) >= 0;

                if (!isEventStream)
                {
                    // 部分兼容服务会忽略 stream 参数，直接返回完整 JSON。
                    var body = await ReadBodySafeAsync(response, token).ConfigureAwait(false);
                    var full = ExtractFullCompletion(body);
                    if (string.IsNullOrEmpty(full.Content) && full.TotalTokens == null)
                        throw new AiGenerationException(AiFailureKind.Protocol, "AI 服务返回了空内容，请稍后重试。");
                    yield return full;
                    yield break;
                }

                Stream stream;
                try
                {
                    stream = await response.Content.ReadAsStreamAsync(idleCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new AiGenerationException(AiFailureKind.Timeout,
                        $"AI 服务在 {settings.TimeoutSeconds} 秒内没有响应，已判定超时。");
                }
                catch (HttpRequestException ex)
                {
                    throw new AiGenerationException(AiFailureKind.Network, "网络连接中断，未能读取 AI 返回内容。", ex);
                }

                using (stream)
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    while (true)
                    {
                        string line;
                        try
                        {
                            line = await reader.ReadLineAsync(idleCts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested)
                        {
                            throw new AiGenerationException(AiFailureKind.Timeout,
                                $"AI 服务在 {settings.TimeoutSeconds} 秒内没有任何增量，已判定超时。");
                        }
                        catch (IOException ex)
                        {
                            throw new AiGenerationException(AiFailureKind.Network, "网络连接中断，未能继续读取 AI 返回内容。", ex);
                        }

                        if (line == null)
                            break;
                        if (line.Length == 0)
                            continue;
                        if (!line.StartsWith("data:", StringComparison.Ordinal))
                            continue;

                        var data = line.Substring(5).Trim();
                        if (data.Length == 0)
                            continue;
                        if (data == "[DONE]")
                            break;

                        var chunk = ParseChunk(data);
                        if (chunk == null)
                            continue;

                        // 有增量即重置空闲计时。
                        idleCts.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
                        yield return chunk;
                    }
                }
            }
        }

        private async Task<HttpResponseMessage> SendWithUsageFallbackAsync(
            AiSettings settings,
            string apiKey,
            string endpoint,
            string systemPrompt,
            string userPrompt,
            CancellationToken token)
        {
            var response = await SendAsync(settings, apiKey, endpoint, systemPrompt, userPrompt, true, token).ConfigureAwait(false);
            if ((int)response.StatusCode != 400)
                return response;

            // 部分兼容服务不认识 stream_options，去掉后重试一次。
            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            response.Dispose();

            var lower = body.ToLowerInvariant();
            if (lower.Contains("stream_options") || lower.Contains("include_usage"))
            {
                return await SendAsync(settings, apiKey, endpoint, systemPrompt, userPrompt, false, token).ConfigureAwait(false);
            }

            throw ClassifyHttpFailure(400, body);
        }

        private async Task<HttpResponseMessage> SendAsync(
            AiSettings settings,
            string apiKey,
            string endpoint,
            string systemPrompt,
            string userPrompt,
            bool includeUsage,
            CancellationToken token)
        {
            var payload = new JObject
            {
                ["model"] = settings.Model,
                ["stream"] = true,
                ["temperature"] = 0.7,
                ["messages"] = new JArray
                {
                    new JObject { ["role"] = "system", ["content"] = systemPrompt },
                    new JObject { ["role"] = "user", ["content"] = userPrompt }
                }
            };
            if (includeUsage)
                payload["stream_options"] = new JObject { ["include_usage"] = true };

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        }

        private static CompletionChunk ParseChunk(string data)
        {
            JObject obj;
            try
            {
                obj = JObject.Parse(data);
            }
            catch (JsonException)
            {
                return null;
            }

            var error = obj["error"];
            if (error is JObject errorObject)
            {
                var message = errorObject["message"]?.ToString() ?? string.Empty;
                var code = errorObject["code"]?.ToString() ?? string.Empty;
                if (LooksLikeContentPolicy(message + " " + code))
                    throw new AiGenerationException(AiFailureKind.ContentRejected,
                        "生成内容被服务商判定为不合规，请修改描述后重试。");
                if (LooksLikeMissingModel(message + " " + code))
                    throw new AiGenerationException(AiFailureKind.ModelNotFound,
                        "模型不存在或无权限访问：请检查 Model 配置是否与 BaseUrl 匹配。");
                throw new AiGenerationException(AiFailureKind.Protocol,
                    "AI 服务返回错误：" + Truncate(message, 160));
            }

            var chunk = new CompletionChunk();
            if (obj["usage"] is JObject usage)
            {
                chunk.PromptTokens = usage.Value<int?>("prompt_tokens");
                chunk.CompletionTokens = usage.Value<int?>("completion_tokens");
                chunk.TotalTokens = usage.Value<int?>("total_tokens");
            }

            var choice = obj["choices"] is JArray choices && choices.Count > 0 ? choices[0] : null;
            var content = ExtractChoiceContent(choice);
            chunk.Content = content;

            if (chunk.Content == null && chunk.TotalTokens == null && chunk.PromptTokens == null && chunk.CompletionTokens == null)
                return null;
            return chunk;
        }

        private static string ExtractChoiceContent(JToken choice)
        {
            if (choice is not JObject choiceObject)
                return null;

            if (choiceObject["delta"] is JObject delta)
            {
                var content = delta["content"]?.ToString();
                if (!string.IsNullOrEmpty(content))
                    return content;
            }

            if (choiceObject["message"] is JObject message)
                return message["content"]?.ToString();

            return null;
        }

        private static CompletionChunk ExtractFullCompletion(string body)
        {
            try
            {
                var obj = JObject.Parse(body);
                var error = obj["error"];
                if (error is JObject errorObject)
                {
                    var message = errorObject["message"]?.ToString() ?? string.Empty;
                    if (LooksLikeContentPolicy(message))
                        throw new AiGenerationException(AiFailureKind.ContentRejected,
                            "生成内容被服务商判定为不合规，请修改描述后重试。");
                    if (LooksLikeMissingModel(message))
                        throw new AiGenerationException(AiFailureKind.ModelNotFound,
                            "模型不存在或无权限访问：请检查 Model 配置是否与 BaseUrl 匹配。");
                    throw new AiGenerationException(AiFailureKind.Protocol, "AI 服务返回错误：" + Truncate(message, 160));
                }

                var chunk = new CompletionChunk
                {
                    Content = ExtractChoiceContent(obj["choices"] is JArray choices && choices.Count > 0 ? choices[0] : null)
                };
                if (obj["usage"] is JObject usage)
                {
                    chunk.PromptTokens = usage.Value<int?>("prompt_tokens");
                    chunk.CompletionTokens = usage.Value<int?>("completion_tokens");
                    chunk.TotalTokens = usage.Value<int?>("total_tokens");
                }
                return chunk;
            }
            catch (JsonException)
            {
                throw new AiGenerationException(AiFailureKind.Protocol, "AI 服务返回了无法解析的内容。");
            }
        }

        private static AiGenerationException ClassifyHttpFailure(int statusCode, string body)
        {
            var lower = (body ?? string.Empty).ToLowerInvariant();

            if (statusCode == 401 || statusCode == 403)
                return new AiGenerationException(AiFailureKind.Authentication,
                    "AI 服务鉴权失败：API Key 无效、已过期或无权访问该模型，请检查配置。");
            if (statusCode == 429)
                return new AiGenerationException(AiFailureKind.Quota,
                    "AI 服务额度不足或请求过于频繁，请检查账户额度后重试。");
            if (statusCode == 404 || LooksLikeMissingModel(lower))
                return new AiGenerationException(AiFailureKind.ModelNotFound,
                    "模型不存在或无权限访问：请检查 Model 配置是否与 BaseUrl 匹配。");
            if (statusCode == 400 && LooksLikeContentPolicy(lower))
                return new AiGenerationException(AiFailureKind.ContentRejected,
                    "生成内容被服务商判定为不合规，请修改描述后重试。");
            return new AiGenerationException(AiFailureKind.Protocol,
                $"AI 服务返回 HTTP {statusCode}：" + Truncate(body, 200));
        }

        private static bool LooksLikeMissingModel(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;
            var lower = text.ToLowerInvariant();
            return lower.Contains("model_not_found") ||
                   (lower.Contains("model") && (lower.Contains("not found") ||
                                                lower.Contains("does not exist") ||
                                                lower.Contains("doesn't exist") ||
                                                lower.Contains("not exist") ||
                                                lower.Contains("unknown model") ||
                                                lower.Contains("invalid model") ||
                                                lower.Contains("no such model")));
        }

        private static bool LooksLikeContentPolicy(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;
            var lower = text.ToLowerInvariant();
            return lower.Contains("content_policy") || lower.Contains("content policy") ||
                   lower.Contains("content_filter") || lower.Contains("content filter") ||
                   lower.Contains("safety") || lower.Contains("moderation");
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
            var trimmed = baseUrl.Trim().TrimEnd('/');
            if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                return trimmed;
            return trimmed + "/chat/completions";
        }

        private static string SafeHost(string endpoint)
        {
            return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Host : "AI 服务";
        }

        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }
    }
}
