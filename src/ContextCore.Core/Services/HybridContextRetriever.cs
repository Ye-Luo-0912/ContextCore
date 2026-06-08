using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>第一版混合检索器：规则召回、向量召回、关系扩展、去重和预算打包。</summary>
public sealed class HybridContextRetriever : IContextRetriever
{
    private readonly IRetrievalChannelExecutor _contextRecallChannelExecutor;
    private readonly IRetrievalChannelExecutor _mandatoryRecallChannelExecutor;
    private readonly IRetrievalChannelExecutor _memoryRecallChannelExecutor;
    private readonly IRetrievalChannelExecutor _relationRecallChannelExecutor;
    private readonly IContextAttentionScorer? _attentionScorer;
    private readonly AttentionProfileExperimentRunner? _attentionProfileExperimentRunner;
    private readonly RetrievalAttentionRerankOptions _attentionRerankOptions;
    private readonly GuardedAttentionRerankPolicy _attentionRerankPolicy;
    private readonly RetrievalPlanningOptions _planningOptions;
    private readonly RetrievalPlanProposalService? _planningProposalService;
    private readonly ShadowRetrievalPlanExecutor? _planningShadowExecutor;
    private readonly LifecycleAwareRankerShadowOptions _rankerShadowOptions;
    private readonly LifecycleAwareRankerTraceBuilder? _rankerShadowTraceBuilder;
    private readonly RetrievalResultAssembler _resultAssembler;
    private readonly IRetrievalTraceStore? _traceStore;
    private readonly RetrievalTraceAssembler _traceAssembler;
    private readonly IRetrievalChannelExecutor _vectorRecallChannelExecutor;

    // 自动计划器（无状态，可安全静态共享）
    private static readonly RetrievalPlanner _autoPlanner = new();

    public HybridContextRetriever(
        IContextStore contextStore,
        IMemoryStore? memoryStore = null,
        IRelationStore? relationStore = null,
        IEmbeddingProvider? embeddingProvider = null,
        IVectorStore? vectorStore = null,
        IRetrievalTraceStore? traceStore = null,
        IContextAttentionScorer? attentionScorer = null,
        IEnumerable<ContextAttentionProfile>? attentionProfileExperiments = null,
        IContextLearningStore? attentionLearningStore = null,
        RetrievalAttentionRerankOptions? attentionRerankOptions = null,
        RetrievalPlanningOptions? planningOptions = null,
        RetrievalPlanProposalService? planningProposalService = null,
        ShadowRetrievalPlanExecutor? planningShadowExecutor = null,
        LifecycleAwareRankerShadowOptions? rankerShadowOptions = null,
        LifecycleAwareRankerTraceBuilder? rankerShadowTraceBuilder = null)
    {
        _traceStore = traceStore;
        _attentionScorer = attentionScorer;
        _attentionProfileExperimentRunner = attentionScorer is null
            ? null
            : new AttentionProfileExperimentRunner(attentionProfileExperiments, attentionLearningStore);
        _attentionRerankOptions = attentionRerankOptions ?? new RetrievalAttentionRerankOptions();
        _attentionRerankPolicy = new GuardedAttentionRerankPolicy(_attentionRerankOptions);
        _planningOptions = planningOptions ?? new RetrievalPlanningOptions();
        _planningProposalService = planningProposalService;
        _planningShadowExecutor = planningShadowExecutor;
        _rankerShadowOptions = rankerShadowOptions ?? new LifecycleAwareRankerShadowOptions();
        _rankerShadowTraceBuilder = rankerShadowTraceBuilder;
        var contextObjectResolver = new DefaultContextObjectResolver(contextStore, memoryStore);
        var relationFrontierBuilder = new RelationFrontierBuilder();
        var relationExpansionService = relationStore is null
            ? null
            : new RelationExpansionService(relationStore, contextObjectResolver);
        _mandatoryRecallChannelExecutor = new MandatoryRecallChannelExecutor(contextStore, memoryStore);
        _contextRecallChannelExecutor = new ContextRecallChannelExecutor(contextStore);
        _memoryRecallChannelExecutor = new MemoryRecallChannelExecutor(memoryStore);
        _vectorRecallChannelExecutor = new VectorRecallChannelExecutor(contextStore, memoryStore, embeddingProvider, vectorStore);
        _relationRecallChannelExecutor = new RelationRecallChannelExecutor(relationFrontierBuilder, relationExpansionService);
        _traceAssembler = new RetrievalTraceAssembler();
        _resultAssembler = new RetrievalResultAssembler();
    }

