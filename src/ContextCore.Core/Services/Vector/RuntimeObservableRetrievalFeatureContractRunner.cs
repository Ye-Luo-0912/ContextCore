using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// V5.6 Runtime-observable Retrieval Feature Contract。
/// 把 V5.5 所有 repair profile 用到的 retrieval feature 按
/// RuntimeObservable / DerivedAtRuntime / EvalOnly / ForbiddenForScoring 四类
/// 分类，校验 best profile 在 runtime 能合法地拿到所有 scoring 输入；
/// 同时校验源码里没有 fixture / sampleId / 领域字面量的特例。只读：
/// 不接 formal retrieval、不写 formal package、不动 formal selected set、
/// 不改 PackingPolicy / package output、不切 runtime、不绑定 IVectorIndexStore。
/// </summary>
public sealed class RuntimeObservableRetrievalFeatureContractRunner
{
    private const string GraphCandidateSource = "read-only relation evidence / expansion preview";

    private static readonly string[] AllProfileIds =
    [
        RetrievalQualityRepairProfiles.Baseline,
        RetrievalQualityRepairProfiles.CandidatePoolExpansion,
        RetrievalQualityRepairProfiles.TopKAdjustment,
        RetrievalQualityRepairProfiles.SectionAwareBoost,
        RetrievalQualityRepairProfiles.MustHitEvidenceBoost,
        RetrievalQualityRepairProfiles.GraphRelationAnchorBoost,
        RetrievalQualityRepairProfiles.LexicalFallbackBoost,
        RetrievalQualityRepairProfiles.Combined
    ];

