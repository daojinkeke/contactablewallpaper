using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lively.Common;
using Lively.Models.Ai;
using Newtonsoft.Json.Linq;

namespace Lively.Ai
{
    /// <summary>
    /// 生成入口（07 契约 §5.1）。异常一律转换为 failed 事件，不向调用方抛出。
    /// </summary>
    public interface IAiGenerationService
    {
        IAsyncEnumerable<GenerationEvent> GenerateAsync(GenerationRequest request, CancellationToken token);

        Task CancelAsync(string jobId);

        GenerationJobSnapshot GetJob(string jobId);
    }

    public class AiGenerationService : IAiGenerationService
    {
        private const int MaxPromptLength = 2000;

        /// <summary>
        /// 超过该体积（UTF-8 字节）的壁纸不再整份重写：模型单次输出上限（如 DeepSeek 约 8K token）
        /// 会让整篇重写在 </html> 之前被截断，改为少量定点 find/replace。
        /// </summary>
        private const int LargeDocumentBytes = 6 * 1024;

        /// <summary>失败时给用户的可执行建议，避免只说“重试”。</summary>
        private const string ConcreteEditHint =
            "请把诉求改得更具体，例如指定颜色、速度或位置：「把背景改成深蓝」「粒子速度减半」「把光晕移到右下角」，再试一次。";

        private readonly AiSettingsStore settingsStore;
        private readonly OpenAiCompatibleClient client;
        private readonly IHtmlWallpaperValidator validator;
        private readonly ITemplateProvider templateProvider;
        private readonly ConcurrentDictionary<string, JobState> jobs = new ConcurrentDictionary<string, JobState>();

        public AiGenerationService(
            AiSettingsStore settingsStore = null,
            OpenAiCompatibleClient client = null,
            IHtmlWallpaperValidator validator = null,
            ITemplateProvider templateProvider = null)
        {
            this.settingsStore = settingsStore ?? new AiSettingsStore();
            this.client = client ?? new OpenAiCompatibleClient();
            this.validator = validator ?? new HtmlWallpaperValidator();
            this.templateProvider = templateProvider ?? new EmbeddedTemplateProvider();
        }

        IAsyncEnumerable<GenerationEvent> IAiGenerationService.GenerateAsync(GenerationRequest request, CancellationToken token)
            => GenerateAsync(request, token, null);

