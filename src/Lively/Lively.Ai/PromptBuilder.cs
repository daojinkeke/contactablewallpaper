using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Lively.Models.Ai;

namespace Lively.Ai
{
    /// <summary>
    /// 拼装 OpenAI 兼容接口的 system/user 指令。输出约束来自 07 契约 §5.2 与 PRD 6.2。
    /// </summary>
    public static class PromptBuilder
    {
        public const string SystemPrompt =
            "You are a senior creative front-end engineer who builds desktop live wallpapers.\n" +
            "Return exactly one self-contained HTML document and nothing else (no markdown fences, no explanations).\n" +
            "Hard requirements:\n" +
            "1. Single file: all CSS in <style> and all JavaScript in <script>. Entry file is index.html.\n" +
            "2. No external resources at all: no http(s) URLs in src/href/url()/@import, no external fonts, images, scripts or styles. " +
            "Use inline SVG, CSS gradients, canvas or data: URIs only.\n" +
            "3. No absolute paths and no file:/// references.\n" +
            "4. Forbidden APIs: alert/confirm/prompt, window.open, location.href/location.replace, target=\"_blank\", " +
            "navigation or downloads; getUserMedia/getDisplayMedia/navigator.mediaDevices; geolocation; Notification; " +
            "showSaveFilePicker/showOpenFilePicker.\n" +
            "5. No audio autoplay; any sound must require a user gesture. Prefer silent wallpapers.\n" +
            "6. The wallpaper must look complete on its own without any mouse movement, and must adapt to ANY resolution: " +
            "use 100vw/100vh or fixed inset:0 canvases, recompute layout on window resize, never fix the page to a hard-coded pixel size " +
            "and never rely on the pointer position to reveal the scene.\n" +
            "7. Keep it lightweight enough for a wallpaper: target <= 60 FPS, avoid runaway timers and memory growth.\n" +
            "8. Interaction must be real code, not a promise: register native DOM listeners (pointermove/pointerdown/keydown) " +
            "and drive the visuals from them as described in the user message. Do NOT implement a proprietary handshake and do NOT " +
            "listen for Lively APIs; the host injects a bridge. Always reset interaction state on pointerleave/blur, and never let " +
            "interaction freeze or replace the idle animation.\n" +
            "9. Output must be a complete document starting with <!DOCTYPE html> and ending with </html>.";

        private const string InteractionScaffoldResource = "Lively.Ai.References.interaction-scaffold.js.txt";
        private static readonly string? InteractionScaffold = LoadEmbeddedText(InteractionScaffoldResource);

        /// <summary>
        /// 节能模式约束：只描述行为，不给具体数值，避免不同模型照抄参数导致效果僵硬。
        /// 追加在用户描述之后、交互骨架之前，不覆盖或改写用户的构图/主题/风格诉求。
        /// </summary>
        private const string EcoSpec =
            "Eco mode (mandatory, the page must implement this itself):\n" +
            "- While the user has been idle for a while (no pointer or keyboard activity), the page should progressively " +
            "lower its rendering frame rate, simplify or skip expensive per-frame calculations, and pause non-essential " +
            "visual effects. The idle scene must stay visually coherent, never freeze abruptly or go black.\n" +
            "- As soon as any pointer or keyboard input arrives, restore the original frame rate and full effects immediately. " +
            "Ease the transition so the picture does not visibly jump, flash or stutter.\n" +
            "- While the user is interacting, smoothness must be at least as good as without eco mode; never trade " +
            "interaction quality for power savings.\n" +
            "- Choose your own idle detection, throttling and easing approach; do not copy fixed numbers or hard-code values " +
            "that came from this instruction. Keep it native browser code with no external resources and no forbidden APIs.\n" +
            "- Eco behavior only adjusts timing and effect budget. It must not weaken the user's description: keep the " +
            "composition, theme, palette and style exactly as requested, and keep the existing interaction behavior intact.";

        /// <summary>
        /// Self-Refine 评审指令：只打分和列问题，不重写页面，输出严格 JSON。
        /// 与生成用的 static system prompt 分开，避免污染生成请求的前缀缓存。
        /// </summary>
        public const string CritiqueSystemPrompt =
            "You are a ruthless senior art director reviewing a generated desktop live wallpaper. You also act as the planner " +
            "of an incremental edit (ReAct: reason first, then the rewrite follows).\n" +
            "Score it honestly on a 1-10 scale (10 = portfolio quality). Be strict: most first drafts are 5-7.\n" +
            "Judge five dimensions: composition, color, motion, theme, interaction.\n" +
            "Return ONLY a JSON object, no markdown fences and no prose, using exactly this shape:\n" +
            "{\"composition\":n,\"color\":n,\"motion\":n,\"theme\":n,\"interaction\":n,\"overall\":n," +
            "\"plan\":[\"...\"],\"rationale\":\"...\",\"issues\":[\"...\"],\"fixes\":[\"...\"]}\n" +
            "Rules: overall must reflect the whole piece, not the average; plan must contain 2-4 short, concrete edit steps " +
            "(which parts to change and why); rationale is one sentence; issues must be concrete visual or code defects " +
            "(max 5, e.g. 'flat background with no depth cues'); fixes must be actionable and listed in the same order. " +
            "Do not reward valid code; judge only the visual result. When the user asks for a revision, the plan must target " +
            "that request and must not propose rebuilding the whole scene unless the user explicitly asks for a redo.";