    private static readonly IReadOnlyDictionary<string, string> ProfileLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [RetrievalQualityRepairProfiles.Baseline] = "Baseline",
            [RetrievalQualityRepairProfiles.CandidatePoolExpansion] = "Candidate pool expansion",
            [RetrievalQualityRepairProfiles.TopKAdjustment] = "TopK adjustment",
            [RetrievalQualityRepairProfiles.SectionAwareBoost] = "Section-aware boost",
            [RetrievalQualityRepairProfiles.MustHitEvidenceBoost] = "Must-hit evidence boost",
            [RetrievalQualityRepairProfiles.GraphRelationAnchorBoost] = "Graph relation anchor boost",
            [RetrievalQualityRepairProfiles.LexicalFallbackBoost] = "Lexical fallback boost",
            [RetrievalQualityRepairProfiles.Combined] = "Combined repair"
        };

    public RuntimeObservableFeatureContractReport BuildContract(
        RetrievalQualityRepairPreviewReport? repairGate,
        RuntimeObservableFeatureContractSourceScan? sourceScan,
        RuntimeObservableFeatureContractOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => Build(repairGate, sourceScan, options, sourceReports, gateMode: false);

    public RuntimeObservableFeatureContractReport BuildGate(
        RetrievalQualityRepairPreviewReport? repairGate,
        RuntimeObservableFeatureContractSourceScan? sourceScan,
        RuntimeObservableFeatureContractOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => Build(repairGate, sourceScan, options, sourceReports, gateMode: true);

    public static string BuildMarkdown(string title, RuntimeObservableFeatureContractReport report)
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
        builder.AppendLine($"- ContractPassed: `{report.ContractPassed}`");
        builder.AppendLine($"- GatePassed: `{report.GatePassed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- AllowedMode: `{report.AllowedMode}`");
        builder.AppendLine($"- RequiredNextPhase: `{report.RequiredNextPhase}`");
        builder.AppendLine($"- VectorProviderSource: `{report.VectorProviderSource}`");
        builder.AppendLine($"- GraphCandidateSource: `{report.GraphCandidateSource}`");
        builder.AppendLine($"- BestProfileId: `{(string.IsNullOrEmpty(report.BestProfileId) ? "-" : report.BestProfileId)}`");
        builder.AppendLine($"- BestProfileContractStatus: `{report.BestProfileContractStatus}`");
        builder.AppendLine($"- ScoringFeatureCount: `{report.ScoringFeatureCount}`");
        builder.AppendLine($"- FilteringFeatureCount: `{report.FilteringFeatureCount}`");
        builder.AppendLine($"- CandidateExpansionFeatureCount: `{report.CandidateExpansionFeatureCount}`");
        builder.AppendLine($"- RuntimeObservableCount: `{report.RuntimeObservableCount}`");
        builder.AppendLine($"- DerivedAtRuntimeCount: `{report.DerivedAtRuntimeCount}`");
        builder.AppendLine($"- EvalOnlyCount: `{report.EvalOnlyCount}`");
        builder.AppendLine($"- ForbiddenForScoringCount: `{report.ForbiddenForScoringCount}`");
        builder.AppendLine($"- FormalOutputChanged: `{report.FormalOutputChanged}`");
        builder.AppendLine($"- FormalSelectedSetChanged: `{report.FormalSelectedSetChanged}`");
        builder.AppendLine($"- FormalPackageWritten: `{report.FormalPackageWritten}`");
        builder.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}`");
        builder.AppendLine($"- PackingPolicyChanged: `{report.PackingPolicyChanged}`");
        builder.AppendLine($"- RuntimeMutated: `{report.RuntimeMutated}`");
        builder.AppendLine($"- VectorStoreBindingChanged: `{report.VectorStoreBindingChanged}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- RuntimeSwitchAllowed: `{report.RuntimeSwitchAllowed}`");
        builder.AppendLine($"- ReadyForRuntimeSwitch: `{report.ReadyForRuntimeSwitch}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- NoRuntimeMutationInvariant: `{report.NoRuntimeMutationInvariant}`");

        AppendBestProfile(builder, report);
        AppendProfiles(builder, report.Profiles);
        AppendCatalog(builder, report.Catalog);
        AppendSourceScan(builder, report.SourceScan);
        AppendList(builder, "Blocked Reasons", report.BlockedReasons);
        AppendMap(builder, "Source Reports", report.SourceReports);
        builder.AppendLine();
        builder.AppendLine("V5.6 audit only. No formal retrieval, formal package write, formal selected set change, package output mutation, packing policy mutation, runtime switch, or vector store binding change.");
        return builder.ToString();
    }

    private static RuntimeObservableFeatureContractReport Build(
        RetrievalQualityRepairPreviewReport? repairGate,
        RuntimeObservableFeatureContractSourceScan? sourceScan,
        RuntimeObservableFeatureContractOptions? options,
        IReadOnlyDictionary<string, string>? sourceReports,
        bool gateMode)
    {
        options ??= new RuntimeObservableFeatureContractOptions();
        var blocked = new List<string>();

        if (repairGate is null)
        {
            blocked.Add("RepairGateMissing");
        }
        else
        {
            if (options.RequireRepairGatePassed && !repairGate.GatePassed)
            {
                blocked.Add("RepairGateNotPassed");
            }

            AddBoundaryBlocks(blocked, "RepairGate",
                repairGate.FormalRetrievalAllowed,
                repairGate.RuntimeSwitchAllowed,
                repairGate.ReadyForRuntimeSwitch,
                repairGate.UseForRuntime,
                repairGate.PackageOutputChanged,
                repairGate.PackingPolicyChanged,
                repairGate.RuntimeMutated,
                repairGate.VectorStoreBindingChanged,
                repairGate.FormalPackageWritten);
        }

        if (options.RequireSourceScan && (sourceScan is null || !sourceScan.ScanPerformed))
        {
            blocked.Add("SourceScanMissing");
        }

        if (sourceScan is not null && sourceScan.FixtureTokenHitCount > 0)
        {
            blocked.Add("FixtureSpecialCasingDetected");
        }

        if (options.UseForRuntime || options.FormalRetrievalAllowed)
        {
            blocked.Add("RuntimeMutationAttempt");
        }

        var catalog = BuildCatalog();
        var profiles = BuildProfiles(catalog);
        var globalForbiddenInScoring = profiles.Any(p => p.UsesForbiddenForScoring);
        var globalEvalOnlyInScoring = profiles.Any(p => p.UsesEvalOnlyForScoring);
        if (globalForbiddenInScoring)
        {
            blocked.Add("ForbiddenFeatureInScoring");
        }

        if (globalEvalOnlyInScoring)
        {
            blocked.Add("EvalOnlyFeatureInScoring");
        }

        var bestProfileId = repairGate?.BestProfileId ?? string.Empty;
        var bestProfile = string.IsNullOrEmpty(bestProfileId)
            ? null
            : profiles.FirstOrDefault(p => string.Equals(p.ProfileId, bestProfileId, StringComparison.OrdinalIgnoreCase));
        var bestProfileContractStatus = bestProfile?.ContractStatus ?? RuntimeObservableFeatureContractStatuses.None;
        if (bestProfile is not null)
        {
            if (bestProfile.UsesForbiddenForScoring)
            {
                blocked.Add("BestProfileForbiddenFeatureInScoring");
            }

            if (bestProfile.UsesEvalOnlyForScoring)
            {
                blocked.Add("BestProfileEvalOnlyFeatureInScoring");
            }

            if (bestProfile.RequiresRuntimeDerivation
                && bestProfile.RequiredRuntimeDerivationPaths.Any(string.IsNullOrWhiteSpace))
            {
                blocked.Add("BestProfileMissingRuntimeDerivationPath");
            }
        }

        var scoringFeatureCount = catalog.Count(f => string.Equals(f.UsageKind, RuntimeObservableFeatureUsageKinds.Scoring, StringComparison.OrdinalIgnoreCase));
        var filteringFeatureCount = catalog.Count(f => string.Equals(f.UsageKind, RuntimeObservableFeatureUsageKinds.Filtering, StringComparison.OrdinalIgnoreCase));
        var candidateExpansionCount = catalog.Count(f => string.Equals(f.UsageKind, RuntimeObservableFeatureUsageKinds.CandidateExpansion, StringComparison.OrdinalIgnoreCase));
        var runtimeObservableCount = catalog.Count(f => string.Equals(f.Classification, RuntimeObservableFeatureClassifications.RuntimeObservable, StringComparison.OrdinalIgnoreCase));
        var derivedAtRuntimeCount = catalog.Count(f => string.Equals(f.Classification, RuntimeObservableFeatureClassifications.DerivedAtRuntime, StringComparison.OrdinalIgnoreCase));
        var evalOnlyCount = catalog.Count(f => string.Equals(f.Classification, RuntimeObservableFeatureClassifications.EvalOnly, StringComparison.OrdinalIgnoreCase));
        var forbiddenForScoringCount = catalog.Count(f => string.Equals(f.Classification, RuntimeObservableFeatureClassifications.ForbiddenForScoring, StringComparison.OrdinalIgnoreCase));

        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static reason => reason, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var passed = distinctBlocked.Length == 0;
        var operationId = (gateMode
            ? "runtime-observable-feature-contract-gate-"
            : "runtime-observable-feature-contract-")
            + Guid.NewGuid().ToString("N");

        return new RuntimeObservableFeatureContractReport
        {
            OperationId = operationId,
            CreatedAt = DateTimeOffset.UtcNow,
            ContractPassed = passed,
            GatePassed = gateMode && passed,
            Recommendation = ResolveRecommendation(distinctBlocked, passed),
            AllowedMode = "AuditOnly",
            RequiredNextPhase = "RuntimeObservableFeatureFreeze",
            VectorProviderSource = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1,
            GraphCandidateSource = GraphCandidateSource,
            BestProfileId = bestProfileId,
            BestProfileContractStatus = bestProfileContractStatus,
            Profiles = profiles,
            Catalog = catalog,
            ScoringFeatureCount = scoringFeatureCount,
            FilteringFeatureCount = filteringFeatureCount,
            CandidateExpansionFeatureCount = candidateExpansionCount,
            RuntimeObservableCount = runtimeObservableCount,
            DerivedAtRuntimeCount = derivedAtRuntimeCount,
            EvalOnlyCount = evalOnlyCount,
            ForbiddenForScoringCount = forbiddenForScoringCount,
            SourceScan = sourceScan ?? new RuntimeObservableFeatureContractSourceScan(),
            FormalOutputChanged = 0,
            FormalSelectedSetChanged = false,
            FormalPackageWritten = false,
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

    private static IReadOnlyList<RuntimeObservableFeatureUsage> BuildCatalog()
    {
        var allProfiles = AllProfileIds;
        var sectionAwareScoring = new[]
        {
            RetrievalQualityRepairProfiles.SectionAwareBoost,
            RetrievalQualityRepairProfiles.Combined
        };
        var evidenceScoring = new[]
        {
            RetrievalQualityRepairProfiles.MustHitEvidenceBoost,
            RetrievalQualityRepairProfiles.Combined
        };
        var relationScoring = new[]
        {
            RetrievalQualityRepairProfiles.GraphRelationAnchorBoost,
            RetrievalQualityRepairProfiles.Combined
        };
        var lexicalScoring = new[]
        {
            RetrievalQualityRepairProfiles.LexicalFallbackBoost,
            RetrievalQualityRepairProfiles.Combined
        };

        return new RuntimeObservableFeatureUsage[]
        {
            // Vector scoring inputs (item-side, runtime-observable, used by every profile).
            new()
            {
                FeatureId = "query.tokens",
                Classification = RuntimeObservableFeatureClassifications.RuntimeObservable,
                UsageKind = RuntimeObservableFeatureUsageKinds.Scoring,
                CurrentSource = "tokenized RetrievalDatasetV2Sample.QueryText (== runtime query text)",
                Description = "Query token set fed into dense / lexical / anchor / negative-cue scoring.",
                ProfileIds = allProfiles
            },
            new()
            {
                FeatureId = "item.Content",
                Classification = RuntimeObservableFeatureClassifications.RuntimeObservable,
                UsageKind = RuntimeObservableFeatureUsageKinds.Scoring,
                CurrentSource = "RetrievalDatasetV2CorpusItem.Content (== ContextItem.Content at runtime)",
                Description = "Item body tokens for dense and lexical scoring.",
                ProfileIds = allProfiles
            },
            new()
            {
                FeatureId = "item.Tags",
                Classification = RuntimeObservableFeatureClassifications.RuntimeObservable,
                UsageKind = RuntimeObservableFeatureUsageKinds.Scoring,
                CurrentSource = "RetrievalDatasetV2CorpusItem.Tags (== ContextItem.Tags)",
                Description = "Tag tokens contributing to dense and anchor scoring.",
                ProfileIds = allProfiles
            },
            new()
            {
                FeatureId = "item.Anchors",
                Classification = RuntimeObservableFeatureClassifications.RuntimeObservable,
                UsageKind = RuntimeObservableFeatureUsageKinds.Scoring,
                CurrentSource = "RetrievalDatasetV2CorpusItem.Anchors (== item anchor metadata)",
                Description = "Anchor tokens contributing to anchor and negative-cue scoring.",
                ProfileIds = allProfiles
            },
            new()
            {
                FeatureId = "item.TargetSection",
                Classification = RuntimeObservableFeatureClassifications.RuntimeObservable,
                UsageKind = RuntimeObservableFeatureUsageKinds.Scoring,
                CurrentSource = "RetrievalDatasetV2CorpusItem.TargetSection (== item target section)",
                Description = "Target section token contributing to dense scoring.",
                ProfileIds = allProfiles
            },
            new()
            {
                FeatureId = "item.ItemKind",
                Classification = RuntimeObservableFeatureClassifications.RuntimeObservable,
                UsageKind = RuntimeObservableFeatureUsageKinds.Scoring,
                CurrentSource = "RetrievalDatasetV2CorpusItem.ItemKind",
                Description = "Item kind contributing to dense scoring.",
                ProfileIds = allProfiles
            },
            new()
            {
                FeatureId = "item.SourceKind",
                Classification = RuntimeObservableFeatureClassifications.RuntimeObservable,
                UsageKind = RuntimeObservableFeatureUsageKinds.Scoring,
                CurrentSource = "RetrievalDatasetV2CorpusItem.SourceKind",
                Description = "Source kind contributing to dense scoring.",
                ProfileIds = allProfiles
            },

            // Section-aware boost — runtime needs sample.ExpectedTargetSection from router.
            new()
            {
                FeatureId = "item.TargetSection × sample.ExpectedTargetSection",
                Classification = RuntimeObservableFeatureClassifications.DerivedAtRuntime,
                UsageKind = RuntimeObservableFeatureUsageKinds.Scoring,
                CurrentSource = "RetrievalDatasetV2CorpusItem.TargetSection × RetrievalDatasetV2Sample.ExpectedTargetSection",
                DerivationPath = "router.targetSection or packageContext.targetSection",
                Description = "Section-aware boost activates when item.TargetSection equals sample.ExpectedTargetSection.",
                ProfileIds = sectionAwareScoring
            },

            // Must-hit evidence boost — sample-side EvidenceRefs / SourceRefs.
            new()
            {
                FeatureId = "item.EvidenceRefs × sample.EvidenceRefs",
                Classification = RuntimeObservableFeatureClassifications.DerivedAtRuntime,
                UsageKind = RuntimeObservableFeatureUsageKinds.Scoring,
                CurrentSource = "RetrievalDatasetV2CorpusItem.EvidenceRefs × RetrievalDatasetV2Sample.EvidenceRefs",
                DerivationPath = "query.evidenceAnchors",
                Description = "Evidence boost activates when item.EvidenceRefs intersects sample.EvidenceRefs.",
                ProfileIds = evidenceScoring
            },
            new()
            {
                FeatureId = "item.SourceRefs × sample.SourceRefs",
                Classification = RuntimeObservableFeatureClassifications.DerivedAtRuntime,
                UsageKind = RuntimeObservableFeatureUsageKinds.Scoring,
                CurrentSource = "RetrievalDatasetV2CorpusItem.SourceRefs × RetrievalDatasetV2Sample.SourceRefs",
                DerivationPath = "query.sourceAnchors",
                Description = "Evidence boost activates when item.SourceRefs intersects sample.SourceRefs.",
                ProfileIds = evidenceScoring
            },

            // Graph relation anchor boost — sample.RequiredRelations.
            new()
            {
                FeatureId = "item.Relations × sample.RequiredRelations",
                Classification = RuntimeObservableFeatureClassifications.DerivedAtRuntime,
                UsageKind = RuntimeObservableFeatureUsageKinds.Scoring,
                CurrentSource = "RetrievalDatasetV2CorpusItem.Relations × RetrievalDatasetV2Sample.RequiredRelations",
                DerivationPath = "planner.requiredRelations or relationStore lookup",
                Description = "Relation boost activates when item.Relations intersects sample.RequiredRelations.",
                ProfileIds = relationScoring
            },

            // Lexical fallback boost — runtime-observable ratio.
            new()
            {
                FeatureId = "lexical/dense ratio",
                Classification = RuntimeObservableFeatureClassifications.RuntimeObservable,
                UsageKind = RuntimeObservableFeatureUsageKinds.Scoring,
                CurrentSource = "Computed from query.tokens × item.Content",
                Description = "Boost when lexical/dense > 0.6, recovering low-overlap items.",
                ProfileIds = lexicalScoring
            },

            // Filtering — eligibility / lifecycle / mustNot / target section.
            new()
            {
                FeatureId = "item.Lifecycle",
                Classification = RuntimeObservableFeatureClassifications.RuntimeObservable,
                UsageKind = RuntimeObservableFeatureUsageKinds.Filtering,
                CurrentSource = "RetrievalDatasetV2CorpusItem.Lifecycle (== item lifecycle metadata)",
                Description = "Used by IsLifecycleRisk and IsBlockedByEligibility.",
                ProfileIds = allProfiles
            },
            new()
            {
                FeatureId = "item.ReplacementState",
                Classification = RuntimeObservableFeatureClassifications.RuntimeObservable,
                UsageKind = RuntimeObservableFeatureUsageKinds.Filtering,
                CurrentSource = "RetrievalDatasetV2CorpusItem.ReplacementState",
                Description = "Used by IsLifecycleRisk and IsBlockedByEligibility.",
                ProfileIds = allProfiles
            },
            new()
            {
                FeatureId = "item.ReviewStatus",
                Classification = RuntimeObservableFeatureClassifications.RuntimeObservable,
                UsageKind = RuntimeObservableFeatureUsageKinds.Filtering,
                CurrentSource = "RetrievalDatasetV2CorpusItem.ReviewStatus",
                Description = "Available for eligibility filters; not actively branched in V5.5 runner.",
                ProfileIds = allProfiles
            },
            new()
            {
                FeatureId = "item.TargetSection × sample.ExpectedTargetSection (filter)",
                Classification = RuntimeObservableFeatureClassifications.DerivedAtRuntime,
                UsageKind = RuntimeObservableFeatureUsageKinds.Filtering,
                CurrentSource = "RetrievalDatasetV2CorpusItem.TargetSection × RetrievalDatasetV2Sample.ExpectedTargetSection",
                DerivationPath = "router.targetSection or packageContext.targetSection",
                Description = "Eligibility filter requires sample target section equality at runtime.",
                ProfileIds = allProfiles
            },
            new()
            {
                FeatureId = "sample.MustNotHitItemIds (filter)",
                Classification = RuntimeObservableFeatureClassifications.ForbiddenForScoring,
                UsageKind = RuntimeObservableFeatureUsageKinds.Filtering,
                CurrentSource = "RetrievalDatasetV2Sample.MustNotHitItemIds (eval ground truth)",
                DerivationPath = "query.mustNotItemIds from policy (runtime equivalent only)",
                Description = "post-scoring risk gate references mustNot list; current runner reads eval-side label, runtime needs query.mustNotItemIds policy.",
                ProfileIds = allProfiles
            },

            // Candidate expansion — graph candidate scoring leaning on sample-side metadata.
            new()
            {
                FeatureId = "item.Relations × sample.RequiredRelations (graph collection)",
                Classification = RuntimeObservableFeatureClassifications.DerivedAtRuntime,
                UsageKind = RuntimeObservableFeatureUsageKinds.CandidateExpansion,
                CurrentSource = "RetrievalDatasetV2CorpusItem.Relations × RetrievalDatasetV2Sample.RequiredRelations",
                DerivationPath = "planner.requiredRelations or relationStore lookup",
                Description = "Graph candidate overlap += 2 when item.Relations intersects sample.RequiredRelations.",
                ProfileIds = allProfiles
            },
            new()
            {
                FeatureId = "item.EvidenceRefs × sample.EvidenceRefs (graph collection)",
                Classification = RuntimeObservableFeatureClassifications.DerivedAtRuntime,
                UsageKind = RuntimeObservableFeatureUsageKinds.CandidateExpansion,
                CurrentSource = "RetrievalDatasetV2CorpusItem.EvidenceRefs × RetrievalDatasetV2Sample.EvidenceRefs",
                DerivationPath = "query.evidenceAnchors",
                Description = "Graph candidate overlap += 1 on evidence overlap.",
                ProfileIds = allProfiles
            },
            new()
            {
                FeatureId = "item.SourceRefs × sample.SourceRefs (graph collection)",
                Classification = RuntimeObservableFeatureClassifications.DerivedAtRuntime,
                UsageKind = RuntimeObservableFeatureUsageKinds.CandidateExpansion,
                CurrentSource = "RetrievalDatasetV2CorpusItem.SourceRefs × RetrievalDatasetV2Sample.SourceRefs",
                DerivationPath = "query.sourceAnchors",
                Description = "Graph candidate overlap += 1 on source overlap.",
                ProfileIds = allProfiles
            },
            new()
            {
                FeatureId = "sample.MustHitItemIds (graph collection)",
                Classification = RuntimeObservableFeatureClassifications.ForbiddenForScoring,
                UsageKind = RuntimeObservableFeatureUsageKinds.CandidateExpansion,
                CurrentSource = "RetrievalDatasetV2Sample.MustHitItemIds (eval ground truth)",
                DerivationPath = "(none — runtime must avoid leaking labels into candidate ranking)",
                Description = "Graph candidate overlap += 3 when item is in MustHitItemIds; this is eval-only and must be removed before runtime promotion.",
                ProfileIds = allProfiles
            },

            // Knobs (top-K values).
            new()
            {
                FeatureId = "options.VectorTopK",
                Classification = RuntimeObservableFeatureClassifications.RuntimeObservable,
                UsageKind = RuntimeObservableFeatureUsageKinds.Knob,
                CurrentSource = "RetrievalQualityRepairPreviewOptions.{Baseline,Expansion}VectorTopK",
                Description = "Number of vector candidates retained.",
                ProfileIds = allProfiles
            },
            new()
            {
                FeatureId = "options.GraphTopK",
                Classification = RuntimeObservableFeatureClassifications.RuntimeObservable,
                UsageKind = RuntimeObservableFeatureUsageKinds.Knob,
                CurrentSource = "RetrievalQualityRepairPreviewOptions.{Baseline,Expansion}GraphTopK",
                Description = "Number of graph candidates retained.",
                ProfileIds = allProfiles
            },
            new()
            {
                FeatureId = "options.MergedTopK",
                Classification = RuntimeObservableFeatureClassifications.RuntimeObservable,
                UsageKind = RuntimeObservableFeatureUsageKinds.Knob,
                CurrentSource = "RetrievalQualityRepairPreviewOptions.{Baseline,Expansion,Adjusted}MergedTopK",
                Description = "Size of merged candidate window before TopK eval slice.",
                ProfileIds = allProfiles
            }
        };
    }

    private static IReadOnlyList<RuntimeObservableFeatureContractProfile> BuildProfiles(
        IReadOnlyList<RuntimeObservableFeatureUsage> catalog)
    {
        var profiles = new List<RuntimeObservableFeatureContractProfile>();
        foreach (var profileId in AllProfileIds)
        {
            var features = catalog
                .Where(f => f.ProfileIds.Contains(profileId, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            var scoringFeatures = features
                .Where(f => string.Equals(f.UsageKind, RuntimeObservableFeatureUsageKinds.Scoring, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var usesForbiddenInScoring = scoringFeatures.Any(f =>
                string.Equals(f.Classification, RuntimeObservableFeatureClassifications.ForbiddenForScoring, StringComparison.OrdinalIgnoreCase));
            var usesEvalOnlyInScoring = scoringFeatures.Any(f =>
                string.Equals(f.Classification, RuntimeObservableFeatureClassifications.EvalOnly, StringComparison.OrdinalIgnoreCase));
            var requiresRuntimeDerivation = scoringFeatures.Any(f =>
                string.Equals(f.Classification, RuntimeObservableFeatureClassifications.DerivedAtRuntime, StringComparison.OrdinalIgnoreCase));

            string contractStatus;
            if (usesForbiddenInScoring)
            {
                contractStatus = RuntimeObservableFeatureContractStatuses.ForbiddenForScoring;
            }
            else if (usesEvalOnlyInScoring)
            {
                contractStatus = RuntimeObservableFeatureContractStatuses.EvalOnly;
            }
            else if (requiresRuntimeDerivation)
            {
                contractStatus = RuntimeObservableFeatureContractStatuses.RequiresRuntimeDerivation;
            }
            else
            {
                contractStatus = RuntimeObservableFeatureContractStatuses.RuntimeSafe;
            }

            var derivationPaths = scoringFeatures
                .Where(f => string.Equals(f.Classification, RuntimeObservableFeatureClassifications.DerivedAtRuntime, StringComparison.OrdinalIgnoreCase))
                .Select(f => f.DerivationPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var notes = new List<string>();
            var nonScoringEvalSide = features
                .Where(f => !string.Equals(f.UsageKind, RuntimeObservableFeatureUsageKinds.Scoring, StringComparison.OrdinalIgnoreCase)
                    && (string.Equals(f.Classification, RuntimeObservableFeatureClassifications.DerivedAtRuntime, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(f.Classification, RuntimeObservableFeatureClassifications.ForbiddenForScoring, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            foreach (var feature in nonScoringEvalSide)
            {
                notes.Add($"{feature.UsageKind} uses {feature.FeatureId} (currently from {feature.CurrentSource}); runtime must supply {feature.DerivationPath}");
            }

            profiles.Add(new RuntimeObservableFeatureContractProfile
            {
                ProfileId = profileId,
                ProfileLabel = ProfileLabels.TryGetValue(profileId, out var label) ? label : profileId,
                ContractStatus = contractStatus,
                Features = features,
                UsesForbiddenForScoring = usesForbiddenInScoring,
                UsesEvalOnlyForScoring = usesEvalOnlyInScoring,
                RequiresRuntimeDerivation = requiresRuntimeDerivation,
                RequiredRuntimeDerivationPaths = derivationPaths,
                Notes = notes
            });
        }

        return profiles;
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

    private static string ResolveRecommendation(IReadOnlyList<string> blocked, bool passed)
    {
        if (passed)
        {
            return RuntimeObservableFeatureContractRecommendations.ReadyForRuntimeObservableFeatureFreeze;
        }

        if (blocked.Contains("RepairGateMissing", StringComparer.OrdinalIgnoreCase))
        {
            return RuntimeObservableFeatureContractRecommendations.BlockedByMissingRepairGate;
        }

        if (blocked.Contains("RepairGateNotPassed", StringComparer.OrdinalIgnoreCase))
        {
            return RuntimeObservableFeatureContractRecommendations.BlockedByRepairGateNotPassed;
        }

        if (blocked.Contains("SourceScanMissing", StringComparer.OrdinalIgnoreCase))
        {
            return RuntimeObservableFeatureContractRecommendations.BlockedBySourceScanMissing;
        }

        if (blocked.Contains("FixtureSpecialCasingDetected", StringComparer.OrdinalIgnoreCase))
        {
            return RuntimeObservableFeatureContractRecommendations.BlockedByFixtureSpecialCasing;
        }

        if (blocked.Contains("ForbiddenFeatureInScoring", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("BestProfileForbiddenFeatureInScoring", StringComparer.OrdinalIgnoreCase))
        {
            return RuntimeObservableFeatureContractRecommendations.BlockedByForbiddenFeatureInScoring;
        }

        if (blocked.Contains("EvalOnlyFeatureInScoring", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("BestProfileEvalOnlyFeatureInScoring", StringComparer.OrdinalIgnoreCase))
        {
            return RuntimeObservableFeatureContractRecommendations.BlockedByEvalOnlyFeatureInScoring;
        }

        if (blocked.Contains("BestProfileMissingRuntimeDerivationPath", StringComparer.OrdinalIgnoreCase))
        {
            return RuntimeObservableFeatureContractRecommendations.BlockedByMissingRuntimeDerivationPath;
        }

        if (blocked.Any(static reason => reason.Contains("PackageOutput", StringComparison.OrdinalIgnoreCase)))
        {
            return RuntimeObservableFeatureContractRecommendations.BlockedByPackageOutputChange;
        }

        if (blocked.Any(static reason => reason.Contains("PackingPolicy", StringComparison.OrdinalIgnoreCase)))
        {
            return RuntimeObservableFeatureContractRecommendations.BlockedByPackingPolicyChange;
        }

        if (blocked.Any(static reason => reason.Contains("VectorStoreBinding", StringComparison.OrdinalIgnoreCase)))
        {
            return RuntimeObservableFeatureContractRecommendations.BlockedByVectorStoreBindingChange;
        }

        if (blocked.Any(static reason => reason.Contains("FormalRetrieval", StringComparison.OrdinalIgnoreCase)))
        {
            return RuntimeObservableFeatureContractRecommendations.BlockedByFormalOutputChange;
        }

        if (blocked.Any(static reason => reason.Contains("Runtime", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("FormalPackageWritten", StringComparison.OrdinalIgnoreCase)))
        {
            return RuntimeObservableFeatureContractRecommendations.BlockedByRuntimeMutation;
        }

        return RuntimeObservableFeatureContractRecommendations.KeepPreviewOnly;
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

    private static void AppendBestProfile(StringBuilder builder, RuntimeObservableFeatureContractReport report)
    {
        builder.AppendLine();
        builder.AppendLine("## Best Profile Contract");
        if (string.IsNullOrEmpty(report.BestProfileId))
        {
            builder.AppendLine("- (no best profile selected)");
            return;
        }

        var bestProfile = report.Profiles.FirstOrDefault(p => string.Equals(p.ProfileId, report.BestProfileId, StringComparison.OrdinalIgnoreCase));
        if (bestProfile is null)
        {
            builder.AppendLine($"- bestProfileId: `{report.BestProfileId}` (not found in catalog)");
            return;
        }

        builder.AppendLine($"- profileId: `{bestProfile.ProfileId}` ({bestProfile.ProfileLabel})");
        builder.AppendLine($"- status: `{bestProfile.ContractStatus}`");
        builder.AppendLine($"- usesForbiddenForScoring: `{bestProfile.UsesForbiddenForScoring}`");
        builder.AppendLine($"- usesEvalOnlyForScoring: `{bestProfile.UsesEvalOnlyForScoring}`");
        builder.AppendLine($"- requiresRuntimeDerivation: `{bestProfile.RequiresRuntimeDerivation}`");
        if (bestProfile.RequiredRuntimeDerivationPaths.Count > 0)
        {
            builder.AppendLine($"- derivation paths: `{string.Join("; ", bestProfile.RequiredRuntimeDerivationPaths)}`");
        }
    }

    private static void AppendProfiles(StringBuilder builder, IReadOnlyList<RuntimeObservableFeatureContractProfile> profiles)
    {
        builder.AppendLine();
        builder.AppendLine("## Profile Contracts");
        if (profiles.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return;
        }

        foreach (var profile in profiles)
        {
            builder.AppendLine($"- profileId: `{profile.ProfileId}` ({profile.ProfileLabel}) status=`{profile.ContractStatus}`");
            var scoringFeatures = profile.Features
                .Where(f => string.Equals(f.UsageKind, RuntimeObservableFeatureUsageKinds.Scoring, StringComparison.OrdinalIgnoreCase))
                .Select(f => $"{f.FeatureId} [{f.Classification}]")
                .ToArray();
            builder.AppendLine($"  - scoring features: {(scoringFeatures.Length == 0 ? "-" : string.Join(", ", scoringFeatures))}");
            if (profile.RequiredRuntimeDerivationPaths.Count > 0)
            {
                builder.AppendLine($"  - derivation paths: {string.Join("; ", profile.RequiredRuntimeDerivationPaths)}");
            }

            if (profile.Notes.Count > 0)
            {
                builder.AppendLine("  - notes:");
                foreach (var note in profile.Notes.Take(5))
                {
                    builder.AppendLine($"    - {note}");
                }
            }
        }
    }

    private static void AppendCatalog(StringBuilder builder, IReadOnlyList<RuntimeObservableFeatureUsage> catalog)
    {
        builder.AppendLine();
        builder.AppendLine("## Feature Catalog");
        if (catalog.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return;
        }

        foreach (var feature in catalog)
        {
            builder.AppendLine($"- featureId: `{feature.FeatureId}`");
            builder.AppendLine($"  - classification: `{feature.Classification}` usageKind: `{feature.UsageKind}`");
            builder.AppendLine($"  - currentSource: `{feature.CurrentSource}`");
            if (!string.IsNullOrWhiteSpace(feature.DerivationPath))
            {
                builder.AppendLine($"  - derivationPath: `{feature.DerivationPath}`");
            }

            builder.AppendLine($"  - profiles: `{string.Join(", ", feature.ProfileIds)}`");
            if (!string.IsNullOrWhiteSpace(feature.Description))
            {
                builder.AppendLine($"  - description: {feature.Description}");
            }
        }
    }

    private static void AppendSourceScan(StringBuilder builder, RuntimeObservableFeatureContractSourceScan scan)
    {
        builder.AppendLine();
        builder.AppendLine("## Source Scan");
        builder.AppendLine($"- scanPerformed: `{scan.ScanPerformed}`");
        builder.AppendLine($"- scannedFileCount: `{scan.ScannedFileCount}`");
        builder.AppendLine($"- fixtureTokenHitCount: `{scan.FixtureTokenHitCount}`");
        if (scan.FlaggedTokens.Count > 0)
        {
            builder.AppendLine($"- flaggedTokens: `{string.Join(", ", scan.FlaggedTokens)}`");
        }

        if (scan.FlaggedFiles.Count > 0)
        {
            builder.AppendLine($"- flaggedFiles: `{string.Join(", ", scan.FlaggedFiles)}`");
        }

        if (scan.ScannedFiles.Count > 0)
        {
            builder.AppendLine("- scannedFiles:");
            foreach (var file in scan.ScannedFiles)
            {
                builder.AppendLine($"  - `{file}`");
            }
        }
    }
}

/// <summary>V5.6 runtime-observable retrieval feature contract 选项。</summary>
public sealed class RuntimeObservableFeatureContractOptions
{
    public bool RequireRepairGatePassed { get; init; } = true;

    public bool RequireSourceScan { get; init; } = true;

    public bool UseForRuntime { get; init; } = false;

    public bool FormalRetrievalAllowed { get; init; } = false;
}