    public async Task<ContextRetrievalResult> RetrieveAsync(
        ContextRetrievalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var _sw = System.Diagnostics.Stopwatch.StartNew();
        var operationId = string.IsNullOrWhiteSpace(request.OperationId)
            ? Guid.NewGuid().ToString("N")
            : request.OperationId;
        var candidates = new RetrievalCandidateAccumulator();
        var relOnlyCandidates = new RetrievalCandidateAccumulator();  // 独立关系扩展通道
        var stages = new List<ContextRetrievalStageTrace>();
        var metadata = new Dictionary<string, string>();

        // 短期锚定召回计划：外部传入则直接使用，否则从请求元数据自动派生（plan 始终非 null）
        var effectivePlan = request.Plan ?? _autoPlanner.Plan(request);

        var mandatoryContext = RetrievalChannelContext.Create(request, effectivePlan, metadata);
        var mandatoryResult = await _mandatoryRecallChannelExecutor.ExecuteAsync(mandatoryContext, cancellationToken).ConfigureAwait(false);
        candidates.AddOrMerge(mandatoryResult);
        stages.Add(CreateStageTrace(mandatoryResult));
        if (request.IncludeKeywordRecall)
        {
            var keywordContext = RetrievalChannelContext.Create(request, effectivePlan, metadata);
            var keywordResult = await _contextRecallChannelExecutor.ExecuteAsync(keywordContext, cancellationToken).ConfigureAwait(false);
            candidates.AddOrMerge(keywordResult);
            stages.Add(CreateStageTrace(keywordResult));

            var memoryContext = RetrievalChannelContext.Create(request, effectivePlan, metadata);
            var memoryResult = await _memoryRecallChannelExecutor.ExecuteAsync(memoryContext, cancellationToken).ConfigureAwait(false);
            candidates.AddOrMerge(memoryResult);
            stages.Add(CreateStageTrace(memoryResult));
        }

        if (request.IncludeVectorRecall)
        {
            var vectorContext = RetrievalChannelContext.Create(request, effectivePlan, metadata);
            var vectorResult = await _vectorRecallChannelExecutor.ExecuteAsync(vectorContext, cancellationToken).ConfigureAwait(false);
            candidates.AddOrMerge(vectorResult);
            stages.Add(CreateStageTrace(vectorResult));
        }

        if (request.IncludeRelationExpansion && request.RelationExpansionDepth > 0)
        {
            var relationContext = RetrievalChannelContext.Create(
                request,
                effectivePlan,
                metadata,
                candidates.ToCandidates(includeContent: false));
            var relationResult = await _relationRecallChannelExecutor.ExecuteAsync(relationContext, cancellationToken).ConfigureAwait(false);
            foreach (var candidate in relationResult.Candidates)
            {
                if (candidates.Contains(candidate.Kind, candidate.SourceId))
                {
                    candidates.AddOrMerge(candidate);
                }
                else
                {
                    relOnlyCandidates.AddOrMerge(candidate);
                }
            }

            stages.Add(CreateStageTrace(relationResult));
        }

        // 合并主通道与关系扩展通道：为关系独有条目预留保证槽位后，全量按分数排序
        var ranked = RetrievalPackingPolicy.BuildRankedCandidates(
            request,
            candidates.ToCandidates(request.IncludeContent),
            relOnlyCandidates.ToCandidates(request.IncludeContent));

        var attentionScores = _attentionScorer is null
            ? Array.Empty<ContextAttentionScore>()
            : await _attentionScorer.ScoreAsync(request, ranked, cancellationToken).ConfigureAwait(false);
        var packed = RetrievalPackingPolicy.Pack(request, ranked);
        if (attentionScores.Count > 0)
        {
            metadata["attentionShadowMode"] = "true";
            metadata["attentionProfileId"] = attentionScores[0].ProfileId;
            metadata["attentionPolicyVersion"] = attentionScores[0].PolicyVersion;
        }
        var attentionShadowReport = AttentionShadowReportBuilder.Build(
            operationId,
            request,
            ranked,
            packed,
            attentionScores);
        var attentionProfileComparison = _attentionProfileExperimentRunner is null
            ? new AttentionProfileExperimentReport { OperationId = operationId }
            : await _attentionProfileExperimentRunner.RunAsync(
                operationId,
                request,
                ranked,
                packed,
                cancellationToken).ConfigureAwait(false);
        var rerankScores = ResolveRerankAttentionScores(
            attentionProfileComparison,
            attentionScores,
            _attentionRerankOptions.EffectiveProfile);
        var rerankResult = _attentionRerankPolicy.Apply(
            operationId,
            request,
            packed,
            rerankScores);
        var effectivePacked = rerankResult.PackingResult;
        if (attentionShadowReport.Ranks.Count > 0)
        {
            metadata["attentionShadowCandidateCount"] = attentionShadowReport.CandidateCount.ToString();
            metadata["attentionShadowWouldChangeSelectedSet"] = attentionShadowReport.WouldChangeSelectedSet.ToString().ToLowerInvariant();
            metadata["attentionShadowSelectedSetChangeRatio"] = attentionShadowReport.SelectedSetChangeRatio.ToString("0.###");
            metadata["attentionProfileComparisonCount"] = attentionProfileComparison.Profiles.Count.ToString();
            if (attentionShadowReport.MustNotHitPromotedCount > 0)
            {
                metadata["attentionShadowMustNotHitPromotedCount"] = attentionShadowReport.MustNotHitPromotedCount.ToString();
            }
        }
        metadata["attentionRerankEnabled"] = rerankResult.Report.Enabled.ToString().ToLowerInvariant();
        metadata["attentionRerankMode"] = rerankResult.Report.AttentionRerankMode;
        metadata["attentionProfile"] = rerankResult.Report.AttentionProfile;
        metadata["attentionRerankProfileId"] = rerankResult.Report.ProfileId;
        metadata["attentionRerankApplied"] = rerankResult.Report.AttentionApplied.ToString().ToLowerInvariant();
        metadata["attentionRerankBlocked"] = rerankResult.Report.Blocked.ToString().ToLowerInvariant();
        metadata["selectedSetPreserved"] = rerankResult.Report.SelectedSetPreserved.ToString().ToLowerInvariant();
        metadata["orderChangedCount"] = rerankResult.Report.OrderChangedCount.ToString();
        metadata["oldOrder"] = string.Join(",", rerankResult.Report.OldOrder);
        metadata["newOrder"] = string.Join(",", rerankResult.Report.NewOrder);
        metadata["guardViolation"] = rerankResult.Report.GuardViolation;
        if (!string.IsNullOrWhiteSpace(rerankResult.Report.SkippedReason))
        {
            metadata["attentionRerankSkippedReason"] = rerankResult.Report.SkippedReason;
        }

        if (!string.IsNullOrWhiteSpace(rerankResult.Report.BlockedReason))
        {
            metadata["attentionRerankBlockedReason"] = rerankResult.Report.BlockedReason;
        }

        var planningResult = await ApplyPlanningAsync(
                operationId,
                request,
                effectivePacked,
                ranked,
                metadata,
                cancellationToken)
            .ConfigureAwait(false);
        effectivePacked = planningResult.PackingResult;
        var traceCandidates = planningResult.TraceCandidates;
        var rankerShadowTrace = BuildRankerShadowTrace(
            request,
            effectivePacked,
            traceCandidates,
            metadata);

        var trace = _traceAssembler.Assemble(
            operationId,
            request,
            stages,
            traceCandidates,
            effectivePacked,
            attentionScores,
            attentionShadowReport,
            attentionProfileComparison,
            metadata,
            rerankResult.Report,
            rankerShadowTrace);

        if (_traceStore is not null)
        {
            await _traceStore.SaveAsync(trace, cancellationToken).ConfigureAwait(false);
        }

        var result = _resultAssembler.Assemble(operationId, request, effectivePacked, trace, metadata);
        CoreMetrics.RetrievalDuration.Record(_sw.Elapsed.TotalMilliseconds);
        return result;
    }