        public static string BuildUserPrompt(GenerationRequest request)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Create an animated HTML desktop wallpaper.");
            sb.AppendLine("User description:");
            sb.AppendLine(request.Prompt.Trim());
            sb.AppendLine();
            sb.AppendLine("Target viewport: " + request.TargetWidth + "x" + request.TargetHeight +
                          " CSS px at DPI scale " + request.DpiScale.ToString("0.##", CultureInfo.InvariantCulture) +
                          ". It must still be responsive to other resolutions.");
            sb.AppendLine("Style preset: " + request.Style);
            if (!string.IsNullOrWhiteSpace(request.PrimaryColor))
                sb.AppendLine("Primary color: " + request.PrimaryColor);
            sb.AppendLine("Interaction requested: " + DescribeInteraction(request.Interaction));
            AppendInteractionSpec(sb, request.Interaction);
            AppendReference(sb, request);
            sb.AppendLine("Performance preference: " + request.PerformanceMode +
                          (request.PerformanceMode == PerformanceMode.saver
                              ? " (prefer CSS/compositor animation, avoid WebGL and heavy per-frame JavaScript)"
                              : request.PerformanceMode == PerformanceMode.quality
                                  ? " (WebGL/particles allowed)"
                                  : " (balanced, WebGL only if clearly beneficial)"));
            AppendEcoSpec(sb, request);
            sb.AppendLine("Offline required: yes. Absolutely no network requests.");
            return sb.ToString();
        }

        public static string BuildRepairPrompt(GenerationRequest request, string previousHtml, ValidationResult issues)
        {
            var sb = new StringBuilder();
            sb.AppendLine("The HTML below failed validation for a desktop wallpaper.");
            sb.AppendLine("Fix ONLY the listed problems, keep the visual concept, and return the full corrected HTML document.");
            sb.AppendLine("Problems:");
            foreach (var issue in issues.Issues)
                sb.AppendLine("- [" + issue.Code + "] " + issue.Message);
            sb.AppendLine();
            sb.AppendLine("Original request: " + request.Prompt.Trim());
            sb.AppendLine("Interaction requested: " + DescribeInteraction(request.Interaction));
            AppendInteractionSpec(sb, request.Interaction);
            AppendReference(sb, request);
            AppendEcoSpec(sb, request);
            sb.AppendLine();
            sb.AppendLine("HTML to fix:");
            sb.AppendLine(previousHtml);
            return sb.ToString();
        }

        /// <summary>
        /// Self-Refine 评审输入：描述 + 硬约束 + 当前 HTML，要求只返回评分 JSON。
        /// </summary>
        public static string BuildCritiquePrompt(
            GenerationRequest request,
            string html,
            string revisionInstruction = null,
            IReadOnlyList<string> historySummaries = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Review this desktop live wallpaper and return only the JSON object described in the system message.");
            if (!string.IsNullOrWhiteSpace(revisionInstruction))
            {
                sb.AppendLine();
                sb.AppendLine("This is an incremental revision of the current version, not a new wallpaper.");
                sb.AppendLine("The user's new request: " + revisionInstruction.Trim());
                sb.AppendLine("Your plan must focus on that request plus the most important existing defects; " +
                              "do not propose rebuilding the whole scene.");
                if (historySummaries != null && historySummaries.Count > 0)
                {
                    sb.AppendLine("Recent revisions already applied (do not undo them):");
                    foreach (var summary in historySummaries)
                        sb.AppendLine("- " + summary);
                }
            }
            sb.AppendLine();
            sb.AppendLine("User description:");
            sb.AppendLine(request.Prompt.Trim());
            sb.AppendLine();
            sb.AppendLine("Interaction requested: " + DescribeInteraction(request.Interaction));
            sb.AppendLine();
            sb.AppendLine("Context: the loop must look complete without any mouse movement, fill any resolution, " +
                          "stay fully offline, and expose real DOM interaction.");
            if (request.EcoMode)
                sb.AppendLine("Eco mode is enabled: idle throttling is expected. Do not propose removing it; " +
                              "check that interaction still restores full smoothness.");
            sb.AppendLine();
            sb.AppendLine("HTML:");
            sb.AppendLine(html);
            return sb.ToString();
        }

