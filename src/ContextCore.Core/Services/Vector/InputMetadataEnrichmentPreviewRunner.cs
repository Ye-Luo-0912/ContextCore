using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// V5.12 input metadata enrichment preview。
/// 只生成运行时可观测 metadata 的内存投影，不写 ingestion/source item，不改变正式检索或 package 输出。
/// </summary>
public sealed class InputMetadataEnrichmentPreviewRunner
{
    private const double Epsilon = 1e-9;
    private const string GeneratedBy = "input-metadata-enrichment-preview/v1";

    public InputMetadataEnrichmentPreviewReport BuildPreview(
        RetrievalDatasetV2GeneratedDataset? dataset,
        RetrievalEvalProtocolGateReport? protocolGate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        RuntimeObservableFeatureContractSourceScan? sourceScan,
        InputMetadataEnrichmentPreviewOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => Build(dataset, protocolGate, runtimeChangeGate, sourceScan, options, sourceReports, gateMode: false);

    public InputMetadataEnrichmentPreviewReport BuildGate(
        RetrievalDatasetV2GeneratedDataset? dataset,
        RetrievalEvalProtocolGateReport? protocolGate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        RuntimeObservableFeatureContractSourceScan? sourceScan,
        InputMetadataEnrichmentPreviewOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => Build(dataset, protocolGate, runtimeChangeGate, sourceScan, options, sourceReports, gateMode: true);

    private InputMetadataEnrichmentPreviewReport Build(
        RetrievalDatasetV2GeneratedDataset? dataset,
        RetrievalEvalProtocolGateReport? protocolGate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        RuntimeObservableFeatureContractSourceScan? sourceScan,
        InputMetadataEnrichmentPreviewOptions? options,
        IReadOnlyDictionary<string, string>? sourceReports,
        bool gateMode)
    {
        options ??= new InputMetadataEnrichmentPreviewOptions();
        var blocked = new List<string>();
        if (dataset is null || dataset.CorpusItems.Count == 0 || dataset.Samples.Count == 0)
        {
            blocked.Add("MissingDataset");
            dataset = new RetrievalDatasetV2GeneratedDataset();
        }

        if (options.RequireV511ProtocolGatePassed && (protocolGate is null || !protocolGate.GatePassed))
        {
            blocked.Add("V511ProtocolGateNotPassed");
        }

        if (options.RequireRuntimeChangeGate && (runtimeChangeGate is null || !runtimeChangeGate.Passed))
        {
            blocked.Add("RuntimeChangeReadinessGateNotPassed");
        }

        if (options.RequireSourceScan && (sourceScan is null || sourceScan.FixtureTokenHitCount > 0))
        {
            blocked.Add("EvalLabelOrFixtureSpecialCasingDetected");
        }

        var protocol = protocolGate?.Protocol ?? options.Protocol ?? new RetrievalEvalProtocol();
        var auditOptions = new RetrievalEvalProtocolAuditOptions
        {
            Protocol = protocol,
            RequireRuntimeChangeGate = options.RequireRuntimeChangeGate,
            RequireSourceScan = options.RequireSourceScan,
            TemplateHomogeneityThreshold = options.TemplateHomogeneityThreshold,
            MinNonDiscriminativeSourcesForDatasetIssue = options.MinNonDiscriminativeSourcesForDatasetIssue,
            UseForRuntime = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false
        };

        var beforeCoverage = ComputeCoverage(dataset);
        var enriched = BuildEnrichedProjection(dataset);
        var afterCoverage = ComputeCoverage(enriched);
        var runner = new RetrievalEvalProtocolAuditRunner();
        var before = runner.Build(dataset, runtimeChangeGate, sourceScan, auditOptions, sourceReports);
        var after = runner.Build(enriched, runtimeChangeGate, sourceScan, auditOptions, sourceReports);

        var coverageDelta = afterCoverage.CoverageScore - beforeCoverage.CoverageScore;
        var recallDelta = after.SourceDiscriminabilityAudit.MergedRecall - before.SourceDiscriminabilityAudit.MergedRecall;
        var mrrDelta = after.SourceDiscriminabilityAudit.MergedMrr - before.SourceDiscriminabilityAudit.MergedMrr;
        var holdoutBefore = ComputeHoldoutMarginal(before.SourceDiscriminabilityAudit.SplitSummaries, protocol.HoldoutSplit);
        var holdoutAfter = ComputeHoldoutMarginal(after.SourceDiscriminabilityAudit.SplitSummaries, protocol.HoldoutSplit);
        var holdoutRecallDelta = holdoutAfter.Recall - holdoutBefore.Recall;
        var holdoutMrrDelta = holdoutAfter.Mrr - holdoutBefore.Mrr;
        var independentNonDense = CountIndependentNonDenseSources(after.SourceDiscriminabilityAudit.SourceSummaries);
        var metadataCoverageImproved = coverageDelta > 0;
        var recallStable = recallDelta >= -options.MetricTolerance
            && mrrDelta >= -options.MetricTolerance
            && holdoutRecallDelta >= -options.MetricTolerance
            && holdoutMrrDelta >= -options.MetricTolerance;
        var riskAfterPolicy = after.SourceDiscriminabilityAudit.RiskAfterPolicy;
        var mustNotRisk = after.SourceDiscriminabilityAudit.MustNotHitRiskAfterPolicy;
        var lifecycleRisk = after.SourceDiscriminabilityAudit.LifecycleRiskAfterPolicy;

        if (!metadataCoverageImproved)
        {
            blocked.Add("MetadataCoverageNotImproved");
        }

        if (!recallStable)
        {
            blocked.Add("RecallOrMrrRegression");
        }

        if (riskAfterPolicy != 0 || mustNotRisk != 0 || lifecycleRisk != 0)
        {
            blocked.Add("RiskAfterPolicyNonZero");
        }

        if (after.SourceDiscriminabilityAudit.FormalPackageWritten
            || after.SourceDiscriminabilityAudit.PackageOutputChanged
            || after.SourceDiscriminabilityAudit.PackingPolicyChanged
            || after.SourceDiscriminabilityAudit.RuntimeMutated
            || after.SourceDiscriminabilityAudit.VectorStoreBindingChanged)
        {
            blocked.Add("RuntimeOrPackageInvariantChanged");
        }

        var uniqueBlocked = independentNonDense == 0;
        var recommendation = ResolveRecommendation(blocked, uniqueBlocked);
        var gatePassed = blocked.Count == 0
            && (independentNonDense > 0 || string.Equals(recommendation, InputMetadataEnrichmentPreviewRecommendations.NeedsSourceDiverseDataset, StringComparison.OrdinalIgnoreCase));

        return new InputMetadataEnrichmentPreviewReport
        {
            OperationId = (gateMode ? "vector-input-metadata-enrichment-gate-" : "vector-input-metadata-enrichment-preview-") + Guid.NewGuid().ToString("N"),
            PreviewPassed = blocked.Count == 0,
            GatePassed = gateMode && gatePassed,
            Recommendation = recommendation,
            Protocol = protocol,
            CorpusItemCount = dataset.CorpusItems.Count,
            SampleCount = dataset.Samples.Count,
            BeforeCoverage = beforeCoverage,
            AfterCoverage = afterCoverage,
            MetadataCoverageDelta = coverageDelta,
            BeforeRecall = before.SourceDiscriminabilityAudit.MergedRecall,
            AfterRecall = after.SourceDiscriminabilityAudit.MergedRecall,
            RecallDelta = recallDelta,
            BeforeMrr = before.SourceDiscriminabilityAudit.MergedMrr,
            AfterMrr = after.SourceDiscriminabilityAudit.MergedMrr,
            MrrDelta = mrrDelta,
            BeforeHoldoutMarginalRecall = holdoutBefore.Recall,
            AfterHoldoutMarginalRecall = holdoutAfter.Recall,
            HoldoutMarginalRecallDelta = holdoutRecallDelta,
            BeforeHoldoutMarginalMrr = holdoutBefore.Mrr,
            AfterHoldoutMarginalMrr = holdoutAfter.Mrr,
            HoldoutMarginalMrrDelta = holdoutMrrDelta,
            BeforeSourceSummaries = before.SourceDiscriminabilityAudit.SourceSummaries,
            AfterSourceSummaries = after.SourceDiscriminabilityAudit.SourceSummaries,
            BeforeSplitSummaries = before.SourceDiscriminabilityAudit.SplitSummaries,
            AfterSplitSummaries = after.SourceDiscriminabilityAudit.SplitSummaries,
            IndependentNonDenseSourceCount = independentNonDense,
            NonDiscriminativeSourceCount = after.SourceDiscriminabilityAudit.NonDiscriminativeSourceCount,
            TemplateHomogeneityScore = after.SourceDiscriminabilityAudit.TemplateHomogeneityScore,
            RiskAfterPolicy = riskAfterPolicy,
            MustNotHitRiskAfterPolicy = mustNotRisk,
            LifecycleRiskAfterPolicy = lifecycleRisk,
            FormalOutputChanged = after.SourceDiscriminabilityAudit.FormalOutputChanged,
            FormalPackageWritten = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            RuntimeMutated = false,
            VectorStoreBindingChanged = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false,
            RuntimeChangeGatePassed = runtimeChangeGate?.Passed ?? false,
            V511ProtocolGatePassed = protocolGate?.GatePassed ?? false,
            SourceScan = sourceScan ?? new RuntimeObservableFeatureContractSourceScan(),
            SourceReports = sourceReports ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            BlockedReasons = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static reason => reason, StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    public static RetrievalDatasetV2GeneratedDataset BuildEnrichedProjection(RetrievalDatasetV2GeneratedDataset dataset)
    {
        var queryTokens = dataset.Samples
            .SelectMany(static sample => Tokenize(sample.QueryText))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new RetrievalDatasetV2GeneratedDataset
        {
            CorpusItems = dataset.CorpusItems.Select(item => EnrichItem(item, queryTokens)).ToArray(),
            Samples = dataset.Samples
        };
    }

    public static string BuildMarkdown(string title, InputMetadataEnrichmentPreviewReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {title}");
        builder.AppendLine();
        builder.AppendLine($"OperationId: `{report.OperationId}`");
        builder.AppendLine($"CreatedAt: `{report.CreatedAt:O}`");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine($"- PreviewPassed: `{report.PreviewPassed}`");
        builder.AppendLine($"- GatePassed: `{report.GatePassed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- Protocol: `{report.Protocol.ProtocolVersion}` vector/merged/final topK `{report.Protocol.VectorTopK}/{report.Protocol.MergedTopK}/{report.Protocol.FinalTopK}`");
        builder.AppendLine($"- Metadata coverage delta: `{report.MetadataCoverageDelta}`");
        builder.AppendLine($"- Recall before/after/delta: `{report.BeforeRecall:F4}` / `{report.AfterRecall:F4}` / `{report.RecallDelta:+0.0000;-0.0000;0.0000}`");
        builder.AppendLine($"- MRR before/after/delta: `{report.BeforeMrr:F4}` / `{report.AfterMrr:F4}` / `{report.MrrDelta:+0.0000;-0.0000;0.0000}`");
        builder.AppendLine($"- Holdout marginal recall before/after/delta: `{report.BeforeHoldoutMarginalRecall:F4}` / `{report.AfterHoldoutMarginalRecall:F4}` / `{report.HoldoutMarginalRecallDelta:+0.0000;-0.0000;0.0000}`");
        builder.AppendLine($"- Independent non-dense source count: `{report.IndependentNonDenseSourceCount}`");
        builder.AppendLine($"- Risk/mustNot/lifecycle: `{report.RiskAfterPolicy}` / `{report.MustNotHitRiskAfterPolicy}` / `{report.LifecycleRiskAfterPolicy}`");
        builder.AppendLine($"- Runtime/package invariants: formalPackage=`{report.FormalPackageWritten}`, packageOutput=`{report.PackageOutputChanged}`, packingPolicy=`{report.PackingPolicyChanged}`, runtime=`{report.RuntimeMutated}`, vectorBinding=`{report.VectorStoreBindingChanged}`");
        builder.AppendLine();
        builder.AppendLine("## Coverage");
        AppendCoverage(builder, "Before", report.BeforeCoverage);
        AppendCoverage(builder, "After", report.AfterCoverage);
        builder.AppendLine();
        builder.AppendLine("## Source Contribution After Enrichment");
        builder.AppendLine("| Source | Candidate | Unique | Unique must-hit recovery | Marginal recall | Marginal MRR | Dense overlap | Non-discriminative |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|---|");
        foreach (var source in report.AfterSourceSummaries.OrderBy(static s => s.SourceId, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"| `{Escape(source.SourceId)}` | {source.CandidateCount} | {source.UniqueCandidateCount} | {source.UniqueMustHitRecoveryCount} | {source.MarginalRecall:F4} | {source.MarginalMrr:F4} | {source.OverlapRateWithDense:F4} | {source.NonDiscriminative} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Blocked Reasons");
        if (report.BlockedReasons.Count == 0)
        {
            builder.AppendLine("- (none)");
        }
        else
        {
            foreach (var reason in report.BlockedReasons)
            {
                builder.AppendLine($"- `{Escape(reason)}`");
            }
        }

        return builder.ToString();
    }

    private static void AppendCoverage(StringBuilder builder, string label, InputMetadataCoverageSnapshot coverage)
    {
        builder.AppendLine($"### {label}");
        builder.AppendLine($"- CoverageScore: `{coverage.CoverageScore}`");
        builder.AppendLine($"- Source/Evidence/Provenance/Fingerprint: `{coverage.SourceRefPresentCount}` / `{coverage.EvidenceRefPresentCount}` / `{coverage.ProvenancePresentCount}` / `{coverage.SourceFingerprintPresentCount}`");
        builder.AppendLine($"- Relation/Lifecycle/Canonical/Query anchors: `{coverage.RelationMetadataPresentCount}` / `{coverage.LifecycleMetadataPresentCount}` / `{coverage.CanonicalMetadataTokenCount}` / `{coverage.QueryDerivedAnchorCount}`");
    }

    private static RetrievalDatasetV2CorpusItem EnrichItem(RetrievalDatasetV2CorpusItem item, IReadOnlySet<string> queryTokens)
    {
        var metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["enrichment.generatedBy"] = GeneratedBy,
            ["enrichment.previewOnly"] = "true",
            ["useForRuntime"] = "false",
            ["canonical.sourceRefs"] = JoinCanonical(item.SourceRefs),
            ["canonical.evidenceRefs"] = JoinCanonical(item.EvidenceRefs),
            ["canonical.provenanceRecordId"] = Canonical(item.Provenance.RecordId),
            ["canonical.sourceFingerprint"] = Canonical(FirstNonEmpty(item.SourceFingerprint, item.Provenance.SourceFingerprint)),
            ["canonical.itemKind"] = Canonical(item.ItemKind),
            ["canonical.sourceKind"] = Canonical(item.SourceKind),
            ["canonical.lifecycle"] = Canonical(item.Lifecycle),
            ["canonical.reviewStatus"] = Canonical(item.ReviewStatus),
            ["canonical.targetSection"] = Canonical(item.TargetSection)
        };
        var relationTypes = item.Relations.Select(static relation => Canonical(relation.RelationType)).Where(static value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var relationDirections = item.Relations.Select(relation => string.Equals(relation.SourceItemId, item.ItemId, StringComparison.OrdinalIgnoreCase) ? "out" : "in").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (relationTypes.Length > 0)
        {
            metadata["canonical.relationTypes"] = string.Join(' ', relationTypes);
            metadata["canonical.relationDirections"] = string.Join(' ', relationDirections);
            metadata["canonical.relationConfidence"] = "1.0";
        }

        var runtimeTokens = Tokenize($"{item.Content} {item.ItemKind} {item.SourceKind} {item.Layer} {item.Lifecycle} {item.ReviewStatus} {item.ReplacementState} {item.TargetSection} {string.Join(' ', item.Tags)} {string.Join(' ', item.Anchors)} {string.Join(' ', item.SourceRefs)} {string.Join(' ', item.EvidenceRefs)} {item.Provenance.RecordId} {item.Provenance.SourceFingerprint} {string.Join(' ', item.Relations.Select(r => r.RelationType))}");
        var queryAnchors = queryTokens.Where(runtimeTokens.Contains).OrderBy(static token => token, StringComparer.OrdinalIgnoreCase).Take(16).ToArray();
        metadata["canonical.queryDerivedAnchors"] = string.Join(' ', queryAnchors);

        var canonicalAnchors = new[]
            {
                "kind-" + Canonical(item.ItemKind),
                "source-" + Canonical(item.SourceKind),
                "lifecycle-" + Canonical(item.Lifecycle),
                "review-" + Canonical(item.ReviewStatus),
                "section-" + Canonical(item.TargetSection)
            }
            .Concat(relationTypes.Select(static relation => "relation-" + relation))
            .Concat(queryAnchors.Select(static anchor => "query-" + anchor))
            .Where(static value => value.Length > 6)
            .ToArray();

        return CopyItem(
            item,
            tags: MergeDistinct(item.Tags, canonicalAnchors),
            anchors: MergeDistinct(item.Anchors, canonicalAnchors),
            metadata: metadata);
    }

    private static InputMetadataCoverageSnapshot ComputeCoverage(RetrievalDatasetV2GeneratedDataset dataset)
    {
        var source = 0;
        var evidence = 0;
        var provenance = 0;
        var fingerprint = 0;
        var relation = 0;
        var lifecycle = 0;
        var canonicalTokens = 0;
        var queryAnchors = 0;
        foreach (var item in dataset.CorpusItems)
        {
            if (item.SourceRefs.Count > 0) source++;
            if (item.EvidenceRefs.Count > 0) evidence++;
            if (!string.IsNullOrWhiteSpace(item.Provenance.RecordId)) provenance++;
            if (!string.IsNullOrWhiteSpace(FirstNonEmpty(item.SourceFingerprint, item.Provenance.SourceFingerprint))) fingerprint++;
            if (item.Relations.Any(static relation => !string.IsNullOrWhiteSpace(relation.RelationType))) relation++;
            if (!string.IsNullOrWhiteSpace(item.Lifecycle)
                && !string.IsNullOrWhiteSpace(item.ReviewStatus)
                && !string.IsNullOrWhiteSpace(item.TargetSection)) lifecycle++;
            canonicalTokens += item.Metadata.Count(pair => pair.Key.StartsWith("canonical.", StringComparison.OrdinalIgnoreCase)
                                                          && !string.IsNullOrWhiteSpace(pair.Value));
            if (item.Metadata.TryGetValue("canonical.queryDerivedAnchors", out var anchors)
                && !string.IsNullOrWhiteSpace(anchors))
            {
                queryAnchors++;
            }
        }

        var score = source + evidence + provenance + fingerprint + relation + lifecycle + canonicalTokens + queryAnchors;
        return new InputMetadataCoverageSnapshot
        {
            CorpusItemCount = dataset.CorpusItems.Count,
            SourceRefPresentCount = source,
            EvidenceRefPresentCount = evidence,
            ProvenancePresentCount = provenance,
            SourceFingerprintPresentCount = fingerprint,
            RelationMetadataPresentCount = relation,
            LifecycleMetadataPresentCount = lifecycle,
            CanonicalMetadataTokenCount = canonicalTokens,
            QueryDerivedAnchorCount = queryAnchors,
            CoverageScore = score
        };
    }

    private static (double Recall, double Mrr) ComputeHoldoutMarginal(
        IReadOnlyList<CandidateSourceDiscriminabilitySplitSummary> summaries,
        string holdoutSplit)
    {
        var holdout = summaries
            .Where(summary => string.Equals(summary.Split, holdoutSplit, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var count = holdout.Sum(static summary => summary.SampleCount);
        if (count == 0)
        {
            return (0, 0);
        }

        return (
            holdout.Sum(static summary => summary.MarginalRecall * summary.SampleCount) / count,
            holdout.Sum(static summary => summary.MarginalMrr * summary.SampleCount) / count);
    }

    private static int CountIndependentNonDenseSources(IReadOnlyList<CandidateSourceContributionSummary> sources)
        => sources.Count(static source =>
            !string.Equals(source.SourceId, RetrievalCandidateSourceIds.Dense, StringComparison.OrdinalIgnoreCase)
            && (source.UniqueMustHitRecoveryCount > 0
                || source.MarginalRecall > Epsilon
                || (source.UniqueCandidateCount > 0 && source.OverlapRateWithDense < 0.95)));

    private static string ResolveRecommendation(IReadOnlyList<string> blocked, bool uniqueBlocked)
    {
        if (blocked.Contains("V511ProtocolGateNotPassed", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("RuntimeChangeReadinessGateNotPassed", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("EvalLabelOrFixtureSpecialCasingDetected", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("RecallOrMrrRegression", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("RiskAfterPolicyNonZero", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("RuntimeOrPackageInvariantChanged", StringComparer.OrdinalIgnoreCase))
        {
            return InputMetadataEnrichmentPreviewRecommendations.BlockedByProtocolMismatch;
        }

        if (blocked.Contains("MetadataCoverageNotImproved", StringComparer.OrdinalIgnoreCase))
        {
            return InputMetadataEnrichmentPreviewRecommendations.NeedsInputMetadataEnrichment;
        }

        return uniqueBlocked
            ? InputMetadataEnrichmentPreviewRecommendations.NeedsSourceDiverseDataset
            : InputMetadataEnrichmentPreviewRecommendations.ReadyForSourceRepairRecheck;
    }

    private static RetrievalDatasetV2CorpusItem CopyItem(
        RetrievalDatasetV2CorpusItem item,
        IReadOnlyList<string> tags,
        IReadOnlyList<string> anchors,
        Dictionary<string, string> metadata)
        => new()
        {
            ItemId = item.ItemId,
            ItemKind = item.ItemKind,
            SourceKind = item.SourceKind,
            Layer = item.Layer,
            Lifecycle = item.Lifecycle,
            ReviewStatus = item.ReviewStatus,
            ReplacementState = item.ReplacementState,
            TargetSection = item.TargetSection,
            SourceRefs = item.SourceRefs,
            EvidenceRefs = item.EvidenceRefs,
            Provenance = item.Provenance,
            SourceFingerprint = item.SourceFingerprint,
            CreatedAt = item.CreatedAt,
            Relations = item.Relations,
            Tags = tags,
            Anchors = anchors,
            Content = item.Content,
            Split = item.Split,
            Metadata = metadata
        };

    private static IReadOnlyList<string> MergeDistinct(IReadOnlyList<string> current, IReadOnlyList<string> extra)
        => current
            .Concat(extra)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string JoinCanonical(IReadOnlyList<string> values)
        => string.Join(' ', values.Select(Canonical).Where(static value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));

    private static string Canonical(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var lastWasSeparator = false;
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                lastWasSeparator = false;
                continue;
            }

            if (!lastWasSeparator)
            {
                builder.Append('-');
                lastWasSeparator = true;
            }
        }

        return builder.ToString().Trim('-');
    }

    private static HashSet<string> Tokenize(string value)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value))
        {
            return result;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch == '-')
            {
                builder.Append(char.ToLowerInvariant(ch));
                continue;
            }

            AddToken(result, builder);
        }

        AddToken(result, builder);
        return result;
    }

    private static void AddToken(ISet<string> result, StringBuilder builder)
    {
        if (builder.Length == 0)
        {
            return;
        }

        result.Add(builder.ToString());
        builder.Clear();
    }

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string Escape(string value)
        => value.Replace("|", "\\|", StringComparison.Ordinal);
}

public static class InputMetadataEnrichmentPreviewRecommendations
{
    public const string ReadyForSourceRepairRecheck = nameof(ReadyForSourceRepairRecheck);
    public const string NeedsSourceDiverseDataset = nameof(NeedsSourceDiverseDataset);
    public const string NeedsInputMetadataEnrichment = nameof(NeedsInputMetadataEnrichment);
    public const string BlockedByProtocolMismatch = nameof(BlockedByProtocolMismatch);
}

public sealed class InputMetadataEnrichmentPreviewOptions
{
    public RetrievalEvalProtocol? Protocol { get; init; }
    public bool RequireV511ProtocolGatePassed { get; init; } = true;
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

public sealed class InputMetadataCoverageSnapshot
{
    public int CorpusItemCount { get; init; }
    public int SourceRefPresentCount { get; init; }
    public int EvidenceRefPresentCount { get; init; }
    public int ProvenancePresentCount { get; init; }
    public int SourceFingerprintPresentCount { get; init; }
    public int RelationMetadataPresentCount { get; init; }
    public int LifecycleMetadataPresentCount { get; init; }
    public int CanonicalMetadataTokenCount { get; init; }
    public int QueryDerivedAnchorCount { get; init; }
    public int CoverageScore { get; init; }
}

public sealed class InputMetadataEnrichmentPreviewReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool PreviewPassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; } = InputMetadataEnrichmentPreviewRecommendations.BlockedByProtocolMismatch;
    public RetrievalEvalProtocol Protocol { get; init; } = new();
    public int CorpusItemCount { get; init; }
    public int SampleCount { get; init; }
    public InputMetadataCoverageSnapshot BeforeCoverage { get; init; } = new();
    public InputMetadataCoverageSnapshot AfterCoverage { get; init; } = new();
    public int MetadataCoverageDelta { get; init; }
    public double BeforeRecall { get; init; }
    public double AfterRecall { get; init; }
    public double RecallDelta { get; init; }
    public double BeforeMrr { get; init; }
    public double AfterMrr { get; init; }
    public double MrrDelta { get; init; }
    public double BeforeHoldoutMarginalRecall { get; init; }
    public double AfterHoldoutMarginalRecall { get; init; }
    public double HoldoutMarginalRecallDelta { get; init; }
    public double BeforeHoldoutMarginalMrr { get; init; }
    public double AfterHoldoutMarginalMrr { get; init; }
    public double HoldoutMarginalMrrDelta { get; init; }
    public IReadOnlyList<CandidateSourceContributionSummary> BeforeSourceSummaries { get; init; } = Array.Empty<CandidateSourceContributionSummary>();
    public IReadOnlyList<CandidateSourceContributionSummary> AfterSourceSummaries { get; init; } = Array.Empty<CandidateSourceContributionSummary>();
    public IReadOnlyList<CandidateSourceDiscriminabilitySplitSummary> BeforeSplitSummaries { get; init; } = Array.Empty<CandidateSourceDiscriminabilitySplitSummary>();
    public IReadOnlyList<CandidateSourceDiscriminabilitySplitSummary> AfterSplitSummaries { get; init; } = Array.Empty<CandidateSourceDiscriminabilitySplitSummary>();
    public int IndependentNonDenseSourceCount { get; init; }
    public int NonDiscriminativeSourceCount { get; init; }
    public double TemplateHomogeneityScore { get; init; }
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
    public bool RuntimeChangeGatePassed { get; init; }
    public bool V511ProtocolGatePassed { get; init; }
    public RuntimeObservableFeatureContractSourceScan SourceScan { get; init; } = new();
    public IReadOnlyDictionary<string, string> SourceReports { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}