    private static IReadOnlyList<ContextAttentionScore> ResolveRerankAttentionScores(
        AttentionProfileExperimentReport comparison,
        IReadOnlyList<ContextAttentionScore> fallbackScores,
        string profileId)
    {
        var profile = comparison.Profiles.FirstOrDefault(item =>
            string.Equals(item.ProfileId, profileId, StringComparison.OrdinalIgnoreCase));
        return profile?.AttentionScores.Count > 0
            ? profile.AttentionScores
            : fallbackScores;
    }

    private LifecycleAwareRankerShadowTrace BuildRankerShadowTrace(
        ContextRetrievalRequest request,
        RetrievalPackingResult packingResult,
        IReadOnlyList<ContextRetrievalCandidate> traceCandidates,
        Dictionary<string, string> metadata)
    {
        var profile = string.IsNullOrWhiteSpace(_rankerShadowOptions.Profile)
            ? LifecycleAwareRankerShadowScorer.DefaultProfile
            : _rankerShadowOptions.Profile;
        metadata["rankerShadowTraceCollectionEnabled"] = _rankerShadowOptions.TraceCollectionEnabled
            .ToString()
            .ToLowerInvariant();
        metadata["rankerShadowProfile"] = profile;
        metadata["rankerShadowMaxCandidatesPerTrace"] = (_rankerShadowOptions.MaxCandidatesPerTrace > 0
            ? _rankerShadowOptions.MaxCandidatesPerTrace
            : 50).ToString();

        if (!_rankerShadowOptions.TraceCollectionEnabled || _rankerShadowTraceBuilder is null)
        {
            metadata["rankerShadowCandidateScoreCount"] = "0";
            return new LifecycleAwareRankerShadowTrace
            {
                RankerShadowEnabled = false,
                RankerShadowProfile = profile
            };
        }

        var trace = _rankerShadowTraceBuilder.Build(
            packingResult.SelectedCandidates,
            packingResult.DroppedDecisions,
            traceCandidates,
            new LifecycleAwareRankerShadowOptions
            {
                Enabled = true,
                DebugEndpointEnabled = _rankerShadowOptions.DebugEndpointEnabled,
                TraceCollectionEnabled = true,
                MaxCandidatesPerTrace = _rankerShadowOptions.MaxCandidatesPerTrace,
                Profile = profile
            });
        metadata["rankerShadowEnabled"] = trace.RankerShadowEnabled.ToString().ToLowerInvariant();
        metadata["rankerShadowCandidateScoreCount"] = trace.CandidateShadowScores.Count.ToString();
        metadata["rankerShadowDeprecatedDemotionCount"] = trace.DeprecatedDemotions.Count.ToString();
        metadata["rankerShadowVersionConflictFixCount"] = trace.VersionConflictFixes.Count.ToString();
        metadata["rankerShadowMustHitDemotionCount"] = trace.MustHitDemotions.Count.ToString();
        metadata["rankerShadowMustNotHitPromotionCount"] = trace.MustNotHitPromotions.Count.ToString();
        metadata["rankerShadowFormalOutputChanged"] = "false";
        metadata["rankerShadowSelectedSetChanged"] = "false";
        metadata["rankerShadowPackageSectionsChanged"] = "false";
        metadata["rankerShadowQueryMode"] = ResolvePlanningMode(request) ?? string.Empty;
        return trace;
    }

