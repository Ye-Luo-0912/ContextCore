using System.Globalization;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>Vector miss-set 表示审计与 representation benchmark；只读评估，不写正式索引或检索输出。</summary>
public sealed class VectorMissSetRepresentationAuditRunner
{
    private const int DiagnosticTopK = 1000;
    private const int DefaultTopK = 10;
    private const double LowConfidenceThreshold = 0.25;

    private static readonly string[] DocumentProfiles =
    [
        DocumentRepresentationProfiles.RawContentV1,
        DocumentRepresentationProfiles.TitleContentV1,
        DocumentRepresentationProfiles.TitleSummaryContentV1,
        DocumentRepresentationProfiles.AnchorEnrichedV1,
        DocumentRepresentationProfiles.MetadataEnrichedV1,
        DocumentRepresentationProfiles.CompactRetrievalTextV1
    ];

    private static readonly string[] QueryProfiles =
    [
        QueryRepresentationProfiles.RawQueryV1,
        QueryRepresentationProfiles.IntentQueryV1,
        QueryRepresentationProfiles.AnchorQueryV1,
        QueryRepresentationProfiles.ModeIntentQueryV1,
        QueryRepresentationProfiles.ExpandedAnchorQueryV1
    ];

    private readonly VectorQueryPreviewService? _previewService;
    private readonly IEmbeddingGenerator? _generator;

    public VectorMissSetRepresentationAuditRunner(
        VectorQueryPreviewService? previewService = null,
        IEmbeddingGenerator? generator = null)
    {
        _previewService = previewService;
        _generator = generator;
    }

