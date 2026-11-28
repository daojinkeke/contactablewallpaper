using System;

namespace Lively.Models.Ai
{
    /// <summary>
    /// AI 生成配置（对应 docs/contracts/07-ai-and-packaging.md §5.4）。
    /// 密钥只允许以 DPAPI 密文形式落盘，禁止明文。
    /// </summary>
    public class AiSettings
    {
        /// <summary>服务商标识，仅用于展示；为空表示未配置，只能走模板模式。</summary>
        public string Provider { get; set; }

        /// <summary>OpenAI 兼容接口的基础地址，例如 https://api.openai.com/v1 。</summary>
        public string BaseUrl { get; set; }

        /// <summary>模型名，例如 gpt-4o-mini / qwen-plus。</summary>
        public string Model { get; set; }

        /// <summary>DPAPI（CurrentUser）加密后的 Base64 密文。</summary>
        public string ApiKeyProtected { get; set; }

        /// <summary>读图专用视觉模型的 BaseUrl；留空沿用主 BaseUrl。</summary>
        public string VisionBaseUrl { get; set; }

        /// <summary>读图专用视觉模型名；留空时 DeepSeek 自动使用 deepseek-flash，其它服务沿用主 Model。</summary>
        public string VisionModel { get; set; }

        /// <summary>读图专用视觉模型的 DPAPI 密文；留空沿用主 API Key。</summary>
        public string VisionApiKeyProtected { get; set; }

        /// <summary>无任何增量的空闲超时（秒），契约默认 120。</summary>
        public int TimeoutSeconds { get; set; }

        /// <summary>校验失败后的自动修复次数上限，契约固定最多 2。</summary>
        public int MaxRepairAttempts { get; set; }

        /// <summary>未显式指定时使用的交互偏好（可选，默认不要求交互）。</summary>
        public InteractionCapability DefaultInteraction { get; set; }

        /// <summary>交互默认值迁移版本；小于 1 时把旧的 hover+click 默认重置为 none。</summary>
        public int InteractionDefaultsVersion { get; set; }

        /// <summary>Self-Refine 最大重写轮数，0 表示关闭；运行时限制在 0–3。</summary>
        public int MaxRefineRounds { get; set; }

        /// <summary>Self-Refine 阈值（1–10），模型自评分低于该值时按批评重写。</summary>
        public double RefineScoreThreshold { get; set; }

        /// <summary>界面「节能模式」开关的持久值（仅 UI 使用；为 true 时生成与续改追加 --eco）。</summary>
        public bool EcoMode { get; set; }

        /// <summary>新建壁纸时默认选中的风格键；空或 auto 表示不指定，由 AI 决定。</summary>
        public string DefaultStyle { get; set; }

        /// <summary>新建壁纸时默认选中的配色（#RRGGBB）；空表示不指定配色。</summary>
        public string DefaultColor { get; set; }

        public AiSettings()
        {
            Provider = string.Empty;
            BaseUrl = string.Empty;
            Model = string.Empty;
            ApiKeyProtected = string.Empty;
            VisionBaseUrl = string.Empty;
            VisionModel = string.Empty;
            VisionApiKeyProtected = string.Empty;
            TimeoutSeconds = 120;
            MaxRepairAttempts = 2;
            DefaultInteraction = InteractionCapability.none;
            InteractionDefaultsVersion = 1;
            MaxRefineRounds = 2;
            RefineScoreThreshold = 7.5;
            DefaultStyle = "auto";
            DefaultColor = string.Empty;
        }
    }

    /// <summary>
    /// 解析后的读图端点：允许与主生成模型使用不同的 BaseUrl/Model/Key。
    /// </summary>
    public sealed class AiVisionEndpoint
    {
        public string BaseUrl { get; set; }
        public string Model { get; set; }
        public string ApiKey { get; set; }
    }

    /// <summary>
    /// 对话续改附带的参考图片；只作为本次修改的视觉参考，不改变原始主题。
    /// </summary>
    public sealed class RevisionAttachment
    {
        public string SourcePath { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
    }

    /// <summary>
    /// 生成状态（数值与 07 契约 §4.2 完全一致，只允许末尾追加）。
    /// </summary>
    public enum GenerationState
    {
        unspecified = 0,
        queued = 1,
        generating = 2,
        validating = 3,
        repairing = 4,
        ready = 5,
        failed = 6,
        canceled = 7,
        /// <summary>Self-Refine：模型正在给当前产物打分（末尾追加，不打乱既有数值）。</summary>
        critiquing = 8,
        /// <summary>Self-Refine：正在按批评重写整篇产物。</summary>
        refining = 9
    }

    public enum StylePreset
    {
        auto = 0,
        minimal = 1,
        cyber = 2,
        nature = 3,
        pixel = 4,
        ink = 5,
        custom = 6,
        // 以下为末尾追加：既有数值保持不变，旧配置与旧记录仍可反序列化。
        ocean = 7,
        space = 8,
        aurora = 9,
        ember = 10,
        desert = 11,
        glass = 12,
        retro = 13,
        geometry = 14
    }

    public enum PerformanceMode
    {
        saver = 0,
        balanced = 1,
        quality = 2
    }

    [Flags]
    public enum InteractionCapability
    {
        none = 0,
        click = 1,
        hover = 2,
        drag = 4,
        keyboard = 8
    }

