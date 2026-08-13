using System.Diagnostics;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.Evolution;
using ContextCore.Core.Services.Retrieval;
using Microsoft.Extensions.Logging;

namespace ContextCore.Core.Services.DecisionEngine;

// HTTP 检索/打包的装饰器：按 CutoverController 在两条实现之间切换。
// 缺省（FromEnvironment）是 100：只走 IContextDecisionRuntime；失败不回退 Legacy。
// 0：只走 HybridContextRetriever / BasicContextPackageBuilder。
// 中间值：按 requestId 哈希分流，并可做 shadow/parity。
// CutoverController 无参构造仍是 0，给 canary 每轮隔离用；HTTP 默认百分比来自 CutoverConfiguration。
// Agent Run 的 ContextBuilding 不经过本装饰器，直接调用 IContextDecisionRuntime。

// ---------------------------------------------------------------------------
// CutoverController — 渐进切换控制
// ---------------------------------------------------------------------------

/// <summary>
/// Cutover 控制器。按 percentage 控制 Legacy → V2 流量比例。
/// </summary>
/// <remarks>
/// 灰度策略：按 requestId 的稳定哈希决定走 V2 还是 Legacy。
/// 0% = 全部 Legacy；100% = 全部 V2；50% = 一半 V2 一半 Legacy。
/// 哈希稳定性保证同一 requestId 始终走同一路径（便于 Shadow parity 对比）。
/// </remarks>
public sealed class CutoverController
{
    private int _cutoverPercentage;

    /// <summary>
    /// 构造控制器。无参时为 0（全部 Legacy），给 canary 每轮隔离用。
    /// HTTP 进程默认百分比来自 <see cref="CutoverConfiguration.FromEnvironment"/>，不是本构造缺省。
    /// </summary>
    public CutoverController(int cutoverPercentage = 0)
    {
        if (cutoverPercentage < 0 || cutoverPercentage > 100)
            throw new ArgumentOutOfRangeException(nameof(cutoverPercentage));
        _cutoverPercentage = cutoverPercentage;
    }

    /// <summary>当前 V2 流量百分比（0-100）。</summary>
    public int CutoverPercentage => _cutoverPercentage;

    /// <summary>设置 V2 流量百分比（线程安全）。</summary>
    public void SetCutoverPercentage(int percentage)
    {
        if (percentage < 0 || percentage > 100)
            throw new ArgumentOutOfRangeException(nameof(percentage));
        Interlocked.Exchange(ref _cutoverPercentage, percentage);
    }

    /// <summary>判断给定 requestId 是否应走 V2 路径。</summary>
    /// <remarks>使用 requestId 的稳定哈希（FNV-1a）取模，保证同一 requestId 始终走同一路径。</remarks>
    public bool ShouldUseV2(string requestId)
    {
        var percentage = _cutoverPercentage;
        if (percentage <= 0) return false;
        if (percentage >= 100) return true;

        var hash = StableHash(requestId);
        return (hash % 100) < percentage;
    }

    private static uint StableHash(string value)
    {
        // FNV-1a 32-bit hash（稳定跨进程/跨平台）
        uint hash = 2166136261u;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(value))
        {
            hash ^= b;
            hash *= 16777619u;
        }
        return hash;
    }
}

/// <summary>
/// 从请求 metadata 中提取 canary run ID 的内部辅助方法。
/// </summary>
internal static class CanaryRunIdResolver
{
    /// <summary>metadata 中标识 canary run ID 的键名。</summary>
    public const string RunIdMetadataKey = "canaryRunId";

    /// <summary>从请求 metadata 中读取 canary run ID；不存在时返回 null。</summary>
    public static string? TryGetCanaryRunId(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
        {
            return null;
        }
        return metadata.TryGetValue(RunIdMetadataKey, out var runId) && !string.IsNullOrWhiteSpace(runId)
            ? runId
            : null;
    }
}

// ===========================================================================
// Canary 质量分计算器
// 
// 目标：
// 从 ContextDecisionExecutionResult 计算 0.0-1.0 范围的质量分，
// 综合 section 覆盖率（token 预算利用率）与候选相关性（FinalScore 均值）：
// quality_score = SectionCoverageWeight × SectionCoverage + RelevanceWeight × AvgRelevance
// 
// 设计边界：
// - 输入为 null（V2 失败/未执行）时返回 0.0（无质量信号）。
// - 权重默认 0.5 / 0.5，可通过 CanaryGateOptions 配置。
// - section 覆盖率优先从 FinalArtifactTokenCost 读取（精确），
// 回退到 Outcome.EffectiveTokens / Outcome.TokenBudget（粗略）。
// - 候选相关性取 SelectedEnvelopes.Utility.FinalScore 均值，无选中候选时为 0.0。
// - 所有中间值 Clamp 到 [0.0, 1.0]，防止异常输入导致质量分越界。
// ===========================================================================

/// <summary>
/// Canary 质量分计算器。从 V2 执行结果计算综合质量分。
/// </summary>
internal static class CanaryQualityScoreCalculator
{
    /// <summary>默认 section 覆盖率权重（0.5）。</summary>
    public const double DefaultSectionCoverageWeight = 0.5;

    /// <summary>默认候选相关性权重（0.5）。</summary>
    public const double DefaultRelevanceWeight = 0.5;

    /// <summary>
    /// 从完整 V2 执行结果计算质量分。execution 为 null 时返回 0.0。
    /// </summary>
    /// <param name="execution">V2 执行结果（含 Decision + WorkingSet + FinalTokenCost）。可为 null。</param>
    /// <param name="sectionCoverageWeight">section 覆盖率权重（默认 0.5）。</param>
    /// <param name="relevanceWeight">候选相关性权重（默认 0.5）。</param>
    /// <returns>质量分（0.0-1.0）。</returns>
    public static double Compute(
        ContextDecisionExecutionResult? execution,
        double sectionCoverageWeight = DefaultSectionCoverageWeight,
        double relevanceWeight = DefaultRelevanceWeight)
    {
        if (execution is null)
        {
            return 0.0;
        }
        return Compute(execution.Decision, execution.FinalTokenCost, sectionCoverageWeight, relevanceWeight);
    }

