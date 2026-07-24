using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.Retrieval;

namespace ContextCore.Core.Services.DecisionEngine;

// ===========================================================================
// R28-B B-4：Authoritative Cutover — Retrieval → Package → AgentContext
//
// 目标（B-4 阶段：V2 Runtime 成为权威路径，Legacy 降级为可选 fallback）：
//   1. AuthoritativeRetrievalRuntime：V2 执行 + 可选 Shadow parity 校验 + fallback。
//   2. AuthoritativePackageRuntime：V2 执行 + 可选 Shadow parity 校验 + fallback。
//   3. AuthoritativeAgentContextRuntime：V2 执行 + AgentContextProjector 投影。
//   4. CutoverController：控制 Legacy → V2 切换比例（0% = Legacy only，
//      100% = V2 only，中间值 = 按 requestId 哈希分流）。
//
// 设计原则：
//   1. 渐进切换：CutoverController 按 percentage 控制流量比例，支持灰度。
//   2. Fallback 安全：V2 失败时自动回退到 Legacy（fail-open）。
//   3. Parity 监控：切换期间持续 Shadow parity 校验，Divergent 自动回退。
//   4. B-4 不删除 Legacy 代码（B-5 才删除）；Legacy 仍可通过 CutoverPercentage=0 启用。
// ===========================================================================

// ---------------------------------------------------------------------------
// §9.1 CutoverController — 渐进切换控制
// ---------------------------------------------------------------------------

/// <summary>
/// R28-B B-4：Cutover 控制器。按 percentage 控制 Legacy → V2 流量比例。
/// </summary>
/// <remarks>
/// 灰度策略：按 requestId 的稳定哈希决定走 V2 还是 Legacy。
/// 0% = 全部 Legacy；100% = 全部 V2；50% = 一半 V2 一半 Legacy。
/// 哈希稳定性保证同一 requestId 始终走同一路径（便于 Shadow parity 对比）。
/// </remarks>
public sealed class CutoverController
{
    private int _cutoverPercentage;

    /// <summary>构造控制器，默认 cutoverPercentage=0（全部 Legacy）。</summary>
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

// ---------------------------------------------------------------------------
// §9.2 AuthoritativeRetrievalRuntime — Retrieval 权威路径
// ---------------------------------------------------------------------------

/// <summary>
/// R28-B B-4：Retrieval 权威路径运行时。
/// 编排 Legacy Retriever + V2 Runtime + 可选 Shadow parity + fallback。
/// </summary>
public sealed class AuthoritativeRetrievalRuntime : IContextRetriever
{
    // P0-1：注入 Legacy 具体类型（HybridContextRetriever），而非 IContextRetriever 接口。
    // 避免将 AuthoritativeRetrievalRuntime 自身注册为 IContextRetriever 时产生 DI 循环。
    private readonly HybridContextRetriever _legacyRetriever;
    private readonly IContextDecisionRuntime _v2Runtime;
    private readonly ShadowDecisionRuntime _shadowRuntime;
    private readonly RetrievalResultProjector _retrievalProjector;
    private readonly CutoverController _cutoverController;
    private readonly ShadowGate? _shadowGate;
    // P0-9：注入实验平面集成（可选），用于自动记录 shadow fixture + sampled shadow。
    private readonly DecisionExperimentPlaneIntegration? _experimentPlane;

    /// <summary>构造 Retrieval 权威路径运行时。</summary>
    public AuthoritativeRetrievalRuntime(
        HybridContextRetriever legacyRetriever,
        IContextDecisionRuntime v2Runtime,
        ShadowDecisionRuntime shadowRuntime,
        RetrievalResultProjector retrievalProjector,
        CutoverController cutoverController,
        ShadowGate? shadowGate = null,
        DecisionExperimentPlaneIntegration? experimentPlane = null)
    {
        _legacyRetriever = legacyRetriever ?? throw new ArgumentNullException(nameof(legacyRetriever));
        _v2Runtime = v2Runtime ?? throw new ArgumentNullException(nameof(v2Runtime));
        _shadowRuntime = shadowRuntime ?? throw new ArgumentNullException(nameof(shadowRuntime));
        _retrievalProjector = retrievalProjector ?? throw new ArgumentNullException(nameof(retrievalProjector));
        _cutoverController = cutoverController ?? throw new ArgumentNullException(nameof(cutoverController));
        _shadowGate = shadowGate;
        _experimentPlane = experimentPlane;
    }

