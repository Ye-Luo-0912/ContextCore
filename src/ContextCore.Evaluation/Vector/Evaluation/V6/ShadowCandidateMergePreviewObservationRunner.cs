using System.Security.Cryptography;
using System.Text;

namespace ContextCore.Core.Services;

/// <summary>
/// V6.7 shadow merge 多轮观察。只重复执行 preview merge，并验证稳定性与安全边界；不改变正式 selected set 或 runtime。
/// </summary>
public sealed class ShadowCandidateMergePreviewObservationRunner
{
    public ShadowCandidateMergePreviewObservationReport RunObservation(
        SourceDiverseShadowAdapterValidationReport? v66Gate,
        ShadowCandidateMergePreviewReport? v67Gate,
        ShadowCandidateMergePreviewObservationOptions? options = null)
    {
        options ??= new ShadowCandidateMergePreviewObservationOptions();
        var blocked = new List<string>();
        if (v67Gate is null || !v67Gate.GatePassed)
            blocked.Add("V67GateMissingOrNotPassed");
        if (options.ObservationRunCount < options.MinimumObservationRunCount)
            blocked.Add("InsufficientObservationRuns");
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

        var runResults = new List<ShadowCandidateMergePreviewObservationRunResult>(Math.Max(options.ObservationRunCount, 0));
        var runner = new ShadowCandidateMergePreviewRunner();
        for (var i = 0; i < options.ObservationRunCount; i++)
        {
            var report = runner.RunPreview(v66Gate, new ShadowCandidateMergePreviewOptions
            {
                GateMode = true,
                TokenDeltaBudget = options.TokenDeltaBudget,
                TokenDeltaMaxBudget = options.TokenDeltaMaxBudget,
                RiskAfterPolicy = options.RiskAfterPolicy,
                MustNotHitRiskAfterPolicy = options.MustNotHitRiskAfterPolicy,
                LifecycleRiskAfterPolicy = options.LifecycleRiskAfterPolicy,
                UseForRuntime = options.UseForRuntime,
                FormalRetrievalAllowed = options.FormalRetrievalAllowed,
                RuntimeSwitchAllowed = options.RuntimeSwitchAllowed,
                ReadyForRuntimeSwitch = options.ReadyForRuntimeSwitch,
                FormalSelectedSetChanged = options.FormalSelectedSetChanged,
                FormalOutputChanged = options.FormalOutputChanged,
                FormalPackageWritten = options.FormalPackageWritten,
                PackageOutputChanged = options.PackageOutputChanged,
                PackingPolicyChanged = options.PackingPolicyChanged,
                RuntimeMutated = options.RuntimeMutated,
                VectorStoreBindingChanged = options.VectorStoreBindingChanged
            });

            runResults.Add(new ShadowCandidateMergePreviewObservationRunResult
            {
                RunIndex = i + 1,
                PreviewPassed = report.PreviewPassed,
                GatePassed = report.GatePassed,
                StableSignature = ComputeStableSignature(report),
                PreviewAddCount = report.PreviewAddCount,
                PreviewRemoveCount = report.PreviewRemoveCount,
                AppliedAddCount = report.AppliedAddCount,
                AppliedRemoveCount = report.AppliedRemoveCount,
                RiskAfterPolicy = report.RiskAfterPolicy,
                MustNotHitRiskAfterPolicy = report.MustNotHitRiskAfterPolicy,
                LifecycleRiskAfterPolicy = report.LifecycleRiskAfterPolicy,
                TokenDeltaTotal = report.TokenDeltaTotal,
                TokenDeltaMax = report.TokenDeltaMax,
                PriorityInversionCount = report.PriorityInversionCount,
                SectionMismatchCount = report.SectionMismatchCount,
                FormalSelectedSetChanged = report.FormalSelectedSetChanged,
                FormalOutputChanged = report.FormalOutputChanged,
                FormalPackageWritten = report.FormalPackageWritten,
                PackageOutputChanged = report.PackageOutputChanged,
                PackingPolicyChanged = report.PackingPolicyChanged,
                RuntimeMutated = report.RuntimeMutated,
                VectorStoreBindingChanged = report.VectorStoreBindingChanged,
                BlockedReasons = report.BlockedReasons
            });
        }

        var failedRunCount = runResults.Count(static run => !run.PreviewPassed || !run.GatePassed);
        if (failedRunCount > 0)
            blocked.Add("PreviewRunFailed");

        var distinctSignatures = runResults
            .Select(static run => run.StableSignature)
            .Distinct(StringComparer.Ordinal)
            .Count();
        var deterministicStable = runResults.Count > 0 && distinctSignatures == 1;
        if (!deterministicStable)
            blocked.Add("PreviewSignatureNotStable");

        var addMin = runResults.Count == 0 ? 0 : runResults.Min(static run => run.PreviewAddCount);
        var addMax = runResults.Count == 0 ? 0 : runResults.Max(static run => run.PreviewAddCount);
        var removeMin = runResults.Count == 0 ? 0 : runResults.Min(static run => run.PreviewRemoveCount);
        var removeMax = runResults.Count == 0 ? 0 : runResults.Max(static run => run.PreviewRemoveCount);
        var previewAddRemoveStable = runResults.Count > 0 && addMin == addMax && removeMin == removeMax && addMin > 0 && removeMin > 0;
        if (!previewAddRemoveStable)
            blocked.Add("PreviewDeltaNotStable");

        var appliedAddMax = runResults.Count == 0 ? 0 : runResults.Max(static run => run.AppliedAddCount);
        var appliedRemoveMax = runResults.Count == 0 ? 0 : runResults.Max(static run => run.AppliedRemoveCount);
        if (appliedAddMax != 0 || appliedRemoveMax != 0)
            blocked.Add("AppliedDeltaDetected");

        var riskMax = runResults.Count == 0 ? 0 : runResults.Max(static run => run.RiskAfterPolicy);
        var mustNotMax = runResults.Count == 0 ? 0 : runResults.Max(static run => run.MustNotHitRiskAfterPolicy);
        var lifecycleMax = runResults.Count == 0 ? 0 : runResults.Max(static run => run.LifecycleRiskAfterPolicy);
        if (riskMax != 0 || mustNotMax != 0 || lifecycleMax != 0)
            blocked.Add("RiskDetected");

        var priorityInversions = runResults.Sum(static run => run.PriorityInversionCount);
        var sectionMismatches = runResults.Sum(static run => run.SectionMismatchCount);
        if (priorityInversions != 0 || sectionMismatches != 0)
            blocked.Add("PriorityOrSectionMismatch");

        var tokenDeltaTotalMax = runResults.Count == 0 ? 0 : runResults.Max(static run => Math.Abs(run.TokenDeltaTotal));
        var tokenDeltaMaxMax = runResults.Count == 0 ? 0 : runResults.Max(static run => run.TokenDeltaMax);
        var tokenDeltaWithinBudget = tokenDeltaTotalMax <= options.TokenDeltaBudget && tokenDeltaMaxMax <= options.TokenDeltaMaxBudget;
        if (!tokenDeltaWithinBudget)
            blocked.Add("TokenDeltaBudgetExceeded");

        var formalSelectedSetChanged = runResults.Any(static run => run.FormalSelectedSetChanged);
        var formalOutputChangedMax = runResults.Count == 0 ? 0 : runResults.Max(static run => run.FormalOutputChanged);
        var formalPackageWritten = runResults.Any(static run => run.FormalPackageWritten);
        var packageOutputChanged = runResults.Any(static run => run.PackageOutputChanged);
        var packingPolicyChanged = runResults.Any(static run => run.PackingPolicyChanged);
        var runtimeMutated = runResults.Any(static run => run.RuntimeMutated);
        var vectorStoreBindingChanged = runResults.Any(static run => run.VectorStoreBindingChanged);
        if (formalSelectedSetChanged || formalOutputChangedMax != 0 || formalPackageWritten || packageOutputChanged || packingPolicyChanged || runtimeMutated || vectorStoreBindingChanged)
            blocked.Add("RuntimeOrFormalInvariantChanged");

        var distinctBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static reason => reason).ToArray();
        var passed = distinctBlocked.Length == 0;
        return new ShadowCandidateMergePreviewObservationReport
        {
            OperationId = $"shadow-candidate-merge-preview-observation-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            ObservationPassed = passed,
            GatePassed = options.GateMode && passed,
            Recommendation = ResolveRecommendation(distinctBlocked, passed),
            V67GatePassed = v67Gate?.GatePassed == true,
            ObservationRunCount = runResults.Count,
            MinimumObservationRunCount = options.MinimumObservationRunCount,
            SampleObservationCount = runResults.Count * (v67Gate?.SampleCount ?? 0),
            FailedRunCount = failedRunCount,
            DistinctStableSignatureCount = distinctSignatures,
            DeterministicPreviewStable = deterministicStable,
            PreviewAddRemoveStable = previewAddRemoveStable,
            PreviewAddCountMin = addMin,
            PreviewAddCountMax = addMax,
            PreviewRemoveCountMin = removeMin,
            PreviewRemoveCountMax = removeMax,
            PreviewAddCountTotal = runResults.Sum(static run => run.PreviewAddCount),
            PreviewRemoveCountTotal = runResults.Sum(static run => run.PreviewRemoveCount),
            AppliedAddCountMax = appliedAddMax,
            AppliedRemoveCountMax = appliedRemoveMax,
            RiskAfterPolicyMax = riskMax,
            MustNotHitRiskAfterPolicyMax = mustNotMax,
            LifecycleRiskAfterPolicyMax = lifecycleMax,
            TokenDeltaTotalMax = tokenDeltaTotalMax,
            TokenDeltaMaxMax = tokenDeltaMaxMax,
            TokenDeltaWithinBudget = tokenDeltaWithinBudget,
            PriorityInversionCountTotal = priorityInversions,
            SectionMismatchCountTotal = sectionMismatches,
            FormalSelectedSetChanged = formalSelectedSetChanged,
            FormalOutputChangedMax = formalOutputChangedMax,
            FormalPackageWritten = formalPackageWritten,
            PackageOutputChanged = packageOutputChanged,
            PackingPolicyChanged = packingPolicyChanged,
            RuntimeMutated = runtimeMutated,
            VectorStoreBindingChanged = vectorStoreBindingChanged,
            UseForRuntime = options.UseForRuntime,
            FormalRetrievalAllowed = options.FormalRetrievalAllowed,
            RuntimeSwitchAllowed = options.RuntimeSwitchAllowed,
            ReadyForRuntimeSwitch = options.ReadyForRuntimeSwitch,
            Runs = runResults,
            BlockedReasons = distinctBlocked
        };
    }

    public static string BuildMarkdown(string title, ShadowCandidateMergePreviewObservationReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}`");
        b.AppendLine();
        b.AppendLine($"- ObservationPassed: `{r.ObservationPassed}`");
        b.AppendLine($"- GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- Recommendation: `{r.Recommendation}`");
        b.AppendLine($"- V67GatePassed: `{r.V67GatePassed}`");
        b.AppendLine($"- ObservationRunCount: `{r.ObservationRunCount}`");
        b.AppendLine($"- SampleObservationCount: `{r.SampleObservationCount}`");
        b.AppendLine($"- DeterministicPreviewStable: `{r.DeterministicPreviewStable}`");
        b.AppendLine($"- DistinctStableSignatureCount: `{r.DistinctStableSignatureCount}`");
        b.AppendLine($"- PreviewAddCount min/max/total: `{r.PreviewAddCountMin}` / `{r.PreviewAddCountMax}` / `{r.PreviewAddCountTotal}`");
        b.AppendLine($"- PreviewRemoveCount min/max/total: `{r.PreviewRemoveCountMin}` / `{r.PreviewRemoveCountMax}` / `{r.PreviewRemoveCountTotal}`");
        b.AppendLine($"- AppliedAdd/Remove max: `{r.AppliedAddCountMax}` / `{r.AppliedRemoveCountMax}`");
        b.AppendLine($"- Risk/MustNot/Lifecycle max: `{r.RiskAfterPolicyMax}` / `{r.MustNotHitRiskAfterPolicyMax}` / `{r.LifecycleRiskAfterPolicyMax}`");
        b.AppendLine($"- TokenDeltaTotalMax/TokenDeltaMaxMax: `{r.TokenDeltaTotalMax}` / `{r.TokenDeltaMaxMax}`");
        b.AppendLine($"- PriorityInversionCountTotal: `{r.PriorityInversionCountTotal}`");
        b.AppendLine($"- SectionMismatchCountTotal: `{r.SectionMismatchCountTotal}`");
        b.AppendLine($"- FormalSelectedSetChanged: `{r.FormalSelectedSetChanged}`");
        b.AppendLine($"- FormalOutputChangedMax: `{r.FormalOutputChangedMax}`");
        b.AppendLine($"- FormalPackageWritten: `{r.FormalPackageWritten}`");
        b.AppendLine($"- PackageOutputChanged: `{r.PackageOutputChanged}`");
        b.AppendLine($"- PackingPolicyChanged: `{r.PackingPolicyChanged}`");
        b.AppendLine($"- RuntimeMutated: `{r.RuntimeMutated}`");
        b.AppendLine($"- VectorStoreBindingChanged: `{r.VectorStoreBindingChanged}`");
        AppendList(b, "BlockedReasons", r.BlockedReasons);
        b.AppendLine();
        b.AppendLine("Observation repeats V6.7 preview merge only. It does not apply add/remove, write a formal package, mutate PackingPolicy/package output, switch runtime, or change vector binding.");
        return b.ToString();
    }

    private static string ComputeStableSignature(ShadowCandidateMergePreviewReport report)
    {
        var b = new StringBuilder();
        b.Append(report.PreviewPassed).Append('|')
            .Append(report.GatePassed).Append('|')
            .Append(report.PreviewAddCount).Append('|')
            .Append(report.PreviewRemoveCount).Append('|')
            .Append(report.AppliedAddCount).Append('|')
            .Append(report.AppliedRemoveCount).Append('|')
            .Append(report.TokenDeltaTotal).Append('|')
            .Append(report.TokenDeltaMax).Append('|')
            .Append(report.PriorityInversionCount).Append('|')
            .Append(report.SectionMismatchCount).Append('|')
            .Append(report.FormalOutputChanged).Append('|')
            .Append(report.PackageOutputChanged).Append('|')
            .Append(report.PackingPolicyChanged).Append('|')
            .Append(report.RuntimeMutated).Append('|')
            .Append(report.VectorStoreBindingChanged).AppendLine();
        foreach (var sample in report.SampleResults.OrderBy(static sample => sample.SampleId, StringComparer.Ordinal))
        {
            b.Append(sample.SampleId).Append('|')
                .AppendJoin(',', sample.BaselineCandidates).Append('|')
                .AppendJoin(',', sample.MergedPreviewCandidates).Append('|')
                .AppendJoin(',', sample.PreviewAddCandidateIds).Append('|')
                .AppendJoin(',', sample.PreviewRemoveCandidateIds).Append('|')
                .Append(sample.TokenDelta).Append('|')
                .Append(sample.SectionMismatch).Append('|')
                .Append(sample.PriorityInversionCount)
                .AppendLine();
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(b.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string ResolveRecommendation(IReadOnlyList<string> blocked, bool passed)
    {
        if (passed)
            return ShadowCandidateMergePreviewObservationRecommendations.ReadyForShadowMergeStabilityFreeze;
        if (blocked.Contains("V67GateMissingOrNotPassed", StringComparer.OrdinalIgnoreCase))
            return ShadowCandidateMergePreviewObservationRecommendations.BlockedByMissingV67Gate;
        if (blocked.Contains("PreviewSignatureNotStable", StringComparer.OrdinalIgnoreCase) || blocked.Contains("PreviewDeltaNotStable", StringComparer.OrdinalIgnoreCase))
            return ShadowCandidateMergePreviewObservationRecommendations.BlockedByInstability;
        if (blocked.Contains("RiskDetected", StringComparer.OrdinalIgnoreCase))
            return ShadowCandidateMergePreviewObservationRecommendations.BlockedByRisk;
        if (blocked.Contains("PriorityOrSectionMismatch", StringComparer.OrdinalIgnoreCase))
            return ShadowCandidateMergePreviewObservationRecommendations.BlockedByPriorityOrSection;
        if (blocked.Contains("TokenDeltaBudgetExceeded", StringComparer.OrdinalIgnoreCase))
            return ShadowCandidateMergePreviewObservationRecommendations.BlockedByTokenBudget;
        if (blocked.Contains("RuntimeOrFormalInvariantChanged", StringComparer.OrdinalIgnoreCase) || blocked.Contains("AppliedDeltaDetected", StringComparer.OrdinalIgnoreCase))
            return ShadowCandidateMergePreviewObservationRecommendations.BlockedByRuntimeInvariant;
        if (blocked.Contains("InsufficientObservationRuns", StringComparer.OrdinalIgnoreCase))
            return ShadowCandidateMergePreviewObservationRecommendations.NeedsMoreObservation;
        return ShadowCandidateMergePreviewObservationRecommendations.KeepPreviewOnly;
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

public sealed class ShadowCandidateMergePreviewObservationOptions
{
    public bool GateMode { get; init; }
    public int ObservationRunCount { get; init; } = 10;
    public int MinimumObservationRunCount { get; init; } = 3;
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

public sealed class ShadowCandidateMergePreviewObservationRunResult
{
    public int RunIndex { get; init; }
    public bool PreviewPassed { get; init; }
    public bool GatePassed { get; init; }
    public string StableSignature { get; init; } = string.Empty;
    public int PreviewAddCount { get; init; }
    public int PreviewRemoveCount { get; init; }
    public int AppliedAddCount { get; init; }
    public int AppliedRemoveCount { get; init; }
    public int RiskAfterPolicy { get; init; }
    public int MustNotHitRiskAfterPolicy { get; init; }
    public int LifecycleRiskAfterPolicy { get; init; }
    public int TokenDeltaTotal { get; init; }
    public int TokenDeltaMax { get; init; }
    public int PriorityInversionCount { get; init; }
    public int SectionMismatchCount { get; init; }
    public bool FormalSelectedSetChanged { get; init; }
    public int FormalOutputChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public sealed class ShadowCandidateMergePreviewObservationReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool ObservationPassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; } = ShadowCandidateMergePreviewObservationRecommendations.KeepPreviewOnly;
    public bool V67GatePassed { get; init; }
    public int ObservationRunCount { get; init; }
    public int MinimumObservationRunCount { get; init; }
    public int SampleObservationCount { get; init; }
    public int FailedRunCount { get; init; }
    public int DistinctStableSignatureCount { get; init; }
    public bool DeterministicPreviewStable { get; init; }
    public bool PreviewAddRemoveStable { get; init; }
    public int PreviewAddCountMin { get; init; }
    public int PreviewAddCountMax { get; init; }
    public int PreviewRemoveCountMin { get; init; }
    public int PreviewRemoveCountMax { get; init; }
    public int PreviewAddCountTotal { get; init; }
    public int PreviewRemoveCountTotal { get; init; }
    public int AppliedAddCountMax { get; init; }
    public int AppliedRemoveCountMax { get; init; }
    public int RiskAfterPolicyMax { get; init; }
    public int MustNotHitRiskAfterPolicyMax { get; init; }
    public int LifecycleRiskAfterPolicyMax { get; init; }
    public int TokenDeltaTotalMax { get; init; }
    public int TokenDeltaMaxMax { get; init; }
    public bool TokenDeltaWithinBudget { get; init; }
    public int PriorityInversionCountTotal { get; init; }
    public int SectionMismatchCountTotal { get; init; }
    public bool FormalSelectedSetChanged { get; init; }
    public int FormalOutputChangedMax { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool UseForRuntime { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool ReadyForRuntimeSwitch { get; init; }
    public IReadOnlyList<ShadowCandidateMergePreviewObservationRunResult> Runs { get; init; } = Array.Empty<ShadowCandidateMergePreviewObservationRunResult>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public static class ShadowCandidateMergePreviewObservationRecommendations
{
    public const string ReadyForShadowMergeStabilityFreeze = nameof(ReadyForShadowMergeStabilityFreeze);
    public const string NeedsMoreObservation = nameof(NeedsMoreObservation);
    public const string BlockedByMissingV67Gate = nameof(BlockedByMissingV67Gate);
    public const string BlockedByInstability = nameof(BlockedByInstability);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByPriorityOrSection = nameof(BlockedByPriorityOrSection);
    public const string BlockedByTokenBudget = nameof(BlockedByTokenBudget);
    public const string BlockedByRuntimeInvariant = nameof(BlockedByRuntimeInvariant);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}