        public async IAsyncEnumerable<GenerationEvent> GenerateAsync(
            GenerationRequest request,
            [EnumeratorCancellation] CancellationToken token,
            int? refineRounds)
        {
            request ??= new GenerationRequest();
            var jobId = Guid.NewGuid().ToString("N");
            var job = new JobState
            {
                CreatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Cancellation = CancellationTokenSource.CreateLinkedTokenSource(token),
                Watch = Stopwatch.StartNew()
            };
            jobs[jobId] = job;

            try
            {
                var prompt = request.Prompt?.Trim() ?? string.Empty;
                if (prompt.Length == 0 || prompt.Length > MaxPromptLength)
                {
                    job.State = GenerationState.failed;
                    job.ErrorCode = AiErrorCodes.ValidationFailed;
                    job.ErrorMessage = prompt.Length == 0
                        ? "请先输入壁纸描述（1–2000 字）。"
                        : $"壁纸描述过长，请压缩到 {MaxPromptLength} 字以内。";
                    yield return CreateEvent(jobId, job);
                    yield break;
                }

                job.State = GenerationState.queued;
                yield return CreateEvent(jobId, job);

                var settings = settingsStore.Load();
                if (refineRounds.HasValue)
                    settings.MaxRefineRounds = Math.Clamp(refineRounds.Value, 0, 3);
                if (!settingsStore.IsConfigured(settings))
                {
                    // 未配置 AI：只能走内置模板模式（契约 §5.4）。
                    await foreach (var templateEvent in RunTemplateModeAsync(jobId, job, request).ConfigureAwait(false))
                        yield return templateEvent;
                    yield break;
                }

                var apiKey = settingsStore.GetApiKey(settings);
                var maxAttempts = 1 + Math.Max(0, Math.Min(2, settings.MaxRepairAttempts));
                string content = string.Empty;
                ValidationResult lastIssues = null;
                var usePatchRepair = false;

                for (var attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    job.Attempt = attempt;
                    job.State = attempt == 1 ? GenerationState.generating : GenerationState.repairing;
                    yield return CreateEvent(jobId, job, issues: lastIssues);

                    ValidationResult validation = null;
                    var truncated = false;

                    if (usePatchRepair)
                    {
                        // 大文件或上一轮被截断：不再整篇重写，只让模型返回少量定点替换。
                        var repair = await ProducePatchCandidateAsync(
                                job, settings, apiKey, request, jobId, attempt, content,
                                PromptBuilder.PatchSystemPrompt,
                                PromptBuilder.BuildRepairPatchPrompt(request, content, lastIssues))
                            .ConfigureAwait(false);

                        if (repair.Canceled)
                        {
                            job.State = GenerationState.canceled;
                            yield return CreateEvent(jobId, job);
                            yield break;
                        }
                        if (repair.Failure != null)
                        {
                            job.State = GenerationState.failed;
                            job.ErrorCode = repair.Failure.ErrorCode;
                            job.ErrorMessage = repair.Failure.Message;
                            yield return CreateEvent(jobId, job);
                            yield break;
                        }
                        if (!repair.Success)
                        {
                            if (attempt >= maxAttempts)
                            {
                                job.State = GenerationState.failed;
                                job.ErrorCode = AiErrorCodes.ValidationFailed;
                                job.ErrorMessage = repair.ErrorMessage;
                                yield return CreateEvent(jobId, job, issues: repair.Validation);
                                yield break;
                            }
                            if (repair.Validation != null)
                                lastIssues = repair.Validation;
                            continue;
                        }

                        content = repair.Html;
                        job.Content = content;
                        job.DraftPath = SaveDraft(jobId, content);
                        validation = repair.Validation;
                    }
                    else
                    {
                        var userPrompt = attempt == 1
                            ? PromptBuilder.BuildUserPrompt(request)
                            : PromptBuilder.BuildRepairPrompt(request, content, lastIssues);

                        var builder = new StringBuilder();
                        var cancellation = job.Cancellation;
                        bool canceled = false;
                        AiGenerationException failure = null;
                        var reqPromptTokens = 0;
                        var reqCompletionTokens = 0;
                        var reqTotalTokens = 0;
                        string finishReason = null;

                        await using (var enumerator = client
                            .StreamCompletionAsync(settings, apiKey, PromptBuilder.SystemPrompt, userPrompt, cancellation.Token)
                            .GetAsyncEnumerator(cancellation.Token))
                        {
                            while (true)
                            {
                                bool moved;
                                try
                                {
                                    moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                                }
                                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                                {
                                    canceled = true;
                                    break;
                                }
                                catch (AiGenerationException ex)
                                {
                                    failure = ex;
                                    break;
                                }
                                catch (Exception ex)
                                {
                                    failure = new AiGenerationException(AiFailureKind.Protocol, "生成过程出错：" + ex.Message, ex);
                                    break;
                                }

                                if (!moved)
                                    break;

                                var chunk = enumerator.Current;
                                if (chunk == null)
                                    continue;

                                if (chunk.PromptTokens.HasValue)
                                    reqPromptTokens = chunk.PromptTokens.Value;
                                if (chunk.CompletionTokens.HasValue)
                                    reqCompletionTokens = chunk.CompletionTokens.Value;
                                if (chunk.TotalTokens.HasValue)
                                    reqTotalTokens = chunk.TotalTokens.Value;
                                if (!string.IsNullOrEmpty(chunk.FinishReason))
                                    finishReason = chunk.FinishReason;

                                job.PromptTokens = job.AccPromptTokens + reqPromptTokens;
                                job.CompletionTokens = job.AccCompletionTokens + reqCompletionTokens;
                                job.TotalTokens = job.AccTotalTokens + reqTotalTokens;

                                if (string.IsNullOrEmpty(chunk.Content))
                                    continue;

                                builder.Append(chunk.Content);
                                yield return new GenerationEvent
                                {
                                    JobId = jobId,
                                    State = job.State,
                                    ContentDelta = chunk.Content,
                                    Attempt = attempt,
                                    PromptTokens = job.PromptTokens,
                                    CompletionTokens = job.CompletionTokens,
                                    TotalTokens = job.TotalTokens,
                                    ElapsedMs = job.Watch.ElapsedMilliseconds
                                };
                            }
                        }

                        job.AccPromptTokens += reqPromptTokens;
                        job.AccCompletionTokens += reqCompletionTokens;
                        job.AccTotalTokens += reqTotalTokens;
                        job.PromptTokens = job.AccPromptTokens;
                        job.CompletionTokens = job.AccCompletionTokens;
                        job.TotalTokens = job.AccTotalTokens;

                        content = ExtractHtml(builder.ToString());
                        job.Content = content;
                        job.DraftPath = SaveDraft(jobId, content);

                        if (canceled)
                        {
                            job.State = GenerationState.canceled;
                            yield return CreateEvent(jobId, job);
                            yield break;
                        }

                        if (failure != null)
                        {
                            job.State = GenerationState.failed;
                            job.ErrorCode = failure.ErrorCode;
                            job.ErrorMessage = failure.Message;
                            yield return CreateEvent(jobId, job);
                            yield break;
                        }

                        job.State = GenerationState.validating;
                        yield return CreateEvent(jobId, job);

                        if (string.IsNullOrWhiteSpace(content))
                        {
                            job.State = GenerationState.failed;
                            job.ErrorCode = AiErrorCodes.ValidationFailed;
                            job.ErrorMessage = "AI 没有返回任何内容，请重试。";
                            yield return CreateEvent(jobId, job);
                            yield break;
                        }

                        truncated = string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase) ||
                                    !IsCompleteHtml(content);
                        validation = validator.Validate(job.DraftPath, CreateValidationOptions(request));
                        lastIssues = validation;
                    }

                    if (validation.IsValid)
                    {
                        // Self-Refine：评分低于阈值时带着具体批评重写；每轮都重新过 V001–V008。
                        await foreach (var refineEvent in RunRefinementAsync(jobId, job, request, settings, apiKey, content)
                            .ConfigureAwait(false))
                        {
                            yield return refineEvent;
                        }

                        if (job.State == GenerationState.canceled)
                            yield break;

                        var finalValidation = validator.Validate(job.DraftPath, CreateValidationOptions(request));
                        TryPersistInitialVersion(job, request);
                        job.State = GenerationState.ready;
                        job.VersionId = Guid.NewGuid().ToString("N");
                        yield return CreateEvent(jobId, job, issues: finalValidation);
                        yield break;
                    }

                    if (attempt >= maxAttempts)
                    {
                        job.State = GenerationState.failed;
                        job.ErrorCode = AiErrorCodes.ValidationFailed;
                        job.ErrorMessage = truncated
                            ? "AI 输出达到单次上限被截断，未能返回完整页面，本次结果未保存。" + ConcreteEditHint
                            : BuildValidationMessage(validation);
                        yield return CreateEvent(jobId, job, issues: validation);
                        yield break;
                    }

                    // 有完整底稿时改用定点修复，避免整篇重写再次撞上输出上限；
                    // 若已被截断（缺 </html>），没有可编辑的底稿，只能整篇重试。
                    if (IsCompleteHtml(content))
                        usePatchRepair = true;
                }
            }
            finally
            {
                job.Cancellation.Dispose();
            }
        }

