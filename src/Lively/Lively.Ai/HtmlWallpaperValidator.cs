using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Lively.Models.Ai;

namespace Lively.Ai
{
    /// <summary>
    /// 纯本地 HTML 校验（07 契约 §6）。无网络、无副作用，同一输入结果稳定。
    /// </summary>
    public interface IHtmlWallpaperValidator
    {
        ValidationResult Validate(string htmlPath, ValidationOptions options);
    }

    public class HtmlWallpaperValidator : IHtmlWallpaperValidator
    {
        private static readonly Regex AbsolutePathRegex = new Regex(
            @"file:///|(?:src|href)\s*=\s*[""']\s*(?:[A-Za-z]:[\\/]|/)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ExternalResourceRegex = new Regex(
            @"(?:src|href)\s*=\s*[""']\s*https?://|url\(\s*[""']?\s*https?://|@import\s+(?:url\()?\s*[""']?\s*https?://|\bfetch\s*\(|\bXMLHttpRequest\b|\bWebSocket\s*\(",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex CanvasSizeRegex = new Regex(
            @"<canvas\b[^>]*\bwidth\s*=\s*[""']?(\d+)[^>]*\bheight\s*=\s*[""']?(\d+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex CanvasSizeReverseRegex = new Regex(
            @"<canvas\b[^>]*\bheight\s*=\s*[""']?(\d+)[^>]*\bwidth\s*=\s*[""']?(\d+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ResizeRegex = new Regex(
            @"addEventListener\s*\(\s*[""']resize[""']|\bonresize\s*=|ResizeObserver",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex AutoplayAudioRegex = new Regex(
            @"<audio\b[^>]*\bautoplay\b|<audio\b[^>]*>\s*<source",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex CharsetRegex = new Regex(
            @"<meta\b[^>]*charset\s*=\s*[""']?\s*utf-8",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>交互监听扫描（V009）：原生 DOM 事件即可，不限定写法。</summary>
        private static readonly Regex PointerMoveRegex = new Regex(
            @"addEventListener\s*\(\s*[""'](?:pointermove|mousemove|touchmove)[""']|\.on(?:pointer|mouse)move\s*=|[\s""']onmousemove\s*=",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex PointerDownRegex = new Regex(
            @"addEventListener\s*\(\s*[""'](?:pointerdown|mousedown|click|touchstart)[""']|\.on(?:pointer|mouse)down\s*=|[\s""']on(?:mousedown|click|pointerdown)\s*=",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex KeyRegex = new Regex(
            @"addEventListener\s*\(\s*[""'](?:keydown|keyup)[""']|\.onkey(?:down|up)\s*=|[\s""']onkey(?:down|up)\s*=",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>07 契约 §6.2 禁用 API 扫描表。</summary>
        private static readonly string[] BannedPatterns =
        {
            "getusermedia",
            "getdisplaymedia",
            "navigator.mediadevices",
            "navigator.geolocation",
            "new notification",
            "notification.requestpermission",
            "window.open",
            "alert(",
            "confirm(",
            "prompt(",
            "target=\"_blank\"",
            "target='_blank'",
            "location.href",
            "location.replace",
            "download=",
            "showsavefilepicker",
            "showopenfilepicker",
            "filereader",
            "chrome.webview.postmessage",
        };

        public ValidationResult Validate(string htmlPath, ValidationOptions options)
        {
            options = options ?? new ValidationOptions();
            var result = new ValidationResult();

            if (string.IsNullOrWhiteSpace(htmlPath) || !File.Exists(htmlPath))
            {
                result.IsValid = false;
                result.Issues.Add(new ValidationIssue("V001", ValidationSeverity.fatal, "找不到待校验的 HTML 文件。"));
                return result;
            }

            var bytes = File.ReadAllBytes(htmlPath);
            result.SizeBytes = bytes.LongLength;

            // V002 体积上限。
            if (bytes.LongLength > options.MaxBytes)
            {
                result.IsValid = false;
                result.Issues.Add(new ValidationIssue("V002", ValidationSeverity.fatal,
                    $"生成结果 {bytes.LongLength / 1024} KB，超过 {options.MaxBytes / 1024} KB 上限。"));
                return result;
            }

            var html = new UTF8Encoding(false, false).GetString(bytes);

            // V001 可解析为 HTML 文档。
            if (html.IndexOf("<html", StringComparison.OrdinalIgnoreCase) < 0 ||
                html.IndexOf("</html>", StringComparison.OrdinalIgnoreCase) < 0)
            {
                result.IsValid = false;
                result.Issues.Add(new ValidationIssue("V001", ValidationSeverity.fatal,
                    "内容不是完整的 HTML 文档（缺少 <html> 或 </html>）。"));
                return result;
            }

            // V003 绝对路径 / file:///。
            if (AbsolutePathRegex.IsMatch(html))
            {
                result.IsValid = false;
                result.Issues.Add(new ValidationIssue("V003", ValidationSeverity.fatal,
                    "包含绝对路径或 file:/// 引用，壁纸只允许相对路径。"));
                return result;
            }

            // V004 离线模式外链资源。
            if (options.OfflineRequired && ExternalResourceRegex.IsMatch(html))
            {
                result.IsValid = false;
                result.Issues.Add(new ValidationIssue("V004", ValidationSeverity.fatal,
                    "离线模式下包含外链资源或网络请求（http/https、fetch、XHR、WebSocket）。"));
                return result;
            }

            // V005 禁用 API。
            var lower = html.ToLowerInvariant();
            var bannedHits = new List<string>();
            foreach (var pattern in BannedPatterns)
            {
                if (lower.Contains(pattern))
                    bannedHits.Add(pattern);
            }
            if (bannedHits.Count > 0)
            {
                result.IsValid = false;
                result.Issues.Add(new ValidationIssue("V005", ValidationSeverity.fatal,
                    "命中禁用 API：" + string.Join(", ", bannedHits) + "。"));
                return result;
            }

            // V006 自动播放音频（warning）。
            if (AutoplayAudioRegex.IsMatch(html))
            {
                result.Issues.Add(new ValidationIssue("V006", ValidationSeverity.warning,
                    "页面包含音频元素，桌面壁纸默认不应自动播放声音。"));
            }

            // V007 编码声明（warning）。
            if (!CharsetRegex.IsMatch(html))
            {
                result.Issues.Add(new ValidationIssue("V007", ValidationSeverity.warning,
                    "缺少 <meta charset=\"utf-8\">，存在中文乱码风险。"));
            }

            // V008 自适应性（warning）。
            if (!ResizeRegex.IsMatch(html))
            {
                result.Issues.Add(new ValidationIssue("V008", ValidationSeverity.warning,
                    "未监听 resize，切换到其他分辨率时可能错位。"));
            }
            var canvas = CanvasSizeRegex.Match(html);
            if (canvas.Success)
            {
                CheckCanvasSize(result, canvas.Groups[1].Value, canvas.Groups[2].Value, options);
            }
            else
            {
                canvas = CanvasSizeReverseRegex.Match(html);
                if (canvas.Success)
                    CheckCanvasSize(result, canvas.Groups[2].Value, canvas.Groups[1].Value, options);
            }

            // V009 交互能力：要求交互时，页面必须真的监听了对应事件。
            if (options.RequiredInteraction != InteractionCapability.none)
            {
                var hasMove = PointerMoveRegex.IsMatch(html);
                var hasDown = PointerDownRegex.IsMatch(html);
                var missing = new List<string>();
                if ((options.RequiredInteraction & InteractionCapability.hover) != 0 && !hasMove)
                    missing.Add("hover（pointermove/mousemove）");
                if ((options.RequiredInteraction & InteractionCapability.click) != 0 && !hasDown)
                    missing.Add("click（pointerdown/click）");
                if ((options.RequiredInteraction & InteractionCapability.drag) != 0 && !(hasMove && hasDown))
                    missing.Add("drag（pointerdown + pointermove）");
                if ((options.RequiredInteraction & InteractionCapability.keyboard) != 0 && !KeyRegex.IsMatch(html))
                    missing.Add("keyboard（keydown/keyup）");
                if (missing.Count > 0)
                {
                    result.Issues.Add(new ValidationIssue("V009", ValidationSeverity.fatal,
                        "缺少要求的交互监听：" + string.Join("、", missing) +
                        "。交互必须使用原生 DOM 事件驱动画面，并在空闲时保持完整动画。"));
                }
            }

            result.IsValid = !result.HasFatal;
            return result;
        }

        private static void CheckCanvasSize(ValidationResult result, string widthText, string heightText, ValidationOptions options)
        {
            if (!int.TryParse(widthText, out var width) || !int.TryParse(heightText, out var height))
                return;
            if (options.TargetWidth <= 0 || options.TargetHeight <= 0)
                return;
            if (width == options.TargetWidth && height == options.TargetHeight)
                return;

            result.Issues.Add(new ValidationIssue("V008", ValidationSeverity.warning,
                $"画布固定为 {width}x{height}，与目标分辨率 {options.TargetWidth}x{options.TargetHeight} 不一致。"));
        }
    }
}