    private static ContextRetrievalStageTrace CreateStageTrace(RetrievalChannelResult result)
    {
        return new ContextRetrievalStageTrace
        {
            Name = result.StageName,
            CandidateCount = result.StageCandidateCount,
            Metadata = result.Metadata
        };
    }

    private async Task<PlanningExecutionResult> ApplyPlanningAsync(
        string operationId,
        ContextRetrievalRequest request,
        RetrievalPackingResult legacyPacking,
        IReadOnlyList<ContextRetrievalCandidate> legacyRanked,
        Dictionary<string, string> metadata,
        CancellationToken cancellationToken)
    {
        var mode = _planningOptions.EffectiveMode;
        var fallbackReason = string.Empty;
        metadata["planningMode"] = mode;
        metadata["planningApplyMode"] = _planningOptions.EffectiveApplyMode;
        metadata["planningExecutionStatus"] = "Legacy";
        metadata["planningIntent"] = string.Empty;
        metadata["planningProposalSummary"] = string.Empty;
        metadata["planningOptInMatched"] = "false";
        metadata["planningFallbackUsed"] = "false";
        metadata["planningFallbackReason"] = string.Empty;
        metadata["planningLegacySelected"] = JoinIds(legacyPacking.SelectedCandidates);
        metadata["planningProposalSelected"] = string.Empty;
        metadata["planningFinalSelected"] = JoinIds(legacyPacking.SelectedCandidates);
        metadata["planningSafetyChecks"] = "not_evaluated";
        metadata["planningVectorEnabled"] = "false";

        if (_planningOptions.IsOff)
        {
            return new PlanningExecutionResult(legacyPacking, legacyRanked);
        }

        if (_planningProposalService is null || _planningShadowExecutor is null)
        {
            fallbackReason = "planning_services_unavailable";
            metadata["planningFallbackUsed"] = _planningOptions.IsApplyGuarded.ToString().ToLowerInvariant();
            metadata["planningFallbackReason"] = fallbackReason;
            metadata["planningSafetyChecks"] = "servicesAvailable=false";
            return new PlanningExecutionResult(legacyPacking, legacyRanked);
        }

        var proposal = await _planningProposalService.ProposeAsync(
                new ContextPlanningProposalRequest
                {
                    WorkspaceId = request.WorkspaceId,
                    CollectionId = request.CollectionId,
                    SessionId = ResolveSessionId(request),
                    CurrentInput = ResolveCurrentInput(request),
                    Mode = ResolvePlanningMode(request)
                },
                cancellationToken)
            .ConfigureAwait(false);
        metadata["planningIntent"] = proposal.Intent;
        metadata["planningProposalId"] = proposal.OperationId;
        metadata["planningProposalSummary"] = BuildProposalSummary(proposal);

        if (_planningOptions.IsShadow)
        {
            var shadow = await _planningShadowExecutor.ExecuteAsync(proposal, request, cancellationToken)
                .ConfigureAwait(false);
            CopyShadowDiagnostics(metadata, shadow);
            metadata["planningExecutionStatus"] = "Shadow";
            metadata["planningProposalSelected"] = JoinIds(shadow.ShadowSelectedItems);
            metadata["planningFinalSelected"] = JoinIds(legacyPacking.SelectedCandidates);
            metadata["planningSafetyChecks"] = EvaluatePlanningSafety(request, shadow).ToMetadataValue();
            return new PlanningExecutionResult(legacyPacking, MergeTraceCandidates(legacyRanked, shadow.ShadowCandidates));
        }

        if (!_planningOptions.IsApplyGuarded)
        {
            metadata["planningFallbackReason"] = "unsupported_planning_mode";
            return new PlanningExecutionResult(legacyPacking, legacyRanked);
        }

        var optInMatched = IsOptInIntent(proposal.Intent);
        metadata["planningOptInMatched"] = optInMatched.ToString().ToLowerInvariant();
        if (_planningOptions.IsIntentScoped && !optInMatched)
        {
            metadata["planningExecutionStatus"] = "Legacy";
            metadata["planningFallbackReason"] = "intent_not_opted_in";
            metadata["planningSafetyChecks"] = "optInMatched=false";
            return new PlanningExecutionResult(legacyPacking, legacyRanked);
        }

        var guardedShadow = await _planningShadowExecutor.ExecuteAsync(proposal, request, cancellationToken)
            .ConfigureAwait(false);
        CopyShadowDiagnostics(metadata, guardedShadow);
        metadata["planningProposalSelected"] = JoinIds(guardedShadow.ShadowSelectedItems);
        var safety = EvaluatePlanningSafety(request, guardedShadow);
        metadata["planningSafetyChecks"] = safety.ToMetadataValue();

        if (!safety.Passed)
        {
            fallbackReason = safety.FallbackReason;
            metadata["planningExecutionStatus"] = "FallbackUsed";
            metadata["planningFallbackUsed"] = "true";
            metadata["planningFallbackReason"] = fallbackReason;
            metadata["planningFinalSelected"] = JoinIds(legacyPacking.SelectedCandidates);
            return new PlanningExecutionResult(
                legacyPacking,
                MergeTraceCandidates(legacyRanked, guardedShadow.ShadowCandidates));
        }

        var planningPacking = BuildPlanningPackingResult(guardedShadow.ShadowSelectedItems, legacyPacking);
        metadata["planningExecutionStatus"] = "ApplyGuarded";
        metadata["planningFallbackUsed"] = "false";
        metadata["planningFallbackReason"] = string.Empty;
        metadata["planningFinalSelected"] = JoinIds(planningPacking.SelectedCandidates);
        return new PlanningExecutionResult(
            planningPacking,
            MergeTraceCandidates(legacyRanked, guardedShadow.ShadowCandidates));
    }