    /// <summary>
    /// 从决策结果 + 最终 token 成本计算质量分。decision 为 null 时返回 0.0。
    /// </summary>
    /// <param name="decision">V2 决策结果（含 SelectedEnvelopes + Outcome）。</param>
    /// <param name="finalTokenCost">最终序列化 token 成本（可为 null，回退到 Outcome 估算）。</param>
    /// <param name="sectionCoverageWeight">section 覆盖率权重（默认 0.5）。</param>
    /// <param name="relevanceWeight">候选相关性权重（默认 0.5）。</param>
    /// <returns>质量分（0.0-1.0）。</returns>
    public static double Compute(
        ContextDecisionResult? decision,
        FinalArtifactTokenCost? finalTokenCost,
        double sectionCoverageWeight = DefaultSectionCoverageWeight,
        double relevanceWeight = DefaultRelevanceWeight)
    {
        if (decision is null)
        {
            return 0.0;
        }

        var sectionCoverage = ComputeSectionCoverage(decision, finalTokenCost);
        var avgRelevance = ComputeAvgRelevance(decision);
        var totalWeight = sectionCoverageWeight + relevanceWeight;
        // 权重全为 0 时退化为等权（避免除零）；否则按比例归一化
        if (totalWeight <= 0.0)
        {
            sectionCoverageWeight = DefaultSectionCoverageWeight;
            relevanceWeight = DefaultRelevanceWeight;
            totalWeight = sectionCoverageWeight + relevanceWeight;
        }
        var score = (sectionCoverageWeight * sectionCoverage + relevanceWeight * avgRelevance) / totalWeight;
        return Math.Clamp(score, 0.0, 1.0);
    }

    /// <summary>
    /// 计算 section 覆盖率（token 预算利用率，capped at 1.0）。
    /// 优先从 FinalArtifactTokenCost 读取（精确，含 section 分隔符 + 头部）；
    /// 回退到 Outcome.EffectiveTokens / Outcome.TokenBudget（粗略）；
    /// 无预算约束时返回 1.0（视为完整覆盖）。
    /// </summary>
    private static double ComputeSectionCoverage(ContextDecisionResult decision, FinalArtifactTokenCost? finalTokenCost)
    {
        // 优先路径：FinalArtifactTokenCost（精确）
        if (finalTokenCost is not null && finalTokenCost.BudgetLimit > 0)
        {
            return Math.Clamp((double)finalTokenCost.TotalTokens / finalTokenCost.BudgetLimit, 0.0, 1.0);
        }

        // 回退路径：Outcome 字段（粗略，可能为 0）
        var budget = decision.Outcome.TokenBudget;
        if (budget > 0)
        {
            return Math.Clamp((double)decision.Outcome.EffectiveTokens / budget, 0.0, 1.0);
        }

        // 无预算约束（如 Retrieval 场景 Outcome.TokenBudget=0）→ 视为完整覆盖
        return 1.0;
    }

    /// <summary>
    /// 计算候选相关性均值：SelectedEnvelopes.Utility.FinalScore 的算术均值。
    /// 无选中候选时返回 0.0（质量差）。FinalScore 异常值（NaN/Infinity/负数）被忽略。
    /// </summary>
    private static double ComputeAvgRelevance(ContextDecisionResult decision)
    {
        var selected = decision.SelectedEnvelopes;
        if (selected is null || selected.Count == 0)
        {
            return 0.0;
        }
        var sum = 0.0;
        var count = 0;
        foreach (var envelope in selected)
        {
            var score = envelope.Utility.FinalScore;
            if (double.IsNaN(score) || double.IsInfinity(score) || score < 0.0)
            {
                continue;
            }
            // FinalScore 通常已归一化到 [0,1]，但仍 Clamp 防止异常输入
            sum += Math.Clamp(score, 0.0, 1.0);
            count++;
        }
        if (count == 0)
        {
            return 0.0;
        }
        return sum / count;
    }
}

// ---------------------------------------------------------------------------
// AuthoritativeRetrievalRuntime — Retrieval 权威路径
// ---------------------------------------------------------------------------

/// <summary>
/// Retrieval 权威路径运行时。
/// 编排 Legacy Retriever + V2 Runtime + 可选 Shadow parity + fallback。
/// </summary>
public sealed class AuthoritativeRetrievalRuntime : IContextRetriever
{
    // 注入 Legacy 具体类型（HybridContextRetriever），而非 IContextRetriever 接口。
    // 避免将 AuthoritativeRetrievalRuntime 自身注册为 IContextRetriever 时产生 DI 循环。
    private readonly HybridContextRetriever _legacyRetriever;
    private readonly IContextDecisionRuntime _v2Runtime;
    private readonly ShadowDecisionRuntime _shadowRuntime;
    private readonly RetrievalResultProjector _retrievalProjector;
    private readonly CutoverController _cutoverController;
    private readonly ShadowGate? _shadowGate;
    // 注入实验平面集成（可选），用于自动记录 shadow fixture + sampled shadow。
    private readonly DecisionExperimentPlaneIntegration? _experimentPlane;
    // 可选的 per-run CutoverController 解析器。
    // 非空时按请求 metadata 中的 canaryRunId 解析到对应 run 的专用控制器；
    // 为 null 时回退到直接注入的 _cutoverController。
    private readonly ICutoverControllerResolver? _cutoverResolver;
    // 可选的 Canary 指标采集器。非空时 Mixed mode + Sampled shadow 路径会调用
    // RecordObservation 上报 V2/Legacy 耗时与 parity，让 CanaryProgressionHostedService 有生产样本可消费。
    private readonly ICanaryMetricsCollector? _canaryMetricsCollector;
    // 可选的集群级 Canary Kill Switch 存储。非空时在 canary 命中 V2 后检查活跃紧急覆盖，
    // 存在则强制回退 V1（Emergency Override 优先级高于 canary DB 百分比与 Cutover 配置）。
    private readonly ICanaryEmergencyOverrideStore? _emergencyOverrideStore;
    // Kill Switch 查询故障告警日志（可选注入；测试可传 null）。
    private readonly ILogger<AuthoritativeRetrievalRuntime>? _logger;

    /// <summary>构造 Retrieval 权威路径运行时。</summary>
    public AuthoritativeRetrievalRuntime(
        HybridContextRetriever legacyRetriever,
        IContextDecisionRuntime v2Runtime,
        ShadowDecisionRuntime shadowRuntime,
        RetrievalResultProjector retrievalProjector,
        CutoverController cutoverController,
        ShadowGate? shadowGate = null,
        DecisionExperimentPlaneIntegration? experimentPlane = null,
        ICutoverControllerResolver? cutoverResolver = null,
        ICanaryMetricsCollector? canaryMetricsCollector = null,
        ICanaryEmergencyOverrideStore? emergencyOverrideStore = null,
        ILogger<AuthoritativeRetrievalRuntime>? logger = null)
    {
        _legacyRetriever = legacyRetriever ?? throw new ArgumentNullException(nameof(legacyRetriever));
        _v2Runtime = v2Runtime ?? throw new ArgumentNullException(nameof(v2Runtime));
        _shadowRuntime = shadowRuntime ?? throw new ArgumentNullException(nameof(shadowRuntime));
        _retrievalProjector = retrievalProjector ?? throw new ArgumentNullException(nameof(retrievalProjector));
        _cutoverController = cutoverController ?? throw new ArgumentNullException(nameof(cutoverController));
        _shadowGate = shadowGate;
        _experimentPlane = experimentPlane;
        _cutoverResolver = cutoverResolver;
        _canaryMetricsCollector = canaryMetricsCollector;
        _emergencyOverrideStore = emergencyOverrideStore;
        _logger = logger;
    }

