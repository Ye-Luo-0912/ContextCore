using System.Text;

namespace ContextCore.Core.Services;

/// <summary>
/// V6.7 shadow candidate merge preview. 仅根据 V6.6 的 hypothetical delta
/// 构造 preview merged set；正式 selected set、formal package、PackingPolicy、runtime 和 vector binding 均不改变。
/// </summary>
public sealed class ShadowCandidateMergePreviewRunner
{
    public ShadowCandidateMergePreviewReport RunPreview(
        SourceDiverseShadowAdapterValidationReport? v66Gate,
        ShadowCandidateMergePreviewOptions? options = null)
    {
        options ??= new ShadowCandidateMergePreviewOptions();
        var blocked = new List<string>();
        if (v66Gate is null || !v66Gate.GatePassed)
            blocked.Add("V66GateMissingOrNotPassed");

        var appliedAddCount = v66Gate?.AppliedAddCount ?? 0;
        var appliedRemoveCount = v66Gate?.AppliedRemoveCount ?? 0;
        if (appliedAddCount != 0 || appliedRemoveCount != 0)
            blocked.Add("AppliedDeltaDetected");

        if (options.UseForRuntime ||
            options.FormalRetrievalAllowed ||
            options.RuntimeSwitchAllowed ||
            options.ReadyForRuntimeSwitch ||
            options.FormalSelectedSetChanged ||
            options.FormalPackageWritten ||
            options.PackageOutputChanged ||
            options.PackingPolicyChanged ||
            options.RuntimeMutated ||
            options.VectorStoreBindingChanged)
        {
            blocked.Add("RuntimeOrFormalInvariantChanged");
        }

        var results = new List<ShadowCandidateMergePreviewSampleResult>();
        var baselineCandidateCount = 0;
        var shadowAdapterCandidateCount = 0;
        var mergedPreviewCandidateCount = 0;
        var previewAddCount = 0;
        var previewRemoveCount = 0;
        var priorityOrderDeltaCount = 0;
        var priorityInversionCount = 0;
        var tokenDeltaTotal = 0;
        var tokenDeltaMax = 0;
        var sectionMismatchCount = 0;
        var droppedRequiredCandidateCount = 0;

        if (v66Gate is not null)
        {
            foreach (var sample in v66Gate.SampleResults)
            {
                var baseline = sample.BaselineTopK.ToArray();
                var shadowAdapter = sample.ShadowExpandedPool.ToArray();
                var shadowFinal = sample.ShadowFinalTopK.ToArray();
                var merged = BuildMergedPreview(baseline, shadowFinal);
                var add = merged.Where(id => !baseline.Contains(id, StringComparer.OrdinalIgnoreCase)).ToArray();
                var remove = baseline.Where(id => !merged.Contains(id, StringComparer.OrdinalIgnoreCase)).ToArray();
                var orderDelta = !baseline.SequenceEqual(merged, StringComparer.OrdinalIgnoreCase);
                var inversions = CountCommonOrderInversions(baseline, merged);
                var sectionMismatch = sample.SectionDelta;

                baselineCandidateCount += baseline.Length;
                shadowAdapterCandidateCount += shadowAdapter.Length;
                mergedPreviewCandidateCount += merged.Count;
                previewAddCount += add.Length;
                previewRemoveCount += remove.Length;
                if (orderDelta)
                    priorityOrderDeltaCount++;
                priorityInversionCount += inversions;
                tokenDeltaTotal += sample.TokenDelta;
                tokenDeltaMax = Math.Max(tokenDeltaMax, Math.Abs(sample.TokenDelta));
                if (sectionMismatch)
                    sectionMismatchCount++;

                results.Add(new ShadowCandidateMergePreviewSampleResult
                {
                    SampleId = sample.SampleId,
                    Split = sample.Split,
                    Difficulty = sample.Difficulty,
                    BaselineCandidates = baseline,
                    ShadowAdapterCandidates = shadowAdapter,
                    MergedPreviewCandidates = merged,
                    PreviewAddCandidateIds = add,
                    PreviewRemoveCandidateIds = remove,
                    TokenDelta = sample.TokenDelta,
                    SectionMismatch = sectionMismatch,
                    PriorityOrderChanged = orderDelta,
                    PriorityInversionCount = inversions,
                    DroppedRequiredCandidateCount = 0
                });
            }
        }

        var previewGenerated = results.Count > 0 && mergedPreviewCandidateCount > 0;
        if (!previewGenerated)
            blocked.Add("PreviewMergedSetMissing");
        if (previewAddCount <= 0 || previewRemoveCount <= 0)
            blocked.Add("PreviewDeltaMissing");
        if (Math.Abs(tokenDeltaTotal) > options.TokenDeltaBudget || tokenDeltaMax > options.TokenDeltaMaxBudget)
            blocked.Add("TokenDeltaBudgetExceeded");
        if (priorityInversionCount > 0 || sectionMismatchCount > 0)
            blocked.Add("PriorityOrSectionMismatch");
        if (options.RiskAfterPolicy != 0 || options.MustNotHitRiskAfterPolicy != 0 || options.LifecycleRiskAfterPolicy != 0)
            blocked.Add("RiskDetected");

        var distinctBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static reason => reason).ToArray();
        var passed = distinctBlocked.Length == 0;
        return new ShadowCandidateMergePreviewReport
        {
            OperationId = $"shadow-candidate-merge-preview-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            PreviewPassed = passed,
            GatePassed = options.GateMode && passed,
            Recommendation = ResolveRecommendation(distinctBlocked, passed),
            V66GatePassed = v66Gate?.GatePassed == true,
            PreviewMergedSetGenerated = previewGenerated,
            SampleCount = results.Count,
            BaselineCandidateCount = baselineCandidateCount,
            ShadowAdapterCandidateCount = shadowAdapterCandidateCount,
            MergedPreviewCandidateCount = mergedPreviewCandidateCount,
            PreviewAddCount = previewAddCount,
            PreviewRemoveCount = previewRemoveCount,
            AppliedAddCount = appliedAddCount,
            AppliedRemoveCount = appliedRemoveCount,
            FormalSelectedSetChanged = options.FormalSelectedSetChanged,
            FormalOutputChanged = options.FormalOutputChanged,
            FormalPackageWritten = options.FormalPackageWritten,
            PackageOutputChanged = options.PackageOutputChanged,
            PackingPolicyChanged = options.PackingPolicyChanged,
            RuntimeMutated = options.RuntimeMutated,
            VectorStoreBindingChanged = options.VectorStoreBindingChanged,
            RiskAfterPolicy = options.RiskAfterPolicy,
            MustNotHitRiskAfterPolicy = options.MustNotHitRiskAfterPolicy,
            LifecycleRiskAfterPolicy = options.LifecycleRiskAfterPolicy,
            TokenDeltaTotal = tokenDeltaTotal,
            TokenDeltaMax = tokenDeltaMax,
            TokenDeltaWithinBudget = Math.Abs(tokenDeltaTotal) <= options.TokenDeltaBudget && tokenDeltaMax <= options.TokenDeltaMaxBudget,
            PriorityOrderDeltaCount = priorityOrderDeltaCount,
            PriorityInversionCount = priorityInversionCount,
            DroppedRequiredCandidateCount = droppedRequiredCandidateCount,
            SectionMismatchCount = sectionMismatchCount,
            UseForRuntime = options.UseForRuntime,
            FormalRetrievalAllowed = options.FormalRetrievalAllowed,
            RuntimeSwitchAllowed = options.RuntimeSwitchAllowed,
            ReadyForRuntimeSwitch = options.ReadyForRuntimeSwitch,
            SampleResults = results,
            BlockedReasons = distinctBlocked
        };
    }

    public static string BuildMarkdown(string title, ShadowCandidateMergePreviewReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}`");
        b.AppendLine();
        b.AppendLine($"- PreviewPassed: `{r.PreviewPassed}`");
        b.AppendLine($"- GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- Recommendation: `{r.Recommendation}`");
        b.AppendLine($"- V66GatePassed: `{r.V66GatePassed}`");
        b.AppendLine($"- PreviewMergedSetGenerated: `{r.PreviewMergedSetGenerated}`");
        b.AppendLine($"- SampleCount: `{r.SampleCount}`");
        b.AppendLine($"- Candidates baseline/shadow/merged: `{r.BaselineCandidateCount}` / `{r.ShadowAdapterCandidateCount}` / `{r.MergedPreviewCandidateCount}`");
        b.AppendLine($"- PreviewAdd/Remove: `{r.PreviewAddCount}` / `{r.PreviewRemoveCount}`");
        b.AppendLine($"- AppliedAdd/Remove: `{r.AppliedAddCount}` / `{r.AppliedRemoveCount}`");
        b.AppendLine($"- TokenDeltaTotal/Max: `{r.TokenDeltaTotal}` / `{r.TokenDeltaMax}`");
        b.AppendLine($"- PriorityOrderDeltaCount: `{r.PriorityOrderDeltaCount}`");
        b.AppendLine($"- PriorityInversionCount: `{r.PriorityInversionCount}`");
        b.AppendLine($"- DroppedRequiredCandidateCount: `{r.DroppedRequiredCandidateCount}`");
        b.AppendLine($"- SectionMismatchCount: `{r.SectionMismatchCount}`");
        b.AppendLine($"- Risk/MustNot/Lifecycle: `{r.RiskAfterPolicy}` / `{r.MustNotHitRiskAfterPolicy}` / `{r.LifecycleRiskAfterPolicy}`");
        b.AppendLine($"- FormalSelectedSetChanged: `{r.FormalSelectedSetChanged}`");
        b.AppendLine($"- FormalOutputChanged: `{r.FormalOutputChanged}`");
        b.AppendLine($"- FormalPackageWritten: `{r.FormalPackageWritten}`");
        b.AppendLine($"- PackageOutputChanged: `{r.PackageOutputChanged}`");
        b.AppendLine($"- PackingPolicyChanged: `{r.PackingPolicyChanged}`");
        b.AppendLine($"- RuntimeMutated: `{r.RuntimeMutated}`");
        b.AppendLine($"- VectorStoreBindingChanged: `{r.VectorStoreBindingChanged}`");
        AppendList(b, "BlockedReasons", r.BlockedReasons);
        b.AppendLine();
        b.AppendLine("V6.7 shadow candidate merge preview only. The merged set is preview-only and never replaces the formal selected set or package output.");
        return b.ToString();
    }

    private static IReadOnlyList<string> BuildMergedPreview(IReadOnlyList<string> baseline, IReadOnlyList<string> shadowFinal)
    {
        var shadowSet = shadowFinal.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var additions = shadowFinal.Where(id => !baseline.Contains(id, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (additions.Length == 0)
            return baseline.ToArray();

        var merged = baseline.Where(shadowSet.Contains).Concat(additions).Take(baseline.Count).ToArray();
        return merged.Length == baseline.Count ? merged : baseline.ToArray();
    }

    private static int CountCommonOrderInversions(IReadOnlyList<string> baseline, IReadOnlyList<string> merged)
    {
        var baselineRank = baseline
            .Select((id, index) => (id, index))
            .ToDictionary(static item => item.id, static item => item.index, StringComparer.OrdinalIgnoreCase);
        var commonRanks = merged
            .Where(baselineRank.ContainsKey)
            .Select(id => baselineRank[id])
            .ToArray();
        var inversions = 0;
        for (var i = 0; i < commonRanks.Length; i++)
        {
            for (var j = i + 1; j < commonRanks.Length; j++)
            {
                if (commonRanks[i] > commonRanks[j])
                    inversions++;
            }
        }

        return inversions;
    }

    private static string ResolveRecommendation(IReadOnlyList<string> blocked, bool passed)
    {
        if (passed)
            return ShadowCandidateMergePreviewRecommendations.ReadyForShadowMergeObservation;
        if (blocked.Contains("V66GateMissingOrNotPassed", StringComparer.OrdinalIgnoreCase))
            return ShadowCandidateMergePreviewRecommendations.BlockedByMissingV66Gate;
        if (blocked.Contains("PreviewDeltaMissing", StringComparer.OrdinalIgnoreCase) || blocked.Contains("PreviewMergedSetMissing", StringComparer.OrdinalIgnoreCase))
            return ShadowCandidateMergePreviewRecommendations.BlockedByPreviewDeltaMissing;
        if (blocked.Contains("RiskDetected", StringComparer.OrdinalIgnoreCase))
            return ShadowCandidateMergePreviewRecommendations.BlockedByRisk;
        if (blocked.Contains("RuntimeOrFormalInvariantChanged", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("AppliedDeltaDetected", StringComparer.OrdinalIgnoreCase))
            return ShadowCandidateMergePreviewRecommendations.BlockedByPackageInvariant;
        if (blocked.Contains("TokenDeltaBudgetExceeded", StringComparer.OrdinalIgnoreCase))
            return ShadowCandidateMergePreviewRecommendations.BlockedByTokenBudget;
        if (blocked.Contains("PriorityOrSectionMismatch", StringComparer.OrdinalIgnoreCase))
            return ShadowCandidateMergePreviewRecommendations.BlockedByPriorityOrSection;
        return ShadowCandidateMergePreviewRecommendations.KeepPreviewOnly;
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

public sealed class ShadowCandidateMergePreviewOptions
{
    public bool GateMode { get; init; }
    public int TokenDeltaBudget { get; init; } = 128;
    public int TokenDeltaMaxBudget { get; init; } = 32;
    public int RiskAfterPolicy { get; init; }
    public int MustNotHitRiskAfterPolicy { get; init; }
    public int LifecycleRiskAfterPolicy { get; init; }
    public bool UseForRuntime { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool ReadyForRuntimeSwitch { get; init; }
    public bool FormalSelectedSetChanged { get; init; }
    public int FormalOutputChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
}

public sealed class ShadowCandidateMergePreviewSampleResult
{
    public string SampleId { get; init; } = string.Empty;
    public string Split { get; init; } = string.Empty;
    public string Difficulty { get; init; } = string.Empty;
    public IReadOnlyList<string> BaselineCandidates { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ShadowAdapterCandidates { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> MergedPreviewCandidates { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PreviewAddCandidateIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PreviewRemoveCandidateIds { get; init; } = Array.Empty<string>();
    public int TokenDelta { get; init; }
    public bool SectionMismatch { get; init; }
    public bool PriorityOrderChanged { get; init; }
    public int PriorityInversionCount { get; init; }
    public int DroppedRequiredCandidateCount { get; init; }
}

public sealed class ShadowCandidateMergePreviewReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool PreviewPassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; } = ShadowCandidateMergePreviewRecommendations.KeepPreviewOnly;
    public bool V66GatePassed { get; init; }
    public bool PreviewMergedSetGenerated { get; init; }
    public int SampleCount { get; init; }
    public int BaselineCandidateCount { get; init; }
    public int ShadowAdapterCandidateCount { get; init; }
    public int MergedPreviewCandidateCount { get; init; }
    public int PreviewAddCount { get; init; }
    public int PreviewRemoveCount { get; init; }
    public int AppliedAddCount { get; init; }
    public int AppliedRemoveCount { get; init; }
    public bool FormalSelectedSetChanged { get; init; }
    public int FormalOutputChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public int RiskAfterPolicy { get; init; }
    public int MustNotHitRiskAfterPolicy { get; init; }
    public int LifecycleRiskAfterPolicy { get; init; }
    public int TokenDeltaTotal { get; init; }
    public int TokenDeltaMax { get; init; }
    public bool TokenDeltaWithinBudget { get; init; }
    public int PriorityOrderDeltaCount { get; init; }
    public int PriorityInversionCount { get; init; }
    public int DroppedRequiredCandidateCount { get; init; }
    public int SectionMismatchCount { get; init; }
    public bool UseForRuntime { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool ReadyForRuntimeSwitch { get; init; }
    public IReadOnlyList<ShadowCandidateMergePreviewSampleResult> SampleResults { get; init; } = Array.Empty<ShadowCandidateMergePreviewSampleResult>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public static class ShadowCandidateMergePreviewRecommendations
{
    public const string ReadyForShadowMergeObservation = nameof(ReadyForShadowMergeObservation);
    public const string BlockedByMissingV66Gate = nameof(BlockedByMissingV66Gate);
    public const string BlockedByPreviewDeltaMissing = nameof(BlockedByPreviewDeltaMissing);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByPackageInvariant = nameof(BlockedByPackageInvariant);
    public const string BlockedByTokenBudget = nameof(BlockedByTokenBudget);
    public const string BlockedByPriorityOrSection = nameof(BlockedByPriorityOrSection);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