    private bool IsOptInIntent(string intent)
    {
        if (_planningOptions.OptInIntents.Count == 0)
        {
            return false;
        }

        return _planningOptions.OptInIntents.Any(item =>
            string.Equals(item, intent, StringComparison.OrdinalIgnoreCase));
    }

    private static void CopyShadowDiagnostics(
        Dictionary<string, string> metadata,
        ShadowRetrievalResult shadow)
    {
        foreach (var pair in shadow.Diagnostics)
        {
            metadata[$"planningShadow.{pair.Key}"] = pair.Value;
        }

        if (shadow.Warnings.Count > 0)
        {
            metadata["planningWarnings"] = string.Join(" | ", shadow.Warnings);
        }
    }

    private static PlanningSafetyResult EvaluatePlanningSafety(
        ContextRetrievalRequest request,
        ShadowRetrievalResult shadow)
    {
        var reasons = new List<string>();
        if (ReadBool(shadow.Diagnostics, "fallbackToLegacySafePlan")
            || !ReadBool(shadow.Diagnostics, "validPlan", defaultValue: true))
        {
            reasons.Add("invalid_proposal");
        }

        if (ReadInt(shadow.Diagnostics, "mustNotHitAddedAfterValidation") > 0
            || SelectedMatchesRefs(shadow.ShadowSelectedItems, ParseRefs(request.Metadata, "attention.mustNotHit", "eval.mustNotHit", "mustNotHit")))
        {
            reasons.Add("must_not_hit_violation");
        }

        if (ReadInt(shadow.Diagnostics, "lifecycleViolationAfterValidation") > 0)
        {
            reasons.Add("lifecycle_violation");
        }

        var missingConstraints = FindMissingExpectedConstraints(
            request,
            shadow.ShadowSelectedItems);
        if (missingConstraints.Count > 0)
        {
            reasons.Add($"hard_constraint_missing:{string.Join(",", missingConstraints)}");
        }

        return new PlanningSafetyResult(
            reasons.Count == 0,
            reasons.Count == 0 ? string.Empty : string.Join("|", reasons),
            reasons.ToArray(),
            missingConstraints);
    }