        private async IAsyncEnumerable<GenerationEvent> RunTemplateModeAsync(
            string jobId,
            JobState job,
            GenerationRequest request)
        {
            job.Attempt = 1;
            job.State = GenerationState.generating;
            yield return CreateEvent(jobId, job);

            string html = null;
            Exception loadError = null;
            try
            {
                html = templateProvider.GetTemplateHtml();
            }
            catch (Exception ex)
            {
                loadError = ex;
            }

            if (loadError != null)
            {
                job.State = GenerationState.failed;
                job.ErrorCode = AiErrorCodes.ValidationFailed;
                job.ErrorMessage = "内置模板不可用：" + loadError.Message;
                yield return CreateEvent(jobId, job);
                yield break;
            }

            job.Content = html;
            yield return new GenerationEvent
            {
                JobId = jobId,
                State = job.State,
                ContentDelta = html,
                Attempt = 1
            };

            job.State = GenerationState.validating;
            yield return CreateEvent(jobId, job);

            job.DraftPath = SaveDraft(jobId, html);
            // 内置模板是固定产物，只做离线/尺寸等硬约束校验。
            var templateOptions = CreateValidationOptions(request);
            var validation = validator.Validate(job.DraftPath, templateOptions);
            if (validation.IsValid)
            {
                job.State = GenerationState.ready;
                job.VersionId = Guid.NewGuid().ToString("N");
                yield return CreateEvent(jobId, job, issues: validation);
            }
            else
            {
                job.State = GenerationState.failed;
                job.ErrorCode = AiErrorCodes.ValidationFailed;
                job.ErrorMessage = BuildValidationMessage(validation);
                yield return CreateEvent(jobId, job, issues: validation);
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// 会话式增量修改：加载上一版 → 计划+评分（一次请求）→ 按计划改写 → 校验 → 落新版本。
        /// 每轮最多两次模型请求；同一轮内计划只生成一次，评分与计划合并。
        /// </summary>
        public async IAsyncEnumerable<GenerationEvent> ReviseAsync(
            string sessionId,
            string instruction,
            GenerationRequest request,
            int? refineRounds = null,
            RevisionAttachment attachment = null,
            string referenceDescription = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var sessionStore = new WallpaperSessionStore();
            if (!sessionStore.TryLoad(sessionId, out var session) || session.Versions.Count == 0)
                throw new InvalidOperationException("会话不存在或没有任何版本：" + sessionId);

            instruction = (instruction ?? string.Empty).Trim();
            if (instruction.Length == 0)
                throw new ArgumentException("请提供一句修改诉求。", nameof(instruction));

            request ??= new GenerationRequest();
            request.Prompt = session.Prompt;
            // 调用方已解析好本次是否节能（显式传参优先，否则沿用会话设置）；变更时回写会话，供下次续接。
            if (session.Eco != request.EcoMode)
            {
                session.Eco = request.EcoMode;
                sessionStore.Save(session);
            }

            var settings = settingsStore.Load();
            var apiKey = settingsStore.GetApiKey(settings);
            var jobId = Guid.NewGuid().ToString("N");
            var job = new JobState
            {
                State = GenerationState.queued,
                CreatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Watch = Stopwatch.StartNew(),
                Cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            };
            job.SessionId = session.SessionId;
            job.SessionVersion = session.Versions[^1].Version;
            jobs[jobId] = job;

            yield return CreateEvent(jobId, job);

            var current = sessionStore.ReadVersionHtml(session.SessionId, session.Versions[^1].Version);
            if (string.IsNullOrWhiteSpace(current))
            {
                job.State = GenerationState.failed;
                job.ErrorCode = AiErrorCodes.ValidationFailed;
                job.ErrorMessage = "会话的最新版本文件缺失：" + sessionStore.GetVersionPath(session.SessionId, session.Versions[^1].Version);
                yield return CreateEvent(jobId, job);
                yield break;
            }

            var maxRounds = Math.Clamp(refineRounds ?? settings.MaxRefineRounds, 1, 3);
            var threshold = Math.Clamp(settings.RefineScoreThreshold, 0d, 10d);
            var history = session.Versions
                .Skip(Math.Max(0, session.Versions.Count - 5))
                .Select(v => "v" + v.Version + "：" + v.Summary)
                .ToList();

            ValidationResult lastValidation = null;
            string attachmentFile = null;
            for (var round = 1; round <= maxRounds; round++)
            {
                // 以「整轮」为口径统计成本：包含本轮的计划/评分请求 + 重写请求。
                var promptTokensBefore = job.AccPromptTokens;
                var completionTokensBefore = job.AccCompletionTokens;
                var totalTokensBefore = job.AccTotalTokens;
                var elapsedBefore = job.Watch.ElapsedMilliseconds;

                job.RefineRound = round;
                job.RefineScore = null;
                job.RefinePlan = null;
                job.RefineSummary = null;
                job.State = GenerationState.critiquing;
                yield return CreateEvent(jobId, job);

                var plan = await CollectCompletionAsync(job, settings, apiKey,
                        PromptBuilder.CritiqueSystemPrompt,
                        PromptBuilder.BuildCritiquePrompt(request, current, instruction, history, referenceDescription),
                        job.Cancellation.Token)
                    .ConfigureAwait(false);

                if (plan.Canceled)
                {
                    job.State = GenerationState.canceled;
                    yield return CreateEvent(jobId, job);
                    yield break;
                }
                if (plan.Failure != null)
                {
                    job.State = GenerationState.failed;
                    job.ErrorCode = plan.Failure.ErrorCode;
                    job.ErrorMessage = plan.Failure.Message;
                    yield return CreateEvent(jobId, job);
                    yield break;
                }

                var parsed = TryParseCritique(plan.Text);
                if (parsed == null)
                {
                    job.State = GenerationState.failed;
                    job.ErrorCode = AiErrorCodes.ProtocolError;
                    job.ErrorMessage = plan.Truncated
                        ? "生成修改计划时输出被截断（达到单次输出上限），未修改任何版本。请把诉求写得更短、更具体后重试。"
                        : "模型返回的计划/评分不是可解析的 JSON，未修改任何版本。";
                    yield return CreateEvent(jobId, job);
                    yield break;
                }

                job.RefineScore = parsed.Overall;
                job.RefineSummary = SummarizeCritique(parsed);
                job.RefinePlan = parsed.PlanText;
                yield return CreateEvent(jobId, job);

                job.State = GenerationState.refining;
                yield return CreateEvent(jobId, job);

                var candidateResult = await ProduceRevisionCandidateAsync(
                        job, settings, apiKey, request, jobId, round, current,
                        PromptBuilder.SystemPrompt,
                        PromptBuilder.BuildRevisionPrompt(request, current, instruction, parsed.PlanText,
                            parsed.Rationale, parsed.Overall, parsed.Issues, parsed.Fixes, history, referenceDescription),
                        PromptBuilder.PatchSystemPrompt,
                        PromptBuilder.BuildRevisionPatchPrompt(request, current, instruction, parsed.PlanText,
                            parsed.Rationale, parsed.Overall, parsed.Issues, parsed.Fixes, history, referenceDescription),
                        IsLargeDocument(current))
                    .ConfigureAwait(false);

                if (candidateResult.Canceled)
                {
                    job.State = GenerationState.canceled;
                    yield return CreateEvent(jobId, job);
                    yield break;
                }
                if (candidateResult.Failure != null)
                {
                    job.State = GenerationState.failed;
                    job.ErrorCode = candidateResult.Failure.ErrorCode;
                    job.ErrorMessage = candidateResult.Failure.Message;
                    yield return CreateEvent(jobId, job);
                    yield break;
                }
                if (!candidateResult.Success)
                {
                    job.State = GenerationState.failed;
                    job.ErrorCode = AiErrorCodes.ValidationFailed;
                    job.ErrorMessage = candidateResult.ErrorMessage;
                    yield return CreateEvent(jobId, job, issues: candidateResult.Validation);
                    yield break;
                }

                var candidate = candidateResult.Html;
                var validation = candidateResult.Validation;
                current = candidate;
                job.Content = current;
                job.DraftPath = SaveDraft(jobId, current);
                lastValidation = validation;

                var summary = BuildRevisionSummary(instruction, parsed.PlanSteps);
                if (candidateResult.UsedPatch)
                    summary = summary.Length <= 190 ? summary + "（定点修改）" : summary;
                if (attachment != null && string.IsNullOrEmpty(attachmentFile))
                    attachmentFile = sessionStore.ImportAttachment(session.SessionId, attachment.SourcePath);
                var entry = sessionStore.AppendVersion(session, current, instruction, summary, parsed.PlanText,
                    parsed.Overall, job.Watch.ElapsedMilliseconds - elapsedBefore,
                    job.AccPromptTokens - promptTokensBefore,
                    job.AccCompletionTokens - completionTokensBefore,
                    job.AccTotalTokens - totalTokensBefore,
                    attachmentFile, attachment?.DisplayName, referenceDescription);
                job.SessionVersion = entry.Version;
                history.Add("v" + entry.Version + "：" + summary);

                if (parsed.Overall >= threshold || round >= maxRounds)
                    break;
            }

            job.State = GenerationState.ready;
            job.VersionId = Guid.NewGuid().ToString("N");
            yield return CreateEvent(jobId, job, issues: lastValidation);
        }

        private void TryPersistInitialVersion(JobState job, GenerationRequest request)
        {
            try
            {
                var store = new WallpaperSessionStore();
                var session = store.CreateSession(request.Prompt, null, request.EcoMode);
                var summary = string.IsNullOrWhiteSpace(job.RefinePlan)
                    ? "初始生成"
                    : "初始生成：" + FirstLines(job.RefinePlan, 2);
                var attachmentFile = string.IsNullOrWhiteSpace(request.ReferenceImagePath)
                    ? null
                    : store.ImportAttachment(session.SessionId, request.ReferenceImagePath);
                var entry = store.AppendVersion(session, job.Content, string.Empty, summary, job.RefinePlan,
                    job.RefineScore, job.Watch?.ElapsedMilliseconds ?? 0,
                    job.AccPromptTokens, job.AccCompletionTokens, job.AccTotalTokens,
                    attachmentFile, request.ReferenceImageName, request.ReferenceImageDescription);
                job.SessionId = session.SessionId;
                job.SessionVersion = entry.Version;
            }
            catch (Exception)
            {
                // 会话落盘失败不阻断本次生成交付。
            }
        }

        private static string BuildRevisionSummary(string instruction, List<string> planSteps)
        {
            var text = "按诉求「" + instruction + "」";
            if (planSteps.Count > 0)
                text += "：" + string.Join("；", planSteps.Take(2));
            return text.Length <= 200 ? text : text.Substring(0, 200) + "…";
        }

        private static string FirstLines(string text, int count)
        {
            var lines = text.Split('\n');
            return string.Join("；", lines.Take(count).Select(l => l.Trim()));
        }

        /// <summary>
        /// Self-Refine：先让模型按五个维度评分，低于阈值时带着具体批评整篇重写。
        /// 每轮候选都必须重新通过 V001–V008；未通过校验的候选直接丢弃，保留上一版有效产物。
        /// </summary>
        private async IAsyncEnumerable<GenerationEvent> RunRefinementAsync(
            string jobId,
            JobState job,
            GenerationRequest request,
            AiSettings settings,
            string apiKey,
            string validatedHtml)
        {
            var maxRounds = Math.Clamp(settings.MaxRefineRounds, 0, 3);
            var threshold = Math.Clamp(settings.RefineScoreThreshold, 0d, 10d);
            if (maxRounds == 0)
                yield break;

            var current = validatedHtml;
            SaveInitialDraft(jobId, current);
            for (var round = 1; round <= maxRounds; round++)
            {
                job.RefineRound = round;
                job.RefineScore = null;
                job.RefineSummary = null;
                job.State = GenerationState.critiquing;
                yield return CreateEvent(jobId, job);

                var critique = await CollectCompletionAsync(job, settings, apiKey,
                        PromptBuilder.CritiqueSystemPrompt,
                        PromptBuilder.BuildCritiquePrompt(request, current),
                        job.Cancellation.Token)
                    .ConfigureAwait(false);

                if (critique.Canceled)
                {
                    job.State = GenerationState.canceled;
                    yield return CreateEvent(jobId, job);
                    yield break;
                }
                if (critique.Failure != null)
                {
                    job.RefineSummary = "评分请求失败，保留当前版本：" + critique.Failure.Message;
                    yield return CreateEvent(jobId, job);
                    break;
                }

                var parsed = TryParseCritique(critique.Text);
                if (parsed == null)
                {
                    job.RefineSummary = "评分结果无法解析，跳过精修";
                    yield return CreateEvent(jobId, job);
                    break;
                }

                job.RefineScore = parsed.Overall;
                job.RefineSummary = SummarizeCritique(parsed);
                job.RefinePlan = parsed.PlanText;
                // 同一状态再发一次，调用方即可看到本轮评分、计划与问题摘要。
                yield return CreateEvent(jobId, job);

                if (parsed.Overall >= threshold)
                    break;

                job.State = GenerationState.refining;
                yield return CreateEvent(jobId, job);

                var candidateResult = await ProduceRevisionCandidateAsync(
                        job, settings, apiKey, request, jobId, round, current,
                        PromptBuilder.SystemPrompt,
                        PromptBuilder.BuildRefinePrompt(request, current, parsed.Overall, parsed.Issues, parsed.Fixes,
                            parsed.PlanText, parsed.Rationale),
                        PromptBuilder.PatchSystemPrompt,
                        PromptBuilder.BuildRefinePatchPrompt(request, current, parsed.Overall, parsed.Issues, parsed.Fixes,
                            parsed.PlanText, parsed.Rationale),
                        IsLargeDocument(current))
                    .ConfigureAwait(false);

                if (candidateResult.Canceled)
                {
                    job.State = GenerationState.canceled;
                    yield return CreateEvent(jobId, job);
                    yield break;
                }
                if (candidateResult.Failure != null || !candidateResult.Success)
                {
                    job.RefineSummary = AppendSummary(job.RefineSummary,
                        "第 " + round + " 轮未生效，保留上一版");
                    yield return CreateEvent(jobId, job);
                    break;
                }

                current = candidateResult.Html;
                job.Content = current;
                job.DraftPath = SaveDraft(jobId, current);
            }

            job.Content = current;
            job.DraftPath = SaveDraft(jobId, current);
        }

        /// <summary>
        /// 产出一次续改候选：小文件先整篇改写，截断或校验失败时自动回退到定点修改；
        /// 超过 6 KB 的文件直接走定点修改，避免整篇输出撞上单次 token 上限。
        /// </summary>
        private async Task<CandidateResult> ProduceRevisionCandidateAsync(
            JobState job,
            AiSettings settings,
            string apiKey,
            GenerationRequest request,
            string jobId,
            int round,
            string currentHtml,
            string fullSystemPrompt,
            string fullUserPrompt,
            string patchSystemPrompt,
            string patchUserPrompt,
            bool preferPatch)
        {
            if (preferPatch)
            {
                return await ProducePatchCandidateAsync(job, settings, apiKey, request, jobId, round, currentHtml,
                        patchSystemPrompt, patchUserPrompt)
                    .ConfigureAwait(false);
            }

            var full = await CollectCompletionAsync(job, settings, apiKey, fullSystemPrompt, fullUserPrompt, job.Cancellation.Token)
                .ConfigureAwait(false);
            if (full.Canceled)
                return new CandidateResult { Canceled = true };
            if (full.Failure != null)
                return new CandidateResult { Failure = full.Failure };

            var html = ExtractHtml(full.Text);
            var truncated = full.Truncated || !IsCompleteHtml(html);
            if (!truncated && !string.IsNullOrWhiteSpace(html))
            {
                var path = SaveCandidateDraft(jobId, round, "full", html);
                var validation = validator.Validate(path, CreateValidationOptions(request));
                if (validation.IsValid)
                    return new CandidateResult { Success = true, Html = html, Validation = validation };

                patchUserPrompt = patchUserPrompt + Environment.NewLine +
                    "A full rewrite was attempted but failed validation (" + FirstFatalMessage(validation) +
                    "). Do not repeat it; return targeted edits only.";
            }
            else
            {
                patchUserPrompt = patchUserPrompt + Environment.NewLine +
                    "A full rewrite was attempted but the output was truncated before </html>. " +
                    "Do not rewrite the document; return targeted edits only.";
            }

            var patchResult = await ProducePatchCandidateAsync(job, settings, apiKey, request, jobId, round, currentHtml,
                    patchSystemPrompt, patchUserPrompt)
                .ConfigureAwait(false);
            if (patchResult.Success || patchResult.Canceled || patchResult.Failure != null)
                return patchResult;

            if (truncated)
                patchResult.ErrorMessage = "整篇改写被截断（达到单次输出上限），" + patchResult.ErrorMessage;
            return patchResult;
        }

        /// <summary>
        /// 只走定点修改：解析 find/replace JSON，校验每处唯一匹配后应用，再走 V001–V008。
        /// </summary>
        private async Task<CandidateResult> ProducePatchCandidateAsync(
            JobState job,
            AiSettings settings,
            string apiKey,
            GenerationRequest request,
            string jobId,
            int round,
            string currentHtml,
            string patchSystemPrompt,
            string patchUserPrompt)
        {
            var patch = await CollectCompletionAsync(job, settings, apiKey, patchSystemPrompt, patchUserPrompt,
                    job.Cancellation.Token)
                .ConfigureAwait(false);
            if (patch.Canceled)
                return new CandidateResult { Canceled = true };
            if (patch.Failure != null)
                return new CandidateResult { Failure = patch.Failure };

            if (patch.Truncated)
                return PatchFailure("模型返回的定点修改被截断（达到单次输出上限），本次修改未生效，当前版本保持不变。" +
                                    ConcreteEditHint);

            if (!TryParsePatchEdits(patch.Text, out var edits, out var parseError))
                return PatchFailure("模型没有返回可用的定点修改（" + parseError + "），本次修改未生效，当前版本保持不变。" +
                                    ConcreteEditHint);

            if (!TryApplyPatchEdits(currentHtml, edits, out var patched, out var applyError))
                return PatchFailure("定点修改无法唯一定位代码（" + applyError + "），本次修改未生效，当前版本保持不变。" +
                                    ConcreteEditHint);

            var path = SaveCandidateDraft(jobId, round, "patch", patched);
            var validation = validator.Validate(path, CreateValidationOptions(request));
            if (!validation.IsValid)
            {
                var failed = PatchFailure("定点修改已应用，但结果未通过校验：" + FirstFatalMessage(validation) +
                                          "。本次修改未生效，当前版本保持不变。" + ConcreteEditHint);
                failed.Validation = validation;
                return failed;
            }

            return new CandidateResult { Success = true, UsedPatch = true, Html = patched, Validation = validation };
        }

        private static CandidateResult PatchFailure(string message) =>
            new CandidateResult { ErrorMessage = message };

        private static bool IsLargeDocument(string html) =>
            !string.IsNullOrEmpty(html) && Encoding.UTF8.GetByteCount(html) > LargeDocumentBytes;

        private static bool IsCompleteHtml(string html) =>
            !string.IsNullOrWhiteSpace(html) &&
            html.IndexOf("<html", StringComparison.OrdinalIgnoreCase) >= 0 &&
            html.IndexOf("</html>", StringComparison.OrdinalIgnoreCase) >= 0;

        private static string FirstFatalMessage(ValidationResult validation)
        {
            if (validation?.Issues != null)
            {
                foreach (var issue in validation.Issues)
                {
                    if (issue.Severity == ValidationSeverity.fatal)
                        return issue.Message;
                }
            }
            return "未知问题";
        }

        private static readonly Regex JsonFenceRegex = new Regex(
            @"```(?:json|JSON)?\s*([\s\S]*?)```",
            RegexOptions.Compiled);

        /// <summary>
        /// 解析模型返回的定点修改。接受 {"edits":[...]} 或直接数组；错误信息面向用户。
        /// </summary>
        private static bool TryParsePatchEdits(string raw, out List<PatchEdit> edits, out string error)
        {
            edits = new List<PatchEdit>();
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(raw))
            {
                error = "返回为空";
                return false;
            }

            var text = raw.Trim();
            var fence = JsonFenceRegex.Match(text);
            if (fence.Success)
                text = fence.Groups[1].Value.Trim();

            JToken root = null;
            var objectStart = text.IndexOf('{');
            var objectEnd = text.LastIndexOf('}');
            if (objectStart >= 0 && objectEnd > objectStart)
            {
                try
                {
                    root = JToken.Parse(text.Substring(objectStart, objectEnd - objectStart + 1));
                }
                catch (Exception)
                {
                    root = null;
                }
            }

            JArray array = null;
            if (root is JObject obj)
            {
                array = obj["edits"] as JArray;
            }
            else if (root is JArray direct)
            {
                array = direct;
            }
            else
            {
                var arrayStart = text.IndexOf('[');
                var arrayEnd = text.LastIndexOf(']');
                if (arrayStart >= 0 && arrayEnd > arrayStart)
                {
                    try
                    {
                        array = JArray.Parse(text.Substring(arrayStart, arrayEnd - arrayStart + 1));
                    }
                    catch (Exception)
                    {
                        array = null;
                    }
                }
            }

            if (array == null || array.Count == 0)
            {
                error = "edits 为空或缺失";
                return false;
            }
            if (array.Count > 8)
            {
                error = "edits 过多（最多 8 处）";
                return false;
            }

            foreach (var item in array)
            {
                if (!(item is JObject edit))
                {
                    error = "edits 中存在非对象项";
                    return false;
                }

                var find = edit["find"]?.ToString();
                var replace = edit["replace"]?.ToString();
                if (string.IsNullOrEmpty(find))
                {
                    error = "存在空的 find";
                    return false;
                }
                if (find.Length > 4000)
                {
                    error = "find 片段过长，可能不是定点修改";
                    return false;
                }
                if (replace == null)
                {
                    error = "存在缺少 replace 的项";
                    return false;
                }
                if (string.Equals(find, replace, StringComparison.Ordinal))
                {
                    error = "存在 find 与 replace 相同的项";
                    return false;
                }

                edits.Add(new PatchEdit { Find = find, Replace = replace });
            }

            return true;
        }

        /// <summary>
        /// 逐条应用替换：每处必须在当前文本中唯一出现，否则整批放弃，保证不会误改。
        /// </summary>
        private static bool TryApplyPatchEdits(string html, List<PatchEdit> edits, out string patched, out string error)
        {
            patched = html ?? string.Empty;
            error = string.Empty;

            for (var i = 0; i < edits.Count; i++)
            {
                var edit = edits[i];
                var first = patched.IndexOf(edit.Find, StringComparison.Ordinal);
                if (first < 0)
                {
                    error = "第 " + (i + 1) + " 处待替换片段在页面中没有找到";
                    return false;
                }
                var second = patched.IndexOf(edit.Find, first + edit.Find.Length, StringComparison.Ordinal);
                if (second >= 0)
                {
                    error = "第 " + (i + 1) + " 处待替换片段在页面中出现多次";
                    return false;
                }

                patched = patched.Substring(0, first) + edit.Replace + patched.Substring(first + edit.Find.Length);
            }

            if (!IsCompleteHtml(patched))
            {
                error = "替换后文档不完整（缺少 <html> 或 </html>）";
                return false;
            }
            if (string.Equals(patched.Trim(), (html ?? string.Empty).Trim(), StringComparison.Ordinal))
            {
                error = "替换没有产生任何实际改动";
                return false;
            }

            return true;
        }

        /// <summary>
        /// 非流式收集一段完整回复（评审/重写用），并把用量累加进任务总量。
        /// </summary>
        private async Task<CompletionResult> CollectCompletionAsync(
            JobState job,
            AiSettings settings,
            string apiKey,
            string systemPrompt,
            string userPrompt,
            CancellationToken token)
        {
            var result = new CompletionResult();
            var builder = new StringBuilder();
            var reqPromptTokens = 0;
            var reqCompletionTokens = 0;
            var reqTotalTokens = 0;

            try
            {
                await foreach (var chunk in client
                    .StreamCompletionAsync(settings, apiKey, systemPrompt, userPrompt, token)
                    .ConfigureAwait(false))
                {
                    if (chunk == null)
                        continue;

                    if (chunk.PromptTokens.HasValue)
                        reqPromptTokens = chunk.PromptTokens.Value;
                    if (chunk.CompletionTokens.HasValue)
                        reqCompletionTokens = chunk.CompletionTokens.Value;
                    if (chunk.TotalTokens.HasValue)
                        reqTotalTokens = chunk.TotalTokens.Value;
                    if (!string.IsNullOrEmpty(chunk.FinishReason) &&
                        chunk.FinishReason.Equals("length", StringComparison.OrdinalIgnoreCase))
                        result.Truncated = true;

                    if (!string.IsNullOrEmpty(chunk.Content))
                        builder.Append(chunk.Content);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                result.Canceled = true;
            }
            catch (AiGenerationException ex)
            {
                result.Failure = ex;
            }
            catch (Exception ex)
            {
                result.Failure = new AiGenerationException(AiFailureKind.Protocol,
                    "精修请求出错：" + ex.Message, ex);
            }
            finally
            {
                job.AccPromptTokens += reqPromptTokens;
                job.AccCompletionTokens += reqCompletionTokens;
                job.AccTotalTokens += reqTotalTokens;
                job.PromptTokens = job.AccPromptTokens;
                job.CompletionTokens = job.AccCompletionTokens;
                job.TotalTokens = job.AccTotalTokens;
                result.Text = builder.ToString();
            }

            return result;
        }

        private static CritiqueResult TryParseCritique(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            var text = raw.Trim();
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start < 0 || end <= start)
                return null;

            try
            {
                var obj = JObject.Parse(text.Substring(start, end - start + 1));
                var overall = ReadScore(obj, "overall");
                if (!overall.HasValue)
                {
                    double sum = 0;
                    var count = 0;
                    foreach (var key in new[] { "composition", "color", "motion", "theme" })
                    {
                        var score = ReadScore(obj, key);
                        if (!score.HasValue)
                            continue;
                        sum += score.Value;
                        count++;
                    }
                    if (count > 0)
                        overall = sum / count;
                }

                if (!overall.HasValue)
                    return null;

                return new CritiqueResult
                {
                    Overall = Math.Clamp(overall.Value, 0d, 10d),
                    Issues = ReadStringList(obj["issues"]),
                    Fixes = ReadStringList(obj["fixes"]),
                    PlanSteps = ReadStringList(obj["plan"]),
                    Rationale = obj["rationale"]?.ToString()
                };
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static double? ReadScore(JObject obj, string name)
        {
            var token = obj[name];
            if (token == null || token.Type == JTokenType.Null)
                return null;
            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
                return token.Value<double>();
            if (double.TryParse(token.ToString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var value))
                return value;
            return null;
        }

        private static List<string> ReadStringList(JToken token)
        {
            var list = new List<string>();
            if (token == null || token.Type == JTokenType.Null)
                return list;
            if (token.Type == JTokenType.String)
            {
                foreach (var line in token.ToString().Split('\n'))
                {
                    var text = line.Trim().TrimStart('-', '*', ' ');
                    if (text.Length > 0)
                        list.Add(text);
                    if (list.Count >= 5)
                        break;
                }
                return list;
            }
            if (!(token is JArray array))
                return list;

            foreach (var item in array)
            {
                string text = null;
                if (item.Type == JTokenType.String)
                    text = item.ToString();
                else if (item is JObject obj)
                    text = obj["message"]?.ToString() ?? obj["issue"]?.ToString() ?? obj["description"]?.ToString();

                if (!string.IsNullOrWhiteSpace(text))
                    list.Add(text.Trim());
                if (list.Count >= 5)
                    break;
            }
            return list;
        }

        private static string SummarizeCritique(CritiqueResult critique)
        {
            if (critique.Issues.Count == 0)
                return "无具体问题";

            var sb = new StringBuilder();
            for (var i = 0; i < critique.Issues.Count && i < 3; i++)
            {
                if (sb.Length > 0)
                    sb.Append("；");
                sb.Append(critique.Issues[i]);
            }
            var summary = sb.ToString();
            return summary.Length <= 200 ? summary : summary.Substring(0, 200) + "…";
        }

        private static string AppendSummary(string existing, string addition)
        {
            return string.IsNullOrEmpty(existing) ? addition : existing + "；" + addition;
        }

        private static string SaveCandidateDraft(string jobId, int round, string tag, string html)
        {
            var directory = Path.Combine(Constants.CommonPaths.AiTempDir, jobId);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "candidate-r" + round + "-" + tag + ".html");
            File.WriteAllText(path, html ?? string.Empty, new UTF8Encoding(false));
            return path;
        }

        /// <summary>保留精修前的首个有效版本，便于对比与回退。</summary>
        private static void SaveInitialDraft(string jobId, string html)
        {
            var directory = Path.Combine(Constants.CommonPaths.AiTempDir, jobId);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "initial.html"), html ?? string.Empty, new UTF8Encoding(false));
        }

