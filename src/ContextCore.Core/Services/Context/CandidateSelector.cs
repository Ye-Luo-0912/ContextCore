using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;

namespace ContextCore.Core;

/// <summary>
/// 候选选择阶段编排器：按原顺序串行调用 3 个 <see cref="SectionCollectorBase"/> 子类，
/// 保持与 <see cref="BasicContextPackageBuilder.BuildWithPolicyAsync"/> 字节级一致的变异顺序。
/// <list type="bullet">
/// <item><see cref="ShortTermSignalCollector"/>：current_task、recent filter + anchors + retrievalPlan、hard_constraints、working_memory、historical_context。</item>
/// <item><see cref="RecallSectionCollector"/>：global_context、recent_context、stable_memory、soft_constraints、merged constraints。</item>
/// <item><see cref="ExpansionDiagnosticsCollector"/>：related_context（graph 扩展）、evidence、excluded、uncertainties。</item>
/// </list>
/// 跨 collector 共享的中间产物（anchors / retrievalPlan / workingMemory / includedRecent / excludedRecent）
/// 通过 <see cref="SelectionContext"/> 传递；可变 accumulators 通过 <see cref="SelectionState"/> 传递。
/// </summary>
internal sealed class CandidateSelector
{
    private readonly ShortTermSignalCollector _shortTermCollector;
    private readonly RecallSectionCollector _recallCollector;
    private readonly ExpansionDiagnosticsCollector _expansionCollector;

    internal CandidateSelector(
        SectionAssembler assembler,
        PackageTraceRecorder traceRecorder,
        GraphExpansionCoordinator graphExpansionCoordinator,
        Func<string?, TokenEstimationContext, int> estimateTokens)
    {
        _shortTermCollector = new ShortTermSignalCollector(assembler, traceRecorder, estimateTokens);
        _recallCollector = new RecallSectionCollector(assembler, traceRecorder, estimateTokens);
        _expansionCollector = new ExpansionDiagnosticsCollector(assembler, traceRecorder, estimateTokens, graphExpansionCoordinator);
    }

    /// <summary>
    /// 选择阶段：消费 <see cref="PackageInputs"/>，按原顺序串行调用 3 个 collector，
    /// 返回 <see cref="SelectionResult"/>（含 sections、accumulators、anchors、retrievalPlan、uncertainties）。
    /// </summary>
    internal async Task<SelectionResult> SelectCandidatesAsync(
        PackageInputs inputs,
        ResolvedPackageOptions options,
        CancellationToken cancellationToken)
    {
        var state = new SelectionState();
        var ctx = new SelectionContext();

        _shortTermCollector.Collect(state, ctx, inputs, options);
        _recallCollector.Collect(state, ctx, inputs, options);
        var uncertainties = await _expansionCollector
            .CollectAsync(state, ctx, inputs, options, cancellationToken)
            .ConfigureAwait(false);

        return new SelectionResult(
            Sections: state.Sections,
            SourceRefs: state.SourceRefs,
            EstimatedTokens: state.EstimatedTokens,
            SelectedItems: state.SelectedItems,
            DroppedItems: state.DroppedItems,
            Anchors: ctx.Anchors,
            RetrievalPlan: ctx.RetrievalPlan,
            ItemReferences: state.ItemReferences,
            Uncertainties: uncertainties,
            ReadPlan: inputs.ReadPlan);
    }
}

/// <summary>
/// 选择阶段共享的可变状态：所有 section 装配过程中读写的 accumulators 集中在此，
/// 避免向 <see cref="SectionCollectorBase.CommitSection"/> 传递十几个 ref 参数。
/// </summary>
internal sealed class SelectionState
{
    internal List<ContextPackageSection> Sections { get; } = new();
    internal HashSet<string> SourceRefs { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal int EstimatedTokens;
    internal HashSet<string> SelectedSourceIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal List<ContextPackageDecision> SelectedItems { get; } = new();
    internal List<DroppedContextItem> DroppedItems { get; } = new();
    internal HashSet<string> AddedConstraintIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal List<ContextRelation> LowConfidenceRelations { get; } = new();
    internal HashSet<string> GlobalSelectedIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, ContextPackageDecision> PrimaryDecisions { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal List<ContextPackageItemReference> ItemReferences { get; } = new();
}

/// <summary>
/// 轻量 section 草稿：描述单个 section 装配所需的全部输入（名称、优先级、segment 列表、
/// 候选列表、引用、预算类别），由 <see cref="SectionCollectorBase.CommitSection"/> 消费。
/// Segments 携带候选 ID 与格式化文本，Packer 按 segment 粒度截断并精确归属。
/// 当所有候选已被前序 section 选入时，Segments 为空，使用 FallbackContent 展示提示信息。
/// </summary>
internal sealed class SectionDraft
{
    internal required string Name { get; init; }
    internal int DefaultPriority { get; init; }
    internal IReadOnlyList<CandidateSegment> Segments { get; init; } = Array.Empty<CandidateSegment>();
    internal string? FallbackContent { get; init; }
    internal IReadOnlyList<PackageTraceCandidate> Candidates { get; init; } = Array.Empty<PackageTraceCandidate>();
    internal IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();
    internal IReadOnlyList<string> ItemRefs { get; init; } = Array.Empty<string>();
    internal SectionBudgetKind BudgetKind { get; init; } = SectionBudgetKind.Normal;
}

internal enum SectionBudgetKind
{
    Normal,
    Historical,
    Diagnostics
}

/// <summary>
/// 选择阶段产出：已装配的 sections、accumulators（selected/dropped/itemReferences）、
/// anchors、retrievalPlan 以及 uncertainties。供 <see cref="ResultProjector"/> 构建最终结果。
/// </summary>
internal sealed record SelectionResult(
    List<ContextPackageSection> Sections,
    HashSet<string> SourceRefs,
    int EstimatedTokens,
    List<ContextPackageDecision> SelectedItems,
    List<DroppedContextItem> DroppedItems,
    IReadOnlyList<ContextAnchor> Anchors,
    RetrievalPlan? RetrievalPlan,
    List<ContextPackageItemReference> ItemReferences,
    IReadOnlyList<ContextPackageUncertainty> Uncertainties,
    PackageReadPlan? ReadPlan);