    /// <summary>
    /// 执行 Retrieval。按 CutoverController 决定走 V2 或 Legacy。
    /// P0-8 修复：
    ///   - 100% V2 时跳过 Legacy 执行（不再 100% Legacy + 100% V2 second pass）。
    ///   - catch 不再捕获 OperationCanceledException（用户取消应立即传播）。
    /// </summary>
    public async Task<ContextRetrievalResult> RetrieveAsync(
        ContextRetrievalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var useV2 = _cutoverController.ShouldUseV2(request.OperationId);

        // P0-8：100% V2 时跳过 Legacy，直接执行 V2-only 路径
        // P0-9：若 sampled shadow 启用，按采样率执行 Legacy + shadow 收集实验数据
        if (useV2 && _cutoverController.CutoverPercentage >= 100)
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
        var legacyResult = await _legacyRetriever.RetrieveAsync(request, cancellationToken).ConfigureAwait(false);

        // V2 路径：Shadow tee 捕获 + V2 执行 + parity 校验
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

            var shadowReport = await _shadowRuntime.ExecuteRetrievalShadowAsync(
                request, legacyResult, tokenBudget, topK, context, cancellationToken).ConfigureAwait(false);

            // P0-9：自动记录 shadow fixture（携带完整 WorkingSet + V2Result，供离线 replay）
            _experimentPlane?.RecordShadowReport(
                shadowReport, request.OperationId, "retrieval-mixed");

            // Parity 校验（B-3 Hard gate）
            if (_shadowGate is not null)
            {
                var gateResult = _shadowGate.Evaluate(shadowReport.Parity);
                if (gateResult.OverallLevel == ParityLevel.Divergent)
                {
                    // Divergent → 回退到 Legacy
                    return legacyResult;
                }
            }

            // Parity 通过 → 使用 V2 结果（通过 Projector 投影为 ContextRetrievalResult）
            // P0-7：传入 WorkingSet，让 Projector 从 Material sidecar 恢复 Content
            return _retrievalProjector.Project(shadowReport.V2Result, shadowReport.WorkingSet);
        }
        // P0-8：用户取消时立即传播，不回退 Legacy
        catch (OperationCanceledException)
        {
            throw;
        }
        // P0-8：V2 失败时回退到 Legacy（fail-open），但记录结构化 trace
        catch (Exception)
        {
            return legacyResult;
        }
    }

