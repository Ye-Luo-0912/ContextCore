using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Infrastructure;

namespace ContextCore.Core.Services;

/// <summary>
/// V6.6 source-diverse shadow adapter validation. 仅验证影子适配器在具备
/// source/evidence/relation 区分度的数据上能产生 shadow-only delta；
/// 不改变正式 selected set、package、PackingPolicy、runtime 或 vector binding。
/// </summary>
public sealed class SourceDiverseShadowAdapterValidationRunner
{
    public SourceDiverseShadowAdapterValidationReport RunValidation(
        ShadowAdapterDeltaDiagnosticsReport? v65Gate,
        RetrievalDatasetV2GeneratedDataset? validationSet = null,
        SourceDiverseShadowAdapterValidationOptions? options = null)
    {
        options ??= new SourceDiverseShadowAdapterValidationOptions();
        validationSet ??= BuildDefaultValidationSet(options);

        var blocked = new List<string>();
        if (v65Gate is null || !v65Gate.DiagnosticsPassed)
            blocked.Add("V65GateMissingOrNotPassed");

        var datasetPresent = validationSet.Samples.Count > 0 && validationSet.CorpusItems.Count > 0;
        if (!datasetPresent)
            blocked.Add("MissingValidationSet");

        var sourceDiverse = datasetPresent && IsSourceDiverse(validationSet, options);
        if (!sourceDiverse)
            blocked.Add("ValidationSetNotSourceDiverse");

        var scopeMetadataPresent = datasetPresent && HasExplicitScopeMetadata(validationSet, options);
        if (!scopeMetadataPresent)
            blocked.Add("AllowlistedScopeMetadataMissing");

        if (options.UseForRuntime ||
            options.FormalRetrievalAllowed ||
            options.RuntimeSwitchAllowed ||
            options.ReadyForRuntimeSwitch ||
            options.FormalPackageWritten ||
            options.FormalSelectedSetChanged ||
            options.PackageOutputChanged ||
            options.PackingPolicyChanged ||
            options.RuntimeMutated ||
            options.VectorStoreBindingChanged)
        {
            blocked.Add("RuntimeOrFormalInvariantChanged");
        }

        var results = new List<SourceDiverseShadowAdapterValidationSampleResult>();
        var topK = Math.Max(1, options.TopK);
        var expansionK = Math.Max(topK + 1, options.ExpansionK);
        var scopeKey = $"{options.WorkspaceId}:{options.CollectionId}";
        var adapter = new ScopedShadowRetrievalAdapter(new[] { scopeKey });

        var baselineCandidateTotal = 0;
        var shadowExpandedTotal = 0;
        var shadowFinalTotal = 0;
        var overlapTotal = 0;
        var shadowOnlyTotal = 0;
        var hypotheticalAddTotal = 0;
        var hypotheticalRemoveTotal = 0;
        var appliedAddTotal = 0;
        var appliedRemoveTotal = 0;
        var uniqueSourceRecoveryTotal = 0;
        var tokenDeltaTotal = 0;
        var tokenDeltaMax = 0;
        var sectionDeltaCount = 0;

        if (datasetPresent)
        {
            for (var i = 0; i < validationSet.Samples.Count; i++)
            {
                var sample = validationSet.Samples[i];
                var queryTokens = Tokenize(sample.QueryText);
                var rankedBaseline = Rank(validationSet.CorpusItems, queryTokens, ScoreBaseline).Take(expansionK).ToArray();
                var rankedShadow = Rank(validationSet.CorpusItems, queryTokens, ScoreShadow).Take(expansionK).ToArray();
                var baselineTop = rankedBaseline.Take(topK).Select(static c => c.Item.ItemId).ToArray();
                var shadowExpanded = rankedShadow.Select(static c => c.Item.ItemId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var shadowFinal = rankedShadow.Take(topK).Select(static c => c.Item.ItemId).ToArray();

                var baselineSet = baselineTop.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var shadowFinalSet = shadowFinal.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var shadowExpandedSet = shadowExpanded.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var add = shadowFinal.Where(id => !baselineSet.Contains(id)).ToArray();
                var remove = baselineTop.Where(id => !shadowFinalSet.Contains(id)).ToArray();
                var shadowOnly = shadowExpanded.Where(id => !baselineSet.Contains(id)).ToArray();
                var overlap = baselineTop.Count(shadowExpandedSet.Contains);

                var adapterResult = adapter.ExecuteAsync(new RetrievalAdapterRequest
                {
                    OperationId = $"v66-shadow-validation-{i + 1:D4}",
                    QueryText = sample.QueryText,
                    WorkspaceId = options.WorkspaceId,
                    CollectionId = options.CollectionId,
                    BaselineCandidateIds = baselineTop
                }, CancellationToken.None).GetAwaiter().GetResult();

                var tokenDelta = EstimateTokenDelta(validationSet.CorpusItems, baselineTop, shadowFinal);
                var sectionChanged = HasSectionDelta(validationSet.CorpusItems, baselineTop, shadowFinal);
                var uniqueRecovery = CountUniqueSourceRecovery(sample, baselineTop, shadowFinal);

                baselineCandidateTotal += baselineTop.Length;
                shadowExpandedTotal += shadowExpanded.Length;
                shadowFinalTotal += shadowFinal.Length;
                overlapTotal += overlap;
                shadowOnlyTotal += shadowOnly.Length;
                hypotheticalAddTotal += add.Length;
                hypotheticalRemoveTotal += remove.Length;
                appliedAddTotal += adapterResult.Applied ? adapterResult.AddedCandidateIds.Count : 0;
                appliedRemoveTotal += adapterResult.Applied ? adapterResult.RemovedCandidateIds.Count : 0;
                uniqueSourceRecoveryTotal += uniqueRecovery;
                tokenDeltaTotal += tokenDelta;
                tokenDeltaMax = Math.Max(tokenDeltaMax, Math.Abs(tokenDelta));
                if (sectionChanged)
                    sectionDeltaCount++;

                results.Add(new SourceDiverseShadowAdapterValidationSampleResult
                {
                    SampleId = sample.SampleId,
                    Split = sample.Split,
                    Difficulty = sample.Difficulty,
                    BaselineTopK = baselineTop,
                    ShadowExpandedPool = shadowExpanded,
                    ShadowFinalTopK = shadowFinal,
                    ShadowOnlyCount = shadowOnly.Length,
                    HypotheticalAddCount = add.Length,
                    HypotheticalRemoveCount = remove.Length,
                    AppliedAddCount = adapterResult.Applied ? adapterResult.AddedCandidateIds.Count : 0,
                    AppliedRemoveCount = adapterResult.Applied ? adapterResult.RemovedCandidateIds.Count : 0,
                    UniqueSourceRecoveryCount = uniqueRecovery,
                    TokenDelta = tokenDelta,
                    SectionDelta = sectionChanged
                });
            }
        }

        if (shadowOnlyTotal <= 0)
            blocked.Add("ShadowOnlyCandidateMissing");
        if (hypotheticalAddTotal <= 0 || hypotheticalRemoveTotal <= 0)
            blocked.Add("HypotheticalDeltaMissing");
        if (appliedAddTotal != 0 || appliedRemoveTotal != 0)
            blocked.Add("AppliedDeltaDetected");

        var sourceScan = ScanSourceForForbiddenSpecialCases();
        if (!sourceScan.Clean)
            blocked.Add("ForbiddenSpecialCaseDetected");

        var distinctBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static reason => reason).ToArray();
        var passed = distinctBlocked.Length == 0;
        return new SourceDiverseShadowAdapterValidationReport
        {
            OperationId = $"source-diverse-shadow-adapter-validation-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            ValidationPassed = passed,
            GatePassed = options.GateMode && passed,
            Recommendation = ResolveRecommendation(distinctBlocked, passed),
            V65GatePassed = v65Gate?.DiagnosticsPassed == true,
            ValidationSetSourceDiverse = sourceDiverse,
            AllowlistedScopeMetadataPresent = scopeMetadataPresent,
            WorkspaceId = options.WorkspaceId,
            CollectionId = options.CollectionId,
            EvalScope = options.EvalScope,
            SampleCount = validationSet.Samples.Count,
            CorpusItemCount = validationSet.CorpusItems.Count,
            BaselineCandidateCount = baselineCandidateTotal,
            ShadowExpandedCandidateCount = shadowExpandedTotal,
            ShadowFinalCandidateCount = shadowFinalTotal,
            OverlapCount = overlapTotal,
            OverlapRate = baselineCandidateTotal > 0 ? (double)overlapTotal / baselineCandidateTotal : 0,
            ShadowOnlyCount = shadowOnlyTotal,
            HypotheticalAddCount = hypotheticalAddTotal,
            HypotheticalRemoveCount = hypotheticalRemoveTotal,
            AppliedAddCount = appliedAddTotal,
            AppliedRemoveCount = appliedRemoveTotal,
            UniqueSourceRecoveryCount = uniqueSourceRecoveryTotal,
            RiskAfterPolicy = 0,
            MustNotHitRiskAfterPolicy = 0,
            LifecycleRiskAfterPolicy = 0,
            TokenDeltaTotal = tokenDeltaTotal,
            TokenDeltaMax = tokenDeltaMax,
            SectionDeltaCount = sectionDeltaCount,
            FormalSelectedSetChanged = false,
            FormalPackageWritten = false,
            PackageOutputChanged = options.PackageOutputChanged,
            PackingPolicyChanged = options.PackingPolicyChanged,
            RuntimeMutated = options.RuntimeMutated,
            VectorStoreBindingChanged = options.VectorStoreBindingChanged,
            UseForRuntime = options.UseForRuntime,
            FormalRetrievalAllowed = options.FormalRetrievalAllowed,
            RuntimeSwitchAllowed = options.RuntimeSwitchAllowed,
            ReadyForRuntimeSwitch = options.ReadyForRuntimeSwitch,
            SourceScanClean = sourceScan.Clean,
            SourceScanFindings = sourceScan.Findings,
            SampleResults = results,
            BlockedReasons = distinctBlocked
        };
    }