    /// <summary>
    /// 检查请求所属 canary run 是否存在活跃紧急覆盖（Kill Switch）。
    /// 存储为 null 或请求未携带 canaryRunId 时返回 false（不拦截非 canary 流量）。
    /// </summary>
    /// <remarks>
    /// Override Store 查询失败必须 fail-closed——按「覆盖活跃」处理强制回退 V1 并告警，
    /// 绝不让在线请求因 Kill Switch 存储故障直接失败。取消异常仍原样传播。
    /// </remarks>
    private async ValueTask<bool> IsEmergencyOverrideActiveAsync(
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken)
    {
        if (_emergencyOverrideStore is null)
        {
            return false;
        }
        var runId = CanaryRunIdResolver.TryGetCanaryRunId(metadata);
        if (string.IsNullOrWhiteSpace(runId))
        {
            return false;
        }
        try
        {
            return await _emergencyOverrideStore.GetActiveAsync(runId, cancellationToken).ConfigureAwait(false) is not null;
        }
        catch (OperationCanceledException)
        {
            throw; // 调用方取消应立即传播（与 RetrieveAsync 取消语义一致）
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "P0-13：Canary run {RunId} 查询 Kill Switch（Emergency Override）失败，按覆盖活跃处理，安全回退 V1。",
                runId);
            return true;
        }
    }

    /// <summary>
    /// 解析当前请求应使用的 CutoverController。
    /// resolver 非空时从请求 metadata 读取 canaryRunId 解析到 per-run 控制器；
    /// 否则回退到直接注入的 _cutoverController。
    /// </summary>
    private CutoverController ResolveController(ContextRetrievalRequest request)
    {
        if (_cutoverResolver is null)
        {
            return _cutoverController;
        }
        var runId = CanaryRunIdResolver.TryGetCanaryRunId(request.Metadata);
        return _cutoverResolver.Resolve(runId);
    }

    /// <summary>
    /// 执行 Retrieval。按 CutoverController 决定走 V2 或 Legacy。
    /// 修复：
    /// - 100% V2 时跳过 Legacy 执行（不再 100% Legacy + 100% V2 second pass）。
    /// - catch 不再捕获 OperationCanceledException（用户取消应立即传播）。
    /// </summary>
    public async Task<ContextRetrievalResult> RetrieveAsync(
        ContextRetrievalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 按请求解析 per-run CutoverController（resolver 为 null 时回退到共享控制器）
        var cutoverController = ResolveController(request);
        var useV2 = cutoverController.ShouldUseV2(request.OperationId);

        // 集群级 Kill Switch：存在活跃紧急覆盖时强制回退 V1（优先级高于 canary 百分比与 Cutover 配置）。
        if (useV2 && await IsEmergencyOverrideActiveAsync(request.Metadata, cancellationToken).ConfigureAwait(false))
        {
            useV2 = false;
        }

        // 100% V2 时跳过 Legacy，直接执行 V2-only 路径
        // 若 sampled shadow 启用，按采样率执行 Legacy + shadow 收集实验数据
        if (useV2 && cutoverController.CutoverPercentage >= 100)
        {
            if (_experimentPlane is not null && _experimentPlane.ShouldRunSampledShadow(request.OperationId))
            {
                return await ExecuteRetrievalSampledShadowAsync(request, cancellationToken).ConfigureAwait(false);
            }
            return await ExecuteV2OnlyRetrievalAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (!useV2)
        {
            // Legacy only
            return await _legacyRetriever.RetrieveAsync(request, cancellationToken).ConfigureAwait(false);
        }

        // Mixed mode（0% < cutover < 100%）：Legacy + V2（Shadow tee + fallback）
        var legacyStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var legacyResult = await _legacyRetriever.RetrieveAsync(request, cancellationToken).ConfigureAwait(false);
        legacyStopwatch.Stop();

        // V2 路径：Shadow tee 捕获 + V2 执行 + parity 校验
        RetrievalShadowReport? shadowReport = null;
        var v2Stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var context = new CandidateAdaptationContext
            {
                WorkspaceId = request.WorkspaceId,
                CollectionId = request.CollectionId,
                RequestId = request.OperationId,
                QueryText = request.QueryText,
                ObservedAt = DateTimeOffset.UtcNow
            };

            var tokenBudget = legacyResult.EstimatedTokens > 0 ? legacyResult.EstimatedTokens : 4096;
            var topK = request.TopK > 0 ? request.TopK : 10;

            shadowReport = await _shadowRuntime.ExecuteRetrievalShadowAsync(
                request, legacyResult, tokenBudget, topK, context, cancellationToken).ConfigureAwait(false);
            v2Stopwatch.Stop();

            // 自动记录 shadow fixture（携带完整 WorkingSet + V2Result，供离线 replay）
            _experimentPlane?.RecordShadowReport(
                shadowReport, request.OperationId, "retrieval-mixed");

            // Parity 校验（B-3 Hard gate）
            if (_shadowGate is not null)
            {
                var gateResult = _shadowGate.Evaluate(shadowReport.Parity);
                if (gateResult.OverallLevel == ParityLevel.Divergent)
                {
                    // Divergent → 回退到 Legacy（仍上报观察样本，让 Canary 看到发散率）
                    // 从 shadowReport.Execution（若存在）计算质量分；
                    // Divergent 不代表 V2 无产出，质量分仍反映 V2 内容质量
                    RecordCanaryObservation(
                        request, shadowReport.Parity,
                        v2Succeeded: true, legacySucceeded: true,
                        v2Duration: v2Stopwatch.Elapsed,
                        legacyDuration: legacyStopwatch.Elapsed,
                        qualityScore: CanaryQualityScoreCalculator.Compute(shadowReport.Execution));
                    return legacyResult;
                }
            }

            // Parity 通过 → 使用 V2 结果（通过 Projector 投影为 ContextRetrievalResult）
            // 传入 WorkingSet，让 Projector 从 Material sidecar 恢复 Content
            // 从 shadowReport.Execution（若存在）计算质量分
            RecordCanaryObservation(
                request, shadowReport.Parity,
                v2Succeeded: true, legacySucceeded: true,
                v2Duration: v2Stopwatch.Elapsed,
                legacyDuration: legacyStopwatch.Elapsed,
                qualityScore: CanaryQualityScoreCalculator.Compute(shadowReport.Execution));
            return _retrievalProjector.Project(shadowReport.V2Result, shadowReport.WorkingSet);
        }
        // 用户取消时立即传播，不回退 Legacy
        catch (OperationCanceledException)
        {
            throw;
        }
        // V2 失败时回退到 Legacy（fail-open），但记录结构化 trace
        catch (Exception)
        {
            v2Stopwatch.Stop();
            // V2 失败也要上报样本（V2 error rate 是回滚阈值之一）。
            // parity 用空报告占位（Divergent 视为 true，让 Canary 看到失败）。
            // V2 失败 → 质量分记为 0.0（默认值，无需显式传参）。
            RecordCanaryObservation(
                request, shadowReport?.Parity ?? BuildEmptyParityReport(),
                v2Succeeded: false, legacySucceeded: true,
                v2Duration: v2Stopwatch.Elapsed,
                legacyDuration: legacyStopwatch.Elapsed);
            return legacyResult;
        }
    }

    /// <summary>
    /// 100% V2-only 路径。不执行 Legacy，直接调用 V2 Runtime。
    /// V2 失败时抛出异常（无 Legacy fallback）。
    /// 使用 ExecuteWithWorkingSetAsync 获取完整 ExecutionResult
    /// （含 WorkingSet），并将 WorkingSet 传给 Projector 恢复 Material 正文。
    /// 构建 RetrievalInput 完整保留原 ContextRetrievalRequest 语义；使用 request.TokenBudget
    /// 而非硬编码 4096。
    /// </summary>
    private async Task<ContextRetrievalResult> ExecuteV2OnlyRetrievalAsync(
        ContextRetrievalRequest request,
        CancellationToken cancellationToken)
    {
        var v2Request = BuildV2RetrievalRequest(request);
        var execution = await _v2Runtime.ExecuteWithWorkingSetAsync(v2Request, cancellationToken).ConfigureAwait(false);
        // 使用 execution 重载，确保 Projector 从 WorkingSet 恢复 Material 正文
        return _retrievalProjector.Project(execution);
    }

    /// <summary>
    /// V2-only 路径，同时返回 raw V2 执行结果（供 sampled shadow 复用，避免重复调用 V2）。
    /// 返回 ContextDecisionExecutionResult（含 WorkingSet），供 sampled shadow
    /// 构建完整 shadow 报告（不丢失 Material）。
    /// </summary>
    /// <returns>(projected RetrievalResult, raw ExecutionResult)。</returns>
    private async Task<(ContextRetrievalResult Projected, ContextDecisionExecutionResult Raw)> ExecuteV2OnlyRetrievalWithRawAsync(
        ContextRetrievalRequest request,
        CancellationToken cancellationToken)
    {
        var v2Request = BuildV2RetrievalRequest(request);
        var execution = await _v2Runtime.ExecuteWithWorkingSetAsync(v2Request, cancellationToken).ConfigureAwait(false);
        // 使用 execution 重载
        return (_retrievalProjector.Project(execution), execution);
    }

    /// <summary>
    /// 从 ContextRetrievalRequest 构建 V2 RuntimeRequest。
    /// 请求带 QueryTexts 时按条词法检索；为空则回退单条 QueryText（RewrittenQueryText 优先）。
    /// </summary>
    private static ContextDecisionRuntimeRequest BuildV2RetrievalRequest(ContextRetrievalRequest request)
    {
        return new ContextDecisionRuntimeRequest
        {
            RequestId = request.OperationId,
            Purpose = ContextDecisionPurpose.Retrieval,
            Scope = new ContextDecisionScope(request.WorkspaceId, request.CollectionId),
            QueryText = request.QueryText,
            TokenBudget = request.TokenBudget > 0 ? request.TokenBudget : 4096,
            TopK = request.TopK > 0 ? request.TopK : 10,
            SeedCandidates = Array.Empty<ContextCandidateEnvelope>(),
            RetrievalInput = new RetrievalInput
            {
                RewrittenQueryText = request.RewrittenQueryText,
                QueryTexts = request.QueryTexts,
                RequiredTags = request.RequiredTags,
                RequiredTypes = request.RequiredTypes,
                RequiredIds = request.RequiredIds,
                Refs = request.Refs,
                QueryVector = request.QueryVector,
                ModelName = request.ModelName,
                QueryInstruction = request.QueryInstruction,
                CandidateTake = request.CandidateTake,
                VectorTopK = request.VectorTopK,
                MinVectorScore = request.MinVectorScore,
                AllowedRelationTypes = request.AllowedRelationTypes,
                RelationExpansionDepth = request.RelationExpansionDepth,
                IncludeKeywordRecall = request.IncludeKeywordRecall,
                IncludeVectorRecall = request.IncludeVectorRecall,
                IncludeRelationExpansion = request.IncludeRelationExpansion,
                IncludeWorkingMemory = request.IncludeWorkingMemory,
                // 补齐原 ContextRetrievalRequest 完整语义（含新字段）
                IncludeStableMemory = request.IncludeStableMemory,
                IncludeContent = request.IncludeContent,
                Metadata = request.Metadata
            }
        };
    }

    /// <summary>
    /// 100% V2 cutover 下的 sampled shadow 路径。
    /// 执行 V2 权威 + Legacy 对照 + 记录 fixture，但始终返回 V2 结果。
    /// V2 只调用一次（权威路径），shadow 复用 V2 结果做 parity 对比，不重复调用 V2。
    /// sampled shadow 同样使用 ExecuteWithWorkingSetAsync 获取完整 ExecutionResult。
    /// Shadow 失败时回退到 V2-only（不影响权威路径）。
    /// sampled shadow 路径同样调用 RecordObservation 上报 Canary 样本。
    /// </summary>
    private async Task<ContextRetrievalResult> ExecuteRetrievalSampledShadowAsync(
        ContextRetrievalRequest request,
        CancellationToken cancellationToken)
    {
        // 先执行 V2 权威路径（只调用一次 V2，同时保留 raw 结果供 shadow 复用）
        var v2Stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var (v2Projected, v2Execution) = await ExecuteV2OnlyRetrievalWithRawAsync(request, cancellationToken).ConfigureAwait(false);
        v2Stopwatch.Stop();

        // Best-effort sampled shadow：失败不影响返回值
        try
        {
            var legacyStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var legacyResult = await _legacyRetriever.RetrieveAsync(request, cancellationToken).ConfigureAwait(false);
            legacyStopwatch.Stop();

            var context = new CandidateAdaptationContext
            {
                WorkspaceId = request.WorkspaceId,
                CollectionId = request.CollectionId,
                RequestId = request.OperationId,
                QueryText = request.QueryText,
                ObservedAt = DateTimeOffset.UtcNow
            };

            var tokenBudget = legacyResult.EstimatedTokens > 0 ? legacyResult.EstimatedTokens : 4096;

            // 复用已计算的 V2 结果构建 shadow 报告，不再次调用 V2 Runtime
            var shadowReport = _shadowRuntime.BuildRetrievalShadowReport(
                request, legacyResult, v2Execution, tokenBudget, context);

            // 记录完整 shadow fixture（携带 WorkingSet + V2Result）
            _experimentPlane?.RecordShadowReport(
                shadowReport, request.OperationId, "retrieval-sampled-shadow");

            // sampled shadow 同样上报 Canary 样本（V2 + Legacy 耗时均为真实测量值）
            // 从 v2Execution 直接计算质量分（sampled shadow 路径有完整 execution）
            RecordCanaryObservation(
                request, shadowReport.Parity,
                v2Succeeded: true, legacySucceeded: true,
                v2Duration: v2Stopwatch.Elapsed,
                legacyDuration: legacyStopwatch.Elapsed,
                qualityScore: CanaryQualityScoreCalculator.Compute(v2Execution));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Sampled shadow 失败不影响权威路径
        }

        return v2Projected;
    }

    /// <summary>
    /// 上报 Canary 观察样本到 ICanaryMetricsCollector（若注入）。
    /// 仅当请求 metadata 中携带 canaryRunId 时上报（无 runId 的请求不属于任何 canary run）。
    /// 新增 qualityScore 参数（null 时记为 0.0；由调用方从 V2 执行结果计算）。
    /// </summary>
    private void RecordCanaryObservation(
        ContextRetrievalRequest request,
        ParityReport parityReport,
        bool v2Succeeded,
        bool legacySucceeded,
        TimeSpan v2Duration,
        TimeSpan legacyDuration,
        double? qualityScore = null)
    {
        if (_canaryMetricsCollector is null)
        {
            return;
        }
        var runId = CanaryRunIdResolver.TryGetCanaryRunId(request.Metadata);
        if (string.IsNullOrWhiteSpace(runId))
        {
            return;
        }
        try
        {
            _canaryMetricsCollector.RecordObservation(
                runId, parityReport, v2Succeeded, legacySucceeded,
                v2Duration, legacyDuration, qualityScore);
        }
        catch
        {
            // 上报失败不影响权威路径（best-effort）
        }
    }

    /// <summary>
    /// 构建 V2 失败时的占位 ParityReport（Divergent，让 Canary 看到失败）。
    /// </summary>
    private static ParityReport BuildEmptyParityReport() => new(
        LegacySelectedCount: 0,
        V2SelectedCount: 0,
        CommonSelectedCount: 0,
        OnlyInLegacyCount: 0,
        OnlyInV2Count: 0,
        JaccardIndex: 0.0,
        ParityLevel: ParityLevel.Divergent,
        LegacyTokenTotal: 0,
        V2TokenTotal: 0,
        WorkingSetCandidateCount: 0);
}