        /// <summary>
        /// Self-Refine 重写输入：把评审给出的具体问题带回，要求整篇重写并保持既有优点。
        /// </summary>
        public static string BuildRefinePrompt(
            GenerationRequest request,
            string previousHtml,
            double score,
            IReadOnlyList<string> issues,
            IReadOnlyList<string> fixes,
            string plan = null,
            string rationale = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("The wallpaper below scored " + score.ToString("0.0", CultureInfo.InvariantCulture) +
                          "/10 in an art director review.");
            sb.AppendLine("Rewrite the FULL wallpaper to fix the issues while keeping what already works.");
            if (!string.IsNullOrWhiteSpace(plan))
            {
                sb.AppendLine();
                sb.AppendLine("Plan (follow these steps):");
                sb.AppendLine(plan.Trim());
            }
            if (!string.IsNullOrWhiteSpace(rationale))
                sb.AppendLine("Why: " + rationale.Trim());
            sb.AppendLine();
            sb.AppendLine("Issues:");
            for (var i = 0; i < issues.Count; i++)
                sb.AppendLine("- " + issues[i]);
            if (fixes.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Suggested fixes:");
                for (var i = 0; i < fixes.Count; i++)
                    sb.AppendLine("- " + fixes[i]);
            }
            sb.AppendLine();
            sb.AppendLine("Original request: " + request.Prompt.Trim());
            sb.AppendLine("Interaction requested: " + DescribeInteraction(request.Interaction));
            AppendInteractionSpec(sb, request.Interaction);
            AppendReference(sb, request);
            AppendEcoSpec(sb, request);
            sb.AppendLine();
            sb.AppendLine("Hard requirements unchanged: one self-contained HTML document, no external resources, " +
                          "complete idle animation, responsive to any resolution.");
            sb.AppendLine("Keep the document compact: reuse the existing structure and make targeted improvements instead of " +
                          "inflating the code. The response must end with </html>; never leave the document truncated.");
            var sizeBudget = Math.Max(4096, (int)(previousHtml.Length * 1.1));
            sb.AppendLine("Hard size budget: keep the improved document under " + sizeBudget.ToString("N0", CultureInfo.InvariantCulture) +
                          " characters (current is " + previousHtml.Length.ToString("N0", CultureInfo.InvariantCulture) +
                          "). Do not add new effect layers; improve the existing ones and delete dead code if needed.");
            sb.AppendLine("Return ONLY the complete improved HTML document starting with <!DOCTYPE html> and ending with </html>.");
            sb.AppendLine();
            sb.AppendLine("Current HTML:");
            sb.AppendLine(previousHtml);
            return sb.ToString();
        }

