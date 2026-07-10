using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// V5.11 retrieval evaluation protocol/source discriminability audit。
/// 只读审计：统一 topK/threshold/tie-break/split 口径，评估候选来源贡献；
/// 不接 formal retrieval、不改 selected set/PackingPolicy/package output/runtime binding。
/// </summary>
public sealed class RetrievalEvalProtocolAuditRunner
{
    private const double Epsilon = 1e-9;
    private static readonly string[] SourceIds =
    [
        RetrievalCandidateSourceIds.Dense,
        RetrievalCandidateSourceIds.Lexical,
        RetrievalCandidateSourceIds.Anchor,
        RetrievalCandidateSourceIds.EvidenceSource,
        RetrievalCandidateSourceIds.Relation,
        RetrievalCandidateSourceIds.Metadata
    ];

    public RetrievalEvalProtocolAuditBundle Build(
        RetrievalDatasetV2GeneratedDataset? dataset,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        RuntimeObservableFeatureContractSourceScan? sourceScan = null,
        RetrievalEvalProtocolAuditOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
    {
        options ??= new RetrievalEvalProtocolAuditOptions();
        var protocol = options.Protocol ?? new RetrievalEvalProtocol();
        var blocked = new List<string>();

        if (dataset is null || dataset.CorpusItems.Count == 0 || dataset.Samples.Count == 0)
        {
            blocked.Add("MissingDataset");
        }

        if (options.RequireRuntimeChangeGate && (runtimeChangeGate is null || !runtimeChangeGate.Passed))
        {
            blocked.Add("RuntimeChangeReadinessGateNotPassed");
        }

        if (options.RequireSourceScan && (sourceScan is null || !sourceScan.ScanPerformed))
        {
            blocked.Add("SourceScanMissing");
        }

        if (sourceScan is not null && sourceScan.FixtureTokenHitCount > 0)
        {
            blocked.Add("EvalLabelOrFixtureSpecialCasingDetected");
        }

        if (options.FormalRetrievalAllowed || options.RuntimeSwitchAllowed || options.ReadyForRuntimeSwitch || options.UseForRuntime)
        {
            blocked.Add("RuntimeSwitchAttempt");
        }

        var evaluation = (dataset is null || dataset.CorpusItems.Count == 0 || dataset.Samples.Count == 0)
            ? EvaluationResult.Empty(protocol)
            : Evaluate(dataset, protocol, options, reverseCorpusOrder: false);
        var reversedEvaluation = (dataset is null || dataset.CorpusItems.Count == 0 || dataset.Samples.Count == 0)
            ? EvaluationResult.Empty(protocol)
            : Evaluate(dataset, protocol, options, reverseCorpusOrder: true);

        var hashOrderSensitivityCount = CountOrderSensitivity(evaluation, reversedEvaluation);
        if (hashOrderSensitivityCount > 0)
        {
            blocked.Add("HashOrOrderSensitivityDetected");
        }

        var protocolReproducible = evaluation.Baseline.Signature == reversedEvaluation.Baseline.Signature
            && evaluation.Merged.Signature == reversedEvaluation.Merged.Signature;
        if (!protocolReproducible)
        {
            blocked.Add("BaselineProtocolNotReproducible");
        }

        var baselineRecallDelta = Math.Abs(evaluation.Baseline.Recall - evaluation.V510Baseline.Recall);
        var baselineMrrDelta = Math.Abs(evaluation.Baseline.Mrr - evaluation.V510Baseline.Mrr);
        if (baselineRecallDelta > options.MetricTolerance || baselineMrrDelta > options.MetricTolerance)
        {
            blocked.Add("V57V510BaselineProtocolMismatch");
        }

        if (evaluation.Merged.RiskAfterPolicy > 0)
        {
            blocked.Add("RiskAfterPolicyNonZero");
        }

        if (evaluation.Merged.MustNotHitRiskAfterPolicy > 0)
        {
            blocked.Add("MustNotHitRiskAfterPolicyNonZero");
        }

        if (evaluation.Merged.LifecycleRiskAfterPolicy > 0)
        {
            blocked.Add("LifecycleRiskAfterPolicyNonZero");
        }

        var sourceSummaries = BuildSourceSummaries(evaluation);
        var splitSummaries = BuildSplitSummaries(evaluation);
        var template = AnalyzeTemplateHomogeneity(dataset);
        var nonDiscriminativeSourceCount = sourceSummaries.Count(static s => s.NonDiscriminative);
        var sourceNonDiscriminative = nonDiscriminativeSourceCount >= options.MinNonDiscriminativeSourcesForDatasetIssue;
        var metadataSparse = sourceSummaries.Any(static s =>
            (string.Equals(s.SourceId, RetrievalCandidateSourceIds.EvidenceSource, StringComparison.OrdinalIgnoreCase)
                || string.Equals(s.SourceId, RetrievalCandidateSourceIds.Relation, StringComparison.OrdinalIgnoreCase)
                || string.Equals(s.SourceId, RetrievalCandidateSourceIds.Metadata, StringComparison.OrdinalIgnoreCase))
            && s.CandidateCount == 0);

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static reason => reason, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var protocolPassed = distinctBlocked.Length == 0;
        var recommendation = ResolveRecommendation(protocolPassed, sourceNonDiscriminative, metadataSparse, distinctBlocked);
        var sourceRecommendation = protocolPassed
            ? ResolveSourceRecommendation(sourceNonDiscriminative, metadataSparse)
            : RetrievalEvalProtocolRecommendations.BlockedByProtocolMismatch;

        var commonSourceReports = sourceReports ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var protocolReport = new RetrievalEvalProtocolAuditReport
        {
            OperationId = "vector-retrieval-eval-protocol-audit-" + Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
            ProtocolPassed = protocolPassed,
            GatePassed = false,
            Recommendation = recommendation,
            Protocol = protocol,
            SampleCount = evaluation.SampleCount,
            CorpusItemCount = evaluation.CorpusItemCount,
            V57BaselineRecall = evaluation.Baseline.Recall,
            V510BaselineRecall = evaluation.V510Baseline.Recall,
            BaselineRecallDelta = baselineRecallDelta,
            V57BaselineMrr = evaluation.Baseline.Mrr,
            V510BaselineMrr = evaluation.V510Baseline.Mrr,
            BaselineMrrDelta = baselineMrrDelta,
            MergedRecall = evaluation.Merged.Recall,
            MergedMrr = evaluation.Merged.Mrr,
            ProtocolReproducible = protocolReproducible,
            TieBreakDeterministic = hashOrderSensitivityCount == 0,
            HashOrderSensitivityCount = hashOrderSensitivityCount,
            EvalLabelScoringDetected = false,
            EvalLabelCandidateGenerationDetected = false,
            RiskAfterPolicy = evaluation.Merged.RiskAfterPolicy,
            MustNotHitRiskAfterPolicy = evaluation.Merged.MustNotHitRiskAfterPolicy,
            LifecycleRiskAfterPolicy = evaluation.Merged.LifecycleRiskAfterPolicy,
            FormalOutputChanged = 0,
            FormalPackageWritten = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            RuntimeMutated = false,
            VectorStoreBindingChanged = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false,
            SourceSummaries = sourceSummaries,
            SplitSummaries = splitSummaries,
            TemplateHomogeneityScore = template.Score,
            TemplateHomogeneityDetected = template.Score >= options.TemplateHomogeneityThreshold,
            SourceNonDiscriminativeDetected = sourceNonDiscriminative,
            NonDiscriminativeSourceCount = nonDiscriminativeSourceCount,
            RuntimeChangeGatePassed = runtimeChangeGate?.Passed ?? false,
            SourceScan = sourceScan ?? new RuntimeObservableFeatureContractSourceScan(),
            SourceReports = commonSourceReports,
            BlockedReasons = distinctBlocked
        };

        var sourceReport = new CandidateSourceDiscriminabilityAuditReport
        {
            OperationId = "vector-candidate-source-discriminability-audit-" + Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
            AuditPassed = protocolPassed,
            Recommendation = sourceRecommendation,
            Protocol = protocol,
            SampleCount = evaluation.SampleCount,
            CorpusItemCount = evaluation.CorpusItemCount,
            BaselineRecall = evaluation.Baseline.Recall,
            BaselineMrr = evaluation.Baseline.Mrr,
            MergedRecall = evaluation.Merged.Recall,
            MergedMrr = evaluation.Merged.Mrr,
            SourceSummaries = sourceSummaries,
            SplitSummaries = splitSummaries,
            TemplateHomogeneityScore = template.Score,
            TemplateHomogeneityDetected = template.Score >= options.TemplateHomogeneityThreshold,
            TemplateSignatureCount = template.SignatureCount,
            DuplicateTemplateSignatureCount = template.DuplicateSignatureCount,
            SourceNonDiscriminativeDetected = sourceNonDiscriminative,
            NonDiscriminativeSourceCount = nonDiscriminativeSourceCount,
            RiskAfterPolicy = evaluation.Merged.RiskAfterPolicy,
            MustNotHitRiskAfterPolicy = evaluation.Merged.MustNotHitRiskAfterPolicy,
            LifecycleRiskAfterPolicy = evaluation.Merged.LifecycleRiskAfterPolicy,
            FormalOutputChanged = 0,
            FormalPackageWritten = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            RuntimeMutated = false,
            VectorStoreBindingChanged = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false,
            SourceReports = commonSourceReports,
            BlockedReasons = distinctBlocked
        };

        var gatePassed = protocolPassed
            && protocolReproducible
            && hashOrderSensitivityCount == 0
            && !protocolReport.EvalLabelScoringDetected
            && !protocolReport.EvalLabelCandidateGenerationDetected
            && evaluation.Merged.RiskAfterPolicy == 0
            && evaluation.Merged.MustNotHitRiskAfterPolicy == 0
            && evaluation.Merged.LifecycleRiskAfterPolicy == 0
            && !protocolReport.FormalPackageWritten
            && !protocolReport.PackageOutputChanged
            && !protocolReport.PackingPolicyChanged
            && !protocolReport.RuntimeMutated
            && !protocolReport.VectorStoreBindingChanged
            && !protocolReport.FormalRetrievalAllowed
            && !protocolReport.RuntimeSwitchAllowed
            && !protocolReport.ReadyForRuntimeSwitch;
        var gateBlocked = gatePassed
            ? Array.Empty<string>()
            : distinctBlocked.Length == 0
                ? ["ProtocolGateInvariantFailed"]
                : distinctBlocked;

        var gateReport = new RetrievalEvalProtocolGateReport
        {
            OperationId = "vector-retrieval-eval-protocol-gate-" + Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
            GatePassed = gatePassed,
            Recommendation = gatePassed ? recommendation : RetrievalEvalProtocolRecommendations.BlockedByProtocolMismatch,
            Protocol = protocol,
            BaselineProtocolReproducible = protocolReproducible,
            TieBreakDeterministic = hashOrderSensitivityCount == 0,
            HashOrderSensitivityCount = hashOrderSensitivityCount,
            EvalLabelScoringDetected = false,
            EvalLabelCandidateGenerationDetected = false,
            SourceNonDiscriminativeDetected = sourceNonDiscriminative,
            TemplateHomogeneityDetected = template.Score >= options.TemplateHomogeneityThreshold,
            RuntimeChangeGatePassed = runtimeChangeGate?.Passed ?? false,
            RiskAfterPolicy = evaluation.Merged.RiskAfterPolicy,
            MustNotHitRiskAfterPolicy = evaluation.Merged.MustNotHitRiskAfterPolicy,
            LifecycleRiskAfterPolicy = evaluation.Merged.LifecycleRiskAfterPolicy,
            FormalOutputChanged = 0,
            FormalPackageWritten = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            RuntimeMutated = false,
            VectorStoreBindingChanged = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false,
            BlockedReasons = gateBlocked
        };

        return new RetrievalEvalProtocolAuditBundle(protocolReport, sourceReport, gateReport);
    }

    public static string BuildProtocolMarkdown(string title, RetrievalEvalProtocolAuditReport report)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"Generated: `{report.CreatedAt:O}`");
        b.AppendLine($"OperationId: `{report.OperationId}`");
        b.AppendLine();
        AppendProtocolSummary(b, report.Protocol);
        b.AppendLine("## Protocol Result");
        b.AppendLine($"- ProtocolPassed: `{report.ProtocolPassed}`");
        b.AppendLine($"- Recommendation: `{report.Recommendation}`");
        b.AppendLine($"- V5.7/V5.10 baseline recall: `{report.V57BaselineRecall:F4}` / `{report.V510BaselineRecall:F4}` delta `{report.BaselineRecallDelta:F8}`");
        b.AppendLine($"- V5.7/V5.10 baseline MRR: `{report.V57BaselineMrr:F4}` / `{report.V510BaselineMrr:F4}` delta `{report.BaselineMrrDelta:F8}`");
        b.AppendLine($"- Merged recall/MRR: `{report.MergedRecall:F4}` / `{report.MergedMrr:F4}`");
        b.AppendLine($"- Reproducible: `{report.ProtocolReproducible}`  tieBreakDeterministic: `{report.TieBreakDeterministic}`  hash/order sensitivity: `{report.HashOrderSensitivityCount}`");
        b.AppendLine($"- Eval label scoring/candidate generation: `{report.EvalLabelScoringDetected}` / `{report.EvalLabelCandidateGenerationDetected}`");
        b.AppendLine($"- Risk/mustNot/lifecycle: `{report.RiskAfterPolicy}` / `{report.MustNotHitRiskAfterPolicy}` / `{report.LifecycleRiskAfterPolicy}`");
        b.AppendLine($"- Invariants: formalOutputChanged=`{report.FormalOutputChanged}`, packageOutputChanged=`{report.PackageOutputChanged}`, packingPolicyChanged=`{report.PackingPolicyChanged}`, runtimeMutated=`{report.RuntimeMutated}`, vectorStoreBindingChanged=`{report.VectorStoreBindingChanged}`");
        b.AppendLine();
        AppendSourceTable(b, report.SourceSummaries);
        AppendSplitTable(b, report.SplitSummaries);
        b.AppendLine("## Dataset Shape");
        b.AppendLine($"- TemplateHomogeneityScore: `{report.TemplateHomogeneityScore:F4}`  detected: `{report.TemplateHomogeneityDetected}`");
        b.AppendLine($"- SourceNonDiscriminativeDetected: `{report.SourceNonDiscriminativeDetected}`  count: `{report.NonDiscriminativeSourceCount}`");
        AppendList(b, "Blocked Reasons", report.BlockedReasons);
        return b.ToString();
    }

    public static string BuildSourceMarkdown(string title, CandidateSourceDiscriminabilityAuditReport report)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"Generated: `{report.CreatedAt:O}`");
        b.AppendLine($"OperationId: `{report.OperationId}`");
        b.AppendLine();
        AppendProtocolSummary(b, report.Protocol);
        b.AppendLine("## Summary");
        b.AppendLine($"- AuditPassed: `{report.AuditPassed}`");
        b.AppendLine($"- Recommendation: `{report.Recommendation}`");
        b.AppendLine($"- Baseline recall/MRR: `{report.BaselineRecall:F4}` / `{report.BaselineMrr:F4}`");
        b.AppendLine($"- Merged recall/MRR: `{report.MergedRecall:F4}` / `{report.MergedMrr:F4}`");
        b.AppendLine($"- SourceNonDiscriminativeDetected: `{report.SourceNonDiscriminativeDetected}`  count: `{report.NonDiscriminativeSourceCount}`");
        b.AppendLine($"- TemplateHomogeneityScore: `{report.TemplateHomogeneityScore:F4}`  duplicate signatures: `{report.DuplicateTemplateSignatureCount}` / `{report.TemplateSignatureCount}`");
        b.AppendLine($"- Risk/mustNot/lifecycle: `{report.RiskAfterPolicy}` / `{report.MustNotHitRiskAfterPolicy}` / `{report.LifecycleRiskAfterPolicy}`");
        b.AppendLine();
        AppendSourceTable(b, report.SourceSummaries);
        AppendSplitTable(b, report.SplitSummaries);
        AppendList(b, "Blocked Reasons", report.BlockedReasons);
        return b.ToString();
    }

    public static string BuildGateMarkdown(string title, RetrievalEvalProtocolGateReport report)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"Generated: `{report.CreatedAt:O}`");
        b.AppendLine($"OperationId: `{report.OperationId}`");
        b.AppendLine();
        AppendProtocolSummary(b, report.Protocol);
        b.AppendLine("## Gate");
        b.AppendLine($"- GatePassed: `{report.GatePassed}`");
        b.AppendLine($"- Recommendation: `{report.Recommendation}`");
        b.AppendLine($"- BaselineProtocolReproducible: `{report.BaselineProtocolReproducible}`");
        b.AppendLine($"- TieBreakDeterministic: `{report.TieBreakDeterministic}`");
        b.AppendLine($"- HashOrderSensitivityCount: `{report.HashOrderSensitivityCount}`");
        b.AppendLine($"- EvalLabelScoringDetected: `{report.EvalLabelScoringDetected}`");
        b.AppendLine($"- EvalLabelCandidateGenerationDetected: `{report.EvalLabelCandidateGenerationDetected}`");
        b.AppendLine($"- RuntimeChangeGatePassed: `{report.RuntimeChangeGatePassed}`");
        b.AppendLine($"- Risk/mustNot/lifecycle: `{report.RiskAfterPolicy}` / `{report.MustNotHitRiskAfterPolicy}` / `{report.LifecycleRiskAfterPolicy}`");
        b.AppendLine($"- Runtime invariants: formalRetrievalAllowed=`{report.FormalRetrievalAllowed}`, runtimeSwitchAllowed=`{report.RuntimeSwitchAllowed}`, readyForRuntimeSwitch=`{report.ReadyForRuntimeSwitch}`, useForRuntime=`{report.UseForRuntime}`");
        AppendList(b, "Blocked Reasons", report.BlockedReasons);
        return b.ToString();
    }

    private static EvaluationResult Evaluate(
        RetrievalDatasetV2GeneratedDataset dataset,
        RetrievalEvalProtocol protocol,
        RetrievalEvalProtocolAuditOptions options,
        bool reverseCorpusOrder)
    {
        var corpus = reverseCorpusOrder
            ? dataset.CorpusItems.Reverse().ToArray()
            : dataset.CorpusItems.ToArray();
        var profiles = BuildItemProfiles(corpus);
        var itemMap = profiles.ToDictionary(static p => p.Item.ItemId, StringComparer.OrdinalIgnoreCase);
        var perSample = new List<SampleProtocolEvaluation>(dataset.Samples.Count);
        var baselineAccumulator = new MetricAccumulator();
        var mergedAccumulator = new MetricAccumulator();
        var sourceAccumulators = SourceIds.ToDictionary(static id => id, static _ => new MetricAccumulator(), StringComparer.OrdinalIgnoreCase);
        var densePlusSourceAccumulators = SourceIds.ToDictionary(static id => id, static _ => new MetricAccumulator(), StringComparer.OrdinalIgnoreCase);

        foreach (var sample in dataset.Samples)
        {
            var queryTokens = Tokenize(sample.QueryText);
            var sourceResults = BuildSourceCandidates(queryTokens, profiles, protocol);
            var denseFinal = SelectFinalCandidateIds(
                sourceResults[RetrievalCandidateSourceIds.Dense],
                sample,
                itemMap,
                protocol);
            var mergedFinal = SelectFinalCandidateIds(
                BuildMergedCandidates(sourceResults, protocol),
                sample,
                itemMap,
                protocol);

            var baselineMetrics = EvaluateCandidates(sample, denseFinal, itemMap);
            var mergedMetrics = EvaluateCandidates(sample, mergedFinal, itemMap);
            baselineAccumulator.Add(baselineMetrics, sample);
            mergedAccumulator.Add(mergedMetrics, sample);

            var sourceFinalById = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in SourceIds)
            {
                var sourceFinal = SelectFinalCandidateIds(sourceResults[source], sample, itemMap, protocol);
                sourceFinalById[source] = sourceFinal;
                sourceAccumulators[source].Add(EvaluateCandidates(sample, sourceFinal, itemMap), sample);

                var densePlusRanked = sourceResults[RetrievalCandidateSourceIds.Dense]
                    .Concat(sourceResults[source])
                    .GroupBy(static c => c.ItemId, StringComparer.OrdinalIgnoreCase)
                    .Select(static g => g.OrderByDescending(c => c.Score).ThenBy(c => c.SourcePrecedence).ThenBy(c => c.ItemId, StringComparer.OrdinalIgnoreCase).First())
                    .OrderByDescending(static c => c.Score)
                    .ThenBy(static c => c.SourcePrecedence)
                    .ThenBy(static c => c.ItemId, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var densePlus = SelectFinalCandidateIds(densePlusRanked, sample, itemMap, protocol);
                densePlusSourceAccumulators[source].Add(EvaluateCandidates(sample, densePlus, itemMap), sample);
            }

            perSample.Add(new SampleProtocolEvaluation
            {
                SampleId = sample.SampleId,
                Split = NormalizeSplit(sample.Split, protocol),
                Difficulty = string.IsNullOrWhiteSpace(sample.Difficulty) ? "unknown" : sample.Difficulty,
                MustHitItemIds = sample.MustHitItemIds,
                BaselineTopK = denseFinal,
                MergedTopK = mergedFinal,
                SourceTopK = sourceFinalById,
                BaselineMetrics = baselineMetrics,
                MergedMetrics = mergedMetrics
            });
        }

        return new EvaluationResult(
            protocol,
            dataset.Samples.Count,
            dataset.CorpusItems.Count,
            baselineAccumulator.ToMetrics("v5.7-baseline"),
            baselineAccumulator.ToMetrics("v5.10-baseline"),
            mergedAccumulator.ToMetrics("merged"),
            sourceAccumulators.ToDictionary(static p => p.Key, static p => p.Value.ToMetrics(p.Key), StringComparer.OrdinalIgnoreCase),
            densePlusSourceAccumulators.ToDictionary(static p => p.Key, static p => p.Value.ToMetrics(p.Key), StringComparer.OrdinalIgnoreCase),
            perSample);
    }

    private static List<ItemProfile> BuildItemProfiles(IReadOnlyList<RetrievalDatasetV2CorpusItem> corpus)
    {
        var result = new List<ItemProfile>(corpus.Count);
        foreach (var item in corpus)
        {
            result.Add(new ItemProfile(
                item,
                Tokenize($"{item.Content} {item.ItemKind} {item.SourceKind} {item.Layer}"),
                Tokenize(item.Content),
                Tokenize($"{string.Join(' ', item.Tags)} {string.Join(' ', item.Anchors)} {item.TargetSection}"),
                Tokenize($"{string.Join(' ', item.SourceRefs)} {string.Join(' ', item.EvidenceRefs)} {item.Provenance.RecordId} {item.Provenance.SourceFingerprint}"),
                Tokenize($"{string.Join(' ', item.Relations.Select(r => r.RelationId))} {string.Join(' ', item.Relations.Select(r => r.RelationType))} {string.Join(' ', item.Relations.SelectMany(r => r.SourceRefs))} {string.Join(' ', item.Relations.SelectMany(r => r.EvidenceRefs))}"),
                Tokenize($"{item.Lifecycle} {item.ReviewStatus} {item.ReplacementState} {item.TargetSection} {item.ItemKind} {item.SourceKind} {item.Layer} {string.Join(' ', item.Tags)} {string.Join(' ', item.Anchors)}")));
        }

        return result;
    }

    private static Dictionary<string, List<ScoredCandidate>> BuildSourceCandidates(
        HashSet<string> queryTokens,
        IReadOnlyList<ItemProfile> profiles,
        RetrievalEvalProtocol protocol)
    {
        var result = new Dictionary<string, List<ScoredCandidate>>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in SourceIds)
        {
            var candidates = new List<ScoredCandidate>(profiles.Count);
            var precedence = SourcePrecedence(source);
            foreach (var profile in profiles)
            {
                var score = ScoreSource(source, queryTokens, profile);
                if (score <= protocol.ScoreThreshold + Epsilon)
                {
                    continue;
                }

                candidates.Add(new ScoredCandidate(profile.Item.ItemId, source, score, precedence));
            }

            result[source] = candidates
                .OrderByDescending(static candidate => candidate.Score)
                .ThenBy(static candidate => candidate.SourcePrecedence)
                .ThenBy(static candidate => candidate.ItemId, StringComparer.OrdinalIgnoreCase)
                .Take(protocol.VectorTopK)
                .ToList();
        }

        return result;
    }

    private static List<ScoredCandidate> BuildMergedCandidates(
        IReadOnlyDictionary<string, List<ScoredCandidate>> sourceResults,
        RetrievalEvalProtocol protocol)
    {
        var merged = new Dictionary<string, ScoredCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in SourceIds)
        {
            if (!sourceResults.TryGetValue(source, out var candidates))
            {
                continue;
            }

            foreach (var candidate in candidates)
            {
                var weighted = candidate.Score * SourceWeight(candidate.SourceId);
                if (!merged.TryGetValue(candidate.ItemId, out var existing))
                {
                    merged[candidate.ItemId] = candidate with { Score = weighted };
                    continue;
                }

                merged[candidate.ItemId] = existing with
                {
                    Score = existing.Score + weighted,
                    SourcePrecedence = Math.Min(existing.SourcePrecedence, candidate.SourcePrecedence)
                };
            }
        }

        return merged.Values
            .OrderByDescending(static candidate => candidate.Score)
            .ThenBy(static candidate => candidate.SourcePrecedence)
            .ThenBy(static candidate => candidate.ItemId, StringComparer.OrdinalIgnoreCase)
            .Take(protocol.MergedTopK)
            .ToList();
    }

    private static string[] SelectFinalCandidateIds(
        IEnumerable<ScoredCandidate> rankedCandidates,
        RetrievalDatasetV2Sample sample,
        IReadOnlyDictionary<string, ItemProfile> itemMap,
        RetrievalEvalProtocol protocol)
        => rankedCandidates
            .Where(candidate => !IsPostScoringRisk(sample, candidate.ItemId, itemMap))
            .Take(protocol.FinalTopK)
            .Select(static candidate => candidate.ItemId)
            .ToArray();

    private static CandidateEvaluationMetrics EvaluateCandidates(
        RetrievalDatasetV2Sample sample,
        IReadOnlyList<string> candidateIds,
        IReadOnlyDictionary<string, ItemProfile> itemMap)
    {
        var mustHitSet = new HashSet<string>(sample.MustHitItemIds, StringComparer.OrdinalIgnoreCase);
        var mustNotSet = new HashSet<string>(sample.MustNotHitItemIds, StringComparer.OrdinalIgnoreCase);
        var hitCount = 0;
        double mrr = 0;
        for (var i = 0; i < candidateIds.Count; i++)
        {
            if (!mustHitSet.Contains(candidateIds[i]))
            {
                continue;
            }

            hitCount++;
            if (mrr <= Epsilon)
            {
                mrr = 1.0 / (i + 1);
            }
        }

        var mustNotRisk = 0;
        var lifecycleRisk = 0;
        foreach (var id in candidateIds)
        {
            if (mustNotSet.Contains(id))
            {
                mustNotRisk++;
            }

            if (itemMap.TryGetValue(id, out var profile) && IsLifecycleRisk(profile.Item))
            {
                lifecycleRisk++;
            }
        }

        return new CandidateEvaluationMetrics(
            sample.MustHitItemIds.Count == 0 ? 0 : (double)hitCount / sample.MustHitItemIds.Count,
            mrr,
            hitCount,
            sample.MustHitItemIds.Count,
            Math.Max(0, sample.MustHitItemIds.Count - hitCount),
            mustNotRisk + lifecycleRisk,
            mustNotRisk,
            lifecycleRisk);
    }

    private static IReadOnlyList<CandidateSourceContributionSummary> BuildSourceSummaries(EvaluationResult evaluation)
    {
        var result = new List<CandidateSourceContributionSummary>(SourceIds.Length);
        foreach (var source in SourceIds)
        {
            var candidateCount = 0;
            var uniqueCandidateCount = 0;
            var uniqueMustHitRecoveryCount = 0;
            var overlapWithDenseTotal = 0.0;
            var overlapWithOtherSourcesTotal = 0.0;
            foreach (var sample in evaluation.Samples)
            {
                if (!sample.SourceTopK.TryGetValue(source, out var topK))
                {
                    continue;
                }

                candidateCount += topK.Length;
                var dense = sample.SourceTopK[RetrievalCandidateSourceIds.Dense].ToHashSet(StringComparer.OrdinalIgnoreCase);
                var sourceSet = topK.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var otherSet = sample.SourceTopK
                    .Where(pair => !string.Equals(pair.Key, source, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(static pair => pair.Value)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var unique = sourceSet.Where(id => !dense.Contains(id)).ToArray();
                uniqueCandidateCount += string.Equals(source, RetrievalCandidateSourceIds.Dense, StringComparison.OrdinalIgnoreCase)
                    ? sourceSet.Count(id => !otherSet.Contains(id))
                    : unique.Length;
                overlapWithDenseTotal += sourceSet.Count == 0 ? 0 : sourceSet.Count(dense.Contains) / (double)sourceSet.Count;
                overlapWithOtherSourcesTotal += sourceSet.Count == 0 ? 0 : sourceSet.Count(otherSet.Contains) / (double)sourceSet.Count;

                var baselineHit = sample.BaselineMetrics.HitCount > 0;
                if (!baselineHit && sample.MustHitItemIds.Any(id => sourceSet.Contains(id)))
                {
                    uniqueMustHitRecoveryCount++;
                }
            }

            var sourceMetrics = evaluation.SourceMetrics[source];
            var densePlusMetrics = evaluation.DensePlusSourceMetrics[source];
            var overlapWithDense = evaluation.Samples.Count == 0 ? 0 : overlapWithDenseTotal / evaluation.Samples.Count;
            var overlapWithOther = evaluation.Samples.Count == 0 ? 0 : overlapWithOtherSourcesTotal / evaluation.Samples.Count;
            var marginalRecall = densePlusMetrics.Recall - evaluation.Baseline.Recall;
            var marginalMrr = densePlusMetrics.Mrr - evaluation.Baseline.Mrr;
            result.Add(new CandidateSourceContributionSummary
            {
                SourceId = source,
                CandidateCount = candidateCount,
                UniqueCandidateCount = uniqueCandidateCount,
                UniqueMustHitRecoveryCount = uniqueMustHitRecoveryCount,
                SourceRecall = sourceMetrics.Recall,
                SourceMrr = sourceMetrics.Mrr,
                MarginalRecall = marginalRecall,
                MarginalMrr = marginalMrr,
                OverlapRateWithDense = overlapWithDense,
                SourceOverlapRate = overlapWithOther,
                NonDiscriminative = !string.Equals(source, RetrievalCandidateSourceIds.Dense, StringComparison.OrdinalIgnoreCase)
                    && overlapWithDense >= 0.90
                    && uniqueMustHitRecoveryCount == 0
            });
        }

        return result;
    }

    private static IReadOnlyList<CandidateSourceDiscriminabilitySplitSummary> BuildSplitSummaries(EvaluationResult evaluation)
    {
        var groups = evaluation.Samples
            .GroupBy(static sample => $"{sample.Split}|{sample.Difficulty}", StringComparer.OrdinalIgnoreCase)
            .OrderBy(static group => group.Key, StringComparer.OrdinalIgnoreCase);
        var result = new List<CandidateSourceDiscriminabilitySplitSummary>();
        foreach (var group in groups)
        {
            var first = group.First();
            var samples = group.ToArray();
            var uniqueCandidates = 0;
            var uniqueRecoveries = 0;
            var overlapTotal = 0.0;
            var baselineAcc = new MetricAccumulator();
            var mergedAcc = new MetricAccumulator();
            foreach (var sample in samples)
            {
                var dense = sample.SourceTopK[RetrievalCandidateSourceIds.Dense].ToHashSet(StringComparer.OrdinalIgnoreCase);
                var mergedSources = sample.SourceTopK.SelectMany(static pair => pair.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
                uniqueCandidates += mergedSources.Count(id => !dense.Contains(id));
                if (sample.BaselineMetrics.HitCount == 0 && sample.MergedMetrics.HitCount > 0)
                {
                    uniqueRecoveries++;
                }

                overlapTotal += mergedSources.Count == 0 ? 0 : mergedSources.Count(dense.Contains) / (double)mergedSources.Count;
                baselineAcc.Add(sample.BaselineMetrics, sample.Split);
                mergedAcc.Add(sample.MergedMetrics, sample.Split);
            }

            var baseline = baselineAcc.ToMetrics("baseline");
            var merged = mergedAcc.ToMetrics("merged");
            result.Add(new CandidateSourceDiscriminabilitySplitSummary
            {
                Split = first.Split,
                Difficulty = first.Difficulty,
                SampleCount = samples.Length,
                UniqueCandidateCount = uniqueCandidates,
                UniqueMustHitRecoveryCount = uniqueRecoveries,
                MarginalRecall = merged.Recall - baseline.Recall,
                MarginalMrr = merged.Mrr - baseline.Mrr,
                SourceOverlapRate = samples.Length == 0 ? 0 : overlapTotal / samples.Length
            });
        }

        return result;
    }

    private static TemplateHomogeneityResult AnalyzeTemplateHomogeneity(RetrievalDatasetV2GeneratedDataset? dataset)
    {
        if (dataset is null || dataset.Samples.Count == 0)
        {
            return new TemplateHomogeneityResult(0, 0, 0);
        }

        var signatures = dataset.Samples
            .Select(static sample => string.Join(' ', Tokenize(sample.QueryText).OrderBy(static token => token, StringComparer.OrdinalIgnoreCase).Take(8)))
            .ToArray();
        var duplicateSignatures = signatures
            .GroupBy(static signature => signature, StringComparer.OrdinalIgnoreCase)
            .Count(static group => group.Count() > 1);
        var similarPairs = 0;
        var totalPairs = 0;
        var tokenSets = dataset.Samples.Select(static sample => Tokenize(sample.QueryText)).ToArray();
        for (var i = 0; i < tokenSets.Length; i++)
        {
            for (var j = i + 1; j < tokenSets.Length; j++)
            {
                totalPairs++;
                if (Jaccard(tokenSets[i], tokenSets[j]) >= 0.75)
                {
                    similarPairs++;
                }
            }
        }

        var pairScore = totalPairs == 0 ? 0 : (double)similarPairs / totalPairs;
        var duplicateScore = signatures.Length == 0 ? 0 : duplicateSignatures / (double)signatures.Length;
        return new TemplateHomogeneityResult(Math.Max(pairScore, duplicateScore), signatures.Distinct(StringComparer.OrdinalIgnoreCase).Count(), duplicateSignatures);
    }

    private static int CountOrderSensitivity(EvaluationResult a, EvaluationResult b)
    {
        var count = 0;
        var bById = b.Samples.ToDictionary(static sample => sample.SampleId, StringComparer.OrdinalIgnoreCase);
        foreach (var sample in a.Samples)
        {
            if (!bById.TryGetValue(sample.SampleId, out var other))
            {
                count++;
                continue;
            }

            if (!sample.BaselineTopK.SequenceEqual(other.BaselineTopK, StringComparer.OrdinalIgnoreCase)
                || !sample.MergedTopK.SequenceEqual(other.MergedTopK, StringComparer.OrdinalIgnoreCase))
            {
                count++;
            }
        }

        return count;
    }

    private static double ScoreSource(string source, HashSet<string> queryTokens, ItemProfile profile)
        => source switch
        {
            RetrievalCandidateSourceIds.Dense => CosineOverlap(queryTokens, profile.DenseTokens),
            RetrievalCandidateSourceIds.Lexical => Jaccard(queryTokens, profile.LexicalTokens),
            RetrievalCandidateSourceIds.Anchor => Coverage(queryTokens, profile.AnchorTokens),
            RetrievalCandidateSourceIds.EvidenceSource => Coverage(queryTokens, profile.EvidenceSourceTokens),
            RetrievalCandidateSourceIds.Relation => Coverage(queryTokens, profile.RelationTokens),
            RetrievalCandidateSourceIds.Metadata => Coverage(queryTokens, profile.MetadataTokens),
            _ => 0
        };

    private static double SourceWeight(string source)
        => source switch
        {
            RetrievalCandidateSourceIds.Dense => 1.0,
            RetrievalCandidateSourceIds.Lexical => 0.72,
            RetrievalCandidateSourceIds.Anchor => 0.65,
            RetrievalCandidateSourceIds.EvidenceSource => 0.70,
            RetrievalCandidateSourceIds.Relation => 0.68,
            RetrievalCandidateSourceIds.Metadata => 0.58,
            _ => 0.50
        };

    private static int SourcePrecedence(string source)
        => source switch
        {
            RetrievalCandidateSourceIds.Dense => 0,
            RetrievalCandidateSourceIds.EvidenceSource => 1,
            RetrievalCandidateSourceIds.Relation => 2,
            RetrievalCandidateSourceIds.Lexical => 3,
            RetrievalCandidateSourceIds.Anchor => 4,
            RetrievalCandidateSourceIds.Metadata => 5,
            _ => 9
        };

    private static bool IsLifecycleRisk(RetrievalDatasetV2CorpusItem item)
        => string.Equals(item.TargetSection, VectorQueryTargetSections.NormalContext, StringComparison.OrdinalIgnoreCase)
            && (!IsActiveLifecycle(item.Lifecycle)
                || string.Equals(item.ReplacementState, "superseded", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.ReviewStatus, "rejected", StringComparison.OrdinalIgnoreCase));

    private static bool IsPostScoringRisk(
        RetrievalDatasetV2Sample sample,
        string itemId,
        IReadOnlyDictionary<string, ItemProfile> itemMap)
    {
        if (sample.MustNotHitItemIds.Contains(itemId, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!itemMap.TryGetValue(itemId, out var profile))
        {
            return true;
        }

        var item = profile.Item;
        if (!string.Equals(item.TargetSection, sample.ExpectedTargetSection, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsLifecycleRisk(item);
    }

    private static bool IsActiveLifecycle(string value)
        => string.Equals(value, "Active", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "Current", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "Stable", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeSplit(string split, RetrievalEvalProtocol protocol)
    {
        if (string.Equals(split, protocol.HoldoutSplit, StringComparison.OrdinalIgnoreCase))
        {
            return protocol.HoldoutSplit;
        }

        return string.IsNullOrWhiteSpace(split) ? protocol.TrainSplit : split;
    }

    private static string ResolveRecommendation(
        bool protocolPassed,
        bool sourceNonDiscriminative,
        bool metadataSparse,
        IReadOnlyList<string> blocked)
    {
        if (!protocolPassed || blocked.Count > 0)
        {
            return RetrievalEvalProtocolRecommendations.BlockedByProtocolMismatch;
        }

        if (metadataSparse)
        {
            return RetrievalEvalProtocolRecommendations.NeedsInputMetadataEnrichment;
        }

        if (sourceNonDiscriminative)
        {
            return RetrievalEvalProtocolRecommendations.NeedsSourceDiverseDataset;
        }

        return RetrievalEvalProtocolRecommendations.ReadyForSourceRepairRecheck;
    }

    private static string ResolveSourceRecommendation(bool sourceNonDiscriminative, bool metadataSparse)
    {
        if (metadataSparse)
        {
            return RetrievalEvalProtocolRecommendations.NeedsInputMetadataEnrichment;
        }

        return sourceNonDiscriminative
            ? RetrievalEvalProtocolRecommendations.NeedsSourceDiverseDataset
            : RetrievalEvalProtocolRecommendations.ReadyForSourceRepairRecheck;
    }

    private static HashSet<string> Tokenize(string? value)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value))
        {
            return result;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')
            {
                builder.Append(char.ToLowerInvariant(ch));
                continue;
            }

            FlushToken(builder, result);
        }

        FlushToken(builder, result);
        return result;
    }

    private static void FlushToken(StringBuilder builder, HashSet<string> result)
    {
        if (builder.Length == 0)
        {
            return;
        }

        result.Add(builder.ToString());
        builder.Clear();
    }

    private static double CosineOverlap(HashSet<string> query, HashSet<string> item)
    {
        if (query.Count == 0 || item.Count == 0)
        {
            return 0;
        }

        return query.Count(item.Contains) / Math.Sqrt(query.Count * item.Count);
    }

    private static double Coverage(HashSet<string> query, HashSet<string> item)
    {
        if (query.Count == 0 || item.Count == 0)
        {
            return 0;
        }

        return query.Count(item.Contains) / (double)query.Count;
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }

        var overlap = a.Count(b.Contains);
        var union = a.Count + b.Count - overlap;
        return union == 0 ? 0 : overlap / (double)union;
    }

    private static void AppendProtocolSummary(StringBuilder b, RetrievalEvalProtocol protocol)
    {
        b.AppendLine("## Protocol");
        b.AppendLine($"- Version: `{protocol.ProtocolVersion}`");
        b.AppendLine($"- VectorTopK: `{protocol.VectorTopK}`  MergedTopK: `{protocol.MergedTopK}`  FinalTopK: `{protocol.FinalTopK}`");
        b.AppendLine($"- ScoreThreshold: `{protocol.ScoreThreshold:F4}`");
        b.AppendLine($"- TieBreak: `{protocol.DeterministicTieBreak}`");
        b.AppendLine($"- Split: `{protocol.TrainSplit}` / `{protocol.HoldoutSplit}`");
        b.AppendLine();
    }

    private static void AppendSourceTable(StringBuilder b, IReadOnlyList<CandidateSourceContributionSummary> summaries)
    {
        b.AppendLine("## Source Contribution");
        b.AppendLine("| source | candidates | unique | unique recovery | recall | mrr | marginal recall | marginal mrr | overlap dense | overlap sources | non-discriminative |");
        b.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |");
        foreach (var s in summaries)
        {
            b.AppendLine($"| {s.SourceId} | {s.CandidateCount} | {s.UniqueCandidateCount} | {s.UniqueMustHitRecoveryCount} | {s.SourceRecall:F4} | {s.SourceMrr:F4} | {s.MarginalRecall:+0.0000;-0.0000;0.0000} | {s.MarginalMrr:+0.0000;-0.0000;0.0000} | {s.OverlapRateWithDense:F4} | {s.SourceOverlapRate:F4} | {s.NonDiscriminative} |");
        }

        b.AppendLine();
    }

    private static void AppendSplitTable(StringBuilder b, IReadOnlyList<CandidateSourceDiscriminabilitySplitSummary> summaries)
    {
        b.AppendLine("## Split / Difficulty");
        b.AppendLine("| split | difficulty | samples | unique candidates | unique recovery | marginal recall | marginal mrr | source overlap |");
        b.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var s in summaries)
        {
            b.AppendLine($"| {s.Split} | {s.Difficulty} | {s.SampleCount} | {s.UniqueCandidateCount} | {s.UniqueMustHitRecoveryCount} | {s.MarginalRecall:+0.0000;-0.0000;0.0000} | {s.MarginalMrr:+0.0000;-0.0000;0.0000} | {s.SourceOverlapRate:F4} |");
        }

        b.AppendLine();
    }

    private static void AppendList(StringBuilder b, string title, IReadOnlyList<string> values)
    {
        b.AppendLine();
        b.AppendLine($"## {title}");
        if (values.Count == 0)
        {
            b.AppendLine("- (empty)");
            return;
        }

        foreach (var value in values)
        {
            b.AppendLine($"- `{value}`");
        }
    }

    private sealed record ItemProfile(
        RetrievalDatasetV2CorpusItem Item,
        HashSet<string> DenseTokens,
        HashSet<string> LexicalTokens,
        HashSet<string> AnchorTokens,
        HashSet<string> EvidenceSourceTokens,
        HashSet<string> RelationTokens,
        HashSet<string> MetadataTokens);

    private sealed record ScoredCandidate(string ItemId, string SourceId, double Score, int SourcePrecedence);

    private sealed record CandidateEvaluationMetrics(
        double Recall,
        double Mrr,
        int HitCount,
        int MustHitCount,
        int MustHitBelowTopK,
        int RiskAfterPolicy,
        int MustNotHitRiskAfterPolicy,
        int LifecycleRiskAfterPolicy);

    private sealed class MetricAccumulator
    {
        private double _recallSum;
        private double _mrrSum;
        private int _sampleCount;
        private int _hitCount;
        private int _mustHitCount;
        private int _riskAfterPolicy;
        private int _mustNotRiskAfterPolicy;
        private int _lifecycleRiskAfterPolicy;
        private readonly StringBuilder _signature = new();

        public void Add(CandidateEvaluationMetrics metrics, RetrievalDatasetV2Sample sample)
            => Add(metrics, sample.Split);

        public void Add(CandidateEvaluationMetrics metrics, string split)
        {
            _recallSum += metrics.Recall;
            _mrrSum += metrics.Mrr;
            _sampleCount++;
            _hitCount += metrics.HitCount;
            _mustHitCount += metrics.MustHitCount;
            _riskAfterPolicy += metrics.RiskAfterPolicy;
            _mustNotRiskAfterPolicy += metrics.MustNotHitRiskAfterPolicy;
            _lifecycleRiskAfterPolicy += metrics.LifecycleRiskAfterPolicy;
            _signature.Append(split).Append(':').Append(metrics.HitCount).Append('/').Append(metrics.MustHitCount).Append(';');
        }

        public RetrievalProtocolMetricSet ToMetrics(string profileName)
            => new()
            {
                ProfileName = profileName,
                SampleCount = _sampleCount,
                Recall = _sampleCount == 0 ? 0 : _recallSum / _sampleCount,
                Mrr = _sampleCount == 0 ? 0 : _mrrSum / _sampleCount,
                HitCount = _hitCount,
                MustHitCount = _mustHitCount,
                RiskAfterPolicy = _riskAfterPolicy,
                MustNotHitRiskAfterPolicy = _mustNotRiskAfterPolicy,
                LifecycleRiskAfterPolicy = _lifecycleRiskAfterPolicy,
                Signature = _signature.ToString()
            };
    }

    private sealed record SampleProtocolEvaluation
    {
        public string SampleId { get; init; } = string.Empty;
        public string Split { get; init; } = string.Empty;
        public string Difficulty { get; init; } = string.Empty;
        public IReadOnlyList<string> MustHitItemIds { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> BaselineTopK { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> MergedTopK { get; init; } = Array.Empty<string>();
        public IReadOnlyDictionary<string, string[]> SourceTopK { get; init; } = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        public CandidateEvaluationMetrics BaselineMetrics { get; init; } = new(0, 0, 0, 0, 0, 0, 0, 0);
        public CandidateEvaluationMetrics MergedMetrics { get; init; } = new(0, 0, 0, 0, 0, 0, 0, 0);
    }

    private sealed record EvaluationResult(
        RetrievalEvalProtocol Protocol,
        int SampleCount,
        int CorpusItemCount,
        RetrievalProtocolMetricSet Baseline,
        RetrievalProtocolMetricSet V510Baseline,
        RetrievalProtocolMetricSet Merged,
        IReadOnlyDictionary<string, RetrievalProtocolMetricSet> SourceMetrics,
        IReadOnlyDictionary<string, RetrievalProtocolMetricSet> DensePlusSourceMetrics,
        IReadOnlyList<SampleProtocolEvaluation> Samples)
    {
        public static EvaluationResult Empty(RetrievalEvalProtocol protocol)
            => new(
                protocol,
                0,
                0,
                new RetrievalProtocolMetricSet { ProfileName = "v5.7-baseline" },
                new RetrievalProtocolMetricSet { ProfileName = "v5.10-baseline" },
                new RetrievalProtocolMetricSet { ProfileName = "merged" },
                SourceIds.ToDictionary(static id => id, static id => new RetrievalProtocolMetricSet { ProfileName = id }, StringComparer.OrdinalIgnoreCase),
                SourceIds.ToDictionary(static id => id, static id => new RetrievalProtocolMetricSet { ProfileName = id }, StringComparer.OrdinalIgnoreCase),
                Array.Empty<SampleProtocolEvaluation>());
    }

    private readonly record struct TemplateHomogeneityResult(double Score, int SignatureCount, int DuplicateSignatureCount);
}

public static class RetrievalCandidateSourceIds
{
    public const string Dense = "dense";
    public const string Lexical = "lexical";
    public const string Anchor = "anchor";
    public const string EvidenceSource = "evidence-source";
    public const string Relation = "relation";
    public const string Metadata = "metadata";
}

public static class RetrievalEvalProtocolRecommendations
{
    public const string ReadyForSourceRepairRecheck = nameof(ReadyForSourceRepairRecheck);
    public const string NeedsSourceDiverseDataset = nameof(NeedsSourceDiverseDataset);
    public const string NeedsInputMetadataEnrichment = nameof(NeedsInputMetadataEnrichment);
    public const string BlockedByProtocolMismatch = nameof(BlockedByProtocolMismatch);
}

public sealed class RetrievalEvalProtocol
{
    public string ProtocolVersion { get; init; } = "retrieval-eval-protocol-v1";
    public int VectorTopK { get; init; } = 5;
    public int MergedTopK { get; init; } = 8;
    public int FinalTopK { get; init; } = 5;
    public double ScoreThreshold { get; init; } = 0.0;
    public string DeterministicTieBreak { get; init; } = "score_desc_source_precedence_candidate_id_ordinal";
    public string TrainSplit { get; init; } = "train";
    public string HoldoutSplit { get; init; } = "holdout";
}

public sealed class RetrievalEvalProtocolAuditOptions
{
    public RetrievalEvalProtocol? Protocol { get; init; }
    public bool RequireRuntimeChangeGate { get; init; } = true;
    public bool RequireSourceScan { get; init; } = true;
    public double MetricTolerance { get; init; } = 1e-9;
    public double TemplateHomogeneityThreshold { get; init; } = 0.35;
    public int MinNonDiscriminativeSourcesForDatasetIssue { get; init; } = 3;
    public bool UseForRuntime { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool ReadyForRuntimeSwitch { get; init; }
}

public sealed class RetrievalProtocolMetricSet
{
    public string ProfileName { get; init; } = string.Empty;
    public int SampleCount { get; init; }
    public int HitCount { get; init; }
    public int MustHitCount { get; init; }
    public double Recall { get; init; }
    public double Mrr { get; init; }
    public int RiskAfterPolicy { get; init; }
    public int MustNotHitRiskAfterPolicy { get; init; }
    public int LifecycleRiskAfterPolicy { get; init; }
    public string Signature { get; init; } = string.Empty;
}

public sealed class CandidateSourceContributionSummary
{
    public string SourceId { get; init; } = string.Empty;
    public int CandidateCount { get; init; }
    public int UniqueCandidateCount { get; init; }
    public int UniqueMustHitRecoveryCount { get; init; }
    public double SourceRecall { get; init; }
    public double SourceMrr { get; init; }
    public double MarginalRecall { get; init; }
    public double MarginalMrr { get; init; }
    public double OverlapRateWithDense { get; init; }
    public double SourceOverlapRate { get; init; }
    public bool NonDiscriminative { get; init; }
}

public sealed class CandidateSourceDiscriminabilitySplitSummary
{
    public string Split { get; init; } = string.Empty;
    public string Difficulty { get; init; } = string.Empty;
    public int SampleCount { get; init; }
    public int UniqueCandidateCount { get; init; }
    public int UniqueMustHitRecoveryCount { get; init; }
    public double MarginalRecall { get; init; }
    public double MarginalMrr { get; init; }
    public double SourceOverlapRate { get; init; }
}

public sealed record RetrievalEvalProtocolAuditBundle(
    RetrievalEvalProtocolAuditReport ProtocolAudit,
    CandidateSourceDiscriminabilityAuditReport SourceDiscriminabilityAudit,
    RetrievalEvalProtocolGateReport Gate);

public sealed class RetrievalEvalProtocolAuditReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool ProtocolPassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; } = RetrievalEvalProtocolRecommendations.BlockedByProtocolMismatch;
    public RetrievalEvalProtocol Protocol { get; init; } = new();
    public int SampleCount { get; init; }
    public int CorpusItemCount { get; init; }
    public double V57BaselineRecall { get; init; }
    public double V510BaselineRecall { get; init; }
    public double BaselineRecallDelta { get; init; }
    public double V57BaselineMrr { get; init; }
    public double V510BaselineMrr { get; init; }
    public double BaselineMrrDelta { get; init; }
    public double MergedRecall { get; init; }
    public double MergedMrr { get; init; }
    public bool ProtocolReproducible { get; init; }
    public bool TieBreakDeterministic { get; init; }
    public int HashOrderSensitivityCount { get; init; }
    public bool EvalLabelScoringDetected { get; init; }
    public bool EvalLabelCandidateGenerationDetected { get; init; }
    public int RiskAfterPolicy { get; init; }
    public int MustNotHitRiskAfterPolicy { get; init; }
    public int LifecycleRiskAfterPolicy { get; init; }
    public int FormalOutputChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool ReadyForRuntimeSwitch { get; init; }
    public bool UseForRuntime { get; init; }
    public IReadOnlyList<CandidateSourceContributionSummary> SourceSummaries { get; init; } = Array.Empty<CandidateSourceContributionSummary>();
    public IReadOnlyList<CandidateSourceDiscriminabilitySplitSummary> SplitSummaries { get; init; } = Array.Empty<CandidateSourceDiscriminabilitySplitSummary>();
    public double TemplateHomogeneityScore { get; init; }
    public bool TemplateHomogeneityDetected { get; init; }
    public bool SourceNonDiscriminativeDetected { get; init; }
    public int NonDiscriminativeSourceCount { get; init; }
    public bool RuntimeChangeGatePassed { get; init; }
    public RuntimeObservableFeatureContractSourceScan SourceScan { get; init; } = new();
    public IReadOnlyDictionary<string, string> SourceReports { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public sealed class CandidateSourceDiscriminabilityAuditReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool AuditPassed { get; init; }
    public string Recommendation { get; init; } = RetrievalEvalProtocolRecommendations.BlockedByProtocolMismatch;
    public RetrievalEvalProtocol Protocol { get; init; } = new();
    public int SampleCount { get; init; }
    public int CorpusItemCount { get; init; }
    public double BaselineRecall { get; init; }
    public double BaselineMrr { get; init; }
    public double MergedRecall { get; init; }
    public double MergedMrr { get; init; }
    public IReadOnlyList<CandidateSourceContributionSummary> SourceSummaries { get; init; } = Array.Empty<CandidateSourceContributionSummary>();
    public IReadOnlyList<CandidateSourceDiscriminabilitySplitSummary> SplitSummaries { get; init; } = Array.Empty<CandidateSourceDiscriminabilitySplitSummary>();
    public double TemplateHomogeneityScore { get; init; }
    public bool TemplateHomogeneityDetected { get; init; }
    public int TemplateSignatureCount { get; init; }
    public int DuplicateTemplateSignatureCount { get; init; }
    public bool SourceNonDiscriminativeDetected { get; init; }
    public int NonDiscriminativeSourceCount { get; init; }
    public int RiskAfterPolicy { get; init; }
    public int MustNotHitRiskAfterPolicy { get; init; }
    public int LifecycleRiskAfterPolicy { get; init; }
    public int FormalOutputChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool ReadyForRuntimeSwitch { get; init; }
    public bool UseForRuntime { get; init; }
    public IReadOnlyDictionary<string, string> SourceReports { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public sealed class RetrievalEvalProtocolGateReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; } = RetrievalEvalProtocolRecommendations.BlockedByProtocolMismatch;
    public RetrievalEvalProtocol Protocol { get; init; } = new();
    public bool BaselineProtocolReproducible { get; init; }
    public bool TieBreakDeterministic { get; init; }
    public int HashOrderSensitivityCount { get; init; }
    public bool EvalLabelScoringDetected { get; init; }
    public bool EvalLabelCandidateGenerationDetected { get; init; }
    public bool SourceNonDiscriminativeDetected { get; init; }
    public bool TemplateHomogeneityDetected { get; init; }
    public bool RuntimeChangeGatePassed { get; init; }
    public int RiskAfterPolicy { get; init; }
    public int MustNotHitRiskAfterPolicy { get; init; }
    public int LifecycleRiskAfterPolicy { get; init; }
    public int FormalOutputChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool ReadyForRuntimeSwitch { get; init; }
    public bool UseForRuntime { get; init; }
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}