// ---------------------------------------------------------------------------
// AuthoritativePackageRuntime — Package 权威路径
// ---------------------------------------------------------------------------

/// <summary>
/// Package 权威路径运行时。
/// 编排 Legacy PackageBuilder + V2 Runtime + 可选 Shadow parity + fallback。
/// </summary>
public sealed class AuthoritativePackageRuntime : IContextPackageBuilder
{
    // 注入 Legacy 具体类型（BasicContextPackageBuilder），而非 IContextPackageBuilder 接口。
    // 避免将 AuthoritativePackageRuntime 自身注册为 IContextPackageBuilder 时产生 DI 循环。
    private readonly BasicContextPackageBuilder _legacyPackageBuilder;
    private readonly IContextDecisionRuntime _v2Runtime;
    private readonly ShadowDecisionRuntime _shadowRuntime;
    private readonly PackageResultProjector _packageProjector;
    private readonly CutoverController _cutoverController;
    private readonly ShadowGate? _shadowGate;
    // 注入实验平面集成（可选），用于自动记录 shadow fixture + sampled shadow。
    private readonly DecisionExperimentPlaneIntegration? _experimentPlane;
    // 可选的 per-run CutoverController 解析器（语义同 Retrieval 运行时）。
    private readonly ICutoverControllerResolver? _cutoverResolver;
    // 可选的 Canary 指标采集器（语义同 Retrieval 运行时）。
    private readonly ICanaryMetricsCollector? _canaryMetricsCollector;
    // 可选的集群级 Canary Kill Switch 存储（语义同 Retrieval 运行时）。
    private readonly ICanaryEmergencyOverrideStore? _emergencyOverrideStore;
    // Kill Switch 查询故障告警日志（可选注入；测试可传 null）。
    private readonly ILogger<AuthoritativePackageRuntime>? _logger;