        public Task CancelAsync(string jobId)
        {
            if (!string.IsNullOrEmpty(jobId) && jobs.TryGetValue(jobId, out var job))
            {
                try
                {
                    job.Cancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // 任务已结束，忽略。
                }
            }
            return Task.CompletedTask;
        }

        public GenerationJobSnapshot GetJob(string jobId)
        {
            if (string.IsNullOrEmpty(jobId) || !jobs.TryGetValue(jobId, out var job))
                return null;

            return new GenerationJobSnapshot
            {
                JobId = jobId,
                State = job.State,
                Attempt = job.Attempt,
                VersionId = job.VersionId,
                CreatedAtUnixMs = job.CreatedAtUnixMs,
                DraftPath = job.DraftPath
            };
        }

        private static GenerationEvent CreateEvent(string jobId, JobState job, ValidationResult issues = null)
        {
            return new GenerationEvent
            {
                JobId = jobId,
                State = job.State,
                Attempt = job.Attempt,
                VersionId = job.VersionId,
                Issues = issues,
                ErrorCode = job.ErrorCode,
                ErrorMessage = job.ErrorMessage,
                PromptTokens = job.PromptTokens,
                CompletionTokens = job.CompletionTokens,
                TotalTokens = job.TotalTokens,
                ElapsedMs = job.Watch?.ElapsedMilliseconds,
                RefineRound = job.RefineRound,
                RefineScore = job.RefineScore,
                RefineSummary = job.RefineSummary,
                RefinePlan = job.RefinePlan,
                SessionId = job.SessionId,
                SessionVersion = job.SessionVersion,
                DraftPath = job.DraftPath
            };
        }

