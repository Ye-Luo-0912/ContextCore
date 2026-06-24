using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// V5.5 Retrieval Quality Repair Preview。
/// 在 V5.4 安全门通过后，针对 MissingCandidate / RankingTooLow 失败簇生成多个
/// repair profile，对比 baseline，挑出 best repair profile 用于后续 freeze。
/// 只读：不接 formal retrieval、不写 formal package、不动 formal selected set、
/// 不改 PackingPolicy / package output、不切 runtime、不绑定 IVectorIndexStore。
/// post-scoring risk gate 始终最后执行。
/// </summary>
public sealed class RetrievalQualityRepairPreviewRunner
{
    private const string DefaultGraphCandidateSource = "read-only relation evidence / expansion preview";
    private const double Epsilon = 1e-6;

    public RetrievalQualityRepairPreviewReport BuildPreview(
        GraphVectorRetrievalQualityAuditReport? qualityGate,
        FormalAdapterPackageShadowComparisonReport? packageShadowGate,
        RetrievalDatasetV2GeneratedDataset? dataset,
        RetrievalQualityRepairPreviewOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => Build(qualityGate, packageShadowGate, dataset, options, sourceReports, gateMode: false);

    public RetrievalQualityRepairPreviewReport BuildGate(
        GraphVectorRetrievalQualityAuditReport? qualityGate,
        FormalAdapterPackageShadowComparisonReport? packageShadowGate,
        RetrievalDatasetV2GeneratedDataset? dataset,
        RetrievalQualityRepairPreviewOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => Build(qualityGate, packageShadowGate, dataset, options, sourceReports, gateMode: true);

    public static string BuildMarkdown(string title, RetrievalQualityRepairPreviewReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine($"# {title}");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{report.CreatedAt:O}`");
        builder.AppendLine($"OperationId: `{report.OperationId}`");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine();
        builder.AppendLine($"- PreviewPassed: `{report.PreviewPassed}`");
        builder.AppendLine($"- GatePassed: `{report.GatePassed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- AllowedMode: `{report.AllowedMode}`");
        builder.AppendLine($"- RequiredNextPhase: `{report.RequiredNextPhase}`");
        builder.AppendLine($"- VectorProviderSource: `{report.VectorProviderSource}`");
        builder.AppendLine($"- GraphCandidateSource: `{report.GraphCandidateSource}`");
        builder.AppendLine($"- TopK: `{report.TopK}`");
        builder.AppendLine($"- MaxTokenDeltaTotal: `{report.MaxTokenDeltaTotal}`");
        builder.AppendLine($"- MaxTokenDeltaPerSample: `{report.MaxTokenDeltaPerSample}`");
        builder.AppendLine($"- BestProfileId: `{(string.IsNullOrEmpty(report.BestProfileId) ? "-" : report.BestProfileId)}`");
        builder.AppendLine($"- FormalOutputChanged: `{report.FormalOutputChanged}`");
        builder.AppendLine($"- FormalSelectedSetChanged: `{report.FormalSelectedSetChanged}`");
        builder.AppendLine($"- FormalPackageWritten: `{report.FormalPackageWritten}`");
        builder.AppendLine($"- ShadowPackageWritten: `{report.ShadowPackageWritten}`");
        builder.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}`");
        builder.AppendLine($"- PackingPolicyChanged: `{report.PackingPolicyChanged}`");
        builder.AppendLine($"- RuntimeMutated: `{report.RuntimeMutated}`");
        builder.AppendLine($"- VectorStoreBindingChanged: `{report.VectorStoreBindingChanged}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- RuntimeSwitchAllowed: `{report.RuntimeSwitchAllowed}`");
        builder.AppendLine($"- ReadyForRuntimeSwitch: `{report.ReadyForRuntimeSwitch}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- NoRuntimeMutationInvariant: `{report.NoRuntimeMutationInvariant}`");
        AppendBaseline(builder, report.Baseline);
        AppendProfileTable(builder, report.Profiles);
        AppendList(builder, "Blocked Reasons", report.BlockedReasons);
        AppendMap(builder, "Source Reports", report.SourceReports);
        builder.AppendLine();
        builder.AppendLine("V5.5 preview only. No formal retrieval, formal package write, formal selected set change, package output mutation, packing policy mutation, runtime switch, or vector store binding change.");
        return builder.ToString();
    }

    private static RetrievalQualityRepairPreviewReport Build(
        GraphVectorRetrievalQualityAuditReport? qualityGate,
        FormalAdapterPackageShadowComparisonReport? packageShadowGate,
        RetrievalDatasetV2GeneratedDataset? dataset,
        RetrievalQualityRepairPreviewOptions? options,
        IReadOnlyDictionary<string, string>? sourceReports,
        bool gateMode)
    {
        options ??= new RetrievalQualityRepairPreviewOptions();
        var profileName = string.IsNullOrWhiteSpace(options.ProfileName)
            ? HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1
            : options.ProfileName;
        var blocked = new List<string>();

        if (qualityGate is null)
        {
            blocked.Add("QualityGateMissing");
        }
        else
        {
            if (options.RequireQualityGatePassed && !qualityGate.GatePassed)
            {
                blocked.Add("QualityGateNotPassed");
            }

            AddBoundaryBlocks(blocked, "QualityGate",
                qualityGate.FormalRetrievalAllowed,
                qualityGate.RuntimeSwitchAllowed,
                qualityGate.ReadyForRuntimeSwitch,
                qualityGate.UseForRuntime,
                qualityGate.PackageOutputChanged,
                qualityGate.PackingPolicyChanged,
                qualityGate.RuntimeMutated,
                qualityGate.VectorStoreBindingChanged,
                qualityGate.FormalPackageWritten);
        }

        if (packageShadowGate is not null)
        {
            if (options.RequirePackageShadowGatePassed && !packageShadowGate.GatePassed)
            {
                blocked.Add("PackageShadowGateNotPassed");
            }

            AddBoundaryBlocks(blocked, "PackageShadowGate",
                packageShadowGate.FormalRetrievalAllowed,
                packageShadowGate.RuntimeSwitchAllowed,
                packageShadowGate.ReadyForRuntimeSwitch,
                packageShadowGate.UseForRuntime,
                packageShadowGate.PackageOutputChanged,
                packageShadowGate.PackingPolicyChanged,
                packageShadowGate.RuntimeMutated,
                packageShadowGate.VectorStoreBindingChanged,
                packageShadowGate.FormalPackageWritten);
        }

        var hasDataset = dataset is not null && dataset.Samples.Count > 0 && dataset.CorpusItems.Count > 0;
        if (!hasDataset)
        {
            blocked.Add("MissingDataset");
        }

        if (!string.Equals(profileName, HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1, StringComparison.OrdinalIgnoreCase))
        {
            blocked.Add("VectorProviderSourceNotPostScoringRiskGatedV1");
        }

        if (options.UseForRuntime || options.FormalRetrievalAllowed)
        {
            blocked.Add("RuntimeMutationAttempt");
        }

        var topK = Math.Max(1, options.TopK);

        var profileResults = new List<RetrievalQualityRepairProfileResult>();
        var baseline = new RetrievalQualityRepairProfileResult();
        if (hasDataset)
        {
            var profileKnobs = BuildProfileKnobs(options);
            var baselineKnobs = profileKnobs[0];
            baseline = EvaluateProfile(dataset!, profileName, topK, baselineKnobs, options, baselineMetrics: null);
            profileResults.Add(baseline);
            for (var i = 1; i < profileKnobs.Length; i++)
            {
                var profile = EvaluateProfile(dataset!, profileName, topK, profileKnobs[i], options, baseline);
                profileResults.Add(profile);
            }

            if (baseline.SampleCount == 0)
            {
                blocked.Add("EmptyRepairOutput");
            }

            if (baseline.RiskAfterPolicy > 0
                || baseline.MustNotHitRiskAfterPolicy > 0
                || baseline.LifecycleRiskAfterPolicy > 0
                || baseline.SectionMismatchCount > 0)
            {
                if (baseline.MustNotHitRiskAfterPolicy > 0)
                {
                    blocked.Add("MustNotHitRiskAfterPolicyNonZero");
                }

                if (baseline.LifecycleRiskAfterPolicy > 0)
                {
                    blocked.Add("LifecycleRiskAfterPolicyNonZero");
                }

                if (baseline.SectionMismatchCount > 0)
                {
                    blocked.Add("SectionMismatchDetected");
                }

                if (baseline.RiskAfterPolicy > 0)
                {
                    blocked.Add("RiskAfterPolicyNonZero");
                }
            }
        }

        var bestProfileId = string.Empty;
        if (hasDataset && profileResults.Count > 1)
        {
            bestProfileId = SelectBestProfile(baseline, profileResults);
        }

        if (gateMode && hasDataset && string.IsNullOrEmpty(bestProfileId))
        {
            blocked.Add("NoRepairProfileImproved");
        }

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static reason => reason, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var passed = distinctBlocked.Length == 0;
        var operationId = (gateMode
            ? "retrieval-quality-repair-gate-"
            : "retrieval-quality-repair-preview-")
            + Guid.NewGuid().ToString("N");

        return new RetrievalQualityRepairPreviewReport
        {
            OperationId = operationId,
            CreatedAt = DateTimeOffset.UtcNow,
            PreviewPassed = passed,
            GatePassed = gateMode && passed,
            Recommendation = ResolveRecommendation(distinctBlocked, passed, bestProfileId),
            AllowedMode = "PreviewOnly",
            RequiredNextPhase = "RetrievalQualityRepairFreeze",
            VectorProviderSource = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            GraphCandidateSource = DefaultGraphCandidateSource,
            TopK = topK,
            MaxTokenDeltaTotal = options.MaxTokenDeltaTotal,
            MaxTokenDeltaPerSample = options.MaxTokenDeltaPerSample,
            Baseline = baseline,
            Profiles = profileResults,
            BestProfileId = bestProfileId,
            FormalOutputChanged = 0,
            FormalSelectedSetChanged = false,
            FormalPackageWritten = false,
            ShadowPackageWritten = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            RuntimeMutated = false,
            VectorStoreBindingChanged = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false,
            NoRuntimeMutationInvariant = true,
            SourceReports = sourceReports ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            BlockedReasons = distinctBlocked
        };
    }

    private static ProfileKnobs[] BuildProfileKnobs(RetrievalQualityRepairPreviewOptions options)
    {
        return new[]
        {
            new ProfileKnobs(
                ProfileId: RetrievalQualityRepairProfiles.Baseline,
                Label: "Baseline",
                VectorTopK: options.BaselineVectorTopK,
                GraphTopK: options.BaselineGraphTopK,
                MergedTopK: options.BaselineMergedTopK,
                SectionBoost: 1.0,
                EvidenceBoost: 1.0,
                RelationBoost: 1.0,
                LexicalBoost: 1.0,
                AppliedAdjustments: Array.Empty<string>()),
            new ProfileKnobs(
                ProfileId: RetrievalQualityRepairProfiles.CandidatePoolExpansion,
                Label: "Candidate pool expansion",
                VectorTopK: options.ExpansionVectorTopK,
                GraphTopK: options.ExpansionGraphTopK,
                MergedTopK: options.ExpansionMergedTopK,
                SectionBoost: 1.0,
                EvidenceBoost: 1.0,
                RelationBoost: 1.0,
                LexicalBoost: 1.0,
                AppliedAdjustments: new[]
                {
                    $"VectorTopK={options.ExpansionVectorTopK}",
                    $"GraphTopK={options.ExpansionGraphTopK}",
                    $"MergedTopK={options.ExpansionMergedTopK}"
                }),
            new ProfileKnobs(
                ProfileId: RetrievalQualityRepairProfiles.TopKAdjustment,
                Label: "TopK adjustment (merged-only)",
                VectorTopK: options.BaselineVectorTopK,
                GraphTopK: options.BaselineGraphTopK,
                MergedTopK: options.AdjustedTopK,
                SectionBoost: 1.0,
                EvidenceBoost: 1.0,
                RelationBoost: 1.0,
                LexicalBoost: 1.0,
                AppliedAdjustments: new[]
                {
                    $"MergedTopK={options.AdjustedTopK}"
                }),
            new ProfileKnobs(
                ProfileId: RetrievalQualityRepairProfiles.SectionAwareBoost,
                Label: "Section-aware boost",
                VectorTopK: options.BaselineVectorTopK,
                GraphTopK: options.BaselineGraphTopK,
                MergedTopK: options.BaselineMergedTopK,
                SectionBoost: options.SectionBoost,
                EvidenceBoost: 1.0,
                RelationBoost: 1.0,
                LexicalBoost: 1.0,
                AppliedAdjustments: new[]
                {
                    $"SectionBoost={options.SectionBoost:F2}"
                }),
            new ProfileKnobs(
                ProfileId: RetrievalQualityRepairProfiles.MustHitEvidenceBoost,
                Label: "Must-hit evidence boost",
                VectorTopK: options.BaselineVectorTopK,
                GraphTopK: options.BaselineGraphTopK,
                MergedTopK: options.BaselineMergedTopK,
                SectionBoost: 1.0,
                EvidenceBoost: options.MustHitEvidenceBoost,
                RelationBoost: 1.0,
                LexicalBoost: 1.0,
                AppliedAdjustments: new[]
                {
                    $"EvidenceBoost={options.MustHitEvidenceBoost:F2}"
                }),
            new ProfileKnobs(
                ProfileId: RetrievalQualityRepairProfiles.GraphRelationAnchorBoost,
                Label: "Graph relation anchor boost",
                VectorTopK: options.BaselineVectorTopK,
                GraphTopK: options.BaselineGraphTopK,
                MergedTopK: options.BaselineMergedTopK,
                SectionBoost: 1.0,
                EvidenceBoost: 1.0,
                RelationBoost: options.GraphRelationAnchorBoost,
                LexicalBoost: 1.0,
                AppliedAdjustments: new[]
                {
                    $"RelationBoost={options.GraphRelationAnchorBoost:F2}"
                }),
            new ProfileKnobs(
                ProfileId: RetrievalQualityRepairProfiles.LexicalFallbackBoost,
                Label: "Lexical fallback boost",
                VectorTopK: options.BaselineVectorTopK,
                GraphTopK: options.BaselineGraphTopK,
                MergedTopK: options.BaselineMergedTopK,
                SectionBoost: 1.0,
                EvidenceBoost: 1.0,
                RelationBoost: 1.0,
                LexicalBoost: options.LexicalFallbackBoost,
                AppliedAdjustments: new[]
                {
                    $"LexicalBoost={options.LexicalFallbackBoost:F2}"
                }),
            new ProfileKnobs(
                ProfileId: RetrievalQualityRepairProfiles.Combined,
                Label: "Combined repair",
                VectorTopK: options.ExpansionVectorTopK,
                GraphTopK: options.ExpansionGraphTopK,
                MergedTopK: options.ExpansionMergedTopK,
                SectionBoost: options.SectionBoost,
                EvidenceBoost: options.MustHitEvidenceBoost,
                RelationBoost: options.GraphRelationAnchorBoost,
                LexicalBoost: options.LexicalFallbackBoost,
                AppliedAdjustments: new[]
                {
                    $"VectorTopK={options.ExpansionVectorTopK}",
                    $"GraphTopK={options.ExpansionGraphTopK}",
                    $"MergedTopK={options.ExpansionMergedTopK}",
                    $"SectionBoost={options.SectionBoost:F2}",
                    $"EvidenceBoost={options.MustHitEvidenceBoost:F2}",
                    $"RelationBoost={options.GraphRelationAnchorBoost:F2}",
                    $"LexicalBoost={options.LexicalFallbackBoost:F2}"
                })
        };
    }

    private static RetrievalQualityRepairProfileResult EvaluateProfile(
        RetrievalDatasetV2GeneratedDataset dataset,
        string profileName,
        int topK,
        ProfileKnobs knobs,
        RetrievalQualityRepairPreviewOptions options,
        RetrievalQualityRepairProfileResult? baselineMetrics)
    {
        var totals = new ProfileTotals();
        var perSampleBudgetExceeded = false;
        foreach (var sample in dataset.Samples)
        {
            var vectorRanked = RankCandidates(
                sample,
                dataset.CorpusItems,
                profileName,
                Math.Max(1, knobs.VectorTopK),
                knobs);
            var denseRanked = RankCandidates(
                sample,
                dataset.CorpusItems,
                "dense-only",
                Math.Max(1, knobs.VectorTopK),
                ProfileKnobs.None);
            var graphRanked = CollectGraphCandidates(
                sample,
                dataset.CorpusItems,
                Math.Max(1, knobs.GraphTopK));
            var merged = MergeCandidates(vectorRanked, graphRanked, Math.Max(1, knobs.MergedTopK));

            var vectorIds = vectorRanked.Select(c => c.ItemId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var graphIds = graphRanked.Select(c => c.ItemId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var overlap = vectorIds.Intersect(graphIds, StringComparer.OrdinalIgnoreCase).Count();
            totals.VectorContribution += vectorRanked.Count;
            totals.GraphContribution += graphRanked.Count;
            totals.Overlap += overlap;
            totals.VectorOnly += Math.Max(0, vectorIds.Count - overlap);
            totals.GraphOnly += Math.Max(0, graphIds.Count - overlap);

            var topKWindow = merged.Take(topK).ToArray();
            var topKWindowIds = topKWindow.Select(c => c.ItemId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var mergedRankByItem = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < merged.Count; i++)
            {
                mergedRankByItem[merged[i].ItemId] = i + 1;
            }

            var denseRankByItem = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < denseRanked.Count; i++)
            {
                denseRankByItem[denseRanked[i].ItemId] = i + 1;
            }

            // Recall / precision / MRR
            var mustHits = sample.MustHitItemIds;
            var mustHitTotal = mustHits.Count;
            var mustHitRecalled = 0;
            var firstMustHitRank = 0;
            var mustHitBelowTopK = 0;
            var rankingRegression = 0;
            foreach (var mustHitId in mustHits)
            {
                if (topKWindowIds.Contains(mustHitId))
                {
                    mustHitRecalled++;
                    if (firstMustHitRank == 0
                        && mergedRankByItem.TryGetValue(mustHitId, out var rank))
                    {
                        firstMustHitRank = rank;
                    }
                }

                var mergedRank = mergedRankByItem.TryGetValue(mustHitId, out var mr) ? mr : int.MaxValue;
                var denseRank = denseRankByItem.TryGetValue(mustHitId, out var dr) ? dr : int.MaxValue;
                if (mergedRank > denseRank)
                {
                    rankingRegression++;
                }

                if (mergedRank != int.MaxValue && mergedRank > topK)
                {
                    mustHitBelowTopK++;
                }
            }

            var recall = mustHitTotal == 0 ? 0d : (double)mustHitRecalled / mustHitTotal;
            var precision = topKWindow.Length == 0 ? 0d : (double)mustHitRecalled / topKWindow.Length;
            var mrr = firstMustHitRank == 0 ? 0d : 1d / firstMustHitRank;

            totals.SampleCount++;
            totals.MustHitTotal += mustHitTotal;
            totals.MustHitRecalledTotal += mustHitRecalled;
            totals.RecallSum += recall;
            totals.PrecisionSum += precision;
            totals.MrrSum += mrr;
            totals.MustHitBelowTopK += mustHitBelowTopK;
            totals.RankingRegression += rankingRegression;

            // Graph noise
            var requiredRelationSet = new HashSet<string>(sample.RequiredRelations, StringComparer.OrdinalIgnoreCase);
            var mustHitSet = new HashSet<string>(sample.MustHitItemIds, StringComparer.OrdinalIgnoreCase);
            foreach (var graphItem in graphRanked)
            {
                var hasRelationLink = graphItem.Relations.Any(rel => requiredRelationSet.Contains(rel.RelationId));
                var hasMustHitLink = mustHitSet.Contains(graphItem.ItemId);
                if (!hasRelationLink && !hasMustHitLink)
                {
                    totals.GraphNoise++;
                }
            }

            // Vector noise: vector candidate that violates eligibility despite the post-scoring-risk-gated profile filter.
            foreach (var vectorItem in vectorRanked)
            {
                if (IsBlockedByEligibility(sample, vectorItem))
                {
                    totals.VectorNoise++;
                }
            }

            // Risk counts on merged top-K window.
            foreach (var item in topKWindow)
            {
                if (sample.MustNotHitItemIds.Contains(item.ItemId, StringComparer.OrdinalIgnoreCase))
                {
                    totals.MustNotHitRisk++;
                }

                if (!string.Equals(item.TargetSection, sample.ExpectedTargetSection, StringComparison.OrdinalIgnoreCase))
                {
                    totals.SectionMismatch++;
                }

                if (IsLifecycleRisk(item))
                {
                    totals.LifecycleRisk++;
                }

                if (IsRisk(sample, item))
                {
                    totals.RiskAfterPolicy++;
                }
            }

            // Token totals (current profile and baseline-equivalent, both using same items).
            // For cross-profile delta we use baselineMetrics.RepairTokenTotal as the reference.
            var profileTokens = topKWindow.Sum(EstimateTokens);
            totals.RepairTokenTotal += profileTokens;
        }

        var sampleCount = totals.SampleCount;
        var avgRecall = sampleCount == 0 ? 0d : totals.RecallSum / sampleCount;
        var avgPrecision = sampleCount == 0 ? 0d : totals.PrecisionSum / sampleCount;
        var avgMrr = sampleCount == 0 ? 0d : totals.MrrSum / sampleCount;

        var baselineTokens = baselineMetrics is null ? totals.RepairTokenTotal : baselineMetrics.RepairTokenTotal;
        var tokenDelta = totals.RepairTokenTotal - baselineTokens;
        var tokenDeltaAbsolute = Math.Abs(tokenDelta);
        var perSamplePerturbation = sampleCount == 0
            ? 0
            : (int)Math.Round((double)tokenDeltaAbsolute / sampleCount);
        if (perSamplePerturbation > options.MaxTokenDeltaPerSample)
        {
            perSampleBudgetExceeded = true;
        }

        var recallDelta = baselineMetrics is null ? 0d : avgRecall - baselineMetrics.Recall;
        var precisionDelta = baselineMetrics is null ? 0d : avgPrecision - baselineMetrics.Precision;
        var mrrDelta = baselineMetrics is null ? 0d : avgMrr - baselineMetrics.MeanReciprocalRank;
        var mustHitBelowTopKDelta = baselineMetrics is null
            ? 0
            : totals.MustHitBelowTopK - baselineMetrics.MustHitBelowTopKCount;

        var recallRegression = baselineMetrics is not null && recallDelta < -Epsilon;
        var mrrRegression = baselineMetrics is not null && mrrDelta < -Epsilon;
        var graphNoiseRegression = baselineMetrics is not null
            && totals.GraphNoise > baselineMetrics.GraphNoiseCount;
        var rankingRegressionDetected = baselineMetrics is not null
            && totals.RankingRegression > baselineMetrics.RankingRegressionCount;
        var riskRegression = totals.RiskAfterPolicy > 0
            || totals.MustNotHitRisk > 0
            || totals.LifecycleRisk > 0
            || totals.SectionMismatch > 0;
        var tokenBudgetExceeded = perSampleBudgetExceeded
            || tokenDeltaAbsolute > options.MaxTokenDeltaTotal;

        return new RetrievalQualityRepairProfileResult
        {
            ProfileId = knobs.ProfileId,
            ProfileLabel = knobs.Label,
            SampleCount = sampleCount,
            MustHitTotal = totals.MustHitTotal,
            MustHitRecalledTotal = totals.MustHitRecalledTotal,
            Recall = avgRecall,
            Precision = avgPrecision,
            MeanReciprocalRank = avgMrr,
            MustHitBelowTopKCount = totals.MustHitBelowTopK,
            VectorContributionCount = totals.VectorContribution,
            GraphContributionCount = totals.GraphContribution,
            OverlapCount = totals.Overlap,
            VectorOnlyCount = totals.VectorOnly,
            GraphOnlyCount = totals.GraphOnly,
            GraphNoiseCount = totals.GraphNoise,
            VectorNoiseCount = totals.VectorNoise,
            RankingRegressionCount = totals.RankingRegression,
            RiskAfterPolicy = totals.RiskAfterPolicy,
            MustNotHitRiskAfterPolicy = totals.MustNotHitRisk,
            LifecycleRiskAfterPolicy = totals.LifecycleRisk,
            SectionMismatchCount = totals.SectionMismatch,
            BaselineTokenTotal = baselineTokens,
            RepairTokenTotal = totals.RepairTokenTotal,
            TokenDelta = tokenDelta,
            TokenDeltaAbsolute = tokenDeltaAbsolute,
            RecallDelta = recallDelta,
            PrecisionDelta = precisionDelta,
            MrrDelta = mrrDelta,
            MustHitBelowTopKDelta = mustHitBelowTopKDelta,
            RiskRegressionDetected = riskRegression,
            RankingRegressionDetected = rankingRegressionDetected,
            RecallRegressionDetected = recallRegression,
            MrrRegressionDetected = mrrRegression,
            GraphNoiseRegressionDetected = graphNoiseRegression,
            TokenBudgetExceeded = tokenBudgetExceeded,
            AppliedAdjustments = knobs.AppliedAdjustments
        };
    }

    private static string SelectBestProfile(
        RetrievalQualityRepairProfileResult baseline,
        IReadOnlyList<RetrievalQualityRepairProfileResult> profiles)
    {
        var candidates = profiles
            .Skip(1)
            .Where(p => !p.RecallRegressionDetected
                && !p.MrrRegressionDetected
                && !p.GraphNoiseRegressionDetected
                && !p.RankingRegressionDetected
                && !p.RiskRegressionDetected
                && !p.TokenBudgetExceeded
                && (p.Recall > baseline.Recall + Epsilon
                    || p.MeanReciprocalRank > baseline.MeanReciprocalRank + Epsilon))
            .OrderByDescending(p => p.Recall)
            .ThenByDescending(p => p.MeanReciprocalRank)
            .ThenBy(p => p.TokenDeltaAbsolute)
            .ThenBy(p => p.ProfileId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return candidates.Length == 0 ? string.Empty : candidates[0].ProfileId;
    }

    private static void AddBoundaryBlocks(
        List<string> blocked,
        string prefix,
        bool formalRetrievalAllowed,
        bool runtimeSwitchAllowed,
        bool readyForRuntimeSwitch,
        bool useForRuntime,
        bool packageOutputChanged,
        bool packingPolicyChanged,
        bool runtimeMutated,
        bool vectorStoreBindingChanged,
        bool formalPackageWritten)
    {
        if (formalRetrievalAllowed)
        {
            blocked.Add($"{prefix}FormalRetrievalAllowed");
        }

        if (runtimeSwitchAllowed || readyForRuntimeSwitch || useForRuntime)
        {
            blocked.Add($"{prefix}RuntimeSwitchAllowed");
        }

        if (packageOutputChanged)
        {
            blocked.Add($"{prefix}PackageOutputChanged");
        }

        if (packingPolicyChanged)
        {
            blocked.Add($"{prefix}PackingPolicyChanged");
        }

        if (runtimeMutated)
        {
            blocked.Add($"{prefix}RuntimeMutated");
        }

        if (vectorStoreBindingChanged)
        {
            blocked.Add($"{prefix}VectorStoreBindingChanged");
        }

        if (formalPackageWritten)
        {
            blocked.Add($"{prefix}FormalPackageWritten");
        }
    }

    private static IReadOnlyList<RetrievalDatasetV2CorpusItem> RankCandidates(
        RetrievalDatasetV2Sample sample,
        IReadOnlyList<RetrievalDatasetV2CorpusItem> corpusItems,
        string profileName,
        int topK,
        ProfileKnobs knobs)
    {
        var queryTokens = Tokenize(sample.QueryText);
        var negativeTokens = ExtractNegativeCueTokens(sample.QueryText);
        var sampleEvidence = new HashSet<string>(sample.EvidenceRefs, StringComparer.OrdinalIgnoreCase);
        var sampleSource = new HashSet<string>(sample.SourceRefs, StringComparer.OrdinalIgnoreCase);
        var sampleRequiredRelations = new HashSet<string>(sample.RequiredRelations, StringComparer.OrdinalIgnoreCase);
        var scored = corpusItems
            .Select(item =>
            {
                var dense = DenseScore(queryTokens, item);
                var lexical = LexicalScore(queryTokens, item);
                var anchor = AnchorScore(queryTokens, item);
                var negative = NegativeCueOverlap(negativeTokens, item);
                var baseScore = ScoreForProfile(profileName, dense, lexical, anchor, negative);
                if (baseScore <= 0)
                {
                    return new ScoredItem(item, 0);
                }

                var multiplier = 1.0;
                if (knobs.SectionBoost > 1.0
                    && string.Equals(item.TargetSection, sample.ExpectedTargetSection, StringComparison.OrdinalIgnoreCase))
                {
                    multiplier *= knobs.SectionBoost;
                }

                if (knobs.EvidenceBoost > 1.0
                    && (item.EvidenceRefs.Any(reference => sampleEvidence.Contains(reference))
                        || item.SourceRefs.Any(reference => sampleSource.Contains(reference))))
                {
                    multiplier *= knobs.EvidenceBoost;
                }

                if (knobs.RelationBoost > 1.0
                    && item.Relations.Any(rel => sampleRequiredRelations.Contains(rel.RelationId)))
                {
                    multiplier *= knobs.RelationBoost;
                }

                if (knobs.LexicalBoost > 1.0 && dense > 0)
                {
                    var ratio = lexical / dense;
                    if (ratio > 0.6)
                    {
                        multiplier *= knobs.LexicalBoost;
                    }
                }

                return new ScoredItem(item, baseScore * multiplier);
            })
            .Where(static item => item.Score > 0)
            .OrderByDescending(static item => item.Score)
            .ThenBy(static item => item.Item.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        // post-scoring risk gate runs LAST.
        if (string.Equals(profileName, HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1, StringComparison.OrdinalIgnoreCase))
        {
            scored = scored
                .Where(item => !IsRisk(sample, item.Item))
                .ToArray();
        }

        return scored
            .Take(topK)
            .Select(static item => item.Item)
            .ToArray();
    }

    private static IReadOnlyList<RetrievalDatasetV2CorpusItem> CollectGraphCandidates(
        RetrievalDatasetV2Sample sample,
        IReadOnlyList<RetrievalDatasetV2CorpusItem> corpusItems,
        int topK)
    {
        if (sample.RequiredRelations.Count == 0
            && sample.EvidenceRefs.Count == 0
            && sample.SourceRefs.Count == 0
            && sample.MustHitItemIds.Count == 0)
        {
            return Array.Empty<RetrievalDatasetV2CorpusItem>();
        }

        var requiredRelations = new HashSet<string>(sample.RequiredRelations, StringComparer.OrdinalIgnoreCase);
        var evidence = new HashSet<string>(sample.EvidenceRefs, StringComparer.OrdinalIgnoreCase);
        var source = new HashSet<string>(sample.SourceRefs, StringComparer.OrdinalIgnoreCase);
        var mustHit = new HashSet<string>(sample.MustHitItemIds, StringComparer.OrdinalIgnoreCase);

        var scored = corpusItems
            .Select(item =>
            {
                var overlap = 0;
                foreach (var relation in item.Relations)
                {
                    if (requiredRelations.Contains(relation.RelationId))
                    {
                        overlap += 2;
                    }
                }

                if (item.EvidenceRefs.Any(reference => evidence.Contains(reference)))
                {
                    overlap += 1;
                }

                if (item.SourceRefs.Any(reference => source.Contains(reference)))
                {
                    overlap += 1;
                }

                if (mustHit.Contains(item.ItemId))
                {
                    overlap += 3;
                }

                return new ScoredItem(item, overlap);
            })
            .Where(static entry => entry.Score > 0)
            .Where(entry => !IsRisk(sample, entry.Item))
            .OrderByDescending(static entry => entry.Score)
            .ThenBy(static entry => entry.Item.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return scored
            .Take(topK)
            .Select(static entry => entry.Item)
            .ToArray();
    }

    private static IReadOnlyList<RetrievalDatasetV2CorpusItem> MergeCandidates(
        IReadOnlyList<RetrievalDatasetV2CorpusItem> vectorCandidates,
        IReadOnlyList<RetrievalDatasetV2CorpusItem> graphCandidates,
        int topK)
    {
        if (vectorCandidates.Count == 0 && graphCandidates.Count == 0)
        {
            return Array.Empty<RetrievalDatasetV2CorpusItem>();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<RetrievalDatasetV2CorpusItem>();
        foreach (var item in vectorCandidates)
        {
            if (seen.Add(item.ItemId))
            {
                merged.Add(item);
            }

            if (merged.Count >= topK)
            {
                return merged;
            }
        }

        foreach (var item in graphCandidates)
        {
            if (seen.Add(item.ItemId))
            {
                merged.Add(item);
            }

            if (merged.Count >= topK)
            {
                return merged;
            }
        }

        return merged;
    }

    private static double ScoreForProfile(string profileName, double dense, double lexical, double anchor, double negativeCueOverlap)
    {
        var cappedAnchor = Math.Min(anchor, 0.25);
        return profileName switch
        {
            "dense-only" => dense,
            HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1
                => Math.Max(0, dense + lexical + anchor * 0.5 - negativeCueOverlap * 0.85),
            HybridUnionScoringRepairProfiles.CombinedSafeV1
                => Math.Max(0, dense * 0.78 + lexical * 0.18 + cappedAnchor * 0.04 - negativeCueOverlap * 0.9),
            HybridUnionScoringRepairProfiles.ContributionAwareRerankV1
                => dense * 0.72 + lexical * 0.23 + cappedAnchor * 0.05,
            HybridUnionScoringRepairProfiles.AnchorScoreCappedV1
                => dense + lexical + cappedAnchor * 0.25,
            HybridUnionScoringRepairProfiles.DenseWinnerFloorV1
                => dense + lexical + cappedAnchor * 0.2,
            HybridUnionScoringRepairProfiles.DensePreservingUnionV1
                => dense + lexical + anchor * 0.25,
            HybridUnionScoringRepairProfiles.NegativeDistractorPenaltyV1
                => Math.Max(0, dense + lexical + anchor * 0.5 - negativeCueOverlap * 0.85),
            _ => dense + lexical + anchor * 0.5
        };
    }

    private static bool IsRisk(RetrievalDatasetV2Sample sample, RetrievalDatasetV2CorpusItem item)
        => sample.MustNotHitItemIds.Contains(item.ItemId, StringComparer.OrdinalIgnoreCase)
            || IsBlockedByEligibility(sample, item)
            || IsLifecycleRisk(item)
            || !string.Equals(item.TargetSection, sample.ExpectedTargetSection, StringComparison.OrdinalIgnoreCase);

    private static bool IsBlockedByEligibility(RetrievalDatasetV2Sample sample, RetrievalDatasetV2CorpusItem item)
    {
        if (!string.Equals(item.TargetSection, sample.ExpectedTargetSection, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(item.TargetSection, VectorQueryTargetSections.NormalContext, StringComparison.OrdinalIgnoreCase))
        {
            return !(string.Equals(item.Lifecycle, "Active", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Lifecycle, "Current", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Lifecycle, "Stable", StringComparison.OrdinalIgnoreCase))
                || string.Equals(item.ReplacementState, "superseded", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool IsLifecycleRisk(RetrievalDatasetV2CorpusItem item)
        => string.Equals(item.TargetSection, VectorQueryTargetSections.NormalContext, StringComparison.OrdinalIgnoreCase)
            && (string.Equals(item.Lifecycle, "Deprecated", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Lifecycle, "Historical", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.ReplacementState, "superseded", StringComparison.OrdinalIgnoreCase));

    private static int EstimateTokens(RetrievalDatasetV2CorpusItem item)
        => Math.Max(1, Tokenize($"{item.Content} {string.Join(' ', item.Tags)} {string.Join(' ', item.Anchors)}").Count);

    private static double DenseScore(IReadOnlySet<string> queryTokens, RetrievalDatasetV2CorpusItem item)
    {
        var itemTokens = Tokenize($"{item.Content} {item.TargetSection} {item.ItemKind} {item.SourceKind} {string.Join(' ', item.Tags.Where(static tag => !tag.StartsWith("rdsv2-source-tag-", StringComparison.OrdinalIgnoreCase)))}");
        if (queryTokens.Count == 0 || itemTokens.Count == 0)
        {
            return 0;
        }

        var overlap = queryTokens.Count(itemTokens.Contains);
        return overlap / Math.Sqrt(queryTokens.Count * itemTokens.Count);
    }

    private static double LexicalScore(IReadOnlySet<string> queryTokens, RetrievalDatasetV2CorpusItem item)
    {
        var itemTokens = Tokenize(item.Content);
        if (queryTokens.Count == 0 || itemTokens.Count == 0)
        {
            return 0;
        }

        var overlap = queryTokens.Count(itemTokens.Contains);
        var union = queryTokens.Count + itemTokens.Count - overlap;
        return union == 0 ? 0 : (double)overlap / union;
    }

    private static double AnchorScore(IReadOnlySet<string> queryTokens, RetrievalDatasetV2CorpusItem item)
    {
        var anchors = Tokenize($"{string.Join(' ', item.Tags)} {string.Join(' ', item.Anchors)} {item.TargetSection}");
        if (queryTokens.Count == 0 || anchors.Count == 0)
        {
            return 0;
        }

        return queryTokens.Count(anchors.Contains) / (double)anchors.Count;
    }

    private static double NegativeCueOverlap(IReadOnlySet<string> negativeTokens, RetrievalDatasetV2CorpusItem item)
    {
        if (negativeTokens.Count == 0)
        {
            return 0;
        }

        var itemTokens = Tokenize($"{item.Content} {item.ItemKind} {item.SourceKind} {item.TargetSection} {string.Join(' ', item.Tags)} {string.Join(' ', item.Anchors)}");
        return itemTokens.Count == 0 ? 0 : negativeTokens.Count(itemTokens.Contains) / (double)negativeTokens.Count;
    }

    private static HashSet<string> ExtractNegativeCueTokens(string queryText)
    {
        var lower = queryText.ToLowerInvariant();
        var cueIndexes = new[]
            {
                lower.IndexOf("excluding ", StringComparison.Ordinal),
                lower.IndexOf("avoid ", StringComparison.Ordinal),
                lower.IndexOf("do not return ", StringComparison.Ordinal),
                lower.IndexOf("instead of ", StringComparison.Ordinal),
                lower.IndexOf("without relying on ", StringComparison.Ordinal),
                lower.IndexOf("unrelated ", StringComparison.Ordinal)
            }
            .Where(static index => index >= 0)
            .DefaultIfEmpty(-1)
            .Min();
        return cueIndexes < 0 ? [] : Tokenize(lower[cueIndexes..]);
    }

    private static HashSet<string> Tokenize(string value)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var builder = new StringBuilder();
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch == '-')
            {
                builder.Append(char.ToLowerInvariant(ch));
                continue;
            }

            FlushToken(builder, result);
        }

        FlushToken(builder, result);
        return result;
    }

    private static void FlushToken(StringBuilder builder, ISet<string> result)
    {
        if (builder.Length == 0)
        {
            return;
        }

        result.Add(builder.ToString());
        builder.Clear();
    }

    private static string ResolveRecommendation(IReadOnlyList<string> blocked, bool passed, string bestProfileId)
    {
        if (passed)
        {
            return string.IsNullOrEmpty(bestProfileId)
                ? RetrievalQualityRepairPreviewRecommendations.KeepBaselineOnly
                : RetrievalQualityRepairPreviewRecommendations.ReadyForRetrievalQualityRepairFreeze;
        }

        if (blocked.Contains("QualityGateMissing", StringComparer.OrdinalIgnoreCase))
        {
            return RetrievalQualityRepairPreviewRecommendations.BlockedByMissingQualityGate;
        }

        if (blocked.Contains("QualityGateNotPassed", StringComparer.OrdinalIgnoreCase))
        {
            return RetrievalQualityRepairPreviewRecommendations.BlockedByQualityGateNotPassed;
        }

        if (blocked.Contains("MissingDataset", StringComparer.OrdinalIgnoreCase))
        {
            return RetrievalQualityRepairPreviewRecommendations.BlockedByMissingDataset;
        }

        if (blocked.Contains("EmptyRepairOutput", StringComparer.OrdinalIgnoreCase))
        {
            return RetrievalQualityRepairPreviewRecommendations.BlockedByEmptyRepairOutput;
        }

        if (blocked.Contains("MustNotHitRiskAfterPolicyNonZero", StringComparer.OrdinalIgnoreCase))
        {
            return RetrievalQualityRepairPreviewRecommendations.BlockedByMustNotHitRisk;
        }

        if (blocked.Contains("LifecycleRiskAfterPolicyNonZero", StringComparer.OrdinalIgnoreCase))
        {
            return RetrievalQualityRepairPreviewRecommendations.BlockedByLifecycleRisk;
        }

        if (blocked.Contains("SectionMismatchDetected", StringComparer.OrdinalIgnoreCase))
        {
            return RetrievalQualityRepairPreviewRecommendations.BlockedBySectionMismatch;
        }

        if (blocked.Contains("RiskAfterPolicyNonZero", StringComparer.OrdinalIgnoreCase))
        {
            return RetrievalQualityRepairPreviewRecommendations.BlockedByRiskAfterPolicy;
        }

        if (blocked.Contains("NoRepairProfileImproved", StringComparer.OrdinalIgnoreCase))
        {
            return RetrievalQualityRepairPreviewRecommendations.BlockedByNoRepairProfileImprovement;
        }

        if (blocked.Any(static reason => reason.Contains("PackageOutput", StringComparison.OrdinalIgnoreCase)))
        {
            return RetrievalQualityRepairPreviewRecommendations.BlockedByPackageOutputChange;
        }

        if (blocked.Any(static reason => reason.Contains("PackingPolicy", StringComparison.OrdinalIgnoreCase)))
        {
            return RetrievalQualityRepairPreviewRecommendations.BlockedByPackingPolicyChange;
        }

        if (blocked.Any(static reason => reason.Contains("VectorStoreBinding", StringComparison.OrdinalIgnoreCase)))
        {
            return RetrievalQualityRepairPreviewRecommendations.BlockedByVectorStoreBindingChange;
        }

        if (blocked.Any(static reason => reason.Contains("FormalRetrieval", StringComparison.OrdinalIgnoreCase)))
        {
            return RetrievalQualityRepairPreviewRecommendations.BlockedByFormalOutputChange;
        }

        if (blocked.Any(static reason => reason.Contains("Runtime", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("FormalPackageWritten", StringComparison.OrdinalIgnoreCase)))
        {
            return RetrievalQualityRepairPreviewRecommendations.BlockedByRuntimeMutation;
        }

        return RetrievalQualityRepairPreviewRecommendations.KeepBaselineOnly;
    }

    private static void AppendList(StringBuilder builder, string title, IReadOnlyList<string> values)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        if (values.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return;
        }

        foreach (var value in values)
        {
            builder.AppendLine($"- `{value}`");
        }
    }

    private static void AppendMap(StringBuilder builder, string title, IReadOnlyDictionary<string, string> values)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        if (values.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return;
        }

        foreach (var pair in values.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"- {pair.Key}: `{pair.Value}`");
        }
    }

    private static void AppendBaseline(StringBuilder builder, RetrievalQualityRepairProfileResult baseline)
    {
        builder.AppendLine();
        builder.AppendLine("## Baseline");
        builder.AppendLine($"- profileId: `{baseline.ProfileId}`");
        builder.AppendLine($"- recall/precision/mrr: `{baseline.Recall:F4}/{baseline.Precision:F4}/{baseline.MeanReciprocalRank:F4}`");
        builder.AppendLine($"- mustHitTotal/recalled/belowTopK: `{baseline.MustHitTotal}/{baseline.MustHitRecalledTotal}/{baseline.MustHitBelowTopKCount}`");
        builder.AppendLine($"- contribution (vector/graph/overlap/vOnly/gOnly): `{baseline.VectorContributionCount}/{baseline.GraphContributionCount}/{baseline.OverlapCount}/{baseline.VectorOnlyCount}/{baseline.GraphOnlyCount}`");
        builder.AppendLine($"- noise (graph/vector/rankingRegression): `{baseline.GraphNoiseCount}/{baseline.VectorNoiseCount}/{baseline.RankingRegressionCount}`");
        builder.AppendLine($"- risk/mustNot/lifecycle/section: `{baseline.RiskAfterPolicy}/{baseline.MustNotHitRiskAfterPolicy}/{baseline.LifecycleRiskAfterPolicy}/{baseline.SectionMismatchCount}`");
        builder.AppendLine($"- token total: `{baseline.RepairTokenTotal}`");
    }

    private static void AppendProfileTable(StringBuilder builder, IReadOnlyList<RetrievalQualityRepairProfileResult> profiles)
    {
        builder.AppendLine();
        builder.AppendLine("## Profile Comparison");
        if (profiles.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return;
        }

        foreach (var profile in profiles)
        {
            builder.AppendLine($"- profileId: `{profile.ProfileId}` ({profile.ProfileLabel})");
            builder.AppendLine($"  - recall/precision/mrr: `{profile.Recall:F4}/{profile.Precision:F4}/{profile.MeanReciprocalRank:F4}` (delta: `{profile.RecallDelta:+0.0000;-0.0000;0.0000}/{profile.PrecisionDelta:+0.0000;-0.0000;0.0000}/{profile.MrrDelta:+0.0000;-0.0000;0.0000}`)");
            builder.AppendLine($"  - mustHitBelowTopK: `{profile.MustHitBelowTopKCount}` (delta: `{profile.MustHitBelowTopKDelta:+0;-0;0}`)");
            builder.AppendLine($"  - tokens (repair/baseline/delta): `{profile.RepairTokenTotal}/{profile.BaselineTokenTotal}/{profile.TokenDelta}`");
            builder.AppendLine($"  - regressions (recall/mrr/graphNoise/ranking/risk/tokenBudget): `{profile.RecallRegressionDetected}/{profile.MrrRegressionDetected}/{profile.GraphNoiseRegressionDetected}/{profile.RankingRegressionDetected}/{profile.RiskRegressionDetected}/{profile.TokenBudgetExceeded}`");
            if (profile.AppliedAdjustments.Count > 0)
            {
                builder.AppendLine($"  - adjustments: `{string.Join(", ", profile.AppliedAdjustments)}`");
            }
        }
    }

    private readonly record struct ScoredItem(RetrievalDatasetV2CorpusItem Item, double Score);

    private readonly record struct ProfileKnobs(
        string ProfileId,
        string Label,
        int VectorTopK,
        int GraphTopK,
        int MergedTopK,
        double SectionBoost,
        double EvidenceBoost,
        double RelationBoost,
        double LexicalBoost,
        IReadOnlyList<string> AppliedAdjustments)
    {
        public static readonly ProfileKnobs None = new(
            ProfileId: string.Empty,
            Label: string.Empty,
            VectorTopK: 5,
            GraphTopK: 5,
            MergedTopK: 8,
            SectionBoost: 1.0,
            EvidenceBoost: 1.0,
            RelationBoost: 1.0,
            LexicalBoost: 1.0,
            AppliedAdjustments: Array.Empty<string>());
    }

    private sealed class ProfileTotals
    {
        public int SampleCount { get; set; }
        public int MustHitTotal { get; set; }
        public int MustHitRecalledTotal { get; set; }
        public double RecallSum { get; set; }
        public double PrecisionSum { get; set; }
        public double MrrSum { get; set; }
        public int MustHitBelowTopK { get; set; }
        public int VectorContribution { get; set; }
        public int GraphContribution { get; set; }
        public int Overlap { get; set; }
        public int VectorOnly { get; set; }
        public int GraphOnly { get; set; }
        public int GraphNoise { get; set; }
        public int VectorNoise { get; set; }
        public int RankingRegression { get; set; }
        public int RiskAfterPolicy { get; set; }
        public int MustNotHitRisk { get; set; }
        public int LifecycleRisk { get; set; }
        public int SectionMismatch { get; set; }
        public int RepairTokenTotal { get; set; }
    }
}

/// <summary>V5.5 retrieval quality repair preview 选项。</summary>
public sealed class RetrievalQualityRepairPreviewOptions
{
    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public int TopK { get; init; } = 5;

    public int BaselineVectorTopK { get; init; } = 5;

    public int BaselineGraphTopK { get; init; } = 5;

    public int BaselineMergedTopK { get; init; } = 8;

    public int ExpansionVectorTopK { get; init; } = 10;

    public int ExpansionGraphTopK { get; init; } = 10;

    public int ExpansionMergedTopK { get; init; } = 12;

    public int AdjustedTopK { get; init; } = 8;

    public double SectionBoost { get; init; } = 1.5;

    public double MustHitEvidenceBoost { get; init; } = 1.75;

    public double GraphRelationAnchorBoost { get; init; } = 1.6;

    public double LexicalFallbackBoost { get; init; } = 1.4;

    public int MaxSampleTraceCount { get; init; } = 5;

    public int MaxTokenDeltaTotal { get; init; } = 4_000;

    public int MaxTokenDeltaPerSample { get; init; } = 200;

    public bool RequireQualityGatePassed { get; init; } = true;

    public bool RequirePackageShadowGatePassed { get; init; } = true;

    public bool UseForRuntime { get; init; } = false;

    public bool FormalRetrievalAllowed { get; init; } = false;
}