    /// <summary>构造 Package 权威路径运行时。</summary>
    public AuthoritativePackageRuntime(
        BasicContextPackageBuilder legacyPackageBuilder,
        IContextDecisionRuntime v2Runtime,
        ShadowDecisionRuntime shadowRuntime,
        PackageResultProjector packageProjector,
        CutoverController cutoverController,
        ShadowGate? shadowGate = null,
        DecisionExperimentPlaneIntegration? experimentPlane = null,
        ICutoverControllerResolver? cutoverResolver = null,
        ICanaryMetricsCollector? canaryMetricsCollector = null,
        ICanaryEmergencyOverrideStore? emergencyOverrideStore = null,
        ILogger<AuthoritativePackageRuntime>? logger = null)
    {
        _legacyPackageBuilder = legacyPackageBuilder ?? throw new ArgumentNullException(nameof(legacyPackageBuilder));
        _v2Runtime = v2Runtime ?? throw new ArgumentNullException(nameof(v2Runtime));
        _shadowRuntime = shadowRuntime ?? throw new ArgumentNullException(nameof(shadowRuntime));
        _packageProjector = packageProjector ?? throw new ArgumentNullException(nameof(packageProjector));
        _cutoverController = cutoverController ?? throw new ArgumentNullException(nameof(cutoverController));
        _shadowGate = shadowGate;
        _experimentPlane = experimentPlane;
        _cutoverResolver = cutoverResolver;
        _canaryMetricsCollector = canaryMetricsCollector;
        _emergencyOverrideStore = emergencyOverrideStore;
        _logger = logger;
    }

