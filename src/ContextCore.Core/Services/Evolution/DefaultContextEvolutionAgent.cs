using ContextCore.Abstractions;

namespace ContextCore.Core.Services.Evolution;

/// <summary>
/// 默认 <see cref="IContextEvolutionAgent"/> 实现：
/// 基于 <see cref="IAgentObservationSource"/> 采集指标并按目标组件模板生成 <see cref="OptimizationProposal"/>。
/// </summary>
/// <remarks>
/// <b>硬边界</b>（与 project memory 一致）：
/// <list type="bullet">
/// <item>仅依赖 <see cref="IAgentObservationSource"/>；不引用任何 Policy / 配置 / 构建路径接口。</item>
/// <item>输出的 <see cref="OptimizationProposal.Status"/> 上限为 <see cref="OptimizationProposalStatus.ExperimentReady"/>；
/// 不允许输出 <see cref="OptimizationProposalStatus.Shadow"/> /
/// <see cref="OptimizationProposalStatus.ScopedCanary"/> /
/// <see cref="OptimizationProposalStatus.Promoted"/> /
/// <see cref="OptimizationProposalStatus.RolledBack"/>（这些状态由 R17 pipeline 推进）。</item>
/// <item><see cref="DiagnoseAsync"/> 总是输出 <see cref="OptimizationProposalStatus.Validated"/>（有可执行假设时）。</item>
/// <item><see cref="RefineProposalAsync"/> 根据新证据方向决定推进到 <see cref="OptimizationProposalStatus.ExperimentReady"/> 或
/// 退回 <see cref="OptimizationProposalStatus.Rejected"/>；状态推进不可逆（已 Rejected 的 proposal 不再变更状态）。</item>
/// </list>
/// </remarks>
public sealed class DefaultContextEvolutionAgent : IContextEvolutionAgent
{
    /// <summary>Agent 标识（写入 proposal.AgentIdentifier 供审计）。</summary>
    public const string AgentIdentifier = "default-evolution-agent-v1";

    private static readonly string[] FallbackObservationKeys =
    {
        "duration_ms", "cache_hit_rate", "recall", "retrieval_cost_ms",
        "topk_hit_rate", "rerank_latency_ms", "eviction_rate", "truncation_rate"
    };

    private readonly IAgentObservationSource _observationSource;
    private readonly TimeProvider _timeProvider;

