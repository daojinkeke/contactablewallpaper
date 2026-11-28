using System;
using Lively.Models.Ai;
using Newtonsoft.Json.Linq;

namespace Lively.Ai
{
    /// <summary>
    /// DeepSeek 新模型默认开启思考模式，长文生成会产生大量思维链 token 并显著拖慢响应。
    /// 壁纸生成/读图只需要最终结果，统一显式关闭思考模式。
    /// </summary>
    internal static class AiRequestModes
    {
        /// <summary>当前模型是否支持 thinking 开关（仅 DeepSeek Flash / V4 系列）。</summary>
        public static bool SupportsThinkingToggle(string baseUrl, string model)
        {
            if (!IsDeepSeek(baseUrl))
                return false;

            var name = (model ?? string.Empty).Trim();
            return name.StartsWith("deepseek-flash", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("deepseek-v4", StringComparison.OrdinalIgnoreCase);
        }

        public static bool SupportsThinkingToggle(AiSettings settings) =>
            settings != null && SupportsThinkingToggle(settings.BaseUrl, settings.Model);

        /// <summary>关闭思考模式；非 DeepSeek 或旧模型不写入该字段，避免兼容服务报 400。</summary>
        public static void DisableThinking(JObject payload, string baseUrl, string model)
        {
            if (payload != null && SupportsThinkingToggle(baseUrl, model))
                payload["thinking"] = new JObject { ["type"] = "disabled" };
        }

        public static void DisableThinking(JObject payload, AiSettings settings) =>
            DisableThinking(payload, settings?.BaseUrl, settings?.Model);

        private static bool IsDeepSeek(string baseUrl) =>
            Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) &&
            uri.Host.EndsWith("deepseek.com", StringComparison.OrdinalIgnoreCase);
    }
}