    /// <summary>
    /// 错误码（07 契约 §10.3）。<see cref="NetworkError"/>、<see cref="AuthenticationFailed"/>、
    /// <see cref="ModelNotFound"/> 为本次实现补充的可读分类，契约表尚未登记，已作为文档缺口上报。
    /// </summary>
    public static class AiErrorCodes
    {
        public const string NotConfigured = "ai_not_configured";
        public const string AuthenticationFailed = "ai_auth_failed";
        public const string QuotaExceeded = "ai_quota_exceeded";
        public const string Timeout = "ai_timeout";
        public const string ContentRejected = "ai_content_rejected";
        public const string ValidationFailed = "ai_validation_failed";
        public const string NetworkError = "ai_network_error";
        public const string ModelNotFound = "ai_model_not_found";
        public const string ProtocolError = "ai_protocol_error";
    }

    /// <summary>
    /// 生成输入（07 契约 §5.1）。
    /// </summary>
    public class GenerationRequest
    {
        public string ProjectId { get; set; }
        public string Prompt { get; set; }
        public StylePreset Style { get; set; }
        public string PrimaryColor { get; set; }
        public InteractionCapability Interaction { get; set; }
        /// <summary>调用方附加的外部参考代码（仅拼入提示词，不参与校验）。</summary>
        public string ReferenceText { get; set; }
        public string TargetDisplayId { get; set; }
        public int TargetWidth { get; set; }
        public int TargetHeight { get; set; }
        public double DpiScale { get; set; }
        public PerformanceMode PerformanceMode { get; set; }
        public bool OfflineRequired { get; set; }
        public string BaseVersionId { get; set; }
        /// <summary>节能模式：为 true 时向提示词追加低功耗行为约束。</summary>
        public bool EcoMode { get; set; }

        /// <summary>允许生成结果访问外部网络（默认 false，关闭时按离线约束校验并拦截页面请求）。</summary>
        public bool AllowExternalNetwork { get; set; }

        /// <summary>视觉模型对用户参考图的结构化转述；仅拼入提示词，不参与校验。</summary>
        public string ReferenceImageDescription { get; set; }

        /// <summary>用户参考图的本地路径；生成成功后复制进会话目录。</summary>
        public string ReferenceImagePath { get; set; }

        /// <summary>用户参考图的原始文件名，用于会话记录展示。</summary>
        public string ReferenceImageName { get; set; }

        public GenerationRequest()
        {
            ProjectId = Guid.NewGuid().ToString("N");
            Prompt = string.Empty;
            Style = StylePreset.auto;
            // 交互只是可选偏好，默认不要求；调用方可显式传入 hover/click。
            Interaction = InteractionCapability.none;
            ReferenceText = string.Empty;
            TargetWidth = 1920;
            TargetHeight = 1080;
            DpiScale = 1.0;
            PerformanceMode = PerformanceMode.balanced;
            OfflineRequired = true;
            AllowExternalNetwork = false;
            ReferenceImageDescription = string.Empty;
            ReferenceImagePath = string.Empty;
            ReferenceImageName = string.Empty;
        }
    }

    /// <summary>
    /// 流式生成事件（07 契约 §5.3 / §10.2）。
    /// </summary>
    public class GenerationEvent
    {
        public string JobId { get; set; }
        public GenerationState State { get; set; }
        public string ContentDelta { get; set; }
        public int Attempt { get; set; }
        public ValidationResult Issues { get; set; }
        public string VersionId { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorMessage { get; set; }

        /// <summary>服务端返回的 token 用量；未返回时为 null。</summary>
        public int? PromptTokens { get; set; }
        public int? CompletionTokens { get; set; }
        public int? TotalTokens { get; set; }

        /// <summary>从提交到当前状态的耗时（毫秒）。</summary>
        public long? ElapsedMs { get; set; }

        /// <summary>Self-Refine 当前轮次（从 1 开始；0 表示尚未开始）。</summary>
        public int RefineRound { get; set; }

        /// <summary>Self-Refine 当前评分（1–10），未评分时为 null。</summary>
        public double? RefineScore { get; set; }

        /// <summary>Self-Refine 的问题摘要（用于进度展示，最多几条关键问题）。</summary>
        public string RefineSummary { get; set; }

        /// <summary>ReAct 计划：本轮准备改哪些部分、为什么（模型推理的可读摘要）。</summary>
        public string RefinePlan { get; set; }

        /// <summary>可续接的会话标识；模板模式为空。</summary>
        public string SessionId { get; set; }

        /// <summary>本事件对应/已落盘的会话版本号（1 基）。</summary>
        public int SessionVersion { get; set; }

        /// <summary>服务端最后保存的草稿路径；仅在 ready 时保证是已通过校验的产物。</summary>
        public string DraftPath { get; set; }
    }

    /// <summary>
    /// 任务快照，用于 UI 重连恢复（07 契约 §5.4）。
    /// </summary>
    public class GenerationJobSnapshot
    {
        public string JobId { get; set; }
        public GenerationState State { get; set; }
        public int Attempt { get; set; }
        public string VersionId { get; set; }
        public long CreatedAtUnixMs { get; set; }

        /// <summary>已接收内容的草稿落盘位置（失败/取消时保留）。</summary>
        public string DraftPath { get; set; }
    }
}
