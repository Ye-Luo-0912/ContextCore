using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.Attention;
using ContextCore.Core.Services.Graph;

namespace ContextCore.Core.Services.Retrieval;

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
    private readonly LifecycleAwareRankerShadowOptions _rankerShadowOptions;
    private readonly LifecycleAwareRankerTraceBuilder? _rankerShadowTraceBuilder;
    private readonly GraphExpansionShadowOptions _graphExpansionShadowOptions;
    private readonly GraphExpansionShadowTraceBuilder? _graphExpansionShadowTraceBuilder;
    private readonly RetrievalResultAssembler _resultAssembler;
    private readonly IRetrievalTraceStore? _traceStore;
    private readonly IDecisionTraceStore? _decisionTraceStore;
    private readonly RetrievalTraceAssembler _traceAssembler;
    private readonly IRetrievalChannelExecutor _vectorRecallChannelExecutor;

    // 自动计划器（无状态，可安全静态共享）
    private static readonly RetrievalPlanner AutoPlanner = new();

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
        LifecycleAwareRankerShadowOptions? rankerShadowOptions = null,
        LifecycleAwareRankerTraceBuilder? rankerShadowTraceBuilder = null,
        GraphExpansionShadowOptions? graphExpansionShadowOptions = null,
        GraphExpansionShadowTraceBuilder? graphExpansionShadowTraceBuilder = null,
        IDecisionTraceStore? decisionTraceStore = null)
    {
        _traceStore = traceStore;
        _decisionTraceStore = decisionTraceStore;
        _attentionScorer = attentionScorer;
        _attentionProfileExperimentRunner = attentionScorer is null
            ? null
            : new AttentionProfileExperimentRunner(attentionProfileExperiments, attentionLearningStore);
        _attentionRerankOptions = attentionRerankOptions ?? new RetrievalAttentionRerankOptions();
        _attentionRerankPolicy = new GuardedAttentionRerankPolicy(_attentionRerankOptions);
        _rankerShadowOptions = rankerShadowOptions ?? new LifecycleAwareRankerShadowOptions();
        _rankerShadowTraceBuilder = rankerShadowTraceBuilder;
        _graphExpansionShadowOptions = graphExpansionShadowOptions ?? new GraphExpansionShadowOptions();
        _graphExpansionShadowTraceBuilder = graphExpansionShadowTraceBuilder;
        var contextObjectResolver = new DefaultContextObjectResolver(contextStore, memoryStore);
        var relationFrontierBuilder = new RelationFrontierBuilder();
        var relationExpansionService = relationStore is null
            ? null
            : new RelationExpansionService(new RelationTraversalEngine(relationStore), contextObjectResolver);
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
        var effectivePlan = request.Plan ?? AutoPlanner.Plan(request);

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

        var traceCandidates = ranked;
        var rankerShadowTrace = BuildRankerShadowTrace(
            request,
            effectivePacked,
            traceCandidates,
            metadata);
        var graphExpansionShadowTrace = await BuildGraphExpansionShadowTraceAsync(
                request,
                effectivePacked,
                metadata,
                cancellationToken)
            .ConfigureAwait(false);

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
            rankerShadowTrace,
            graphExpansionShadowTrace);

        if (_traceStore is not null)
        {
            try
            {
                trace = await SuppressDuplicateGraphExpansionShadowTraceAsync(trace, cancellationToken)
                    .ConfigureAwait(false);
                await _traceStore.SaveAsync(trace, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // P5-0.4: retrieval trace 写入失败不得影响正式检索输出。
            }
        }

        var result = _resultAssembler.Assemble(operationId, request, effectivePacked, trace, metadata);
        CoreMetrics.RetrievalDuration.Record(_sw.Elapsed.TotalMilliseconds);

        // V17.0: 投影只读 decision trace，不改变 result。
        if (_decisionTraceStore is not null)
        {
            try
            {
                var decisionRecord = ContextDecisionProjector.ProjectRetrieval(result);
                await _decisionTraceStore.SaveAsync(decisionRecord, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // decision trace 写入失败不得影响正式检索输出。
            }
        }

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

    private async Task<GraphExpansionShadowTrace> BuildGraphExpansionShadowTraceAsync(
        ContextRetrievalRequest request,
        RetrievalPackingResult packingResult,
        Dictionary<string, string> metadata,
        CancellationToken cancellationToken)
    {
        var profiles = NormalizeGraphExpansionProfiles(_graphExpansionShadowOptions.Profiles);
        var maxRelations = _graphExpansionShadowOptions.MaxRelationsPerTrace > 0
            ? _graphExpansionShadowOptions.MaxRelationsPerTrace
            : 50;
        metadata["graphExpansionShadowEnabled"] = _graphExpansionShadowOptions.Enabled.ToString().ToLowerInvariant();
        metadata["graphExpansionShadowTraceCollectionEnabled"] = _graphExpansionShadowOptions.TraceCollectionEnabled.ToString().ToLowerInvariant();
        metadata["graphExpansionProfiles"] = string.Join(",", profiles);
        metadata["graphExpansionMaxRelationsPerTrace"] = maxRelations.ToString();

        if (!_graphExpansionShadowOptions.Enabled
            || !_graphExpansionShadowOptions.TraceCollectionEnabled
            || _graphExpansionShadowTraceBuilder is null)
        {
            metadata["graphExpansionAcceptedRelationCount"] = "0";
            metadata["graphExpansionBlockedRelationCount"] = "0";
            return new GraphExpansionShadowTrace
            {
                GraphExpansionShadowEnabled = false,
                GraphExpansionProfiles = profiles,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["formalOutputChanged"] = "false",
                    ["selectedSetChanged"] = "false",
                    ["packageSectionsChanged"] = "false",
                    ["disabledReason"] = _graphExpansionShadowTraceBuilder is null
                        ? "builder_unavailable"
                        : "collection_disabled"
                }
            };
        }

        var trace = await _graphExpansionShadowTraceBuilder
            .BuildAsync(
                request,
                packingResult.SelectedCandidates,
                new GraphExpansionShadowOptions
                {
                    Enabled = true,
                    TraceCollectionEnabled = true,
                    Profiles = profiles,
                    MaxRelationsPerTrace = maxRelations
                },
                cancellationToken)
            .ConfigureAwait(false);

        metadata["graphExpansionAcceptedRelationCount"] = trace.AcceptedRelations.Count.ToString();
        metadata["graphExpansionBlockedRelationCount"] = trace.BlockedRelations.Count.ToString();
        metadata["graphExpansionRiskIfNormal"] = trace.RiskIfNormal.ToString();
        metadata["graphExpansionRiskAfterRouting"] = trace.RiskAfterRouting.ToString();
        metadata["graphExpansionHistoricalAuditCount"] = trace.HistoricalAuditCount.ToString();
        metadata["graphExpansionConflictEvidenceCount"] = trace.ConflictEvidenceCount.ToString();
        metadata["graphExpansionWrongSectionRisk"] = trace.WrongSectionRisk.ToString();
        if (trace.Metadata.TryGetValue("traceSignature", out var traceSignature)
            && !string.IsNullOrWhiteSpace(traceSignature))
        {
            metadata["graphExpansionTraceSignature"] = traceSignature;
        }

        metadata["graphExpansionFormalOutputChanged"] = "false";
        metadata["graphExpansionSelectedSetChanged"] = "false";
        metadata["graphExpansionPackageSectionsChanged"] = "false";
        return trace;
    }

    private async Task<ContextRetrievalTrace> SuppressDuplicateGraphExpansionShadowTraceAsync(
        ContextRetrievalTrace trace,
        CancellationToken cancellationToken)
    {
        if (_traceStore is null
            || !trace.GraphExpansionShadowTrace.GraphExpansionShadowEnabled
            || !trace.GraphExpansionShadowTrace.Metadata.TryGetValue("traceSignature", out var signature)
            || string.IsNullOrWhiteSpace(signature))
        {
            return trace;
        }

        var recent = await _traceStore
            .QueryRecentAsync(trace.WorkspaceId, trace.CollectionId, 200, cancellationToken)
            .ConfigureAwait(false);
        var duplicate = recent.FirstOrDefault(item =>
            !string.Equals(item.RetrievalId, trace.RetrievalId, StringComparison.OrdinalIgnoreCase)
            && item.GraphExpansionShadowTrace.GraphExpansionShadowEnabled
            && item.GraphExpansionShadowTrace.AcceptedRelations.Count + item.GraphExpansionShadowTrace.BlockedRelations.Count > 0
            && item.GraphExpansionShadowTrace.Metadata.TryGetValue("traceSignature", out var existingSignature)
            && string.Equals(existingSignature, signature, StringComparison.OrdinalIgnoreCase));
        if (duplicate is null)
        {
            return trace;
        }

        var metadata = new Dictionary<string, string>(trace.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["graphExpansionDuplicateSuppressed"] = "true",
            ["graphExpansionDuplicateOfRetrievalId"] = duplicate.RetrievalId,
            ["graphExpansionTraceSignature"] = signature
        };
        var shadowMetadata = new Dictionary<string, string>(trace.GraphExpansionShadowTrace.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["duplicateSuppressed"] = "true",
            ["duplicateOfRetrievalId"] = duplicate.RetrievalId,
            ["acceptedRelationCountBeforeSuppression"] = trace.GraphExpansionShadowTrace.AcceptedRelations.Count.ToString(),
            ["blockedRelationCountBeforeSuppression"] = trace.GraphExpansionShadowTrace.BlockedRelations.Count.ToString()
        };

        return new ContextRetrievalTrace
        {
            RetrievalId = trace.RetrievalId,
            WorkspaceId = trace.WorkspaceId,
            CollectionId = trace.CollectionId,
            QueryText = trace.QueryText,
            RewrittenQueryText = trace.RewrittenQueryText,
            Stages = trace.Stages,
            Candidates = trace.Candidates,
            SelectedItems = trace.SelectedItems,
            DroppedItems = trace.DroppedItems,
            AttentionScores = trace.AttentionScores,
            AttentionShadowReport = trace.AttentionShadowReport,
            AttentionProfileComparison = trace.AttentionProfileComparison,
            AttentionRerankComparison = trace.AttentionRerankComparison,
            RankerShadowTrace = trace.RankerShadowTrace,
            GraphExpansionShadowTrace = new GraphExpansionShadowTrace
            {
                GraphExpansionShadowEnabled = true,
                GraphExpansionProfiles = trace.GraphExpansionShadowTrace.GraphExpansionProfiles,
                Metadata = shadowMetadata
            },
            Metadata = metadata,
            CreatedAt = trace.CreatedAt
        };
    }

    private static IReadOnlyList<string> NormalizeGraphExpansionProfiles(IReadOnlyList<string>? profiles)
    {
        var materialized = profiles?
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return materialized is { Length: > 0 } ? materialized : ["audit-v1", "conflict-v1"];
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
}