    /// <summary>
    /// 检查请求所属 canary run 是否存在活跃紧急覆盖（Kill Switch，语义同 Retrieval 运行时）。
    /// </summary>
    /// <remarks>
    /// Override Store 查询失败必须 fail-closed——按「覆盖活跃」处理强制回退 V1 并告警，
    /// 绝不让在线请求因 Kill Switch 存储故障直接失败。取消异常仍原样传播。
    /// </remarks>
    private async ValueTask<bool> IsEmergencyOverrideActiveAsync(
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken)
    {
        if (_emergencyOverrideStore is null)
        {
            return false;
        }
        var runId = CanaryRunIdResolver.TryGetCanaryRunId(metadata);
        if (string.IsNullOrWhiteSpace(runId))
        {
            return false;
        }
        try
        {
            return await _emergencyOverrideStore.GetActiveAsync(runId, cancellationToken).ConfigureAwait(false) is not null;
        }
        catch (OperationCanceledException)
        {
            throw; // 调用方取消应立即传播（与 BuildDetailedAsync 取消语义一致）
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "P0-13：Canary run {RunId} 查询 Kill Switch（Emergency Override）失败，按覆盖活跃处理，安全回退 V1。",
                runId);
            return true;
        }
    }

    /// <summary>
    /// 解析当前请求应使用的 CutoverController（语义同 Retrieval 运行时）。
    /// </summary>
    private CutoverController ResolveController(ContextPackageRequest request)
    {
        if (_cutoverResolver is null)
        {
            return _cutoverController;
        }
        var runId = CanaryRunIdResolver.TryGetCanaryRunId(request.Metadata);
        return _cutoverResolver.Resolve(runId);
    }

    /// <summary>
    /// IContextPackageBuilder.BuildAsync 统一走 BuildDetailedAsync（含 V2 路径），
    /// 返回 result.Package。不再绕过 V2 直接走 Legacy 的 BuildAsync。
    /// </summary>
    public async Task<ContextPackage> BuildAsync(
        ContextPackageRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await BuildDetailedAsync(request, cancellationToken).ConfigureAwait(false);
        return result.Package;
    }

    /// <summary>
    /// 执行 Package 构建。按 CutoverController 决定走 V2 或 Legacy。
    /// 修复：100% V2 跳过 Legacy + 异常安全。
    /// </summary>
    public async Task<ContextPackageBuildResult> BuildDetailedAsync(
        ContextPackageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 按请求解析 per-run CutoverController（resolver 为 null 时回退到共享控制器）
        var cutoverController = ResolveController(request);
        var useV2 = cutoverController.ShouldUseV2(request.WorkspaceId + ":" + request.CollectionId + ":" + request.QueryText);

        // 集群级 Kill Switch：存在活跃紧急覆盖时强制回退 V1（优先级高于 canary 百分比与 Cutover 配置）。
        if (useV2 && await IsEmergencyOverrideActiveAsync(request.Metadata, cancellationToken).ConfigureAwait(false))
        {
            useV2 = false;
        }

        // 100% V2 时跳过 Legacy，直接执行 V2-only 路径
        // 若 sampled shadow 启用，按采样率执行 Legacy + shadow 收集实验数据
        if (useV2 && cutoverController.CutoverPercentage >= 100)
        {
            if (_experimentPlane is not null && _experimentPlane.ShouldRunSampledShadow(
                request.WorkspaceId + ":" + request.CollectionId + ":" + request.QueryText))
            {
                return await ExecutePackageSampledShadowAsync(request, cancellationToken).ConfigureAwait(false);
            }
            return await ExecuteV2OnlyPackageAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var legacyStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var legacyResult = await _legacyPackageBuilder.BuildDetailedAsync(request, cancellationToken).ConfigureAwait(false);
        legacyStopwatch.Stop();

        var requestId = legacyResult.BuildId;
        if (!useV2)
        {
            return legacyResult;
        }

        PackageShadowReport? shadowReport = null;
        var v2Stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var context = new CandidateAdaptationContext
            {
                WorkspaceId = request.WorkspaceId,
                CollectionId = request.CollectionId,
                RequestId = requestId,
                QueryText = request.QueryText,
                ObservedAt = DateTimeOffset.UtcNow
            };

            var tokenBudget = legacyResult.TokenBudget > 0
                ? legacyResult.TokenBudget
                : legacyResult.EstimatedTokens;

            shadowReport = await _shadowRuntime.ExecutePackageShadowAsync(
                requestId, legacyResult, tokenBudget, context, cancellationToken).ConfigureAwait(false);
            v2Stopwatch.Stop();

            // 自动记录 shadow fixture（携带完整 WorkingSet + V2Result，供离线 replay）
            _experimentPlane?.RecordShadowReport(
                shadowReport, requestId, "package-mixed");

            if (_shadowGate is not null)
            {
                var gateResult = _shadowGate.Evaluate(shadowReport.Parity);
                if (gateResult.OverallLevel == ParityLevel.Divergent)
                {
                    // Divergent → 回退到 Legacy（仍上报观察样本，让 Canary 看到发散率）
                    // 从 shadowReport.Execution（若存在）计算质量分
                    RecordCanaryObservation(
                        request, shadowReport.Parity,
                        v2Succeeded: true, legacySucceeded: true,
                        v2Duration: v2Stopwatch.Elapsed,
                        legacyDuration: legacyStopwatch.Elapsed,
                        qualityScore: CanaryQualityScoreCalculator.Compute(shadowReport.Execution));
                    return legacyResult;
                }
            }

            // 从 shadowReport.Execution（若存在）计算质量分
            RecordCanaryObservation(
                request, shadowReport.Parity,
                v2Succeeded: true, legacySucceeded: true,
                v2Duration: v2Stopwatch.Elapsed,
                legacyDuration: legacyStopwatch.Elapsed,
                qualityScore: CanaryQualityScoreCalculator.Compute(shadowReport.Execution));
            return _packageProjector.Project(shadowReport.V2Result, shadowReport.WorkingSet);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            v2Stopwatch.Stop();
            // V2 失败也上报样本（V2 error rate 是回滚阈值之一）
            // V2 失败 → 质量分记为 0.0（默认值，无需显式传参）。
            RecordCanaryObservation(
                request, shadowReport?.Parity ?? BuildEmptyParityReport(),
                v2Succeeded: false, legacySucceeded: true,
                v2Duration: v2Stopwatch.Elapsed,
                legacyDuration: legacyStopwatch.Elapsed);
            return legacyResult;
        }
    }

    /// <summary>
    /// 100% V2-only Package 路径。不执行 Legacy，直接调用 V2 Runtime。
    /// 使用 ExecuteWithWorkingSetAsync 获取完整 ExecutionResult
    /// （含 WorkingSet），并将 WorkingSet 传给 Projector 恢复 Material 正文。
    /// 构建 PackageInput 完整保留原 ContextPackageRequest 语义；使用 request.TokenBudget
    /// 而非硬编码 4096。
    /// </summary>
    private async Task<ContextPackageBuildResult> ExecuteV2OnlyPackageAsync(
        ContextPackageRequest request,
        CancellationToken cancellationToken)
    {
        var v2Request = BuildV2PackageRequest(request);
        var execution = await _v2Runtime.ExecuteWithWorkingSetAsync(v2Request, cancellationToken).ConfigureAwait(false);
        // 使用 execution 重载，从 execution.Scope 获取 WorkspaceId/CollectionId
        // 修复空 Package 丢失 Scope 问题（候选为空时仍能从 execution.Scope 获取作用域）
        return _packageProjector.Project(execution);
    }

    /// <summary>
    /// V2-only Package 路径，同时返回 raw V2 执行结果（供 sampled shadow 复用，避免重复调用 V2）。
    /// 返回 ContextDecisionExecutionResult（含 WorkingSet），供 sampled shadow
    /// 构建完整 shadow 报告（不丢失 Material）。
    /// </summary>
    private async Task<(ContextPackageBuildResult Projected, ContextDecisionExecutionResult Raw)> ExecuteV2OnlyPackageWithRawAsync(
        ContextPackageRequest request,
        CancellationToken cancellationToken)
    {
        var v2Request = BuildV2PackageRequest(request);
        var execution = await _v2Runtime.ExecuteWithWorkingSetAsync(v2Request, cancellationToken).ConfigureAwait(false);
        // 使用 execution 重载（含 Scope，修复空 Package Scope 丢失）
        return (_packageProjector.Project(execution), execution);
    }

    /// <summary>
    /// 从 ContextPackageRequest 构建完整的 V2 RuntimeRequest，
    /// 携带 PackageInput（完整保留 RequiredIds/RequiredTags/QueryVector 等）+ 真实 TokenBudget。
    /// 补齐 Mode/Policy/IncludeRecent/IsAuditMode/Metadata 字段映射，
    /// 完整保留原 ContextPackageRequest 语义。
    /// RetrievalInput 只补 Lexical 需要的 QueryTexts（空则回退单条 QueryText），
    /// IncludeContent 保持默认 true，与未设 RetrievalInput 时行为一致。
    /// </summary>
    private static ContextDecisionRuntimeRequest BuildV2PackageRequest(ContextPackageRequest request)
    {
        // PackageRequest 不携带 QueryVector / ModelName 等 retrieval-specific 字段，
        // 但保留 RequiredTags / RequiredTypes / TokenBudget 等公共字段。
        // 补齐 Mode/Policy/IncludeRecent/IsAuditMode/Metadata。
        return new ContextDecisionRuntimeRequest
        {
            RequestId = request.RequestId ?? request.OperationId ?? Guid.NewGuid().ToString("N"),
            Purpose = ContextDecisionPurpose.Package,
            Scope = new ContextDecisionScope(request.WorkspaceId, request.CollectionId),
            QueryText = request.QueryText,
            TokenBudget = request.TokenBudget > 0 ? request.TokenBudget : 4096,
            TopK = int.MaxValue,
            SeedCandidates = Array.Empty<ContextCandidateEnvelope>(),
            RetrievalInput = new RetrievalInput
            {
                QueryTexts = request.QueryTexts
            },
            PackageInput = new PackageInput
            {
                RequiredTags = request.RequiredTags,
                RequiredTypes = request.RequiredTypes,
                // 补齐原 ContextPackageRequest 完整语义
                Mode = request.Mode,
                Policy = request.Policy,
                IncludeRecent = request.IncludeRecent,
                IsAuditMode = request.IsAuditMode,
                Metadata = request.Metadata
            }
        };
    }

    /// <summary>
    /// 100% V2 cutover 下的 sampled shadow 路径（Package）。
    /// 执行 V2 权威 + Legacy 对照 + 记录 fixture，但始终返回 V2 结果。
    /// V2 只调用一次（权威路径），shadow 复用 V2 结果做 parity 对比，不重复调用 V2。
    /// sampled shadow 同样使用 ExecuteWithWorkingSetAsync 获取完整 ExecutionResult。
    /// Shadow 失败时回退到 V2-only（不影响权威路径）。
    /// sampled shadow 路径同样调用 RecordObservation 上报 Canary 样本。
    /// </summary>
    private async Task<ContextPackageBuildResult> ExecutePackageSampledShadowAsync(
        ContextPackageRequest request,
        CancellationToken cancellationToken)
    {
        // 先执行 V2 权威路径（只调用一次 V2，同时保留 raw 结果供 shadow 复用）
        var v2Stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var (v2Projected, v2Execution) = await ExecuteV2OnlyPackageWithRawAsync(request, cancellationToken).ConfigureAwait(false);
        v2Stopwatch.Stop();

        // Best-effort sampled shadow：失败不影响返回值
        try
        {
            var legacyStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var legacyResult = await _legacyPackageBuilder.BuildDetailedAsync(request, cancellationToken).ConfigureAwait(false);
            legacyStopwatch.Stop();
            var requestId = legacyResult.BuildId;

            var context = new CandidateAdaptationContext
            {
                WorkspaceId = request.WorkspaceId,
                CollectionId = request.CollectionId,
                RequestId = requestId,
                QueryText = request.QueryText,
                ObservedAt = DateTimeOffset.UtcNow
            };

            var tokenBudget = legacyResult.TokenBudget > 0
                ? legacyResult.TokenBudget
                : legacyResult.EstimatedTokens;

            // 复用已计算的 V2 结果构建 shadow 报告，不再次调用 V2 Runtime
            var shadowReport = _shadowRuntime.BuildPackageShadowReport(
                requestId, legacyResult, v2Execution, tokenBudget, context);

            // 记录完整 shadow fixture（携带 WorkingSet + V2Result）
            _experimentPlane?.RecordShadowReport(
                shadowReport, requestId, "package-sampled-shadow");

            // sampled shadow 同样上报 Canary 样本（V2 + Legacy 耗时均为真实测量值）
            // 从 v2Execution 直接计算质量分（sampled shadow 路径有完整 execution）
            RecordCanaryObservation(
                request, shadowReport.Parity,
                v2Succeeded: true, legacySucceeded: true,
                v2Duration: v2Stopwatch.Elapsed,
                legacyDuration: legacyStopwatch.Elapsed,
                qualityScore: CanaryQualityScoreCalculator.Compute(v2Execution));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Sampled shadow 失败不影响权威路径
        }

        return v2Projected;
    }

    /// <summary>
    /// 上报 Package 路径的 Canary 观察样本（语义同 Retrieval 路径的同名方法）。
    /// </summary>
    private void RecordCanaryObservation(
        ContextPackageRequest request,
        ParityReport parityReport,
        bool v2Succeeded,
        bool legacySucceeded,
        TimeSpan v2Duration,
        TimeSpan legacyDuration,
        double? qualityScore = null)
    {
        if (_canaryMetricsCollector is null)
        {
            return;
        }
        var runId = CanaryRunIdResolver.TryGetCanaryRunId(request.Metadata);
        if (string.IsNullOrWhiteSpace(runId))
        {
            return;
        }
        try
        {
            _canaryMetricsCollector.RecordObservation(
                runId, parityReport, v2Succeeded, legacySucceeded,
                v2Duration, legacyDuration, qualityScore);
        }
        catch
        {
            // 上报失败不影响权威路径（best-effort）
        }
    }

    /// <summary>
    /// 构建 V2 失败时的占位 ParityReport（Divergent，让 Canary 看到失败）。
    /// </summary>
    private static ParityReport BuildEmptyParityReport() => new(
        LegacySelectedCount: 0,
        V2SelectedCount: 0,
        CommonSelectedCount: 0,
        OnlyInLegacyCount: 0,
        OnlyInV2Count: 0,
        JaccardIndex: 0.0,
        ParityLevel: ParityLevel.Divergent,
        LegacyTokenTotal: 0,
        V2TokenTotal: 0,
        WorkingSetCandidateCount: 0);
}