        /// <summary>
        /// 会话式增量修改：把用户新诉求、ReAct 计划与当前版本一起交给模型，明确要求保留既有结构。
        /// </summary>
        public static string BuildRevisionPrompt(
            GenerationRequest request,
            string previousHtml,
            string instruction,
            string plan,
            string rationale,
            double score,
            IReadOnlyList<string> issues,
            IReadOnlyList<string> fixes,
            IReadOnlyList<string> historySummaries)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Apply the user's revision to the CURRENT wallpaper below. This is an INCREMENTAL edit, not a rewrite.");
            sb.AppendLine("Preserve the existing composition, palette, scene structure and interaction unless the user explicitly " +
                          "asks for a redo. Change only what the request and plan require.");
            sb.AppendLine();
            sb.AppendLine("User request: " + (instruction ?? string.Empty).Trim());
            sb.AppendLine("Current art-director score: " + score.ToString("0.0", CultureInfo.InvariantCulture) + "/10");
            if (!string.IsNullOrWhiteSpace(plan))
            {
                sb.AppendLine();
                sb.AppendLine("Plan (follow these steps):");
                sb.AppendLine(plan.Trim());
            }
            if (!string.IsNullOrWhiteSpace(rationale))
                sb.AppendLine("Why: " + rationale.Trim());
            if (issues.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Known issues to fix while editing:");
                for (var i = 0; i < issues.Count; i++)
                    sb.AppendLine("- " + issues[i]);
            }
            if (fixes.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Suggested fixes:");
                for (var i = 0; i < fixes.Count; i++)
                    sb.AppendLine("- " + fixes[i]);
            }
            if (historySummaries != null && historySummaries.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Previous revisions already applied (do not undo them):");
                foreach (var summary in historySummaries)
                    sb.AppendLine("- " + summary);
            }
            sb.AppendLine();
            sb.AppendLine("Original request: " + request.Prompt.Trim());
            sb.AppendLine("Interaction requested: " + DescribeInteraction(request.Interaction));
            AppendInteractionSpec(sb, request.Interaction);
            AppendReference(sb, request);
            AppendEcoSpec(sb, request);
            var sizeBudget = Math.Max(4096, (int)(previousHtml.Length * 1.1));
            sb.AppendLine();
            sb.AppendLine("Hard size budget: keep the result under " + sizeBudget.ToString("N0", CultureInfo.InvariantCulture) +
                          " characters (current is " + previousHtml.Length.ToString("N0", CultureInfo.InvariantCulture) +
                          "). Do not add new effect layers; edit the existing ones.");
            sb.AppendLine("Hard requirements unchanged: one self-contained HTML document, no external resources, " +
                          "complete idle animation, responsive to any resolution, real DOM interaction.");
            sb.AppendLine("Return ONLY the complete updated HTML document starting with <!DOCTYPE html> and ending with </html>.");
            sb.AppendLine();
            sb.AppendLine("Current HTML:");
            sb.AppendLine(previousHtml);
            return sb.ToString();
        }

        private static string DescribeInteraction(InteractionCapability interaction)
        {
            if (interaction == InteractionCapability.none)
                return "none";
            return interaction.ToString();
        }

        /// <summary>
        /// 把交互从“可选”变成可执行、可校验的硬要求，并逐项说明每种能力的最小行为。
        /// 该校验由 HtmlWallpaperValidator 的 V009 兜底，缺失即判不合格并触发修复。
        /// </summary>
        private static void AppendInteractionSpec(StringBuilder sb, InteractionCapability interaction)
        {
            if (interaction == InteractionCapability.none)
            {
                sb.AppendLine("Interaction rules: none. Do not register pointer or keyboard handlers.");
                return;
            }

            sb.AppendLine("Interaction rules (mandatory, validated):");
            sb.AppendLine("- Use native DOM events registered on the canvas or window; no libraries, no Lively APIs.");
            sb.AppendLine("- The idle animation must keep running at full quality when the pointer is idle or outside the window.");
            sb.AppendLine("- Feedback must appear within one frame and decay smoothly; never block or pause the animation loop.");
            if ((interaction & InteractionCapability.hover) != 0)
                sb.AppendLine("- hover: track pointermove and continuously influence the scene (parallax, particle attraction/repulsion, " +
                              "localized glow or distortion) with a distance falloff; ease back on pointerleave.");
            if ((interaction & InteractionCapability.click) != 0)
                sb.AppendLine("- click: pointerdown must emit a clearly visible response (pulse, ripple, shockwave or burst) that " +
                              "expands and fades out within 0.5-1.5s; holding the button may strengthen it.");
            if ((interaction & InteractionCapability.drag) != 0)
                sb.AppendLine("- drag: while the button is held, pointermove must change a scene parameter (view angle, speed, zoom) " +
                              "with obvious visual feedback, and release must settle without breaking the loop.");
            if ((interaction & InteractionCapability.keyboard) != 0)
                sb.AppendLine("- keyboard: handle keydown/keyup for a small documented key set (space to pause, arrows to steer) and " +
                              "never preventDefault browser shortcuts.");
        }

        /// <summary>
        /// 节能模式下追加固定行为约束；关闭时完全不出现，保证两个版本的可比性。
        /// </summary>
        private static void AppendEcoSpec(StringBuilder sb, GenerationRequest request)
        {
            if (!request.EcoMode)
                return;
            sb.AppendLine();
            sb.AppendLine(EcoSpec);
        }

        /// <summary>
        /// 按交互能力条件注入参考代码：默认内置去视觉骨架，另有调用方提供的外部参考时一并追加。
        /// system prompt 保持完全静态以命中服务端前缀缓存，参考只进入 user prompt。
        /// </summary>
        private static void AppendReference(StringBuilder sb, GenerationRequest request)
        {
            var useBuiltIn = request.Interaction != InteractionCapability.none && !string.IsNullOrWhiteSpace(InteractionScaffold);
            var useExternal = !string.IsNullOrWhiteSpace(request.ReferenceText);
            if (!useBuiltIn && !useExternal)
                return;

            sb.AppendLine();
            if (useBuiltIn)
            {
                sb.AppendLine("Reference scaffold (structure only; do NOT copy its colors, sizes or artwork):");
                sb.AppendLine(InteractionScaffold!.Trim());
            }
            if (useExternal)
            {
                sb.AppendLine();
                sb.AppendLine("Additional reference supplied by the caller (structure only; do NOT copy its colors, sizes or artwork):");
                sb.AppendLine(request.ReferenceText!.Trim());
            }
        }

        private static string? LoadEmbeddedText(string resourceName)
        {
            using var stream = typeof(PromptBuilder).Assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
                return null;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
    }
}