    private static IReadOnlyList<string> FindMissingExpectedConstraints(
        ContextRetrievalRequest request,
        IReadOnlyList<ContextRetrievalCandidate> selectedItems)
    {
        var expected = ParseRefs(
            request.Metadata,
            "eval.expectedConstraints",
            "planning.expectedConstraints",
            "attention.expectedConstraints",
            "expectedConstraints");
        if (expected.Count == 0)
        {
            return Array.Empty<string>();
        }

        return expected
            .Where(expectedConstraint => !selectedItems.Any(item => SatisfiesHardConstraint(item, expectedConstraint)))
            .ToArray();
    }

    private static bool SatisfiesHardConstraint(
        ContextRetrievalCandidate candidate,
        string expected)
    {
        return CandidateContains(candidate, expected) && IsConstraintSection(candidate);
    }

    private static bool CandidateContains(ContextRetrievalCandidate candidate, string expected)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            return true;
        }

        return Contains(candidate.SourceId, expected)
            || Contains(candidate.CandidateId, expected)
            || Contains(candidate.Type, expected)
            || Contains(candidate.Title, expected)
            || Contains(candidate.Content, expected)
            || candidate.Tags.Any(tag => Contains(tag, expected))
            || candidate.SourceRefs.Any(sourceRef => Contains(sourceRef, expected))
            || candidate.Metadata.Any(pair => Contains(pair.Key, expected) || Contains(pair.Value, expected));
    }

    private static bool IsConstraintSection(ContextRetrievalCandidate candidate)
    {
        var section = ReadMetadata(candidate, "section", "sectionName", "planningSection", "constraintSection");
        return section.Equals("constraints", StringComparison.OrdinalIgnoreCase)
            || section.Equals("hard_constraints", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadMetadata(
        ContextRetrievalCandidate candidate,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (candidate.Metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static bool SelectedMatchesRefs(
        IReadOnlyList<ContextRetrievalCandidate> selectedItems,
        IReadOnlyList<string> refs)
    {
        return refs.Count > 0 && selectedItems.Any(item =>
            refs.Any(refId =>
                string.Equals(item.SourceId, refId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.CandidateId, refId, StringComparison.OrdinalIgnoreCase)
                || item.SourceRefs.Contains(refId, StringComparer.OrdinalIgnoreCase)
                || item.Tags.Contains(refId, StringComparer.OrdinalIgnoreCase)));
    }

    private static RetrievalPackingResult BuildPlanningPackingResult(
        IReadOnlyList<ContextRetrievalCandidate> selectedItems,
        RetrievalPackingResult legacyPacking)
    {
        var selectedIds = selectedItems
            .Select(item => item.CandidateId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedDecisions = selectedItems
            .Select(item => ToDecision(item, "planning ApplyGuarded selected"))
            .ToArray();
        var planningDropped = legacyPacking.SelectedCandidates
            .Where(item => !selectedIds.Contains(item.CandidateId))
            .Select(item => ToDecision(item, "planning proposal replaced legacy item"))
            .Concat(legacyPacking.DroppedDecisions)
            .ToArray();

        return new RetrievalPackingResult(selectedItems, selectedDecisions, planningDropped);
    }

    private static ContextRetrievalDecision ToDecision(
        ContextRetrievalCandidate candidate,
        string reason)
    {
        var metadata = new Dictionary<string, string>(candidate.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["planningApplyGuarded"] = "true"
        };
        return new ContextRetrievalDecision
        {
            CandidateId = candidate.CandidateId,
            SourceId = candidate.SourceId,
            Kind = candidate.Kind,
            Type = candidate.Type,
            Reason = reason,
            Score = candidate.Score,
            EstimatedTokens = candidate.EstimatedTokens,
            Metadata = metadata
        };
    }

    private static IReadOnlyList<ContextRetrievalCandidate> MergeTraceCandidates(
        IReadOnlyList<ContextRetrievalCandidate> legacyRanked,
        IReadOnlyList<ContextRetrievalCandidate> shadowCandidates)
    {
        if (shadowCandidates.Count == 0)
        {
            return legacyRanked;
        }

        return legacyRanked
            .Concat(shadowCandidates)
            .GroupBy(item => item.CandidateId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static string BuildProposalSummary(RetrievalPlanProposal proposal)
    {
        return string.Join(
            ';',
            $"intent={proposal.Intent}",
            $"mode={proposal.Mode}",
            $"keywordTopK={proposal.KeywordTopK}",
            $"memoryTopK={proposal.MemoryTopK}",
            $"relationTopK={proposal.RelationTopK}",
            "vectorTopK=0",
            $"finalTopK={proposal.FinalTopK}",
            $"useVector={proposal.UseVector.ToString().ToLowerInvariant()}");
    }

    private static string ResolveSessionId(ContextRetrievalRequest request)
    {
        return request.Metadata.TryGetValue("sessionId", out var sessionId)
            ? sessionId
            : request.Metadata.TryGetValue("session", out var session)
                ? session
                : string.Empty;
    }

    private static string ResolveCurrentInput(ContextRetrievalRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.QueryText))
        {
            return request.QueryText;
        }

        if (!string.IsNullOrWhiteSpace(request.RewrittenQueryText))
        {
            return request.RewrittenQueryText;
        }

        return string.Join(" ", request.Refs.Concat(request.RequiredIds));
    }

    private static string? ResolvePlanningMode(ContextRetrievalRequest request)
    {
        if (request.Metadata.TryGetValue("planning.mode", out var mode)
            && !string.IsNullOrWhiteSpace(mode))
        {
            return mode;
        }

        if (request.Metadata.TryGetValue("mode", out var fallbackMode)
            && !string.IsNullOrWhiteSpace(fallbackMode))
        {
            return fallbackMode;
        }

        return null;
    }

    private static IReadOnlyList<string> ParseRefs(
        IReadOnlyDictionary<string, string> metadata,
        params string[] keys)
    {
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            if (!metadata.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            foreach (var part in value.Split([',', ';', '|', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = part.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    refs.Add(trimmed);
                }
            }
        }

        return refs.ToArray();
    }

    private static bool ReadBool(
        IReadOnlyDictionary<string, string> metadata,
        string key,
        bool defaultValue = false)
    {
        return metadata.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed)
            ? parsed
            : defaultValue;
    }

    private static int ReadInt(
        IReadOnlyDictionary<string, string> metadata,
        string key)
    {
        return metadata.TryGetValue(key, out var value) && int.TryParse(value, out var parsed)
            ? parsed
            : 0;
    }

    private static string JoinIds(IReadOnlyList<ContextRetrievalCandidate> items)
    {
        return string.Join(",", items.Select(item => item.SourceId));
    }

    private static bool Contains(string? value, string expected)
    {
        return !string.IsNullOrWhiteSpace(value)
            && !string.IsNullOrWhiteSpace(expected)
            && value.Contains(expected, StringComparison.OrdinalIgnoreCase);
    }

}

internal sealed record PlanningExecutionResult(
    RetrievalPackingResult PackingResult,
    IReadOnlyList<ContextRetrievalCandidate> TraceCandidates);

internal sealed record PlanningSafetyResult(
    bool Passed,
    string FallbackReason,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> MissingConstraints)
{
    public string ToMetadataValue()
    {
        return string.Join(
            ';',
            $"passed={Passed.ToString().ToLowerInvariant()}",
            $"fallbackReason={FallbackReason}",
            $"reasons={string.Join("|", Reasons)}",
            $"missingConstraints={string.Join(",", MissingConstraints)}");
    }
}