// ---------------------------------------------------------------------------
// AuthoritativeAgentContextRuntime — AgentContext 权威路径
// ---------------------------------------------------------------------------

/// <summary>
/// AgentContext 权威路径运行时。
/// 直接消费 V2 Runtime + AgentContextProjector，无需 Legacy fallback（AgentContext 是新路径）。
/// </summary>
public sealed class AuthoritativeAgentContextRuntime
{
    private readonly IContextDecisionRuntime _v2Runtime;
    private readonly IAgentContextProjector _agentContextProjector;
    private readonly IComponentHealthRegistry? _componentHealthRegistry;

    /// <summary>构造 AgentContext 权威路径运行时。</summary>
    /// <param name="v2Runtime">V2 Runtime（编排 Provider → Merge → Feature → Engine）。</param>
    /// <param name="agentContextProjector">AgentContext 投影器。</param>
    /// <param name="componentHealthRegistry">组件健康注册表（null 时跳过 projection_ms 归因，向后兼容）。</param>
    public AuthoritativeAgentContextRuntime(
        IContextDecisionRuntime v2Runtime,
        IAgentContextProjector agentContextProjector,
        IComponentHealthRegistry? componentHealthRegistry = null)
    {
        _v2Runtime = v2Runtime ?? throw new ArgumentNullException(nameof(v2Runtime));
        _agentContextProjector = agentContextProjector ?? throw new ArgumentNullException(nameof(agentContextProjector));
        _componentHealthRegistry = componentHealthRegistry;
    }