    public async Task<VectorMissSetRepresentationAuditReport> RunMissSetAuditAsync(
        IReadOnlyList<ContextEvalSample> samples,
        IReadOnlyList<VectorReindexSourceItem> sourceItems,
        string workspaceId,
        string collectionId,
        int topK = DefaultTopK,
        string? profileId = null,
        double? minSimilarity = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(sourceItems);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        if (_previewService is null)
        {
            throw new InvalidOperationException("VectorMissSetRepresentationAuditRunner requires VectorQueryPreviewService for miss-set audit.");
        }

        var operationId = $"vector-missset-representation-audit-{Guid.NewGuid():N}";
        var resolvedProfile = string.IsNullOrWhiteSpace(profileId) ? VectorQueryProfileIds.NormalV1 : profileId;
        var configured = new List<SamplePreview>();
        foreach (var sample in samples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var configuredPreview = await PreviewAsync(
                _previewService,
                sample,
                sample.Query,
                workspaceId,
                collectionId,
                operationId,
                "configured",
                topK,
                resolvedProfile,
                minSimilarity,
                cancellationToken).ConfigureAwait(false);
            var diagnosticPreview = await PreviewAsync(
                _previewService,
                sample,
                sample.Query,
                workspaceId,
                collectionId,
                operationId,
                "diagnostic",
                DiagnosticTopK,
                resolvedProfile,
                minSimilarity,
                cancellationToken).ConfigureAwait(false);

            configured.Add(new SamplePreview(
                sample,
                configuredPreview,
                diagnosticPreview,
                VectorQueryShadowEvalRunner.BuildSampleResult(sample, configuredPreview, LowConfidenceThreshold)));
        }

        var sourceById = sourceItems
            .Where(item => !string.IsNullOrWhiteSpace(item.ItemId))
            .GroupBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var records = configured
            .SelectMany(item => BuildMissRecords(
                item,
                sourceById,
                DocumentRepresentationProfiles.RawContentV1,
                QueryRepresentationProfiles.RawQueryV1,
                topK,
                minSimilarity))
            .ToArray();
        var firstCandidate = configured
            .SelectMany(item => item.Configured.Candidates)
            .FirstOrDefault();
        var diagnosisCounts = records
            .GroupBy(item => item.RepresentationDiagnosis, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var repairCounts = records
            .GroupBy(item => item.RecommendedRepair, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        return new VectorMissSetRepresentationAuditReport
        {
            OperationId = operationId,
            CreatedAt = DateTimeOffset.UtcNow,
            Samples = samples.Count,
            ProviderId = firstCandidate?.EmbeddingProvider ?? _generator?.Provider ?? string.Empty,
            EmbeddingModel = firstCandidate?.EmbeddingModel ?? _generator?.Model ?? string.Empty,
            DocumentRepresentationProfile = DocumentRepresentationProfiles.RawContentV1,
            QueryRepresentationProfile = QueryRepresentationProfiles.RawQueryV1,
            MissedMustHitCount = records.Length,
            MissedMustHits = records,
            DiagnosisCounts = diagnosisCounts,
            RecommendedRepairCounts = repairCounts,
            Recommendation = RecommendAudit(records),
            FormalOutputChanged = 0,
            Warnings = BuildAuditWarnings(records)
        };
    }

    public async Task<VectorRepresentationBenchmarkReport> RunBenchmarkAsync(
        IReadOnlyList<ContextEvalSample> samples,
        IReadOnlyList<VectorReindexSourceItem> sourceItems,
        string workspaceId,
        string collectionId,
        int topK = DefaultTopK,
        string? profileId = null,
        double? minSimilarity = null,
        IReadOnlyList<VectorIndexEntry>? metadataEntries = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(sourceItems);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        if (_generator is null)
        {
            return NewUnavailableBenchmark(samples.Count, "当前 provider 未注册 embedding generator，无法执行 representation benchmark。");
        }

        var operationId = $"vector-representation-benchmark-{Guid.NewGuid():N}";
        var resolvedProfile = string.IsNullOrWhiteSpace(profileId) ? VectorQueryProfileIds.NormalV1 : profileId;
        var benchmarkSources = MergeSourceMetadata(sourceItems, metadataEntries);
        IReadOnlySet<string> baselineMisses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<VectorRepresentationBenchmarkResult>();
        var baselineSampleResults = Array.Empty<VectorQueryShadowEvalSample>();

        foreach (var documentProfile in DocumentProfiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var representedSources = benchmarkSources
                .Select(source => BuildRepresentedSource(source, documentProfile))
                .ToArray();
            var store = await BuildTemporaryIndexAsync(
                representedSources,
                workspaceId,
                collectionId,
                documentProfile,
                cancellationToken).ConfigureAwait(false);
            var previewService = new VectorQueryPreviewService(
                store,
                _generator,
                new VectorIndexService(store, _generator, null, null, representedSources));

            foreach (var queryProfile in QueryProfiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sampleResults = new List<VectorQueryShadowEvalSample>();
                foreach (var sample in samples)
                {
                    var representedQuery = BuildQueryRepresentation(sample, queryProfile);
                    var preview = await PreviewAsync(
                        previewService,
                        sample,
                        representedQuery,
                        workspaceId,
                        collectionId,
                        operationId,
                        $"{documentProfile}:{queryProfile}",
                        topK,
                        resolvedProfile,
                        minSimilarity,
                        cancellationToken).ConfigureAwait(false);
                    sampleResults.Add(VectorQueryShadowEvalRunner.BuildSampleResult(sample, preview, LowConfidenceThreshold));
                }

                if (string.Equals(documentProfile, DocumentRepresentationProfiles.RawContentV1, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(queryProfile, QueryRepresentationProfiles.RawQueryV1, StringComparison.OrdinalIgnoreCase))
                {
                    baselineSampleResults = sampleResults.ToArray();
                    baselineMisses = BuildMissKeySet(baselineSampleResults);
                }

                var summary = VectorQueryShadowEvalRunner.BuildReport($"{operationId}:{documentProfile}:{queryProfile}", sampleResults);
                results.Add(BuildBenchmarkResult(
                    summary,
                    sampleResults,
                    documentProfile,
                    queryProfile,
                    topK,
                    minSimilarity,
                    baselineMisses,
                    baselineSampleResults));
            }
        }

        var ordered = results
            .OrderBy(item => item.RiskAfterPolicy)
            .ThenBy(item => item.MustNotHitRisk)
            .ThenBy(item => item.LifecycleRisk)
            .ThenByDescending(item => item.Recall)
            .ThenByDescending(item => item.Mrr)
            .ThenByDescending(item => item.SimilaritySeparation)
            .ToArray();
        var best = ordered.FirstOrDefault();

        return new VectorRepresentationBenchmarkReport
        {
            OperationId = operationId,
            CreatedAt = DateTimeOffset.UtcNow,
            Samples = samples.Count,
            ProviderId = _generator.Provider,
            EmbeddingModel = _generator.Model,
            Results = ordered,
            BestResult = best,
            Recommendation = best?.Recommendation ?? VectorQueryShadowRecommendations.KeepPreviewOnly,
            FormalOutputChanged = 0,
            Warnings = BuildBenchmarkWarnings(best)
        };
    }

    public static string BuildDocumentRepresentation(
        VectorReindexSourceItem source,
        string profileId)
    {
        ArgumentNullException.ThrowIfNull(source);
        var title = ResolveTitle(source);
        var summary = ResolveSummary(source);
        var anchors = ExtractAnchors(source.Text)
            .Take(16)
            .ToArray();
        var metadataText = BuildMetadataText(source.Metadata);

        return profileId switch
        {
            DocumentRepresentationProfiles.TitleContentV1 => JoinText(title, source.Text),
            DocumentRepresentationProfiles.TitleSummaryContentV1 => JoinText(title, summary, source.Text),
            DocumentRepresentationProfiles.AnchorEnrichedV1 => JoinText(string.Join(' ', anchors), source.Text),
            DocumentRepresentationProfiles.MetadataEnrichedV1 => JoinText(metadataText, title, summary, source.Text),
            DocumentRepresentationProfiles.CompactRetrievalTextV1 => JoinText(
                source.Layer,
                source.ItemKind,
                ResolveMetadata(source.Metadata, "lifecycle"),
                ResolveMetadata(source.Metadata, "reviewStatus"),
                title,
                summary,
                string.Join(' ', anchors),
                TrimText(source.Text, 1200)),
            _ => source.Text
        };
    }

    public static string BuildQueryRepresentation(
        ContextEvalSample sample,
        string profileId)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var intent = ResolveIntent(sample);
        var anchors = ExtractAnchors(sample.Query).Take(12).ToArray();

        return profileId switch
        {
            QueryRepresentationProfiles.IntentQueryV1 => JoinText(intent, sample.Query),
            QueryRepresentationProfiles.AnchorQueryV1 => JoinText(string.Join(' ', anchors), sample.Query),
            QueryRepresentationProfiles.ModeIntentQueryV1 => JoinText(sample.Mode, intent, sample.Query),
            QueryRepresentationProfiles.ExpandedAnchorQueryV1 => JoinText(sample.Query, string.Join(' ', anchors), BuildAnchorPairs(anchors)),
            _ => sample.Query
        };
    }

    public static IReadOnlyList<string> ExtractAnchors(string text, int maxAnchors = 24)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in EnumerateTokens(text))
        {
            if (token.Length < 2)
            {
                continue;
            }

            counts[token] = counts.GetValueOrDefault(token) + 1;
        }

        return counts
            .OrderByDescending(item => item.Value)
            .ThenByDescending(item => item.Key.Length)
            .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maxAnchors))
            .Select(item => item.Key)
            .ToArray();
    }

    public static string BuildMissSetMarkdownReport(
        VectorMissSetRepresentationAuditReport a3,
        VectorMissSetRepresentationAuditReport extended)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Vector Miss-set Representation Audit");
        builder.AppendLine();
        builder.AppendLine($"Generated: {DateTimeOffset.UtcNow:O}");
        builder.AppendLine();
        AppendAudit(builder, "A3", a3);
        AppendAudit(builder, "Extended", extended);
        return builder.ToString();
    }

    public static string BuildBenchmarkMarkdownReport(
        VectorRepresentationBenchmarkReport a3,
        VectorRepresentationBenchmarkReport extended)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Vector Representation Benchmark");
        builder.AppendLine();
        builder.AppendLine($"Generated: {DateTimeOffset.UtcNow:O}");
        builder.AppendLine();
        AppendBenchmark(builder, "A3", a3);
        AppendBenchmark(builder, "Extended", extended);
        return builder.ToString();
    }

    private static async Task<VectorQueryPreviewResult> PreviewAsync(
        VectorQueryPreviewService previewService,
        ContextEvalSample sample,
        string queryText,
        string workspaceId,
        string collectionId,
        string operationId,
        string pass,
        int topK,
        string profileId,
        double? minSimilarity,
        CancellationToken cancellationToken)
    {
        return await previewService.PreviewAsync(new VectorQueryPreviewRequest
        {
            OperationId = $"{operationId}:{sample.Id}:{pass}",
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            QueryText = queryText,
            TopK = Math.Clamp(topK > 0 ? topK : DefaultTopK, 1, DiagnosticTopK),
            ProfileId = profileId,
            MinSimilarity = minSimilarity,
            IncludeVector = false,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["mode"] = sample.Mode,
                ["intent"] = ResolveIntent(sample),
                ["createdFrom"] = "vector_representation_benchmark"
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IVectorIndexStore> BuildTemporaryIndexAsync(
        IReadOnlyList<VectorReindexSourceItem> sources,
        string workspaceId,
        string collectionId,
        string documentProfile,
        CancellationToken cancellationToken)
    {
        var store = new TemporaryVectorIndexStore();
        if (sources.Count == 0)
        {
            return store;
        }

        var result = await _generator!.GenerateAsync(new EmbeddingGeneratorRequest
        {
            OperationId = $"vector-representation-temp-index-{documentProfile}-{Guid.NewGuid():N}",
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Inputs = sources.Select(source => new EmbeddingGeneratorInput
            {
                ItemId = source.ItemId,
                Text = source.Text,
                ItemKind = source.ItemKind,
                Layer = source.Layer,
                Metadata = new Dictionary<string, string>(source.Metadata, StringComparer.OrdinalIgnoreCase)
                {
                    ["documentRepresentationProfile"] = documentProfile
                }
            }).ToArray()
        }, cancellationToken).ConfigureAwait(false);

        foreach (var entry in result.Entries)
        {
            await store.UpsertAsync(entry, cancellationToken).ConfigureAwait(false);
        }

        return store;
    }

    private static VectorRepresentationBenchmarkResult BuildBenchmarkResult(
        VectorQueryShadowEvalReport summary,
        IReadOnlyList<VectorQueryShadowEvalSample> samples,
        string documentProfile,
        string queryProfile,
        int topK,
        double? minSimilarity,
        IReadOnlySet<string> baselineMisses,
        IReadOnlyList<VectorQueryShadowEvalSample> baselineSamples)
    {
        var currentMisses = BuildMissKeySet(samples);
        var recovered = baselineMisses.Count == 0
            ? 0
            : baselineMisses.Count(key => !currentMisses.Contains(key));
        var baselineRiskBySample = baselineSamples
            .GroupBy(item => item.SampleId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().RiskAfterPolicy, StringComparer.OrdinalIgnoreCase);
        var newRisk = samples.Count(sample =>
            sample.RiskAfterPolicy > baselineRiskBySample.GetValueOrDefault(sample.SampleId));
        var separation = CalculateSimilaritySeparation(samples);

        return new VectorRepresentationBenchmarkResult
        {
            DocumentRepresentationProfile = documentProfile,
            QueryRepresentationProfile = queryProfile,
            Provider = samples.SelectMany(item => item.Candidates).FirstOrDefault()?.EmbeddingProvider ?? string.Empty,
            TopK = topK,
            MinSimilarity = minSimilarity,
            Recall = summary.MustHitRecallAfterPolicy,
            Mrr = CalculateMrr(samples),
            RiskAfterPolicy = summary.RiskAfterPolicy,
            MustNotHitRisk = summary.MustNotHitRiskAfterPolicy,
            LifecycleRisk = summary.LifecycleRiskAfterPolicy,
            NoCandidateCount = summary.NoCandidateCount,
            RecoveredMissCount = recovered,
            NewRiskCount = newRisk,
            SimilaritySeparation = separation,
            Recommendation = RecommendBenchmark(summary, CalculateMrr(samples), separation, newRisk)
        };
    }

    private static IReadOnlySet<string> BuildMissKeySet(IReadOnlyList<VectorQueryShadowEvalSample> samples)
    {
        return samples
            .SelectMany(sample => sample.MustHitMissing.Select(miss => $"{sample.SampleId}\u001f{miss}"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<VectorMissSetRepresentationAuditRecord> BuildMissRecords(
        SamplePreview preview,
        IReadOnlyDictionary<string, VectorReindexSourceItem> sourceById,
        string documentProfile,
        string queryProfile,
        int topK,
        double? minSimilarity)
    {
        var records = new List<VectorMissSetRepresentationAuditRecord>();
        var intent = ResolveIntent(preview.Sample);
        var queryAnchors = ExtractAnchors(preview.Sample.Query).Take(12).ToArray();
        foreach (var mustHit in preview.Sample.MustHit
                     .Where(item => !string.IsNullOrWhiteSpace(item))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (preview.SampleResult.MustHitMatchedAfterPolicy.Any(match => EvalIdMatches(mustHit, match)))
            {
                continue;
            }

            var configured = FindMatchingCandidate(preview.Configured.Candidates, mustHit);
            var diagnostic = FindMatchingCandidate(preview.Diagnostic.Candidates, mustHit);
            var candidate = configured ?? diagnostic;
            var source = FindMatchingSource(sourceById, mustHit);
            var sourceText = source?.Text ?? string.Empty;
            var title = source is null ? string.Empty : ResolveTitle(source);
            var documentAnchors = ExtractAnchors(sourceText).Take(16).ToArray();
            var missReason = ClassifyMissReason(
                preview.SampleResult,
                source,
                configured,
                diagnostic,
                topK,
                minSimilarity);
            var diagnosis = ClassifyDiagnosis(
                preview.Sample,
                source,
                queryAnchors,
                documentAnchors,
                candidate,
                missReason);

            records.Add(new VectorMissSetRepresentationAuditRecord
            {
                SampleId = preview.Sample.Id,
                Mode = preview.Sample.Mode,
                Intent = intent,
                QueryText = preview.Sample.Query,
                QueryAnchors = queryAnchors,
                MustHitItemId = mustHit,
                DocumentTitle = title,
                DocumentAnchors = documentAnchors,
                DocumentRepresentationProfile = documentProfile,
                QueryRepresentationProfile = queryProfile,
                RawSimilarity = candidate?.Similarity ?? 0,
                RawRank = candidate?.RawRank ?? 0,
                EligibleRank = candidate is null ? 0 : ResolveEligibleRank(preview.Diagnostic.Candidates, candidate.ItemId),
                MissReason = missReason,
                RepresentationDiagnosis = diagnosis,
                RecommendedRepair = RecommendRepair(diagnosis, missReason)
            });
        }

        return records;
    }

    private static string ClassifyMissReason(
        VectorQueryShadowEvalSample sample,
        VectorReindexSourceItem? source,
        VectorQueryPreviewCandidate? configured,
        VectorQueryPreviewCandidate? diagnostic,
        int topK,
        double? minSimilarity)
    {
        if (source is null)
        {
            return VectorRecallLossMissReasons.NotIndexed;
        }

        if (sample.RawCandidateCount == 0 && diagnostic is null)
        {
            return VectorRecallLossMissReasons.NoCandidateGenerated;
        }

        var candidate = configured ?? diagnostic;
        if (candidate is null)
        {
            return VectorRecallLossMissReasons.NoCandidateGenerated;
        }

        if (candidate.BlockedReasons.Contains(VectorCandidateBlockedReason.SimilarityBelowThreshold, StringComparer.OrdinalIgnoreCase)
            || minSimilarity is not null && candidate.Similarity < minSimilarity.Value)
        {
            return VectorRecallLossMissReasons.BelowSimilarityThreshold;
        }

        if (!IsEligible(candidate))
        {
            return VectorRecallLossMissReasons.BlockedByEligibilityPolicy;
        }

        if (configured is null && candidate.RawRank > topK)
        {
            return VectorRecallLossMissReasons.BelowTopK;
        }

        return VectorRecallLossMissReasons.RequiresRankerFusion;
    }

    private static string ClassifyDiagnosis(
        ContextEvalSample sample,
        VectorReindexSourceItem? source,
        IReadOnlyList<string> queryAnchors,
        IReadOnlyList<string> documentAnchors,
        VectorQueryPreviewCandidate? candidate,
        string missReason)
    {
        if (queryAnchors.Count <= 1 || sample.Query.Length < 12)
        {
            return VectorRepresentationDiagnosisTypes.QueryTooShort;
        }

        if (string.Equals(ResolveIntent(sample), "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return VectorRepresentationDiagnosisTypes.QueryIntentMissing;
        }

        if (source is null)
        {
            return VectorRepresentationDiagnosisTypes.MustHitOnlyRecoverableByMetadata;
        }

        if (string.IsNullOrWhiteSpace(ResolveTitle(source)))
        {
            return VectorRepresentationDiagnosisTypes.DocumentTitleMissing;
        }

        if (string.IsNullOrWhiteSpace(ResolveSummary(source)))
        {
            return VectorRepresentationDiagnosisTypes.DocumentSummaryMissing;
        }

        if (!queryAnchors.Any(anchor => documentAnchors.Contains(anchor, StringComparer.OrdinalIgnoreCase)))
        {
            return VectorRepresentationDiagnosisTypes.AnchorMismatch;
        }

        if (source.Text.Length > 1600 || documentAnchors.Count > 20)
        {
            return VectorRepresentationDiagnosisTypes.RepresentationTooNoisy;
        }

        if (string.Equals(missReason, VectorRecallLossMissReasons.BelowTopK, StringComparison.OrdinalIgnoreCase)
            || string.Equals(missReason, VectorRecallLossMissReasons.RequiresRankerFusion, StringComparison.OrdinalIgnoreCase))
        {
            return VectorRepresentationDiagnosisTypes.RequiresQueryExpansion;
        }

        if (candidate is null || candidate.Similarity <= 0.15)
        {
            return VectorRepresentationDiagnosisTypes.RequiresBetterEmbeddingModel;
        }

        return VectorRepresentationDiagnosisTypes.RequiresDocumentRepresentationRewrite;
    }

    private static string RecommendRepair(string diagnosis, string missReason)
    {
        if (string.Equals(missReason, VectorRecallLossMissReasons.BlockedByEligibilityPolicy, StringComparison.OrdinalIgnoreCase))
        {
            return "保持 eligibility safety gate，转入 metadata/profile audit，不放松 lifecycle gate。";
        }

        return diagnosis switch
        {
            VectorRepresentationDiagnosisTypes.QueryTooShort => "尝试 intent/query anchor 表示，不修改 policy。",
            VectorRepresentationDiagnosisTypes.QueryIntentMissing => "补齐通用 intent metadata 或使用 mode-intent-query-v1 做离线验证。",
            VectorRepresentationDiagnosisTypes.DocumentTitleMissing => "补齐 title/summary runtime metadata 后重建 representation index。",
            VectorRepresentationDiagnosisTypes.DocumentSummaryMissing => "补齐 summary runtime metadata 后重建 representation index。",
            VectorRepresentationDiagnosisTypes.AnchorMismatch => "尝试 anchor-enriched-v1 / expanded-anchor-query-v1；仍不达标则保持 shadow-only。",
            VectorRepresentationDiagnosisTypes.RepresentationTooNoisy => "尝试 compact-retrieval-text-v1，减少正文噪声。",
            VectorRepresentationDiagnosisTypes.MustHitOnlyRecoverableByMetadata => "只允许通过 provenance/lifecycle metadata 解释，不做样本特判。",
            VectorRepresentationDiagnosisTypes.RequiresQueryExpansion => "尝试 query representation 扩展，并保留风险 gate。",
            VectorRepresentationDiagnosisTypes.RequiresDocumentRepresentationRewrite => "尝试 title-summary-content-v1 或 compact-retrieval-text-v1。",
            _ => "需要更强 embedding provider 或继续保持 preview-only。"
        };
    }

    private static VectorReindexSourceItem BuildRepresentedSource(
        VectorReindexSourceItem source,
        string documentProfile)
    {
        var metadata = new Dictionary<string, string>(source.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["documentRepresentationProfile"] = documentProfile,
            ["representationCreatedFrom"] = "vector_representation_benchmark"
        };

        return new VectorReindexSourceItem
        {
            ItemId = source.ItemId,
            ItemKind = source.ItemKind,
            Layer = source.Layer,
            Text = BuildDocumentRepresentation(source, documentProfile),
            UpdatedAt = source.UpdatedAt == default ? DateTimeOffset.UtcNow : source.UpdatedAt,
            Metadata = metadata
        };
    }

    private static IReadOnlyList<VectorReindexSourceItem> MergeSourceMetadata(
        IReadOnlyList<VectorReindexSourceItem> sourceItems,
        IReadOnlyList<VectorIndexEntry>? metadataEntries)
    {
        if (metadataEntries is null || metadataEntries.Count == 0)
        {
            return sourceItems;
        }

        var entryByItem = metadataEntries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.ItemId))
            .GroupBy(entry => entry.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(entry => entry.UpdatedAt).First(), StringComparer.OrdinalIgnoreCase);
        return sourceItems.Select(source =>
        {
            if (!entryByItem.TryGetValue(source.ItemId, out var entry))
            {
                return source;
            }

            var metadata = new Dictionary<string, string>(source.Metadata, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in entry.Metadata)
            {
                if (IsRepresentationInternalMetadata(pair.Key))
                {
                    continue;
                }

                metadata[pair.Key] = pair.Value;
            }

            return new VectorReindexSourceItem
            {
                ItemId = source.ItemId,
                ItemKind = string.IsNullOrWhiteSpace(source.ItemKind) ? entry.ItemKind : source.ItemKind,
                Layer = string.IsNullOrWhiteSpace(source.Layer) ? entry.Layer : source.Layer,
                Text = source.Text,
                UpdatedAt = source.UpdatedAt == default ? entry.UpdatedAt : source.UpdatedAt,
                Metadata = metadata
            };
        }).ToArray();
    }

    private static bool IsRepresentationInternalMetadata(string key)
    {
        return key.Contains("representation", StringComparison.OrdinalIgnoreCase)
               || key.Equals("createdFrom", StringComparison.OrdinalIgnoreCase)
               || key.Equals("embeddingProviderType", StringComparison.OrdinalIgnoreCase)
               || key.Equals("poolingStrategy", StringComparison.OrdinalIgnoreCase)
               || key.Equals("normalize", StringComparison.OrdinalIgnoreCase);
    }

    private static double CalculateMrr(IReadOnlyList<VectorQueryShadowEvalSample> samples)
    {
        if (samples.Count == 0)
        {
            return 0;
        }

        var total = 0.0;
        foreach (var sample in samples)
        {
            if (sample.MustHitCount == 0)
            {
                total += 1.0;
                continue;
            }

            var eligible = sample.Candidates
                .Where(IsEligible)
                .OrderBy(candidate => candidate.Rank)
                .ToArray();
            var rank = eligible
                .Select((candidate, index) => new { candidate, index })
                .FirstOrDefault(item => sample.MustHitMatchedAfterPolicy.Any(expected => EvalIdMatches(expected, item.candidate.ItemId)))
                ?.index + 1;
            total += rank is null ? 0 : 1.0 / rank.Value;
        }

        return total / samples.Count;
    }

    private static double CalculateSimilaritySeparation(IReadOnlyList<VectorQueryShadowEvalSample> samples)
    {
        var positives = new List<double>();
        var negatives = new List<double>();
        foreach (var sample in samples)
        {
            positives.AddRange(sample.Candidates
                .Where(candidate => sample.MustHitMatchedBeforePolicy.Any(expected => EvalIdMatches(expected, candidate.ItemId)))
                .Select(candidate => candidate.Similarity));
            negatives.AddRange(sample.Candidates
                .Where(candidate => sample.MustNotHitMatchedBeforePolicy.Any(expected => EvalIdMatches(expected, candidate.ItemId)))
                .Select(candidate => candidate.Similarity));
        }

        if (positives.Count == 0 || negatives.Count == 0)
        {
            return 0;
        }

        return positives.Average() - negatives.Average();
    }

    private static string RecommendBenchmark(
        VectorQueryShadowEvalReport summary,
        double mrr,
        double similaritySeparation,
        int newRiskCount)
    {
        if (summary.RiskAfterPolicy > 0 || newRiskCount > 0)
        {
            return VectorQueryShadowRecommendations.BlockedByRisk;
        }

        if (summary.Samples == 0 || summary.RawCandidateCount == 0 || summary.NoCandidateCount > summary.Samples / 2)
        {
            return VectorQueryShadowRecommendations.NeedsMoreIndexedData;
        }

        if (summary.MustHitRecallAfterPolicy >= 0.8 && mrr >= 0.5)
        {
            return VectorQueryShadowRecommendations.ReadyForRetrievalShadow;
        }

        if (similaritySeparation < 0.02 && summary.MustHitRecallAfterPolicy < 0.75)
        {
            return VectorQueryShadowRecommendations.NeedsBetterEmbedding;
        }

        return VectorQueryShadowRecommendations.KeepPreviewOnly;
    }

    private static string RecommendAudit(IReadOnlyList<VectorMissSetRepresentationAuditRecord> records)
    {
        if (records.Count == 0)
        {
            return VectorQueryShadowRecommendations.ReadyForRetrievalShadow;
        }

        if (records.Any(item => item.RepresentationDiagnosis == VectorRepresentationDiagnosisTypes.RequiresBetterEmbeddingModel))
        {
            return VectorQueryShadowRecommendations.NeedsBetterEmbedding;
        }

        return VectorQueryShadowRecommendations.NeedsProfileTuning;
    }

    private static IReadOnlyList<string> BuildAuditWarnings(IReadOnlyList<VectorMissSetRepresentationAuditRecord> records)
    {
        var warnings = new List<string>
        {
            "miss-set audit 只使用 eval label 解释 missed mustHit，不进入 vector eligibility policy。"
        };
        if (records.Any(item => item.MissReason == VectorRecallLossMissReasons.BlockedByEligibilityPolicy))
        {
            warnings.Add("存在被 safety gate 阻断的 mustHit；不得通过 sampleId/itemId 特判放行。");
        }

        return warnings;
    }

    private static VectorRepresentationBenchmarkReport NewUnavailableBenchmark(int samples, string warning)
    {
        return new VectorRepresentationBenchmarkReport
        {
            OperationId = $"vector-representation-benchmark-unavailable-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            Samples = samples,
            Recommendation = VectorQueryShadowRecommendations.NeedsMoreIndexedData,
            FormalOutputChanged = 0,
            Warnings = [warning]
        };
    }

    private static IReadOnlyList<string> BuildBenchmarkWarnings(VectorRepresentationBenchmarkResult? best)
    {
        var warnings = new List<string>
        {
            "representation benchmark 使用临时 vector index，不写入正式 index，不改变 retrieval/package 输出。"
        };
        if (best is null)
        {
            warnings.Add("未生成 representation benchmark 结果。");
        }
        else if (!string.Equals(best.Recommendation, VectorQueryShadowRecommendations.ReadyForRetrievalShadow, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("best representation profile 尚未满足 retrieval shadow readiness，继续保持 preview-only。");
        }

        return warnings;
    }

    private static IEnumerable<string> EnumerateTokens(string text)
    {
        var current = new StringBuilder();
        CharacterClass currentClass = CharacterClass.Other;
        foreach (var rune in text.EnumerateRunes())
        {
            var nextClass = Classify(rune);
            if (nextClass == CharacterClass.Other)
            {
                foreach (var token in Flush(current, currentClass))
                {
                    yield return token;
                }

                currentClass = CharacterClass.Other;
                continue;
            }

            if (current.Length > 0 && nextClass != currentClass)
            {
                foreach (var token in Flush(current, currentClass))
                {
                    yield return token;
                }

                currentClass = CharacterClass.Other;
            }

            currentClass = nextClass;
            current.Append(rune.ToString().ToLowerInvariant());
        }

        foreach (var token in Flush(current, currentClass))
        {
            yield return token;
        }

        IEnumerable<string> Flush(StringBuilder buffer, CharacterClass characterClass)
        {
            if (buffer.Length == 0)
            {
                yield break;
            }

            var value = buffer.ToString();
            buffer.Clear();
            if (characterClass == CharacterClass.Cjk)
            {
                foreach (var token in EnumerateCjkNgrams(value))
                {
                    yield return token;
                }
            }
            else if (value.Length >= 3)
            {
                yield return value;
            }
        }
    }

    private static IEnumerable<string> EnumerateCjkNgrams(string value)
    {
        if (value.Length <= 4)
        {
            yield return value;
            yield break;
        }

        for (var i = 0; i + 2 <= value.Length; i++)
        {
            yield return value.Substring(i, 2);
        }

        for (var i = 0; i + 3 <= value.Length; i++)
        {
            yield return value.Substring(i, 3);
        }
    }

    private static CharacterClass Classify(Rune rune)
    {
        if (IsCjk(rune))
        {
            return CharacterClass.Cjk;
        }

        var category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.LowercaseLetter
            or UnicodeCategory.UppercaseLetter
            or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter
            or UnicodeCategory.DecimalDigitNumber
            ? CharacterClass.AlphaNumeric
            : CharacterClass.Other;
    }

    private static bool IsCjk(Rune rune)
    {
        var value = rune.Value;
        return value is >= 0x3400 and <= 0x4DBF
            or >= 0x4E00 and <= 0x9FFF
            or >= 0xF900 and <= 0xFAFF
            or >= 0x20000 and <= 0x2A6DF
            or >= 0x2A700 and <= 0x2B73F
            or >= 0x2B740 and <= 0x2B81F
            or >= 0x2B820 and <= 0x2CEAF;
    }

    private static string ResolveTitle(VectorReindexSourceItem source)
    {
        var title = ResolveMetadata(source.Metadata, "title");
        if (!string.IsNullOrWhiteSpace(title))
        {
            return title;
        }

        var name = ResolveMetadata(source.Metadata, "name");
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        return string.Empty;
    }

    private static string ResolveSummary(VectorReindexSourceItem source)
    {
        return ResolveMetadata(source.Metadata, "summary");
    }

    private static string ResolveMetadata(IReadOnlyDictionary<string, string> metadata, string key)
    {
        return metadata.TryGetValue(key, out var value) ? value : string.Empty;
    }

    private static string BuildMetadataText(IReadOnlyDictionary<string, string> metadata)
    {
        var allowedPrefixes = new[]
        {
            "source",
            "lifecycle",
            "review",
            "status",
            "layer",
            "kind",
            "type",
            "scope",
            "tag"
        };
        return string.Join(' ', metadata
            .Where(item => allowedPrefixes.Any(prefix => item.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => $"{item.Key}:{item.Value}"));
    }

    private static string ResolveIntent(ContextEvalSample sample)
    {
        if (sample.Metadata.TryGetValue("intent", out var intent) && !string.IsNullOrWhiteSpace(intent))
        {
            return intent.Trim();
        }

        if (sample.Metadata.TryGetValue("planningIntent", out var planningIntent) && !string.IsNullOrWhiteSpace(planningIntent))
        {
            return planningIntent.Trim();
        }

        return "Unknown";
    }

    private static string BuildAnchorPairs(IReadOnlyList<string> anchors)
    {
        if (anchors.Count <= 1)
        {
            return string.Join(' ', anchors);
        }

        return string.Join(' ', anchors
            .Take(8)
            .Zip(anchors.Skip(1), (left, right) => $"{left} {right}"));
    }

    private static string JoinText(params string?[] parts)
    {
        return string.Join(' ', parts
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part!.Trim()));
    }

    private static string TrimText(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length <= maxLength)
        {
            return text;
        }

        return text[..maxLength];
    }

    private static VectorQueryPreviewCandidate? FindMatchingCandidate(
        IReadOnlyList<VectorQueryPreviewCandidate> candidates,
        string expected)
    {
        return candidates
            .OrderBy(candidate => candidate.RawRank == 0 ? int.MaxValue : candidate.RawRank)
            .ThenByDescending(candidate => candidate.Similarity)
            .FirstOrDefault(candidate => EvalIdMatches(expected, candidate.ItemId));
    }

    private static VectorReindexSourceItem? FindMatchingSource(
        IReadOnlyDictionary<string, VectorReindexSourceItem> sourceById,
        string expected)
    {
        if (sourceById.TryGetValue(expected, out var exact))
        {
            return exact;
        }

        return sourceById.Values.FirstOrDefault(source => EvalIdMatches(expected, source.ItemId));
    }

    private static int ResolveEligibleRank(IReadOnlyList<VectorQueryPreviewCandidate> candidates, string itemId)
    {
        var eligible = candidates
            .Where(IsEligible)
            .OrderBy(candidate => candidate.Rank)
            .ToArray();
        var index = Array.FindIndex(eligible, candidate => string.Equals(candidate.ItemId, itemId, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? 0 : index + 1;
    }

    private static bool IsEligible(VectorQueryPreviewCandidate candidate)
    {
        return string.Equals(candidate.EligibilityStatus, VectorCandidateEligibilityStatuses.Eligible, StringComparison.OrdinalIgnoreCase);
    }

    private static bool EvalIdMatches(string expected, string candidateId)
    {
        return !string.IsNullOrWhiteSpace(expected)
               && !string.IsNullOrWhiteSpace(candidateId)
               && (string.Equals(expected, candidateId, StringComparison.OrdinalIgnoreCase)
                   || candidateId.EndsWith(expected, StringComparison.OrdinalIgnoreCase)
                   || expected.EndsWith(candidateId, StringComparison.OrdinalIgnoreCase));
    }

    private static void AppendAudit(
        StringBuilder builder,
        string title,
        VectorMissSetRepresentationAuditReport report)
    {
        builder.AppendLine($"## {title} Summary");
        builder.AppendLine();
        builder.AppendLine($"- Samples: `{report.Samples}`");
        builder.AppendLine($"- Provider: `{Empty(report.ProviderId)}`");
        builder.AppendLine($"- Model: `{Empty(report.EmbeddingModel)}`");
        builder.AppendLine($"- MissedMustHitCount: `{report.MissedMustHitCount}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
        builder.AppendLine("| Diagnosis | Count |");
        builder.AppendLine("|---|---:|");
        foreach (var item in report.DiagnosisCounts.OrderByDescending(item => item.Value).ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"| {item.Key} | {item.Value} |");
        }

        builder.AppendLine();
        builder.AppendLine("| Sample | Mode | Intent | MustHit | RawRank | RawSim | EligibleRank | MissReason | Diagnosis | Repair |");
        builder.AppendLine("|---|---|---|---|---:|---:|---:|---|---|---|");
        foreach (var item in report.MissedMustHits.Take(100))
        {
            builder.AppendLine($"| {item.SampleId} | {item.Mode} | {item.Intent} | {item.MustHitItemId} | {item.RawRank} | {item.RawSimilarity:F4} | {item.EligibleRank} | {item.MissReason} | {item.RepresentationDiagnosis} | {Sanitize(item.RecommendedRepair)} |");
        }

        builder.AppendLine();
    }

    private static void AppendBenchmark(
        StringBuilder builder,
        string title,
        VectorRepresentationBenchmarkReport report)
    {
        builder.AppendLine($"## {title} Summary");
        builder.AppendLine();
        builder.AppendLine($"- Samples: `{report.Samples}`");
        builder.AppendLine($"- Provider: `{Empty(report.ProviderId)}`");
        builder.AppendLine($"- Model: `{Empty(report.EmbeddingModel)}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        if (report.BestResult is not null)
        {
            builder.AppendLine($"- BestDocumentProfile: `{report.BestResult.DocumentRepresentationProfile}`");
            builder.AppendLine($"- BestQueryProfile: `{report.BestResult.QueryRepresentationProfile}`");
            builder.AppendLine($"- BestRecall: `{report.BestResult.Recall:P2}`");
            builder.AppendLine($"- BestMRR: `{report.BestResult.Mrr:F4}`");
            builder.AppendLine($"- BestRiskAfterPolicy: `{report.BestResult.RiskAfterPolicy}`");
            builder.AppendLine($"- BestRecoveredMissCount: `{report.BestResult.RecoveredMissCount}`");
        }

        builder.AppendLine();
        builder.AppendLine("| Document Profile | Query Profile | Recall | MRR | Risk | MustNotRisk | LifecycleRisk | Recovered | NewRisk | Separation | Recommendation |");
        builder.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var result in report.Results.Take(40))
        {
            builder.AppendLine($"| {result.DocumentRepresentationProfile} | {result.QueryRepresentationProfile} | {result.Recall:P2} | {result.Mrr:F4} | {result.RiskAfterPolicy} | {result.MustNotHitRisk:P2} | {result.LifecycleRisk:P2} | {result.RecoveredMissCount} | {result.NewRiskCount} | {result.SimilaritySeparation:F4} | {result.Recommendation} |");
        }

        builder.AppendLine();
    }

    private static string Empty(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value;
    }

    private static string Sanitize(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value.Replace("|", "/", StringComparison.Ordinal);
    }

    private enum CharacterClass
    {
        Other,
        AlphaNumeric,
        Cjk
    }

    private sealed record SamplePreview(
        ContextEvalSample Sample,
        VectorQueryPreviewResult Configured,
        VectorQueryPreviewResult Diagnostic,
        VectorQueryShadowEvalSample SampleResult);

    private sealed class TemporaryVectorIndexStore : IVectorIndexStore
    {
        private readonly Dictionary<string, VectorIndexEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

        public Task UpsertAsync(VectorIndexEntry entry, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _entries[Key(entry.WorkspaceId, entry.CollectionId, entry.EntryId)] = Clone(entry);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string workspaceId, string collectionId, string entryId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _entries.Remove(Key(workspaceId, collectionId, entryId));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<VectorIndexEntry>> GetByItemIdAsync(
            string workspaceId,
            string collectionId,
            string itemId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<VectorIndexEntry>>(_entries.Values
                .Where(entry => MatchesScope(entry, workspaceId, collectionId))
                .Where(entry => string.Equals(entry.ItemId, itemId, StringComparison.OrdinalIgnoreCase))
                .Select(entry => Clone(entry))
                .ToArray());
        }

        public Task<IReadOnlyList<VectorIndexEntry>> ListAsync(
            VectorIndexQuery query,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<VectorIndexEntry>>(_entries.Values
                .Where(entry => MatchesQuery(entry, query))
                .OrderBy(entry => entry.ItemId, StringComparer.OrdinalIgnoreCase)
                .Skip(Math.Max(0, query.Skip))
                .Take(query.Take > 0 ? query.Take : 100)
                .Select(entry => Clone(entry, query.IncludeVector))
                .ToArray());
        }

        public Task<IReadOnlyList<VectorIndexSearchResult>> SearchAsync(
            VectorIndexSearchQuery query,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var results = _entries.Values
                .Where(entry => MatchesScope(entry, query.WorkspaceId, query.CollectionId))
                .Where(entry => string.IsNullOrWhiteSpace(query.EmbeddingProvider)
                    || string.Equals(entry.EmbeddingProvider, query.EmbeddingProvider, StringComparison.OrdinalIgnoreCase))
                .Where(entry => string.IsNullOrWhiteSpace(query.EmbeddingModel)
                    || string.Equals(entry.EmbeddingModel, query.EmbeddingModel, StringComparison.OrdinalIgnoreCase))
                .Select(entry => new
                {
                    Entry = entry,
                    Score = Cosine(query.Vector, entry.Vector)
                })
                .Where(item => query.MinScore is null || item.Score >= query.MinScore.Value)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Entry.ItemId, StringComparer.OrdinalIgnoreCase)
                .Take(query.TopK > 0 ? query.TopK : DefaultTopK)
                .Select((item, index) => new VectorIndexSearchResult
                {
                    Entry = Clone(item.Entry, query.IncludeVector),
                    Score = item.Score,
                    Rank = index + 1
                })
                .ToArray();
            return Task.FromResult<IReadOnlyList<VectorIndexSearchResult>>(results);
        }

        public Task<IReadOnlyList<VectorIndexDiagnostic>> GetDiagnosticsAsync(
            VectorIndexQuery query,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var diagnostics = new List<VectorIndexDiagnostic>();
            var entries = _entries.Values.Where(entry => MatchesQuery(entry, query)).ToArray();
            foreach (var entry in entries.Where(entry => entry.Dimension != entry.Vector.Count))
            {
                diagnostics.Add(new VectorIndexDiagnostic
                {
                    DiagnosticId = $"{VectorIndexDiagnosticTypes.DimensionMismatch}:{entry.EntryId}",
                    Type = VectorIndexDiagnosticTypes.DimensionMismatch,
                    Severity = "Warning",
                    WorkspaceId = entry.WorkspaceId,
                    CollectionId = entry.CollectionId,
                    ItemId = entry.ItemId,
                    EntryId = entry.EntryId,
                    Message = "临时 representation index 中存在维度不一致 entry。",
                    SuggestedAction = "重新生成临时 embedding。"
                });
            }

            return Task.FromResult<IReadOnlyList<VectorIndexDiagnostic>>(diagnostics);
        }

        private static bool MatchesQuery(VectorIndexEntry entry, VectorIndexQuery query)
        {
            return MatchesScope(entry, query.WorkspaceId, query.CollectionId)
                   && (string.IsNullOrWhiteSpace(query.ItemKind)
                       || string.Equals(entry.ItemKind, query.ItemKind, StringComparison.OrdinalIgnoreCase))
                   && (string.IsNullOrWhiteSpace(query.Layer)
                       || string.Equals(entry.Layer, query.Layer, StringComparison.OrdinalIgnoreCase))
                   && (string.IsNullOrWhiteSpace(query.EmbeddingModel)
                       || string.Equals(entry.EmbeddingModel, query.EmbeddingModel, StringComparison.OrdinalIgnoreCase))
                   && (string.IsNullOrWhiteSpace(query.EmbeddingProvider)
                       || string.Equals(entry.EmbeddingProvider, query.EmbeddingProvider, StringComparison.OrdinalIgnoreCase));
        }

        private static bool MatchesScope(VectorIndexEntry entry, string workspaceId, string? collectionId)
        {
            return string.Equals(entry.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase)
                   && (string.IsNullOrWhiteSpace(collectionId)
                       || string.Equals(entry.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase));
        }

        private static VectorIndexEntry Clone(VectorIndexEntry entry, bool includeVector = true)
        {
            return new VectorIndexEntry
            {
                EntryId = entry.EntryId,
                ItemId = entry.ItemId,
                ItemKind = entry.ItemKind,
                Layer = entry.Layer,
                WorkspaceId = entry.WorkspaceId,
                CollectionId = entry.CollectionId,
                ContentHash = entry.ContentHash,
                EmbeddingModel = entry.EmbeddingModel,
                EmbeddingProvider = entry.EmbeddingProvider,
                Dimension = entry.Dimension,
                Vector = includeVector ? entry.Vector.ToArray() : Array.Empty<float>(),
                CreatedAt = entry.CreatedAt,
                UpdatedAt = entry.UpdatedAt,
                Metadata = new Dictionary<string, string>(entry.Metadata, StringComparer.OrdinalIgnoreCase)
            };
        }

        private static double Cosine(IReadOnlyList<float> left, IReadOnlyList<float> right)
        {
            var length = Math.Min(left.Count, right.Count);
            if (length == 0)
            {
                return 0;
            }

            var dot = 0.0;
            var leftNorm = 0.0;
            var rightNorm = 0.0;
            for (var i = 0; i < length; i++)
            {
                dot += left[i] * right[i];
                leftNorm += left[i] * left[i];
                rightNorm += right[i] * right[i];
            }

            return leftNorm <= 0 || rightNorm <= 0
                ? 0
                : dot / (Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm));
        }

        private static string Key(string workspaceId, string collectionId, string entryId)
        {
            return $"{workspaceId}\u001f{collectionId}\u001f{entryId}";
        }
    }
}