    /// <summary>构造默认 agent。</summary>
    /// <param name="observationSource">观察源（必填）。</param>
    /// <param name="timeProvider">时间提供者（可选，默认 <see cref="TimeProvider.System"/>）。</param>
    public DefaultContextEvolutionAgent(
        IAgentObservationSource observationSource,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(observationSource);
        _observationSource = observationSource;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<AgentDiagnosticResult> DiagnoseAsync(
        AgentDiagnosticRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var observations = new List<string>();
        var hypothesisTrail = new List<string>();

        // 1. Observe
        var metrics = await _observationSource.ObserveAsync(
            request.WorkspaceId, request.CollectionId, cancellationToken).ConfigureAwait(false);
        observations.Add($"ObserveAsync returned {metrics.Count} metric(s) from source '{_observationSource.SourceId}'.");

        // 2. Cluster failures / detect anomalies（V2 简化：只记录存在的 metric）
        var presentKeys = FallbackObservationKeys.Where(k => metrics.ContainsKey(k)).ToList();
        if (presentKeys.Count > 0)
        {
            observations.Add($"Present metrics: {string.Join(", ", presentKeys)}.");
        }
        else
        {
            observations.Add("No known metric keys present in observation; hypothesis formation will rely on template defaults.");
        }

        // 3. Form hypothesis（基于 TargetComponent 模板）
        var template = HypothesisTemplates.TryGet(request.TargetComponent);
        if (template is null)
        {
            hypothesisTrail.Add($"No hypothesis template registered for target component {request.TargetComponent}; returning empty proposal.");
            return new AgentDiagnosticResult(
                proposal: null,
                summary: $"No actionable hypothesis formed for {request.TargetComponent}.",
                observations: observations,
                hypothesisTrail: hypothesisTrail);
        }
        hypothesisTrail.Add($"Selected template '{template.Title}' for {request.TargetComponent}.");

        // 4. Generate evidence：用 observation 中的 metric 值作为 baseline，experiment 值 = baseline + ExpectedGain.EstimatedDelta（前瞻值）
        var capturedAt = _timeProvider.GetUtcNow();
        var evidence = new List<ExperimentEvidence>();
        foreach (var gain in template.ExpectedGains)
        {
            if (!metrics.TryGetValue(gain.MetricName, out var baselineValue))
            {
                // 模板默认 baseline：使用 0.0 占位（表示当前未见此 metric，依赖后续 RefineProposalAsync 提供真实 baseline）
                baselineValue = 0.0;
                observations.Add($"Metric '{gain.MetricName}' not present in observation; using 0.0 as placeholder baseline.");
            }
            var experimentValue = baselineValue + gain.EstimatedDelta;
            evidence.Add(new ExperimentEvidence(
                source: _observationSource.SourceId,
                metricName: gain.MetricName,
                baselineValue: baselineValue,
                experimentValue: experimentValue,
                sampleCount: 1,
                capturedAt: capturedAt,
                notes: $"Projected from hypothesis template; delta target = {gain.EstimatedDelta}."));
        }
        hypothesisTrail.Add($"Generated {evidence.Count} evidence entry/entries from observation metrics.");

        // 5. Generate proposal（status = Validated；具备 RollbackConditions 但仍需外部实验证据才能进入 ExperimentReady）
        var proposal = new OptimizationProposal
        {
            ProposalId = BuildProposalId(request, capturedAt),
            Version = OptimizationProposalVersion.Initial,
            Title = template.Title,
            Hypothesis = template.Hypothesis,
            TargetComponent = request.TargetComponent,
            Status = OptimizationProposalStatus.Validated,
            Evidence = evidence,
            ExpectedGains = template.ExpectedGains,
            Risks = template.Risks,
            RollbackConditions = template.RollbackConditions,
            ExperimentConfigJson = BuildExperimentConfigJson(request, template),
            RollbackPlan = $"Revert {request.TargetComponent} to baseline policy; conditions: {string.Join("; ", template.RollbackConditions.Select(c => c.Description))}.",
            GeneratedAt = capturedAt,
            AgentIdentifier = AgentIdentifier
        };

        var summary = $"Formed hypothesis '{template.Title}' for {request.TargetComponent}; " +
                      $"status=Validated, awaiting external experiment evidence to advance to ExperimentReady.";
        return new AgentDiagnosticResult(
            proposal: proposal,
            summary: summary,
            observations: observations,
            hypothesisTrail: hypothesisTrail);
    }

    /// <inheritdoc />
    public Task<OptimizationProposal> RefineProposalAsync(
        OptimizationProposal existing,
        IReadOnlyList<ExperimentEvidence> additionalEvidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(additionalEvidence);
        cancellationToken.ThrowIfCancellationRequested();

        // 已 Rejected 的 proposal 不再变更状态（语义不可逆）
        if (existing.Status == OptimizationProposalStatus.Rejected)
        {
            return Task.FromResult(existing with
            {
                Version = existing.Version.BumpMinor(),
                Evidence = MergeEvidence(existing.Evidence, additionalEvidence),
                GeneratedAt = _timeProvider.GetUtcNow()
            });
        }

        // 硬边界：Agent 不允许处理 pipeline 状态（Shadow/ScopedCanary/Promoted/RolledBack）的 proposal
        if (IsPipelineStageStatus(existing.Status))
        {
            throw new InvalidOperationException(
                $"DefaultContextEvolutionAgent.RefineProposalAsync received proposal in pipeline stage {existing.Status}; " +
                "Agent only handles Draft/Validated/ExperimentReady/Rejected. Pipeline-managed proposals must be advanced via IGuardedOptimizationPipeline.");
        }

        var mergedEvidence = MergeEvidence(existing.Evidence, additionalEvidence);
        var newVersion = existing.Version.BumpMinor();

        // 用 additional evidence 与 ExpectedGains 方向对比决定是否推进
        var advanceToReady = false;
        var rejectDueToContradiction = false;
        foreach (var evidence in additionalEvidence)
        {
            var matchedGain = existing.ExpectedGains.FirstOrDefault(g => g.MetricName == evidence.MetricName);
            if (matchedGain is null)
            {
                // 未匹配的 metric：忽略（视为外部补充信号，不影响 Status）
                continue;
            }
            var expectedDirection = Math.Sign(matchedGain.EstimatedDelta);
            var actualDirection = Math.Sign(evidence.Delta);
            if (expectedDirection == 0 || actualDirection == 0)
            {
                // 期望或实际 delta 为 0：视为无信号
                continue;
            }
            if (actualDirection != expectedDirection)
            {
                // evidence 方向与假设相反 → 驳回
                rejectDueToContradiction = true;
                break;
            }
            // 至少有一条 evidence 方向匹配
            advanceToReady = true;
        }

        var newStatus = existing.Status;
        if (rejectDueToContradiction)
        {
            newStatus = OptimizationProposalStatus.Rejected;
        }
        else if (advanceToReady && existing.RollbackConditions.Count >= 1)
        {
            // 推进到 ExperimentReady 需要至少 1 条 RollbackCondition（来自契约硬约束）
            newStatus = OptimizationProposalStatus.ExperimentReady;
        }

        var refined = existing with
        {
            Version = newVersion,
            Status = newStatus,
            Evidence = mergedEvidence,
            GeneratedAt = _timeProvider.GetUtcNow(),
            AgentIdentifier = AgentIdentifier
        };
        return Task.FromResult(refined);
    }

    private static bool IsPipelineStageStatus(OptimizationProposalStatus status) => status switch
    {
        OptimizationProposalStatus.Shadow =>
            true,
        OptimizationProposalStatus.ScopedCanary => true,
        OptimizationProposalStatus.Promoted => true,
        OptimizationProposalStatus.RolledBack => true,
        _ => false
    };

    private static IReadOnlyList<ExperimentEvidence> MergeEvidence(
        IReadOnlyList<ExperimentEvidence> existing,
        IReadOnlyList<ExperimentEvidence> additional)
    {
        if (additional.Count == 0)
        {
            return existing;
        }
        var merged = new List<ExperimentEvidence>(existing);
        merged.AddRange(additional);
        return merged;
    }

    private static string BuildProposalId(AgentDiagnosticRequest request, DateTimeOffset capturedAt)
    {
        var componentTag = request.TargetComponent.ToString().ToLowerInvariant();
        var timestampTag = capturedAt.UtcDateTime.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var collectionTag = request.CollectionId is null ? "all" : request.CollectionId;
        return $"prop-{componentTag}-{request.WorkspaceId}-{collectionTag}-{timestampTag}";
    }

    private static string BuildExperimentConfigJson(AgentDiagnosticRequest request, HypothesisTemplate template)
    {
        var evidenceNames = string.Join(",", template.ExpectedGains.Select(g => $"\"{g.MetricName}\""));
        var rollbackNames = string.Join(",", template.RollbackConditions.Select(c => $"\"{c.MetricName}\""));
        var hintsJson = request.Hints.Count == 0
            ? "null"
            : "{" + string.Join(",", request.Hints.Select(kv => $"\"{kv.Key}\":\"{kv.Value}\"")) + "}";
        return $"{{\"targetComponent\":\"{request.TargetComponent}\"," +
               $"\"workspaceId\":\"{request.WorkspaceId}\"," +
               $"\"collectionId\":{(request.CollectionId is null ? "null" : $"\"{request.CollectionId}\"")}," +
               $"\"evidenceMetrics\":[{evidenceNames}]," +
               $"\"rollbackMetrics\":[{rollbackNames}]," +
               $"\"hints\":{hintsJson}}}";
    }
}