    /// <summary>
    /// 执行 AgentContext 构建。直接走 V2 Runtime（无 Legacy fallback）。
    /// </summary>
    /// <param name="request">V2 Runtime 请求。</param>
    /// <param name="workingSet">候选 WorkingSet（含 Envelopes + Materials）。</param>
    /// <param name="projectionContext">真实 Agent session + scope（null 时从 request.AgentInput.Session 自动构造）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// 将 caller WorkingSet 作为 SeedWorkingSet 传入（含 Envelopes + Materials），
    /// 使用 ExecuteWithWorkingSetAsync 获取完整 execution artifact。Projector 从 execution.WorkingSet
    /// （包含 Provider 新召回的 Material）恢复正文，而非 caller 原始 WorkingSet。
    /// projectionContext 为 null 时，从 request.AgentInput.Session 自动构造 ProjectionContext，
    /// 让 Projector 始终使用真实 AgentSessionId，而非回退到伪造的 session-{requestId}。
    /// </remarks>
    public async ValueTask<AgentContextSnapshot> BuildAsync(
        ContextDecisionRuntimeRequest request,
        CandidateWorkingSet workingSet,
        ProjectionContext? projectionContext = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(workingSet);

        // + 将 caller WorkingSet 作为 SeedWorkingSet 传入（含 Materials），
        // 而非仅合并 Envelopes 到 SeedCandidates。Runtime 在合并阶段会保留 SeedWorkingSet.Materials。
        var mergedRequest = request with { SeedWorkingSet = workingSet };

        // 使用 ExecuteWithWorkingSetAsync 获取完整 execution artifact
        var execution = await _v2Runtime.ExecuteWithWorkingSetAsync(
            mergedRequest, cancellationToken).ConfigureAwait(false);

        // projectionContext 为 null 时，从 request.AgentInput.Session 自动构造 ProjectionContext。
        // 这样 Projector 始终使用真实 AgentSessionId（而非回退到伪造的 session-{requestId}）。
        // 仅当 AgentInput.Session 非空时构造；Session 也为 null 时回退到 execution 重载（Projector 内部构造占位 session）。
        var effectiveContext = projectionContext ?? BuildProjectionContextFromAgentInput(request);

        // 使用 execution.WorkingSet（包含 Provider 新召回的 Material），而非 caller 原始 WorkingSet
        // 使用 execution 重载
        // 用 Stopwatch 拆分 projection_ms（IAgentContextProjector 投影耗时），记录到 IComponentHealthRegistry
        var componentRegistry = _componentHealthRegistry;
        var componentScopeKey = $"{request.Scope.WorkspaceId}/{request.Scope.CollectionId}";
        var projectionSw = componentRegistry is not null ? Stopwatch.StartNew() : null;
        bool projectionSucceeded = false;
        try
        {
            var snapshot = effectiveContext is not null
                ? _agentContextProjector.Project(execution, effectiveContext)
                : _agentContextProjector.Project(execution);
            projectionSucceeded = true;
            return snapshot;
        }
        finally
        {
            if (projectionSw is not null)
            {
                projectionSw.Stop();
                componentRegistry!.RecordComponentTime(
                    ComponentKind.Projection, projectionSw.Elapsed.TotalMilliseconds,
                    projectionSucceeded, componentScopeKey, cancellationToken);
            }
        }
    }

    /// <summary>
    /// 从 request.AgentInput.Session 构造 ProjectionContext。
    /// 仅当 AgentInput 非空且 Session 非空时返回非 null；否则返回 null（让 Projector 回退到占位 session）。
    /// </summary>
    private static ProjectionContext? BuildProjectionContextFromAgentInput(ContextDecisionRuntimeRequest request)
    {
        var agentInput = request.AgentInput;
        if (agentInput?.Session is null)
        {
            return null;
        }

        return new ProjectionContext
        {
            AgentSession = agentInput.Session,
            WorkspaceId = request.Scope.WorkspaceId,
            CollectionId = request.Scope.CollectionId
        };
    }
}