    /// <summary>
    /// P0-8：100% V2-only 路径。不执行 Legacy，直接调用 V2 Runtime。
    /// V2 失败时抛出异常（无 Legacy fallback）。
    /// R28-B.6 Blocker-1+4：使用 ExecuteWithWorkingSetAsync 获取完整 ExecutionResult
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
        // R28-B.7 P0-6：使用 execution 重载，确保 Projector 从 WorkingSet 恢复 Material 正文
        return _retrievalProjector.Project(execution);
    }

    /// <summary>
    /// R28-B.6：V2-only 路径，同时返回 raw V2 执行结果（供 sampled shadow 复用，避免重复调用 V2）。
    /// R28-B.6 Blocker-1：返回 ContextDecisionExecutionResult（含 WorkingSet），供 sampled shadow
    /// 构建完整 shadow 报告（不丢失 Material）。
    /// </summary>
    /// <returns>(projected RetrievalResult, raw ExecutionResult)。</returns>
    private async Task<(ContextRetrievalResult Projected, ContextDecisionExecutionResult Raw)> ExecuteV2OnlyRetrievalWithRawAsync(
        ContextRetrievalRequest request,
        CancellationToken cancellationToken)
    {
        var v2Request = BuildV2RetrievalRequest(request);
        var execution = await _v2Runtime.ExecuteWithWorkingSetAsync(v2Request, cancellationToken).ConfigureAwait(false);
        // R28-B.7 P0-6：使用 execution 重载
        return (_retrievalProjector.Project(execution), execution);
    }

    /// <summary>
    /// R28-B.6 Blocker-4：从 ContextRetrievalRequest 构建完整的 V2 RuntimeRequest，
    /// 携带 RetrievalInput（完整保留 RequiredIds/RequiredTags/QueryVector/IncludeVectorRecall/
    /// IncludeRelationExpansion/RewrittenQueryText 等）+ 真实 TokenBudget。
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
                // R28-B.6 P0-2：补齐原 ContextRetrievalRequest 完整语义（含新字段）
                IncludeStableMemory = request.IncludeStableMemory,
                IncludeContent = request.IncludeContent,
                Metadata = request.Metadata
            }
        };
    }

    /// <summary>
    /// P0-9：100% V2 cutover 下的 sampled shadow 路径。
    /// 执行 V2 权威 + Legacy 对照 + 记录 fixture，但始终返回 V2 结果。
    /// R28-B.6：V2 只调用一次（权威路径），shadow 复用 V2 结果做 parity 对比，不重复调用 V2。
    /// R28-B.6 Blocker-1：sampled shadow 同样使用 ExecuteWithWorkingSetAsync 获取完整 ExecutionResult。
    /// Shadow 失败时回退到 V2-only（不影响权威路径）。
    /// </summary>
    private async Task<ContextRetrievalResult> ExecuteRetrievalSampledShadowAsync(
        ContextRetrievalRequest request,
        CancellationToken cancellationToken)
    {
        // 先执行 V2 权威路径（只调用一次 V2，同时保留 raw 结果供 shadow 复用）
        var (v2Projected, v2Execution) = await ExecuteV2OnlyRetrievalWithRawAsync(request, cancellationToken).ConfigureAwait(false);

        // Best-effort sampled shadow：失败不影响返回值
        try
        {
            var legacyResult = await _legacyRetriever.RetrieveAsync(request, cancellationToken).ConfigureAwait(false);

            var context = new CandidateAdaptationContext
            {
                WorkspaceId = request.WorkspaceId,
                CollectionId = request.CollectionId,
                RequestId = request.OperationId,
                QueryText = request.QueryText,
                ObservedAt = DateTimeOffset.UtcNow
            };

            var tokenBudget = legacyResult.EstimatedTokens > 0 ? legacyResult.EstimatedTokens : 4096;

            // R28-B.6：复用已计算的 V2 结果构建 shadow 报告，不再次调用 V2 Runtime
            var shadowReport = _shadowRuntime.BuildRetrievalShadowReport(
                request, legacyResult, v2Execution, tokenBudget, context);

            // P0-9：记录完整 shadow fixture（携带 WorkingSet + V2Result）
            _experimentPlane?.RecordShadowReport(
                shadowReport, request.OperationId, "retrieval-sampled-shadow");
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
}

// ---------------------------------------------------------------------------
// §9.3 AuthoritativePackageRuntime — Package 权威路径
// ---------------------------------------------------------------------------

/// <summary>
/// R28-B B-4：Package 权威路径运行时。
/// 编排 Legacy PackageBuilder + V2 Runtime + 可选 Shadow parity + fallback。
/// </summary>
public sealed class AuthoritativePackageRuntime : IContextPackageBuilder
{
    // P0-1：注入 Legacy 具体类型（BasicContextPackageBuilder），而非 IContextPackageBuilder 接口。
    // 避免将 AuthoritativePackageRuntime 自身注册为 IContextPackageBuilder 时产生 DI 循环。
    private readonly BasicContextPackageBuilder _legacyPackageBuilder;
    private readonly IContextDecisionRuntime _v2Runtime;
    private readonly ShadowDecisionRuntime _shadowRuntime;
    private readonly PackageResultProjector _packageProjector;
    private readonly CutoverController _cutoverController;
    private readonly ShadowGate? _shadowGate;
    // P0-9：注入实验平面集成（可选），用于自动记录 shadow fixture + sampled shadow。
    private readonly DecisionExperimentPlaneIntegration? _experimentPlane;

    /// <summary>构造 Package 权威路径运行时。</summary>
    public AuthoritativePackageRuntime(
        BasicContextPackageBuilder legacyPackageBuilder,
        IContextDecisionRuntime v2Runtime,
        ShadowDecisionRuntime shadowRuntime,
        PackageResultProjector packageProjector,
        CutoverController cutoverController,
        ShadowGate? shadowGate = null,
        DecisionExperimentPlaneIntegration? experimentPlane = null)
    {
        _legacyPackageBuilder = legacyPackageBuilder ?? throw new ArgumentNullException(nameof(legacyPackageBuilder));
        _v2Runtime = v2Runtime ?? throw new ArgumentNullException(nameof(v2Runtime));
        _shadowRuntime = shadowRuntime ?? throw new ArgumentNullException(nameof(shadowRuntime));
        _packageProjector = packageProjector ?? throw new ArgumentNullException(nameof(packageProjector));
        _cutoverController = cutoverController ?? throw new ArgumentNullException(nameof(cutoverController));
        _shadowGate = shadowGate;
        _experimentPlane = experimentPlane;
    }

    /// <summary>
    /// R28-B.6 Blocker-3：IContextPackageBuilder.BuildAsync 统一走 BuildDetailedAsync（含 V2 路径），
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
    /// P0-8 修复：100% V2 跳过 Legacy + 异常安全。
    /// </summary>
    public async Task<ContextPackageBuildResult> BuildDetailedAsync(
        ContextPackageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var useV2 = _cutoverController.ShouldUseV2(request.WorkspaceId + ":" + request.CollectionId + ":" + request.QueryText);

        // P0-8：100% V2 时跳过 Legacy，直接执行 V2-only 路径
        // P0-9：若 sampled shadow 启用，按采样率执行 Legacy + shadow 收集实验数据
        if (useV2 && _cutoverController.CutoverPercentage >= 100)
        {
            if (_experimentPlane is not null && _experimentPlane.ShouldRunSampledShadow(
                request.WorkspaceId + ":" + request.CollectionId + ":" + request.QueryText))
            {
                return await ExecutePackageSampledShadowAsync(request, cancellationToken).ConfigureAwait(false);
            }
            return await ExecuteV2OnlyPackageAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var legacyResult = await _legacyPackageBuilder.BuildDetailedAsync(request, cancellationToken).ConfigureAwait(false);

        var requestId = legacyResult.BuildId;
        if (!useV2)
        {
            return legacyResult;
        }

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

            var shadowReport = await _shadowRuntime.ExecutePackageShadowAsync(
                requestId, legacyResult, tokenBudget, context, cancellationToken).ConfigureAwait(false);

            // P0-9：自动记录 shadow fixture（携带完整 WorkingSet + V2Result，供离线 replay）
            _experimentPlane?.RecordShadowReport(
                shadowReport, requestId, "package-mixed");

            if (_shadowGate is not null)
            {
                var gateResult = _shadowGate.Evaluate(shadowReport.Parity);
                if (gateResult.OverallLevel == ParityLevel.Divergent)
                {
                    return legacyResult;
                }
            }

            return _packageProjector.Project(shadowReport.V2Result, shadowReport.WorkingSet);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return legacyResult;
        }
    }

    /// <summary>
    /// P0-8：100% V2-only Package 路径。不执行 Legacy，直接调用 V2 Runtime。
    /// R28-B.6 Blocker-1+4：使用 ExecuteWithWorkingSetAsync 获取完整 ExecutionResult
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
        // R28-B.7 P0-6：使用 execution 重载，从 execution.Scope 获取 WorkspaceId/CollectionId
        // 修复空 Package 丢失 Scope 问题（候选为空时仍能从 execution.Scope 获取作用域）
        return _packageProjector.Project(execution);
    }

    /// <summary>
    /// R28-B.6：V2-only Package 路径，同时返回 raw V2 执行结果（供 sampled shadow 复用，避免重复调用 V2）。
    /// R28-B.6 Blocker-1：返回 ContextDecisionExecutionResult（含 WorkingSet），供 sampled shadow
    /// 构建完整 shadow 报告（不丢失 Material）。
    /// </summary>
    private async Task<(ContextPackageBuildResult Projected, ContextDecisionExecutionResult Raw)> ExecuteV2OnlyPackageWithRawAsync(
        ContextPackageRequest request,
        CancellationToken cancellationToken)
    {
        var v2Request = BuildV2PackageRequest(request);
        var execution = await _v2Runtime.ExecuteWithWorkingSetAsync(v2Request, cancellationToken).ConfigureAwait(false);
        // R28-B.7 P0-6：使用 execution 重载（含 Scope，修复空 Package Scope 丢失）
        return (_packageProjector.Project(execution), execution);
    }

    /// <summary>
    /// R28-B.6 Blocker-4：从 ContextPackageRequest 构建完整的 V2 RuntimeRequest，
    /// 携带 PackageInput（完整保留 RequiredIds/RequiredTags/QueryVector 等）+ 真实 TokenBudget。
    /// R28-B.7 P1-2：补齐 Mode/Policy/IncludeRecent/IsAuditMode/Metadata 字段映射，
    /// 完整保留原 ContextPackageRequest 语义。
    /// </summary>
    private static ContextDecisionRuntimeRequest BuildV2PackageRequest(ContextPackageRequest request)
    {
        // PackageRequest 不携带 QueryVector / ModelName 等 retrieval-specific 字段，
        // 但保留 RequiredTags / RequiredTypes / TokenBudget 等公共字段。
        // R28-B.7 P1-2：补齐 Mode/Policy/IncludeRecent/IsAuditMode/Metadata。
        return new ContextDecisionRuntimeRequest
        {
            RequestId = request.RequestId ?? request.OperationId ?? Guid.NewGuid().ToString("N"),
            Purpose = ContextDecisionPurpose.Package,
            Scope = new ContextDecisionScope(request.WorkspaceId, request.CollectionId),
            QueryText = request.QueryText,
            TokenBudget = request.TokenBudget > 0 ? request.TokenBudget : 4096,
            TopK = int.MaxValue,
            SeedCandidates = Array.Empty<ContextCandidateEnvelope>(),
            PackageInput = new PackageInput
            {
                RequiredTags = request.RequiredTags,
                RequiredTypes = request.RequiredTypes,
                // R28-B.7 P1-2：补齐原 ContextPackageRequest 完整语义
                Mode = request.Mode,
                Policy = request.Policy,
                IncludeRecent = request.IncludeRecent,
                IsAuditMode = request.IsAuditMode,
                Metadata = request.Metadata
            }
        };
    }

    /// <summary>
    /// P0-9：100% V2 cutover 下的 sampled shadow 路径（Package）。
    /// 执行 V2 权威 + Legacy 对照 + 记录 fixture，但始终返回 V2 结果。
    /// R28-B.6：V2 只调用一次（权威路径），shadow 复用 V2 结果做 parity 对比，不重复调用 V2。
    /// R28-B.6 Blocker-1：sampled shadow 同样使用 ExecuteWithWorkingSetAsync 获取完整 ExecutionResult。
    /// Shadow 失败时回退到 V2-only（不影响权威路径）。
    /// </summary>
    private async Task<ContextPackageBuildResult> ExecutePackageSampledShadowAsync(
        ContextPackageRequest request,
        CancellationToken cancellationToken)
    {
        // 先执行 V2 权威路径（只调用一次 V2，同时保留 raw 结果供 shadow 复用）
        var (v2Projected, v2Execution) = await ExecuteV2OnlyPackageWithRawAsync(request, cancellationToken).ConfigureAwait(false);

        // Best-effort sampled shadow：失败不影响返回值
        try
        {
            var legacyResult = await _legacyPackageBuilder.BuildDetailedAsync(request, cancellationToken).ConfigureAwait(false);
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

            // R28-B.6：复用已计算的 V2 结果构建 shadow 报告，不再次调用 V2 Runtime
            var shadowReport = _shadowRuntime.BuildPackageShadowReport(
                requestId, legacyResult, v2Execution, tokenBudget, context);

            // P0-9：记录完整 shadow fixture（携带 WorkingSet + V2Result）
            _experimentPlane?.RecordShadowReport(
                shadowReport, requestId, "package-sampled-shadow");
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
}

// ---------------------------------------------------------------------------
// §9.4 AuthoritativeAgentContextRuntime — AgentContext 权威路径
// ---------------------------------------------------------------------------

/// <summary>
/// R28-B B-4：AgentContext 权威路径运行时。
/// 直接消费 V2 Runtime + AgentContextProjector，无需 Legacy fallback（AgentContext 是新路径）。
/// </summary>
public sealed class AuthoritativeAgentContextRuntime
{
    private readonly IContextDecisionRuntime _v2Runtime;
    private readonly IAgentContextProjector _agentContextProjector;

    /// <summary>构造 AgentContext 权威路径运行时。</summary>
    public AuthoritativeAgentContextRuntime(
        IContextDecisionRuntime v2Runtime,
        IAgentContextProjector agentContextProjector)
    {
        _v2Runtime = v2Runtime ?? throw new ArgumentNullException(nameof(v2Runtime));
        _agentContextProjector = agentContextProjector ?? throw new ArgumentNullException(nameof(agentContextProjector));
    }

    /// <summary>
    /// 执行 AgentContext 构建。直接走 V2 Runtime（无 Legacy fallback）。
    /// </summary>
    /// <param name="request">V2 Runtime 请求。</param>
    /// <param name="workingSet">候选 WorkingSet（含 Envelopes + Materials）。</param>
    /// <param name="projectionContext">R28-B.6：真实 Agent session + scope（null 时从 request.AgentInput.Session 自动构造）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// R28-B.6 P0-3：将 caller WorkingSet 作为 SeedWorkingSet 传入（含 Envelopes + Materials），
    /// 使用 ExecuteWithWorkingSetAsync 获取完整 execution artifact。Projector 从 execution.WorkingSet
    /// （包含 Provider 新召回的 Material）恢复正文，而非 caller 原始 WorkingSet。
    /// R28-B.7 P1-3：projectionContext 为 null 时，从 request.AgentInput.Session 自动构造 ProjectionContext，
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

        // R28-B.6 P0-3 + P0-4：将 caller WorkingSet 作为 SeedWorkingSet 传入（含 Materials），
        // 而非仅合并 Envelopes 到 SeedCandidates。Runtime 在合并阶段会保留 SeedWorkingSet.Materials。
        var mergedRequest = request with { SeedWorkingSet = workingSet };

        // R28-B.6 P0-3：使用 ExecuteWithWorkingSetAsync 获取完整 execution artifact
        var execution = await _v2Runtime.ExecuteWithWorkingSetAsync(
            mergedRequest, cancellationToken).ConfigureAwait(false);

        // R28-B.7 P1-3：projectionContext 为 null 时，从 request.AgentInput.Session 自动构造 ProjectionContext。
        // 这样 Projector 始终使用真实 AgentSessionId（而非回退到伪造的 session-{requestId}）。
        // 仅当 AgentInput.Session 非空时构造；Session 也为 null 时回退到 execution 重载（Projector 内部构造占位 session）。
        var effectiveContext = projectionContext ?? BuildProjectionContextFromAgentInput(request);

        // R28-B.6 P0-3：使用 execution.WorkingSet（包含 Provider 新召回的 Material），而非 caller 原始 WorkingSet
        // R28-B.7 P0-6：使用 execution 重载
        return effectiveContext is not null
            ? _agentContextProjector.Project(execution, effectiveContext)
            : _agentContextProjector.Project(execution);
    }

    /// <summary>
    /// R28-B.7 P1-3：从 request.AgentInput.Session 构造 ProjectionContext。
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
