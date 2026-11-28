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
            "2. Network policy follows the user message: by default no external resources at all (no http(s) URLs in " +
            "src/href/url()/@import, no external fonts, images, scripts or styles; use inline SVG, CSS gradients, canvas " +
            "or data: URIs only). If the user message explicitly says external network is allowed, you may use HTTPS " +
            "resources and fetch() only as described there, and must degrade gracefully when offline.\n" +
            "3. No absolute paths and no file:/// references.\n" +
            "4. Forbidden APIs: alert/confirm/prompt, window.open, location.href/location.replace, target=\"_blank\", " +
            "navigation or downloads; getUserMedia/getDisplayMedia/navigator.mediaDevices; geolocation; Notification; " +
            "showSaveFilePicker/showOpenFilePicker.\n" +
            "5. No audio autoplay; any sound must require a user gesture. Prefer silent wallpapers.\n" +
            "6. The wallpaper must look complete on its own without any mouse movement, and must adapt to ANY resolution: " +
            "use 100vw/100vh or fixed inset:0 canvases, recompute layout on window resize, never fix the page to a hard-coded pixel size " +
            "and never rely on the pointer position to reveal the scene.\n" +
            "7. Keep it lightweight enough for a wallpaper: target <= 60 FPS, avoid runaway timers and memory growth.\n" +
            "8. Interaction is optional: only add pointer/click/keyboard feedback when it genuinely fits the theme and " +
            "improves the scene. If you add it, use real native DOM listeners, reset state on pointerleave/blur, and never " +
            "let it freeze or replace the idle animation. Do not add generic mouse ripples or click bursts just to satisfy " +
            "a checklist; if interaction does not fit, omit it entirely. Do NOT implement a proprietary handshake and do NOT " +
            "listen for Lively APIs; the host injects a bridge.\n" +
            "9. Output must be a complete document starting with <!DOCTYPE html> and ending with </html>.";


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
            "composition, theme, palette and style exactly as requested, and if the page chose to add interaction, keep it intact.";

        /// <summary>
        /// 宿主注入的数据契约：页面脚本执行前 window.contactableEnv 已存在，页面只读、自己不发请求。
        /// 天/时间由宿主取数，因此页面即使在离线校验下也能显示真实天气与时钟。
        /// </summary>
        private const string HostEnvironmentSpec =
            "Host data (window.contactableEnv is ALREADY defined before your script runs — read it, never request it):\n" +
            "window.contactableEnv = {\n" +
            "  time: { iso, hour, minute, dayOfWeek, isDay, timezoneOffsetMinutes },\n" +
            "  weather: null | { condition, conditionCode, temperatureC, windKph, humidity, isDay, updatedAt },\n" +
            "  allowExternalNetwork (boolean set by the host)\n" +
            "}\n" +
            "- Take the initial time/day-night state from window.contactableEnv.time (hour, minute, isDay, dayOfWeek); " +
            "if it is missing, fall back to new Date(). Keep the displayed clock advancing from new Date() so it never " +
            "freezes while the wallpaper runs.\n" +
            "- Take sky/weather from window.contactableEnv.weather (condition/conditionCode, temperatureC, windKph, " +
            "humidity, isDay). When it is null the host could not get any data: fall back to the local clock plus a " +
            "neutral default sky and keep rendering — never wait for weather, never block the first frame.\n" +
            "- The host refreshes weather after the page has loaded: also listen for the host event and re-apply the " +
            "new values in place (addEventListener('contactable:env', e => apply(e.detail || window.contactableEnv))) " +
            "without rebuilding the scene or restarting the animation.\n" +
            "- Never fetch/XHR/WebSocket any weather or time API, and never use navigator.geolocation; the host owns " +
            "that data and injects it.";

        /// <summary>
        /// Self-Refine 评审指令：只打分和列问题，不重写页面，输出严格 JSON。
        /// 与生成用的 static system prompt 分开，避免污染生成请求的前缀缓存。
        /// </summary>
        public const string CritiqueSystemPrompt =
            "You are a ruthless senior art director reviewing a generated desktop live wallpaper. You also act as the planner " +
            "of an incremental edit (ReAct: reason first, then the rewrite follows).\n" +
            "Score it honestly on a 1-10 scale (10 = portfolio quality). Be strict: most first drafts are 5-7.\n" +
            "Judge four visual dimensions: composition, color, motion, theme. Do not score interaction.\n" +
            "Return ONLY a JSON object, no markdown fences and no prose, using exactly this shape:\n" +
            "{\"composition\":n,\"color\":n,\"motion\":n,\"theme\":n,\"overall\":n," +
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
            if (request.Style == StylePreset.auto)
                sb.AppendLine("Style: not specified — choose the visual style and palette that best fit the user's description.");
            else if (request.Style == StylePreset.custom)
                sb.AppendLine("Style: custom — follow the user's description literally, do not apply a named preset.");
            else
                sb.AppendLine("Style preset: " + request.Style);
            if (!string.IsNullOrWhiteSpace(request.PrimaryColor))
                sb.AppendLine("Primary color: " + request.PrimaryColor);
            else
                sb.AppendLine("Primary color: not specified — derive it from the description.");
            AppendInteractionPreference(sb, request.Interaction);
            AppendReference(sb, request);
            AppendReferenceImage(sb, request.ReferenceImageDescription);
            sb.AppendLine("Performance preference: " + request.PerformanceMode +
                          (request.PerformanceMode == PerformanceMode.saver
                              ? " (prefer CSS/compositor animation, avoid WebGL and heavy per-frame JavaScript)"
                              : request.PerformanceMode == PerformanceMode.quality
                                  ? " (WebGL/particles allowed)"
                                  : " (balanced, WebGL only if clearly beneficial)"));
            AppendEcoSpec(sb, request);
            AppendHostEnvironmentSpec(sb);
            AppendNetworkPolicy(sb, request);
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
            AppendInteractionPreference(sb, request.Interaction);
            AppendReference(sb, request);
            AppendReferenceImage(sb, request.ReferenceImageDescription);
            AppendEcoSpec(sb, request);
            AppendHostEnvironmentSpec(sb);
            AppendNetworkPolicy(sb, request);
            AppendHardRequirementLine(sb, request);
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
            IReadOnlyList<string> historySummaries = null,
            string referenceDescription = null)
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
                AppendReferenceImage(sb, referenceDescription);
            }
            AppendReferenceImage(sb, request.ReferenceImageDescription);
            sb.AppendLine();
            sb.AppendLine("User description:");
            sb.AppendLine(request.Prompt.Trim());
            sb.AppendLine();
            AppendInteractionPreference(sb, request.Interaction);
            sb.AppendLine();
            sb.AppendLine("Context: the loop must look complete without any mouse movement, fill any resolution, " +
                          (request.AllowExternalNetwork
                              ? "stay within the allowed HTTPS data sources, "
                              : "stay fully offline, ") +
                          "and, only if the page chose to add interaction, expose real DOM interaction.");
            if (request.EcoMode)
                sb.AppendLine("Eco mode is enabled: idle throttling is expected. Do not propose removing it; " +
                              "if the page has interaction, check that it still restores full smoothness.");
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
            AppendInteractionPreference(sb, request.Interaction);
            AppendReference(sb, request);
            AppendReferenceImage(sb, request.ReferenceImageDescription);
            AppendEcoSpec(sb, request);
            AppendHostEnvironmentSpec(sb);
            AppendNetworkPolicy(sb, request);
            sb.AppendLine();
            AppendHardRequirementLine(sb, request);
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
            IReadOnlyList<string> historySummaries,
            string referenceDescription = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Apply the user's revision to the CURRENT wallpaper below. This is an INCREMENTAL edit, not a rewrite.");
            sb.AppendLine("Preserve the existing composition, palette, scene structure and interaction unless the user explicitly " +
                          "asks for a redo or asks to remove the interaction. Change only what the request and plan require.");
            sb.AppendLine();
            sb.AppendLine("User request: " + (instruction ?? string.Empty).Trim());
            AppendReferenceImage(sb, referenceDescription);
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
            AppendInteractionPreference(sb, request.Interaction);
            AppendReference(sb, request);
            AppendEcoSpec(sb, request);
            AppendHostEnvironmentSpec(sb);
            AppendNetworkPolicy(sb, request);
            var sizeBudget = Math.Max(4096, (int)(previousHtml.Length * 1.1));
            sb.AppendLine();
            sb.AppendLine("Hard size budget: keep the result under " + sizeBudget.ToString("N0", CultureInfo.InvariantCulture) +
                          " characters (current is " + previousHtml.Length.ToString("N0", CultureInfo.InvariantCulture) +
                          "). Do not add new effect layers; edit the existing ones.");
            AppendHardRequirementLine(sb, request);
            sb.AppendLine("Return ONLY the complete updated HTML document starting with <!DOCTYPE html> and ending with </html>.");
            sb.AppendLine();
            sb.AppendLine("Current HTML:");
            sb.AppendLine(previousHtml);
            return sb.ToString();
        }

        /// <summary>
        /// 定点修改指令：只返回 find/replace JSON。用于大文件续改，以及整篇重写被截断/校验失败后的回退，
        /// 避免模型再次整份输出时撞到单次 token 上限。
        /// </summary>
        public const string PatchSystemPrompt =
            "You are a precise code editor working on a single-file HTML desktop wallpaper.\n" +
            "Do NOT rewrite or return the whole document. Return ONLY a JSON object with a few targeted find/replace edits.\n" +
            "Output shape (no markdown fences, no prose):\n" +
            "{\"edits\":[{\"find\":\"exact snippet from the CURRENT HTML\",\"replace\":\"new snippet\"}],\"summary\":\"one short line\"}\n" +
            "Hard rules:\n" +
            "1. Each \"find\" must be copied character-for-character from the CURRENT HTML, including whitespace and quotes, and must appear EXACTLY ONCE in that document.\n" +
            "2. Keep each snippet short: one line or one small block, under 400 characters. Never put the whole document into find or replace.\n" +
            "3. \"replace\" is the final text for that snippet; use an empty string to delete it. Never use \"...\" or placeholders.\n" +
            "4. Do not reformat or touch unrelated code. Preserve the existing composition and palette; keep interaction only if the user did not ask to remove it.\n" +
            "5. Return 1-5 edits. If the change cannot be expressed as a unique replacement, return {\"edits\":[],\"summary\":\"<reason>\"}.";

        /// <summary>
        /// 续改的定点修改输入：诉求、计划、既有问题与当前完整 HTML。
        /// </summary>
        public static string BuildRevisionPatchPrompt(
            GenerationRequest request,
            string previousHtml,
            string instruction,
            string plan,
            string rationale,
            double score,
            IReadOnlyList<string> issues,
            IReadOnlyList<string> fixes,
            IReadOnlyList<string> historySummaries,
            string referenceDescription = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Express the user's revision as targeted find/replace edits on the CURRENT wallpaper.");
            sb.AppendLine("This is an INCREMENTAL edit. Preserve the existing composition, palette, scene structure and interaction " +
                          "unless the user explicitly asks for a redo or asks to remove the interaction.");
            sb.AppendLine();
            sb.AppendLine("User request: " + (instruction ?? string.Empty).Trim());
            AppendReferenceImage(sb, referenceDescription);
            sb.AppendLine("Current art-director score: " + score.ToString("0.0", CultureInfo.InvariantCulture) + "/10");
            if (!string.IsNullOrWhiteSpace(plan))
            {
                sb.AppendLine();
                sb.AppendLine("Plan (follow these steps):");
                sb.AppendLine(plan.Trim());
            }
            if (!string.IsNullOrWhiteSpace(rationale))
                sb.AppendLine("Why: " + rationale.Trim());
            if (issues != null && issues.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Known issues to fix while editing:");
                foreach (var issue in issues)
                    sb.AppendLine("- " + issue);
            }
            if (fixes != null && fixes.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Suggested fixes:");
                foreach (var fix in fixes)
                    sb.AppendLine("- " + fix);
            }
            if (historySummaries != null && historySummaries.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Previous revisions already applied (do not undo them):");
                foreach (var summary in historySummaries)
                    sb.AppendLine("- " + summary);
            }
            AppendPatchRequestContext(sb, request);
            sb.AppendLine();
            sb.AppendLine("CURRENT HTML:");
            sb.AppendLine(previousHtml);
            sb.AppendLine();
            sb.AppendLine(PatchOutputReminder);
            return sb.ToString();
        }

        /// <summary>
        /// 校验失败后的定点修复输入（整篇修复的同款约束，但只允许少量替换）。
        /// </summary>
        public static string BuildRepairPatchPrompt(GenerationRequest request, string previousHtml, ValidationResult issues)
        {
            var sb = new StringBuilder();
            sb.AppendLine("The wallpaper below failed validation. Fix ONLY the listed problems with targeted find/replace edits.");
            sb.AppendLine("Problems:");
            if (issues?.Issues != null)
            {
                foreach (var issue in issues.Issues)
                    sb.AppendLine("- [" + issue.Code + "] " + issue.Message);
            }
            sb.AppendLine();
            sb.AppendLine("Original request: " + request.Prompt.Trim());
            AppendInteractionPreference(sb, request.Interaction);
            AppendReference(sb, request);
            AppendReferenceImage(sb, request.ReferenceImageDescription);
            AppendEcoSpec(sb, request);
            sb.AppendLine();
            sb.AppendLine("CURRENT HTML:");
            sb.AppendLine(previousHtml);
            sb.AppendLine();
            sb.AppendLine(PatchOutputReminder);
            return sb.ToString();
        }

        /// <summary>
        /// Self-Refine 的定点修改输入：按评审计划与建议做小步替换，不整篇重写。
        /// </summary>
        public static string BuildRefinePatchPrompt(
            GenerationRequest request,
            string previousHtml,
            double score,
            IReadOnlyList<string> issues,
            IReadOnlyList<string> fixes,
            string plan = null,
            string rationale = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Apply the art-director review as targeted find/replace edits, not a rewrite.");
            sb.AppendLine("The wallpaper scored " + score.ToString("0.0", CultureInfo.InvariantCulture) + "/10.");
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
            if (issues != null)
            {
                foreach (var issue in issues)
                    sb.AppendLine("- " + issue);
            }
            if (fixes != null && fixes.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Suggested fixes:");
                foreach (var fix in fixes)
                    sb.AppendLine("- " + fix);
            }
            AppendPatchRequestContext(sb, request);
            sb.AppendLine();
            sb.AppendLine("CURRENT HTML:");
            sb.AppendLine(previousHtml);
            sb.AppendLine();
            sb.AppendLine(PatchOutputReminder);
            return sb.ToString();
        }

        private const string PatchOutputReminder =
            "Return ONLY the JSON object {\"edits\":[{\"find\":\"...\",\"replace\":\"...\"}],\"summary\":\"...\"} " +
            "with 1-5 unique, exact snippets from CURRENT HTML. Never return the full document.";

        private static void AppendPatchRequestContext(StringBuilder sb, GenerationRequest request)
        {
            sb.AppendLine();
            sb.AppendLine("Original request: " + request.Prompt.Trim());
            AppendInteractionPreference(sb, request.Interaction);
            AppendReference(sb, request);
            AppendReferenceImage(sb, request.ReferenceImageDescription);
            AppendEcoSpec(sb, request);
            AppendHardRequirementLine(sb, request);
        }

        /// <summary>
        /// 修改/修复类提示词的硬要求行：网络约束跟随开关，避免与允许联网的页面自相矛盾。
        /// </summary>
        private static void AppendHardRequirementLine(StringBuilder sb, GenerationRequest request)
        {
            sb.AppendLine("Hard requirements unchanged: one self-contained HTML document, " +
                          (request.AllowExternalNetwork
                              ? "external HTTPS requests only when the user asked for live data, always with graceful degradation, "
                              : "no external resources, ") +
                          "complete idle animation, responsive to any resolution. Interaction is optional, not a requirement.");
        }

        /// <summary>
        /// 把视觉模型转述的参考图内容放进续改提示词；只作为视觉参考，不替换用户的原始主题。
        /// </summary>
        private static void AppendReferenceImage(StringBuilder sb, string referenceDescription)
        {
            if (string.IsNullOrWhiteSpace(referenceDescription))
                return;

            sb.AppendLine();
            sb.AppendLine("Reference image supplied by the user (visual/style reference only; do not replace the original " +
                          "theme unless the request explicitly asks for it):");
            sb.AppendLine(referenceDescription.Trim());
        }

        private static string DescribeInteraction(InteractionCapability interaction)
        {
            if (interaction == InteractionCapability.none)
                return "none";
            return interaction.ToString();
        }

        /// <summary>
        /// 交互只是可选偏好：说明用户倾向，但由模型按主题决定是否实现，不做校验、不强制波纹。
        /// </summary>
        private static void AppendInteractionPreference(StringBuilder sb, InteractionCapability interaction)
        {
            if (interaction == InteractionCapability.none)
            {
                sb.AppendLine("Interaction: optional. Add pointer/click/keyboard feedback only if it genuinely fits the theme; " +
                              "do not add generic mouse ripples or click bursts just to satisfy a checklist. Omit it when it " +
                              "does not improve the scene.");
                return;
            }

            sb.AppendLine("Interaction preference (optional): the user leans toward " + DescribeInteraction(interaction) +
                          ". Add it only when it genuinely fits the theme and improves the scene; do not add generic mouse " +
                          "ripples or click bursts just to satisfy a checklist. If it does not fit, omit interaction and keep " +
                          "the idle animation complete.");
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
        /// 宿主注入数据契约（生成与修改共用，保证同一份页面在两种提示词下都读同一份数据）。
        /// </summary>
        private static void AppendHostEnvironmentSpec(StringBuilder sb)
        {
            sb.AppendLine();
            sb.AppendLine(HostEnvironmentSpec);
        }

        /// <summary>
        /// 网络策略：默认完全离线；仅在宿主开关打开时追加可联网说明，system prompt 保持静态。
        /// </summary>
        private static void AppendNetworkPolicy(StringBuilder sb, GenerationRequest request)
        {
            if (!request.AllowExternalNetwork)
            {
                sb.AppendLine("Offline required: yes. Absolutely no network requests.");
                return;
            }

            sb.AppendLine("External network access is allowed for this wallpaper: you may call HTTPS data sources when the " +
                          "user explicitly asked for live data, but every request must degrade gracefully — short timeout, " +
                          "keep the last known value and keep the scene complete and animated when the network fails.");
            sb.AppendLine("The host still injects window.contactableEnv for time and weather; prefer it and never fetch " +
                          "weather or time yourself.");
        }

        /// <summary>
        /// 按交互能力条件注入参考代码：默认内置去视觉骨架，另有调用方提供的外部参考时一并追加。
        /// system prompt 保持完全静态以命中服务端前缀缓存，参考只进入 user prompt。
        /// </summary>
        private static void AppendReference(StringBuilder sb, GenerationRequest request)
        {
            var useExternal = !string.IsNullOrWhiteSpace(request.ReferenceText);
            if (!useExternal)
                return;

            sb.AppendLine();
            sb.AppendLine("Additional reference supplied by the caller (structure only; do NOT copy its colors, sizes or artwork):");
            sb.AppendLine(request.ReferenceText!.Trim());
        }

    }
}