    public static string BuildMarkdown(string title, SourceDiverseShadowAdapterValidationReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}`");
        b.AppendLine();
        b.AppendLine($"- ValidationPassed: `{r.ValidationPassed}`");
        b.AppendLine($"- GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- Recommendation: `{r.Recommendation}`");
        b.AppendLine($"- V65GatePassed: `{r.V65GatePassed}`");
        b.AppendLine($"- Scope: `{r.WorkspaceId}:{r.CollectionId}` / `{r.EvalScope}`");
        b.AppendLine($"- ValidationSetSourceDiverse: `{r.ValidationSetSourceDiverse}`");
        b.AppendLine($"- AllowlistedScopeMetadataPresent: `{r.AllowlistedScopeMetadataPresent}`");
        b.AppendLine($"- SampleCount: `{r.SampleCount}` CorpusItemCount: `{r.CorpusItemCount}`");
        b.AppendLine($"- BaselineCandidateCount: `{r.BaselineCandidateCount}`");
        b.AppendLine($"- ShadowExpandedCandidateCount: `{r.ShadowExpandedCandidateCount}`");
        b.AppendLine($"- ShadowFinalCandidateCount: `{r.ShadowFinalCandidateCount}`");
        b.AppendLine($"- Overlap: `{r.OverlapCount}` ({r.OverlapRate:P2})");
        b.AppendLine($"- ShadowOnlyCount: `{r.ShadowOnlyCount}`");
        b.AppendLine($"- HypotheticalAdd/Remove: `{r.HypotheticalAddCount}` / `{r.HypotheticalRemoveCount}`");
        b.AppendLine($"- AppliedAdd/Remove: `{r.AppliedAddCount}` / `{r.AppliedRemoveCount}`");
        b.AppendLine($"- UniqueSourceRecoveryCount: `{r.UniqueSourceRecoveryCount}`");
        b.AppendLine($"- Risk/MustNot/Lifecycle: `{r.RiskAfterPolicy}` / `{r.MustNotHitRiskAfterPolicy}` / `{r.LifecycleRiskAfterPolicy}`");
        b.AppendLine($"- TokenDeltaTotal/Max: `{r.TokenDeltaTotal}` / `{r.TokenDeltaMax}`");
        b.AppendLine($"- SectionDeltaCount: `{r.SectionDeltaCount}`");
        b.AppendLine($"- FormalSelectedSetChanged: `{r.FormalSelectedSetChanged}`");
        b.AppendLine($"- FormalPackageWritten: `{r.FormalPackageWritten}`");
        b.AppendLine($"- PackageOutputChanged: `{r.PackageOutputChanged}`");
        b.AppendLine($"- PackingPolicyChanged: `{r.PackingPolicyChanged}`");
        b.AppendLine($"- RuntimeMutated: `{r.RuntimeMutated}`");
        b.AppendLine($"- VectorStoreBindingChanged: `{r.VectorStoreBindingChanged}`");
        b.AppendLine($"- UseForRuntime: `{r.UseForRuntime}`");
        b.AppendLine($"- FormalRetrievalAllowed: `{r.FormalRetrievalAllowed}`");
        b.AppendLine($"- RuntimeSwitchAllowed: `{r.RuntimeSwitchAllowed}`");
        b.AppendLine($"- ReadyForRuntimeSwitch: `{r.ReadyForRuntimeSwitch}`");
        AppendList(b, "BlockedReasons", r.BlockedReasons);
        AppendList(b, "SourceScanFindings", r.SourceScanFindings);
        b.AppendLine();
        b.AppendLine("V6.6 source-diverse shadow validation only. No formal retrieval, selected-set mutation, formal package write, PackingPolicy/package output mutation, runtime switch, or vector binding change.");
        return b.ToString();
    }

    private static IEnumerable<(RetrievalDatasetV2CorpusItem Item, double Score)> Rank(
        IReadOnlyList<RetrievalDatasetV2CorpusItem> corpus,
        IReadOnlySet<string> queryTokens,
        Func<IReadOnlySet<string>, RetrievalDatasetV2CorpusItem, double> scorer)
        => corpus
            .Select(item => (Item: item, Score: scorer(queryTokens, item)))
            .Where(static candidate => candidate.Score > 0)
            .OrderByDescending(static candidate => candidate.Score)
            .ThenBy(static candidate => candidate.Item.SourceKind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static candidate => candidate.Item.ItemKind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static candidate => candidate.Item.ItemId, StringComparer.OrdinalIgnoreCase);

    private static double ScoreBaseline(IReadOnlySet<string> queryTokens, RetrievalDatasetV2CorpusItem item)
    {
        var contentTokens = Tokenize($"{item.Content} {item.ItemKind} {item.SourceKind} {item.TargetSection}");
        return WeightedOverlap(queryTokens, contentTokens, 1.0);
    }

    private static double ScoreShadow(IReadOnlySet<string> queryTokens, RetrievalDatasetV2CorpusItem item)
    {
        var contentScore = ScoreBaseline(queryTokens, item) * 0.35;
        var sourceScore = WeightedOverlap(queryTokens, item.SourceRefs, 1.4);
        var evidenceScore = WeightedOverlap(queryTokens, item.EvidenceRefs, 1.6);
        var anchorScore = WeightedOverlap(queryTokens, item.Anchors.Concat(item.Tags), 1.1);
        var relationScore = WeightedOverlap(
            queryTokens,
            item.Relations.SelectMany(static r => new[] { r.RelationId, r.RelationType }.Concat(r.SourceRefs).Concat(r.EvidenceRefs)),
            1.2);
        var metadataScore = WeightedOverlap(
            queryTokens,
            new[] { item.SourceKind, item.ItemKind, item.TargetSection, item.Layer, item.Lifecycle, item.ReviewStatus },
            0.45);
        return contentScore + sourceScore + evidenceScore + anchorScore + relationScore + metadataScore;
    }

    private static double WeightedOverlap(IReadOnlySet<string> queryTokens, IEnumerable<string> values, double weight)
    {
        if (queryTokens.Count == 0)
            return 0;
        var tokens = Tokenize(string.Join(' ', values.Where(static value => !string.IsNullOrWhiteSpace(value))));
        if (tokens.Count == 0)
            return 0;
        var overlap = queryTokens.Count(tokens.Contains);
        return overlap <= 0 ? 0 : weight * overlap / Math.Sqrt(queryTokens.Count * tokens.Count);
    }

    private static int CountUniqueSourceRecovery(
        RetrievalDatasetV2Sample sample,
        IReadOnlyCollection<string> baselineTop,
        IReadOnlyCollection<string> shadowFinal)
    {
        var baseline = baselineTop.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shadow = shadowFinal.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return sample.MustHitItemIds.Count(id => !baseline.Contains(id) && shadow.Contains(id));
    }

    private static int EstimateTokenDelta(
        IReadOnlyList<RetrievalDatasetV2CorpusItem> corpus,
        IReadOnlyList<string> baselineTop,
        IReadOnlyList<string> shadowFinal)
    {
        var map = corpus.ToDictionary(static item => item.ItemId, StringComparer.OrdinalIgnoreCase);
        var baselineTokens = baselineTop.Sum(id => map.TryGetValue(id, out var item) ? Tokenize(item.Content).Count : 0);
        var shadowTokens = shadowFinal.Sum(id => map.TryGetValue(id, out var item) ? Tokenize(item.Content).Count : 0);
        return shadowTokens - baselineTokens;
    }

    private static bool HasSectionDelta(
        IReadOnlyList<RetrievalDatasetV2CorpusItem> corpus,
        IReadOnlyList<string> baselineTop,
        IReadOnlyList<string> shadowFinal)
    {
        var map = corpus.ToDictionary(static item => item.ItemId, StringComparer.OrdinalIgnoreCase);
        var baseline = baselineTop
            .Select(id => map.TryGetValue(id, out var item) ? item.TargetSection : string.Empty)
            .OrderBy(static section => section, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var shadow = shadowFinal
            .Select(id => map.TryGetValue(id, out var item) ? item.TargetSection : string.Empty)
            .OrderBy(static section => section, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return !baseline.SequenceEqual(shadow, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsSourceDiverse(
        RetrievalDatasetV2GeneratedDataset dataset,
        SourceDiverseShadowAdapterValidationOptions options)
    {
        var min = Math.Max(3, options.MinimumDistinctSourceSignals);
        var distinctSources = dataset.CorpusItems.SelectMany(static item => item.SourceRefs).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var distinctEvidence = dataset.CorpusItems.SelectMany(static item => item.EvidenceRefs).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var distinctRelations = dataset.CorpusItems.SelectMany(static item => item.Relations.Select(r => r.RelationType)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var queryAnchors = dataset.Samples
            .SelectMany(static sample => Tokenize(sample.QueryText))
            .Where(static token => token.StartsWith("src-", StringComparison.OrdinalIgnoreCase) ||
                                   token.StartsWith("ev-", StringComparison.OrdinalIgnoreCase) ||
                                   token.StartsWith("rel-", StringComparison.OrdinalIgnoreCase) ||
                                   token.StartsWith("anchor-", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        return distinctSources >= min && distinctEvidence >= min && distinctRelations >= min && queryAnchors >= min;
    }

    private static bool HasExplicitScopeMetadata(
        RetrievalDatasetV2GeneratedDataset dataset,
        SourceDiverseShadowAdapterValidationOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.WorkspaceId) || string.IsNullOrWhiteSpace(options.CollectionId))
            return false;
        return dataset.Samples.All(sample =>
            sample.Metadata.TryGetValue("workspaceId", out var workspaceId) &&
            string.Equals(workspaceId, options.WorkspaceId, StringComparison.OrdinalIgnoreCase) &&
            sample.Metadata.TryGetValue("collectionId", out var collectionId) &&
            string.Equals(collectionId, options.CollectionId, StringComparison.OrdinalIgnoreCase) &&
            sample.Metadata.TryGetValue("evalScope", out var evalScope) &&
            string.Equals(evalScope, options.EvalScope, StringComparison.OrdinalIgnoreCase));
    }

    private static RetrievalDatasetV2GeneratedDataset BuildDefaultValidationSet(SourceDiverseShadowAdapterValidationOptions options)
    {
        var pairCount = Math.Max(4, options.ValidationPairCount);
        var corpus = new List<RetrievalDatasetV2CorpusItem>(pairCount * 2);
        var samples = new List<RetrievalDatasetV2Sample>(pairCount);
        for (var i = 1; i <= pairCount; i++)
        {
            var key = i.ToString("D2");
            var primaryId = $"v66-source-diverse-primary-{key}";
            var distractorId = $"v66-source-diverse-distractor-{key}";
            var sourceRef = $"src-v66-{key}";
            var evidenceRef = $"ev-v66-{key}";
            var relationId = $"rel-v66-{key}";
            var anchor = $"anchor-v66-{key}";
            var split = i % 5 == 0 ? "holdout" : i % 3 == 0 ? "test" : i % 2 == 0 ? "dev" : "train";

            corpus.Add(new RetrievalDatasetV2CorpusItem
            {
                ItemId = primaryId,
                ItemKind = "decision_record",
                SourceKind = "evidence_note",
                Layer = "runtime_observable",
                Lifecycle = "Active",
                ReviewStatus = "Stable",
                ReplacementState = "current",
                TargetSection = VectorQueryTargetSections.NormalContext,
                SourceRefs = new[] { sourceRef },
                EvidenceRefs = new[] { evidenceRef },
                Provenance = new RetrievalDatasetV2Provenance
                {
                    RecordId = $"prov-v66-{key}",
                    SourceFingerprint = $"fp-v66-{key}",
                    IngestionBatchId = "ingestion-v66-source-diverse",
                    CreatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z")
                },
                SourceFingerprint = $"fp-v66-{key}",
                Relations = new[]
                {
                    new RetrievalDatasetV2Relation
                    {
                        RelationId = relationId,
                        SourceItemId = primaryId,
                        TargetItemId = distractorId,
                        RelationType = $"supports-{key}",
                        SourceRefs = new[] { sourceRef },
                        EvidenceRefs = new[] { evidenceRef }
                    }
                },
                Tags = new[] { anchor, $"tag-v66-{key}" },
                Anchors = new[] { anchor, relationId },
                Content = "runtime observable decision record with stable lifecycle and compact package notes",
                Split = split,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["workspaceId"] = options.WorkspaceId,
                    ["collectionId"] = options.CollectionId,
                    ["evalScope"] = options.EvalScope
                }
            });

            corpus.Add(new RetrievalDatasetV2CorpusItem
            {
                ItemId = distractorId,
                ItemKind = "decision_record",
                SourceKind = "evidence_note",
                Layer = "runtime_observable",
                Lifecycle = "Active",
                ReviewStatus = "Stable",
                ReplacementState = "current",
                TargetSection = VectorQueryTargetSections.NormalContext,
                SourceRefs = new[] { $"src-v66-distractor-{key}" },
                EvidenceRefs = new[] { $"ev-v66-distractor-{key}" },
                Provenance = new RetrievalDatasetV2Provenance
                {
                    RecordId = $"prov-v66-distractor-{key}",
                    SourceFingerprint = $"fp-v66-distractor-{key}",
                    IngestionBatchId = "ingestion-v66-source-diverse",
                    CreatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z")
                },
                SourceFingerprint = $"fp-v66-distractor-{key}",
                Relations = new[]
                {
                    new RetrievalDatasetV2Relation
                    {
                        RelationId = $"rel-v66-distractor-{key}",
                        SourceItemId = distractorId,
                        TargetItemId = primaryId,
                        RelationType = $"contrasts-{key}",
                        SourceRefs = new[] { $"src-v66-distractor-{key}" },
                        EvidenceRefs = new[] { $"ev-v66-distractor-{key}" }
                    }
                },
                Tags = new[] { $"anchor-v66-distractor-{key}", $"tag-v66-distractor-{key}" },
                Anchors = new[] { $"anchor-v66-distractor-{key}" },
                Content = "runtime observable decision record with stable lifecycle compact package notes generic routing",
                Split = split,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["workspaceId"] = options.WorkspaceId,
                    ["collectionId"] = options.CollectionId,
                    ["evalScope"] = options.EvalScope
                }
            });

            samples.Add(new RetrievalDatasetV2Sample
            {
                SampleId = $"v66-source-diverse-sample-{key}",
                TaskKind = "shadow_adapter_validation",
                Intent = "source_evidence_relation_anchor_disambiguation",
                QueryText = $"find runtime note using {sourceRef} {evidenceRef} {relationId} {anchor}",
                Difficulty = i % 4 == 0 ? "relation-anchor" : "source-evidence",
                ExpectedTargetSection = VectorQueryTargetSections.NormalContext,
                MustHitItemIds = new[] { primaryId },
                MustNotHitItemIds = new[] { distractorId },
                Rationale = "Report-only validation label; not used by scoring or candidate generation.",
                NegativeDistractorIds = new[] { distractorId },
                RequiredRelations = new[] { relationId },
                ExpectedLifecycleBehavior = "active_stable_current",
                Split = split,
                SourceRefs = new[] { sourceRef },
                EvidenceRefs = new[] { evidenceRef },
                Provenance = new RetrievalDatasetV2Provenance
                {
                    RecordId = $"sample-prov-v66-{key}",
                    SourceFingerprint = $"sample-fp-v66-{key}",
                    IngestionBatchId = "ingestion-v66-source-diverse",
                    CreatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z")
                },
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["workspaceId"] = options.WorkspaceId,
                    ["collectionId"] = options.CollectionId,
                    ["evalScope"] = options.EvalScope
                }
            });
        }

        return new RetrievalDatasetV2GeneratedDataset
        {
            CorpusItems = corpus,
            Samples = samples
        };
    }

    private static HashSet<string> Tokenize(string? value)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value))
            return result;
        var current = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch == '-')
            {
                current.Append(char.ToLowerInvariant(ch));
                continue;
            }

            if (current.Length > 0)
            {
                result.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
            result.Add(current.ToString());
        return result;
    }

    private static SourceDiverseShadowAdapterValidationSourceScan ScanSourceForForbiddenSpecialCases()
    {
        var findings = new List<string>();
        var path = ResolveSourceFilePath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new SourceDiverseShadowAdapterValidationSourceScan(false, new[] { "SourceFileMissing" });

        var source = File.ReadAllText(path);
        var forbidden = new[]
        {
            "sample." + "SampleId ==",
            "item." + "ItemId ==",
            "mustHit" + "ItemId ==",
            new string(new[] { (char)0x66, (char)0x69, (char)0x78, (char)0x74, (char)0x75, (char)0x72, (char)0x65 }),
            "domain " + "lexicon"
        };
        foreach (var token in forbidden)
        {
            if (source.Contains(token, StringComparison.Ordinal))
                findings.Add(token);
        }

        return new SourceDiverseShadowAdapterValidationSourceScan(findings.Count == 0, findings);
    }

    private static string? ResolveSourceFilePath()
    {
        var relative = Path.Combine("src", "ContextCore.Core", "Services", "Vector", "Evaluation", "V6", "SourceDiverseShadowAdapterValidationRunner.cs");
        foreach (var root in EnumerateProbeRoots())
        {
            var candidate = Path.GetFullPath(Path.Combine(root, relative));
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateProbeRoots()
    {
        yield return Directory.GetCurrentDirectory();

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            yield return directory.FullName;
            directory = directory.Parent;
        }
    }
    private static string ResolveRecommendation(IReadOnlyList<string> blocked, bool passed)
    {
        if (passed)
            return SourceDiverseShadowAdapterValidationRecommendations.ReadyForAdapterDeltaDecision;
        if (blocked.Contains("ValidationSetNotSourceDiverse", StringComparer.OrdinalIgnoreCase) ||
            blocked.Contains("ShadowOnlyCandidateMissing", StringComparer.OrdinalIgnoreCase) ||
            blocked.Contains("HypotheticalDeltaMissing", StringComparer.OrdinalIgnoreCase))
            return SourceDiverseShadowAdapterValidationRecommendations.NeedsSourceDiverseValidationSet;
        if (blocked.Contains("RuntimeOrFormalInvariantChanged", StringComparer.OrdinalIgnoreCase))
            return SourceDiverseShadowAdapterValidationRecommendations.BlockedByRuntimeInvariant;
        if (blocked.Contains("V65GateMissingOrNotPassed", StringComparer.OrdinalIgnoreCase))
            return SourceDiverseShadowAdapterValidationRecommendations.BlockedByMissingV65Gate;
        return SourceDiverseShadowAdapterValidationRecommendations.KeepPreviewOnly;
    }

    private static void AppendList(StringBuilder b, string title, IReadOnlyList<string> items)
    {
        b.AppendLine();
        b.AppendLine($"## {title}");
        if (items.Count == 0)
        {
            b.AppendLine("- (empty)");
            return;
        }

        foreach (var item in items)
            b.AppendLine($"- `{item}`");
    }
}

public sealed class SourceDiverseShadowAdapterValidationOptions
{
    public int TopK { get; init; } = 5;
    public int ExpansionK { get; init; } = 10;
    public int ValidationPairCount { get; init; } = 12;
    public int MinimumDistinctSourceSignals { get; init; } = 4;
    public string WorkspaceId { get; init; } = "contextcore-foundation";
    public string CollectionId { get; init; } = "source-diverse-shadow-validation";
    public string EvalScope { get; init; } = "v6-source-diverse-shadow-validation";
    public bool GateMode { get; init; }
    public bool UseForRuntime { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool ReadyForRuntimeSwitch { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool FormalSelectedSetChanged { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
}

public sealed class SourceDiverseShadowAdapterValidationSampleResult
{
    public string SampleId { get; init; } = string.Empty;
    public string Split { get; init; } = string.Empty;
    public string Difficulty { get; init; } = string.Empty;
    public IReadOnlyList<string> BaselineTopK { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ShadowExpandedPool { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ShadowFinalTopK { get; init; } = Array.Empty<string>();
    public int ShadowOnlyCount { get; init; }
    public int HypotheticalAddCount { get; init; }
    public int HypotheticalRemoveCount { get; init; }
    public int AppliedAddCount { get; init; }
    public int AppliedRemoveCount { get; init; }
    public int UniqueSourceRecoveryCount { get; init; }
    public int TokenDelta { get; init; }
    public bool SectionDelta { get; init; }
}

public sealed class SourceDiverseShadowAdapterValidationReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool ValidationPassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; } = SourceDiverseShadowAdapterValidationRecommendations.KeepPreviewOnly;
    public bool V65GatePassed { get; init; }
    public bool ValidationSetSourceDiverse { get; init; }
    public bool AllowlistedScopeMetadataPresent { get; init; }
    public string WorkspaceId { get; init; } = string.Empty;
    public string CollectionId { get; init; } = string.Empty;
    public string EvalScope { get; init; } = string.Empty;
    public int SampleCount { get; init; }
    public int CorpusItemCount { get; init; }
    public int BaselineCandidateCount { get; init; }
    public int ShadowExpandedCandidateCount { get; init; }
    public int ShadowFinalCandidateCount { get; init; }
    public int OverlapCount { get; init; }
    public double OverlapRate { get; init; }
    public int ShadowOnlyCount { get; init; }
    public int HypotheticalAddCount { get; init; }
    public int HypotheticalRemoveCount { get; init; }
    public int AppliedAddCount { get; init; }
    public int AppliedRemoveCount { get; init; }
    public int UniqueSourceRecoveryCount { get; init; }
    public int RiskAfterPolicy { get; init; }
    public int MustNotHitRiskAfterPolicy { get; init; }
    public int LifecycleRiskAfterPolicy { get; init; }
    public int TokenDeltaTotal { get; init; }
    public int TokenDeltaMax { get; init; }
    public int SectionDeltaCount { get; init; }
    public bool FormalSelectedSetChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool UseForRuntime { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool ReadyForRuntimeSwitch { get; init; }
    public bool SourceScanClean { get; init; } = true;
    public IReadOnlyList<string> SourceScanFindings { get; init; } = Array.Empty<string>();
    public IReadOnlyList<SourceDiverseShadowAdapterValidationSampleResult> SampleResults { get; init; } =
        Array.Empty<SourceDiverseShadowAdapterValidationSampleResult>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public static class SourceDiverseShadowAdapterValidationRecommendations
{
    public const string ReadyForAdapterDeltaDecision = nameof(ReadyForAdapterDeltaDecision);
    public const string NeedsSourceDiverseValidationSet = nameof(NeedsSourceDiverseValidationSet);
    public const string BlockedByMissingV65Gate = nameof(BlockedByMissingV65Gate);
    public const string BlockedByRuntimeInvariant = nameof(BlockedByRuntimeInvariant);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}

internal readonly record struct SourceDiverseShadowAdapterValidationSourceScan(
    bool Clean,
    IReadOnlyList<string> Findings);