        private static ValidationOptions CreateValidationOptions(GenerationRequest request)
        {
            return new ValidationOptions
            {
                OfflineRequired = request.OfflineRequired,
                AllowExternalNetwork = request.AllowExternalNetwork,
                PerformanceMode = request.PerformanceMode,
                TargetWidth = request.TargetWidth,
                TargetHeight = request.TargetHeight
            };
        }

        private static string BuildValidationMessage(ValidationResult validation)
        {
            foreach (var issue in validation.Issues)
            {
                if (issue.Severity == ValidationSeverity.fatal)
                    return "生成结果未通过校验：" + issue.Message;
            }
            return "生成结果未通过校验，请重试。";
        }

        private static string SaveDraft(string jobId, string html)
        {
            var directory = Path.Combine(Constants.CommonPaths.AiTempDir, jobId);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "index.html");
            File.WriteAllText(path, html ?? string.Empty, new UTF8Encoding(false));
            return path;
        }

        private static readonly Regex FenceRegex = new Regex(
            @"```(?:html|HTML)?\s*([\s\S]*?)```",
            RegexOptions.Compiled);

        /// <summary>
        /// 容忍模型在 HTML 外包裹解释文字或 markdown 代码块。
        /// </summary>
        public static string ExtractHtml(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return string.Empty;

            var text = raw.Trim();
            var fence = FenceRegex.Match(text);
            if (fence.Success)
                text = fence.Groups[1].Value.Trim();

            var docStart = text.IndexOf("<!DOCTYPE", StringComparison.OrdinalIgnoreCase);
            if (docStart < 0)
                docStart = text.IndexOf("<html", StringComparison.OrdinalIgnoreCase);
            if (docStart > 0)
                text = text.Substring(docStart);

            var docEnd = text.LastIndexOf("</html>", StringComparison.OrdinalIgnoreCase);
            if (docEnd >= 0)
                text = text.Substring(0, docEnd + "</html>".Length);

            return text.Trim();
        }

        private sealed class JobState
        {
            public GenerationState State { get; set; }
            public int Attempt { get; set; }
            public string VersionId { get; set; }
            public string DraftPath { get; set; }
            public string Content { get; set; }
            public string ErrorCode { get; set; }
            public string ErrorMessage { get; set; }
            public int? PromptTokens { get; set; }
            public int? CompletionTokens { get; set; }
            public int? TotalTokens { get; set; }
            public int AccPromptTokens { get; set; }
            public int AccCompletionTokens { get; set; }
            public int AccTotalTokens { get; set; }
            public int RefineRound { get; set; }
            public double? RefineScore { get; set; }
            public string RefineSummary { get; set; }
            public string RefinePlan { get; set; }
            public string SessionId { get; set; }
            public int SessionVersion { get; set; }
            public long CreatedAtUnixMs { get; set; }
            public Stopwatch Watch { get; set; }
            public CancellationTokenSource Cancellation { get; set; }
        }

        private sealed class CompletionResult
        {
            public string Text { get; set; }
            public bool Canceled { get; set; }
            public AiGenerationException Failure { get; set; }

            /// <summary>服务端以 finish_reason=length 结束，说明输出撞到单次上限被截断。</summary>
            public bool Truncated { get; set; }
        }

        private sealed class CandidateResult
        {
            public bool Success { get; set; }
            public bool UsedPatch { get; set; }
            public string Html { get; set; }
            public ValidationResult Validation { get; set; }
            public string ErrorMessage { get; set; }
            public bool Canceled { get; set; }
            public AiGenerationException Failure { get; set; }
        }

        private sealed class PatchEdit
        {
            public string Find { get; set; }
            public string Replace { get; set; }
        }

        private sealed class CritiqueResult
        {
            public double Overall { get; set; }
            public List<string> Issues { get; set; } = new List<string>();
            public List<string> Fixes { get; set; } = new List<string>();
            public List<string> PlanSteps { get; set; } = new List<string>();
            public string Rationale { get; set; }

            public string PlanText => PlanSteps.Count == 0
                ? string.Empty
                : string.Join("\n", PlanSteps.Select((step, index) => (index + 1) + ". " + step));
        }
    }
}
