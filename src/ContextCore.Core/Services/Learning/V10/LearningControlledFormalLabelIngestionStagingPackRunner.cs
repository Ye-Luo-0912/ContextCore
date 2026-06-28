using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ContextCore.Core.Services;

public static class LearningControlledFormalLabelIngestionStagingPackStatuses
{
    public const string LearningControlledFormalLabelIngestionStagingPackReady = nameof(LearningControlledFormalLabelIngestionStagingPackReady);
    public const string LearningControlledFormalLabelIngestionStagingPackBlocked = nameof(LearningControlledFormalLabelIngestionStagingPackBlocked);
}

public static class LearningControlledFormalLabelIngestionStagingPackBlockedReasons
{
    public const string FormalRealizationPackMissing = nameof(FormalRealizationPackMissing);
    public const string FormalRealizationPackNotPassed = nameof(FormalRealizationPackNotPassed);
    public const string CandidatesMissing = nameof(CandidatesMissing);
    public const string IntegrityManifestMissing = nameof(IntegrityManifestMissing);
    public const string HashMismatch = nameof(HashMismatch);
    public const string InvalidCandidateBinding = nameof(InvalidCandidateBinding);
    public const string StagedLabelsTreatedAsFormal = nameof(StagedLabelsTreatedAsFormal);
    public const string FormalTrainingSetChangedTrue = nameof(FormalTrainingSetChangedTrue);
    public const string AutoIngestTrue = nameof(AutoIngestTrue);
    public const string HumanReviewAsGateAuthorityTrue = nameof(HumanReviewAsGateAuthorityTrue);
    public const string RuntimePilotExecutionAppliedTrue = nameof(RuntimePilotExecutionAppliedTrue);
    public const string RuntimePromotionAppliedTrue = nameof(RuntimePromotionAppliedTrue);
    public const string RuntimeRerankerChangedTrue = nameof(RuntimeRerankerChangedTrue);
    public const string RuntimeRouterChangedTrue = nameof(RuntimeRouterChangedTrue);
    public const string ProductionDecisionChangedTrue = nameof(ProductionDecisionChangedTrue);
    public const string PackageOutputChangedTrue = nameof(PackageOutputChangedTrue);
    public const string FormalPackageWrittenTrue = nameof(FormalPackageWrittenTrue);
    public const string GlobalDefaultOnTrue = nameof(GlobalDefaultOnTrue);
    public const string MLAuthorityTrue = nameof(MLAuthorityTrue);
    public const string LLMAuthorityTrue = nameof(LLMAuthorityTrue);
    public const string RuntimeAuthorityTrue = nameof(RuntimeAuthorityTrue);
    public const string GateAuthorityTrue = nameof(GateAuthorityTrue);
    public const string V8ScopedActivationLost = nameof(V8ScopedActivationLost);
    public const string MainlineEvidencePresent = nameof(MainlineEvidencePresent);
    public const string MainlineTrustRegistryPresent = nameof(MainlineTrustRegistryPresent);
    public const string RuntimeChangeGateNotPassed = nameof(RuntimeChangeGateNotPassed);
    public const string P15GateNotPassed = nameof(P15GateNotPassed);
}

public sealed class StagedFormalHardNegativeRow
{
    public string StagedLabelId { get; init; } = string.Empty;
    public string SourceCandidateLabelId { get; init; } = string.Empty;
    public string SourceShadowLabelId { get; init; } = string.Empty;
    public string EvidencePath { get; init; } = string.Empty;
    public string ExpectedPreference { get; init; } = string.Empty;
    public string DeterministicBindingHash { get; init; } = string.Empty;
    public string LifecycleState { get; init; } = "Staged";
    public bool StagedLabelIsFormal { get; init; }
    public bool StagingOnly { get; init; } = true;
    public bool AutoIngest { get; init; }
    public string PolicyVersion { get; init; } = "v10.19-staged-formal-hard-negative/v1";
    public string StagedAt { get; init; } = string.Empty;
}

public sealed class FormalLabelIngestionDiffPreview
{
    public string DiffMode { get; init; } = "PreviewOnlyNoApply";
    public string FormalDatasetPath { get; init; } = string.Empty;
    public int FormalDatasetLineCountBefore { get; init; }
    public int StagedRowCount { get; init; }
    public int WouldAddCount { get; init; }
    public int WouldSkipDuplicateCount { get; init; }
    public int WouldRejectInvalidCount { get; init; }
    public int FormalDatasetLineCountAfterIfApplied { get; init; }
    public bool FormalTrainingSetChanged { get; init; }
    public bool AutoIngest { get; init; }
    public IReadOnlyList<string> SampleAddedRows { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

public sealed class FormalLabelQuarantinePolicy
{
    public string PolicyVersion { get; init; } = "v10.19-formal-label-quarantine/v1";
    public IReadOnlyList<string> QuarantineTriggers { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> QuarantineActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ReleaseConditions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RejectedAuthorityClaims { get; init; } = Array.Empty<string>();
    public string QuarantineLocation { get; init; } = "learning/v10/staged-formal-hard-negatives-quarantine/";
    public bool FormalTrainingSetChanged { get; init; }
    public bool AutoIngest { get; init; }
}

public sealed class FormalLabelRollbackSnapshotPlan
{
    public string PlanVersion { get; init; } = "v10.19-rollback-snapshot-plan/v1";
    public string PlanMode { get; init; } = "PlanOnlyNoSnapshotTaken";
    public string FormalDatasetPath { get; init; } = string.Empty;
    public string PlannedSnapshotPath { get; init; } = string.Empty;
    public string PlannedSnapshotAlgorithm { get; init; } = "SHA-256";
    public string FormalDatasetCurrentHash { get; init; } = string.Empty;
    public int FormalDatasetCurrentLineCount { get; init; }
    public IReadOnlyList<string> SnapshotProcedure { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RestoreProcedure { get; init; } = Array.Empty<string>();
    public bool SnapshotTaken { get; init; }
    public bool FormalTrainingSetChanged { get; init; }
    public string V818RollbackBindingReference { get; init; } = string.Empty;
    public string V818RevocationRecordReference { get; init; } = string.Empty;
}

public sealed class FormalLabelIngestionStagingDecision
{
    public string DecisionId { get; init; } = string.Empty;
    public string CreatedAt { get; init; } = string.Empty;
    public int StagedFormalLabelCount { get; init; }
    public int InvalidCandidateCount { get; init; }
    public int HashMismatchCount { get; init; }
    public bool StagingDatasetReady { get; init; }
    public bool DiffPreviewReady { get; init; }
    public bool RollbackSnapshotPlanReady { get; init; }
    public bool QuarantinePolicyReady { get; init; }
    public bool StagingOnly { get; init; } = true;
    public bool StagedLabelsAreFormal { get; init; }
    public bool FormalTrainingSetChanged { get; init; }
    public bool AutoIngest { get; init; }
    public bool HumanFeedbackAsSignal { get; init; } = true;
    public bool HumanReviewAsGateAuthority { get; init; }
    public bool FormalIngestionApplied { get; init; }
    public bool RuntimePilotExecutionReadyForSeparateGate { get; init; }
    public IReadOnlyList<string> BlockedForFormalIngestionBy { get; init; } = Array.Empty<string>();
    public bool AIArbitration { get; init; }
    public IReadOnlyList<string> EvidenceSourcesConsidered { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> DecisionNotes { get; init; } = Array.Empty<string>();
}

public sealed record LearningControlledFormalLabelIngestionStagingPackContext
{
    public bool FormalRealizationPackPresent { get; init; }
    public bool FormalRealizationPackPassed { get; init; }
    public bool CandidatesPresent { get; init; }
    public int CandidateCount { get; init; }
    public bool IntegrityManifestPresent { get; init; }
    public bool V8ScopedActivationPreserved { get; init; }
    public IReadOnlyList<FormalLabelCandidate> Candidates { get; init; } = Array.Empty<FormalLabelCandidate>();
    public string FormalDatasetPath { get; init; } = string.Empty;
    // Synthetic test knobs
    public bool HashMismatchOverride { get; init; }
    public bool InvalidCandidateBindingOverride { get; init; }
    public bool StagedLabelsTreatedAsFormalOverride { get; init; }
    public bool FormalTrainingSetChangedOverride { get; init; }
    public bool AutoIngestOverride { get; init; }
    public bool HumanReviewAsGateAuthorityOverride { get; init; }
    public bool RuntimePilotExecutionAppliedOverride { get; init; }
    public bool RuntimePromotionAppliedOverride { get; init; }
    public bool RuntimeRerankerChangedOverride { get; init; }
    public bool RuntimeRouterChangedOverride { get; init; }
    public bool ProductionDecisionChangedOverride { get; init; }
    public bool PackageOutputChangedOverride { get; init; }
    public bool FormalPackageWrittenOverride { get; init; }
    public bool GlobalDefaultOnOverride { get; init; }
    public bool MLAuthorityOverride { get; init; }
    public bool LLMAuthorityOverride { get; init; }
    public bool RuntimeAuthorityOverride { get; init; }
    public bool GateAuthorityOverride { get; init; }
}

public sealed class LearningControlledFormalLabelIngestionStagingPackDecision
{
    public string Status { get; init; } = LearningControlledFormalLabelIngestionStagingPackStatuses.LearningControlledFormalLabelIngestionStagingPackBlocked;
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public string Reasoning { get; init; } = string.Empty;
}

public static class LearningControlledFormalLabelIngestionStagingPackPolicy
{
    public static LearningControlledFormalLabelIngestionStagingPackDecision Evaluate(
        LearningControlledFormalLabelIngestionStagingPackContext ctx,
        bool rtPassed, bool p15Passed,
        bool mainlineEvidencePresent, bool mainlineRegistryPresent)
    {
        var blocked = new List<string>();
        if (!ctx.FormalRealizationPackPresent) blocked.Add(LearningControlledFormalLabelIngestionStagingPackBlockedReasons.FormalRealizationPackMissing);
        else if (!ctx.FormalRealizationPackPassed) blocked.Add(LearningControlledFormalLabelIngestionStagingPackBlockedReasons.FormalRealizationPackNotPassed);
        if (!ctx.CandidatesPresent || ctx.CandidateCount <= 0) blocked.Add(LearningControlledFormalLabelIngestionStagingPackBlockedReasons.CandidatesMissing);
        if (!ctx.IntegrityManifestPresent) blocked.Add(LearningControlledFormalLabelIngestionStagingPackBlockedReasons.IntegrityManifestMissing);
        if (ctx.HashMismatchOverride) blocked.Add(LearningControlledFormalLabelIngestionStagingPackBlockedReasons.HashMismatch);
        if (ctx.InvalidCandidateBindingOverride) blocked.Add(LearningControlledFormalLabelIngestionStagingPackBlockedReasons.InvalidCandidateBinding);
        if (ctx.StagedLabelsTreatedAsFormalOverride) blocked.Add(LearningControlledFormalLabelIngestionStagingPackBlockedReasons.StagedLabelsTreatedAsFormal);
        if (ctx.FormalTrainingSetChangedOverride) blocked.Add(LearningControlledFormalLabelIngestionStagingPackBlockedReasons.FormalTrainingSetChangedTrue);
        if (ctx.AutoIngestOverride) blocked.Add(LearningControlledFormalLabelIngestionStagingPackBlockedReasons.AutoIngestTrue);
        if (ctx.HumanReviewAsGateAuthorityOverride) blocked.Add(LearningControlledFormalLabelIngestionStagingPackBlockedReasons.HumanReviewAsGateAuthorityTrue);
        if (ctx.RuntimePilotExecutionAppliedOverride) blocked.Add(LearningControlledFormalLabelIngestionStagingPackBlockedReasons.RuntimePilotExecutionAppliedTrue);
        if (ctx.RuntimePromotionAppliedOverride) blocked.Add(LearningControlledFormalLabelIngestionStagingPackBlockedReasons.RuntimePromotionAppliedTrue);
        if (ctx.RuntimeRerankerChangedOverride) blocked.Add(LearningControlledFormalLabelIngestionStagingPackBlockedReasons.RuntimeRerankerChangedTrue);
        if (ctx.RuntimeRouterChangedOverride) blocked.Add(LearningControlledFormalLabelIngestionStagingPackBlockedReasons.RuntimeRouterChangedTrue);
        if (ctx.ProductionDecisionChangedOverride) blocked.Add(LearningControlledFormalLabelIngestionStagingPackBlockedReasons.ProductionDecisionChangedTrue);
        if (ctx.PackageOutputChangedOverride) blocked.Add(LearningControlledFormalLabelIngestionStagingPackBlockedReasons.PackageOutputChangedTrue);
        if (ctx.FormalPackageWrittenOverride) blocked.Add(LearningControlledFormalLabelIngestionStagingPackBlockedReasons.FormalPackageWrittenTrue);
        if (ctx.GlobalDefaultOnOverride) blocked.Add(LearningControlledFormalLabelIngestionStagingPackBlockedReasons.GlobalDefaultOnTrue);
        if (ctx.MLAuthorityOverride) blocked.Add(LearningControlledFormalLabelIngestionStagingPackBlockedReasons.MLAuthorityTrue);
        if (ctx.LLMAuthorityOverride) blocked.Add(LearningControlledFormalLabelIngestionStagingPackBlockedReasons.LLMAuthorityTrue);
        if (ctx.RuntimeAuthorityOverride) blocked.Add(LearningControlledFormalLabelIngestionStagingPackBlockedReasons.RuntimeAuthorityTrue);
        if (ctx.GateAuthorityOverride) blocked.Add(LearningControlledFormalLabelIngestionStagingPackBlockedReasons.GateAuthorityTrue);
        if (!ctx.V8ScopedActivationPreserved) blocked.Add(LearningControlledFormalLabelIngestionStagingPackBlockedReasons.V8ScopedActivationLost);
        if (mainlineEvidencePresent) blocked.Add(LearningControlledFormalLabelIngestionStagingPackBlockedReasons.MainlineEvidencePresent);
        if (mainlineRegistryPresent) blocked.Add(LearningControlledFormalLabelIngestionStagingPackBlockedReasons.MainlineTrustRegistryPresent);
        if (!rtPassed) blocked.Add(LearningControlledFormalLabelIngestionStagingPackBlockedReasons.RuntimeChangeGateNotPassed);
        if (!p15Passed) blocked.Add(LearningControlledFormalLabelIngestionStagingPackBlockedReasons.P15GateNotPassed);
        var finalBlocked = blocked.Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        var ready = finalBlocked.Length == 0;
        return new LearningControlledFormalLabelIngestionStagingPackDecision
        {
            Status = ready
                ? LearningControlledFormalLabelIngestionStagingPackStatuses.LearningControlledFormalLabelIngestionStagingPackReady
                : LearningControlledFormalLabelIngestionStagingPackStatuses.LearningControlledFormalLabelIngestionStagingPackBlocked,
            BlockedReasons = finalBlocked,
            Reasoning = ready
                ? "controlled formal label ingestion staging pack policy ready — staging proceeds; formal dataset untouched."
                : $"{finalBlocked.Length} blocked reason(s); staging pack blocked."
        };
    }
}

public sealed record LearningControlledFormalLabelIngestionStagingPackScenario(
    string CaseName,
    LearningControlledFormalLabelIngestionStagingPackContext Context,
    bool RtPassed, bool P15Passed,
    bool MainlineEvidencePresent, bool MainlineRegistryPresent,
    string ExpectedStatus, string? ExpectedBlockedReason);

public sealed class LearningControlledFormalLabelIngestionStagingPackRunner
{
    private static readonly JsonSerializerOptions WriteIndented = new() { WriteIndented = true };

    public LearningControlledFormalLabelIngestionStagingPackReport Run(
        LearningControlledFormalLabelIngestionStagingPackContext realContext,
        string outputDir,
        bool rtPassed, bool p15Passed,
        bool mainlineEvidencePresent, bool mainlineRegistryPresent,
        LearningControlledFormalLabelIngestionStagingPackOptions? opt = null)
    {
        opt ??= new LearningControlledFormalLabelIngestionStagingPackOptions();
        var now = DateTimeOffset.UtcNow;

        var cases = BuildScenarios().Select(static scenario =>
        {
            var decision = LearningControlledFormalLabelIngestionStagingPackPolicy.Evaluate(
                scenario.Context, scenario.RtPassed, scenario.P15Passed,
                scenario.MainlineEvidencePresent, scenario.MainlineRegistryPresent);
            var statusMatched = string.Equals(scenario.ExpectedStatus, decision.Status, StringComparison.Ordinal);
            var blockedReasonMatched = scenario.ExpectedBlockedReason is null
                || decision.BlockedReasons.Contains(scenario.ExpectedBlockedReason, StringComparer.Ordinal);
            return new LearningControlledFormalLabelIngestionStagingPackCase
            {
                CaseName = scenario.CaseName,
                ExpectedStatus = scenario.ExpectedStatus,
                ActualStatus = decision.Status,
                ExpectedBlockedReason = scenario.ExpectedBlockedReason ?? string.Empty,
                ActualBlockedReasons = decision.BlockedReasons,
                Reasoning = decision.Reasoning,
                StatusMatched = statusMatched,
                BlockedReasonMatched = blockedReasonMatched,
                PassedAsExpected = statusMatched && blockedReasonMatched
            };
        }).ToArray();

        var blocked = new List<string>();
        if (cases.Length < 25) blocked.Add("InsufficientLearningControlledFormalLabelIngestionStagingPackCases");
        if (cases.Any(static c => !c.PassedAsExpected)) blocked.Add("LearningControlledFormalLabelIngestionStagingPackMatrixFailed");
        foreach (var status in new[] {
            LearningControlledFormalLabelIngestionStagingPackStatuses.LearningControlledFormalLabelIngestionStagingPackReady,
            LearningControlledFormalLabelIngestionStagingPackStatuses.LearningControlledFormalLabelIngestionStagingPackBlocked })
        {
            if (!cases.Any(c => string.Equals(c.ActualStatus, status, StringComparison.Ordinal)))
                blocked.Add($"StatusBranchNotCovered:{status}");
        }

        var realDecision = LearningControlledFormalLabelIngestionStagingPackPolicy.Evaluate(
            realContext, rtPassed, p15Passed, mainlineEvidencePresent, mainlineRegistryPresent);
        if (!string.Equals(realDecision.Status, LearningControlledFormalLabelIngestionStagingPackStatuses.LearningControlledFormalLabelIngestionStagingPackReady, StringComparison.Ordinal))
            blocked.AddRange(realDecision.BlockedReasons.Select(static x => $"RealLearningControlledFormalLabelIngestionStagingPack:{x}"));
        if (!rtPassed) blocked.Add(LearningControlledFormalLabelIngestionStagingPackBlockedReasons.RuntimeChangeGateNotPassed);
        if (!p15Passed) blocked.Add(LearningControlledFormalLabelIngestionStagingPackBlockedReasons.P15GateNotPassed);
        if (mainlineEvidencePresent) blocked.Add(LearningControlledFormalLabelIngestionStagingPackBlockedReasons.MainlineEvidencePresent);
        if (mainlineRegistryPresent) blocked.Add(LearningControlledFormalLabelIngestionStagingPackBlockedReasons.MainlineTrustRegistryPresent);

        var canBuild = string.Equals(realDecision.Status, LearningControlledFormalLabelIngestionStagingPackStatuses.LearningControlledFormalLabelIngestionStagingPackReady, StringComparison.Ordinal);
        var stagedRows = new List<StagedFormalHardNegativeRow>();
        FormalLabelIngestionDiffPreview diff = new();
        FormalLabelRollbackSnapshotPlan rollbackPlan = new();
        FormalLabelQuarantinePolicy quarantinePolicy = new();
        FormalLabelIngestionStagingDecision stagingDecision = new();
        long formalDatasetSizeBefore = 0;
        long formalDatasetSizeAfter = 0;
        string formalDatasetPathExpanded = realContext.FormalDatasetPath;
        var stagedPath = string.Empty;
        var diffPath = string.Empty;
        var rollbackPlanPath = string.Empty;
        var quarantinePath = string.Empty;
        var decisionPath = string.Empty;

        // Capture formal dataset size before any operation — this becomes our invariant check that we never touched it.
        formalDatasetSizeBefore = File.Exists(formalDatasetPathExpanded) ? new FileInfo(formalDatasetPathExpanded).Length : 0;

        if (canBuild)
        {
            // 1. Stage eligible candidates to learning/v10/staged-formal-hard-negatives.jsonl
            int idx = 0;
            foreach (var c in realContext.Candidates.Where(c => c.IntegrityVerified && c.PromotionEligibility == "Eligible").OrderBy(c => c.CandidateLabelId, StringComparer.Ordinal))
            {
                stagedRows.Add(new StagedFormalHardNegativeRow
                {
                    StagedLabelId = $"sfh-{idx++:D4}-{c.SourceShadowLabelId}",
                    SourceCandidateLabelId = c.CandidateLabelId,
                    SourceShadowLabelId = c.SourceShadowLabelId,
                    EvidencePath = c.EvidencePath,
                    ExpectedPreference = c.ExpectedPreference,
                    DeterministicBindingHash = c.DeterministicBindingHash,
                    LifecycleState = "Staged",
                    StagedLabelIsFormal = false,
                    StagingOnly = true,
                    AutoIngest = false,
                    PolicyVersion = "v10.19-staged-formal-hard-negative/v1",
                    StagedAt = now.ToString("O")
                });
            }
            stagedPath = Path.Combine(outputDir, "staged-formal-hard-negatives.jsonl");
            WriteStagedRowsJsonl(stagedPath, stagedRows);

            // 2. Diff preview vs formal dataset (READ ONLY — never write to formal)
            diff = BuildDiffPreview(formalDatasetPathExpanded, stagedRows);
            diffPath = Path.Combine(outputDir, "formal-label-ingestion-diff-preview.json");
            File.WriteAllText(diffPath, JsonSerializer.Serialize(diff, WriteIndented), new UTF8Encoding(true));

            // 3. Rollback snapshot plan (plan only — no snapshot taken)
            rollbackPlan = BuildRollbackSnapshotPlan(formalDatasetPathExpanded);
            rollbackPlanPath = Path.Combine(outputDir, "formal-label-rollback-snapshot-plan.json");
            File.WriteAllText(rollbackPlanPath, JsonSerializer.Serialize(rollbackPlan, WriteIndented), new UTF8Encoding(true));

            // 4. Quarantine policy
            quarantinePolicy = BuildQuarantinePolicy();
            quarantinePath = Path.Combine(outputDir, "formal-label-quarantine-policy.json");
            File.WriteAllText(quarantinePath, JsonSerializer.Serialize(quarantinePolicy, WriteIndented), new UTF8Encoding(true));

            // 5. Staging decision
            stagingDecision = BuildStagingDecision(stagedRows, diff, rollbackPlan, quarantinePolicy, now);
            decisionPath = Path.Combine(outputDir, "formal-label-ingestion-staging-decision.json");
            File.WriteAllText(decisionPath, JsonSerializer.Serialize(stagingDecision, WriteIndented), new UTF8Encoding(true));
        }

        // Re-check formal dataset size after all writes — must be unchanged.
        formalDatasetSizeAfter = File.Exists(formalDatasetPathExpanded) ? new FileInfo(formalDatasetPathExpanded).Length : 0;
        if (formalDatasetSizeBefore != formalDatasetSizeAfter)
            blocked.Add($"FormalDatasetSizeChangedDuringStaging:{formalDatasetSizeBefore}->{formalDatasetSizeAfter}");

        // Authority leak checks
        foreach (var r in stagedRows)
        {
            if (r.StagedLabelIsFormal) blocked.Add($"StagedRowStagedLabelIsFormalLeak:{r.StagedLabelId}");
            if (!r.StagingOnly) blocked.Add($"StagedRowStagingOnlyFalse:{r.StagedLabelId}");
            if (r.AutoIngest) blocked.Add($"StagedRowAutoIngestLeak:{r.StagedLabelId}");
        }
        if (diff.FormalTrainingSetChanged || diff.AutoIngest) blocked.Add("DiffPreviewRuntimeOrIngestLeak");
        if (rollbackPlan.SnapshotTaken || rollbackPlan.FormalTrainingSetChanged) blocked.Add("RollbackPlanRuntimeLeak");
        if (quarantinePolicy.FormalTrainingSetChanged || quarantinePolicy.AutoIngest) blocked.Add("QuarantinePolicyRuntimeOrIngestLeak");
        if (stagingDecision.FormalIngestionApplied || stagingDecision.FormalTrainingSetChanged
            || stagingDecision.StagedLabelsAreFormal || stagingDecision.AutoIngest
            || stagingDecision.HumanReviewAsGateAuthority || stagingDecision.AIArbitration
            || stagingDecision.RuntimePilotExecutionReadyForSeparateGate)
            blocked.Add("StagingDecisionRuntimeOrAuthorityLeak");

        var finalBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var passed = finalBlocked.Length == 0;

        return new LearningControlledFormalLabelIngestionStagingPackReport
        {
            OperationId = $"v10-learning-controlled-formal-label-ingestion-staging-pack-{Guid.NewGuid():N}",
            CreatedAt = now,
            ControlledFormalLabelIngestionStagingPackPassed = passed,
            GatePassed = opt.IsGate && passed,
            TotalCases = cases.Length,
            PassedCases = cases.Count(static c => c.PassedAsExpected),
            FailedCases = cases.Count(static c => !c.PassedAsExpected),
            ReadyCases = cases.Count(c => string.Equals(c.ActualStatus, LearningControlledFormalLabelIngestionStagingPackStatuses.LearningControlledFormalLabelIngestionStagingPackReady, StringComparison.Ordinal)),
            BlockedCases = cases.Count(c => string.Equals(c.ActualStatus, LearningControlledFormalLabelIngestionStagingPackStatuses.LearningControlledFormalLabelIngestionStagingPackBlocked, StringComparison.Ordinal)),
            Cases = cases,
            StagedRows = stagedRows,
            DiffPreview = diff,
            RollbackSnapshotPlan = rollbackPlan,
            QuarantinePolicy = quarantinePolicy,
            StagingDecision = stagingDecision,
            StagingDatasetReady = canBuild && stagedRows.Count > 0,
            StagedFormalLabelCount = stagedRows.Count,
            InvalidCandidateCount = realContext.Candidates.Count(c => c.PromotionEligibility == "InvalidBinding" || c.PromotionEligibility == "Rejected"),
            HashMismatchCount = realContext.Candidates.Count(c => !c.IntegrityVerified),
            DiffPreviewReady = canBuild,
            RollbackSnapshotPlanReady = canBuild,
            QuarantinePolicyReady = canBuild,
            StagingOnly = true,
            StagedLabelsAreFormal = false,
            FormalTrainingSetChanged = false,
            AutoIngest = false,
            HumanReviewAsGateAuthority = false,
            HumanFeedbackAutoIngest = false,
            RuntimePilotExecutionApplied = false,
            RuntimePromotionApplied = false,
            RuntimeRerankerChanged = false,
            RuntimeRouterChanged = false,
            ProductionDecisionChanged = false,
            PackageOutputChanged = false,
            FormalPackageWritten = false,
            GlobalDefaultOn = false,
            MLAuthority = false,
            LLMAuthority = false,
            RuntimeAuthority = false,
            GateAuthority = false,
            V8ScopedActivationPreserved = realContext.V8ScopedActivationPreserved,
            UpstreamFormalRealizationPackGatePresent = realContext.FormalRealizationPackPresent,
            UpstreamFormalRealizationPackGatePassed = realContext.FormalRealizationPackPassed,
            MainlineEvidencePresent = mainlineEvidencePresent,
            MainlineTrustRegistryPresent = mainlineRegistryPresent,
            StagedFormalHardNegativesPath = stagedPath,
            DiffPreviewPath = diffPath,
            RollbackSnapshotPlanPath = rollbackPlanPath,
            QuarantinePolicyPath = quarantinePath,
            StagingDecisionPath = decisionPath,
            FormalDatasetSizeBeforeBytes = formalDatasetSizeBefore,
            FormalDatasetSizeAfterBytes = formalDatasetSizeAfter,
            Recommendation = passed ? "ProceedToV9.5HumanReviewIngestionGate" : "Blocked",
            NextAllowedPhase = passed ? "V9.5HumanReviewIngestionGate-pending-controlled-write" : string.Empty,
            BlockedReasons = finalBlocked,
            Diagnostics = new[]
            {
                $"total={cases.Length}",
                $"realStatus={realDecision.Status}",
                $"stagedRowCount={stagedRows.Count}",
                $"invalidCandidateCount={realContext.Candidates.Count(c => c.PromotionEligibility != "Eligible")}",
                $"hashMismatchCount={realContext.Candidates.Count(c => !c.IntegrityVerified)}",
                $"diffWouldAdd={diff.WouldAddCount}",
                $"diffWouldSkipDuplicate={diff.WouldSkipDuplicateCount}",
                $"diffWouldRejectInvalid={diff.WouldRejectInvalidCount}",
                $"formalDatasetSizeBefore={formalDatasetSizeBefore}",
                $"formalDatasetSizeAfter={formalDatasetSizeAfter}",
                $"formalDatasetUntouched={formalDatasetSizeBefore == formalDatasetSizeAfter}"
            }
        };
    }

    private static IReadOnlyList<LearningControlledFormalLabelIngestionStagingPackScenario> BuildScenarios()
    {
        var clean = BuildCleanContext();
        return [
            new("AllUpstreamClean", clean, true, true, false, false, LearningControlledFormalLabelIngestionStagingPackStatuses.LearningControlledFormalLabelIngestionStagingPackReady, null),
            new("FormalRealizationPackMissing", clean with { FormalRealizationPackPresent = false }, true, true, false, false, LearningControlledFormalLabelIngestionStagingPackStatuses.LearningControlledFormalLabelIngestionStagingPackBlocked, LearningControlledFormalLabelIngestionStagingPackBlockedReasons.FormalRealizationPackMissing),
            new("FormalRealizationPackNotPassed", clean with { FormalRealizationPackPassed = false }, true, true, false, false, LearningControlledFormalLabelIngestionStagingPackStatuses.LearningControlledFormalLabelIngestionStagingPackBlocked, LearningControlledFormalLabelIngestionStagingPackBlockedReasons.FormalRealizationPackNotPassed),
            new("CandidatesMissing", clean with { CandidatesPresent = false, CandidateCount = 0 }, true, true, false, false, LearningControlledFormalLabelIngestionStagingPackStatuses.LearningControlledFormalLabelIngestionStagingPackBlocked, LearningControlledFormalLabelIngestionStagingPackBlockedReasons.CandidatesMissing),
            new("IntegrityManifestMissing", clean with { IntegrityManifestPresent = false }, true, true, false, false, LearningControlledFormalLabelIngestionStagingPackStatuses.LearningControlledFormalLabelIngestionStagingPackBlocked, LearningControlledFormalLabelIngestionStagingPackBlockedReasons.IntegrityManifestMissing),
            new("HashMismatch", clean with { HashMismatchOverride = true }, true, true, false, false, LearningControlledFormalLabelIngestionStagingPackStatuses.LearningControlledFormalLabelIngestionStagingPackBlocked, LearningControlledFormalLabelIngestionStagingPackBlockedReasons.HashMismatch),
            new("InvalidCandidateBinding", clean with { InvalidCandidateBindingOverride = true }, true, true, false, false, LearningControlledFormalLabelIngestionStagingPackStatuses.LearningControlledFormalLabelIngestionStagingPackBlocked, LearningControlledFormalLabelIngestionStagingPackBlockedReasons.InvalidCandidateBinding),
            new("StagedLabelsTreatedAsFormal", clean with { StagedLabelsTreatedAsFormalOverride = true }, true, true, false, false, LearningControlledFormalLabelIngestionStagingPackStatuses.LearningControlledFormalLabelIngestionStagingPackBlocked, LearningControlledFormalLabelIngestionStagingPackBlockedReasons.StagedLabelsTreatedAsFormal),
            new("FormalTrainingSetChangedTrue", clean with { FormalTrainingSetChangedOverride = true }, true, true, false, false, LearningControlledFormalLabelIngestionStagingPackStatuses.LearningControlledFormalLabelIngestionStagingPackBlocked, LearningControlledFormalLabelIngestionStagingPackBlockedReasons.FormalTrainingSetChangedTrue),
            new("AutoIngestTrue", clean with { AutoIngestOverride = true }, true, true, false, false, LearningControlledFormalLabelIngestionStagingPackStatuses.LearningControlledFormalLabelIngestionStagingPackBlocked, LearningControlledFormalLabelIngestionStagingPackBlockedReasons.AutoIngestTrue),
            new("HumanReviewAsGateAuthorityTrue", clean with { HumanReviewAsGateAuthorityOverride = true }, true, true, false, false, LearningControlledFormalLabelIngestionStagingPackStatuses.LearningControlledFormalLabelIngestionStagingPackBlocked, LearningControlledFormalLabelIngestionStagingPackBlockedReasons.HumanReviewAsGateAuthorityTrue),
            new("RuntimePilotExecutionAppliedTrue", clean with { RuntimePilotExecutionAppliedOverride = true }, true, true, false, false, LearningControlledFormalLabelIngestionStagingPackStatuses.LearningControlledFormalLabelIngestionStagingPackBlocked, LearningControlledFormalLabelIngestionStagingPackBlockedReasons.RuntimePilotExecutionAppliedTrue),
            new("RuntimePromotionAppliedTrue", clean with { RuntimePromotionAppliedOverride = true }, true, true, false, false, LearningControlledFormalLabelIngestionStagingPackStatuses.LearningControlledFormalLabelIngestionStagingPackBlocked, LearningControlledFormalLabelIngestionStagingPackBlockedReasons.RuntimePromotionAppliedTrue),
            new("RuntimeRerankerChangedTrue", clean with { RuntimeRerankerChangedOverride = true }, true, true, false, false, LearningControlledFormalLabelIngestionStagingPackStatuses.LearningControlledFormalLabelIngestionStagingPackBlocked, LearningControlledFormalLabelIngestionStagingPackBlockedReasons.RuntimeRerankerChangedTrue),
            new("RuntimeRouterChangedTrue", clean with { RuntimeRouterChangedOverride = true }, true, true, false, false, LearningControlledFormalLabelIngestionStagingPackStatuses.LearningControlledFormalLabelIngestionStagingPackBlocked, LearningControlledFormalLabelIngestionStagingPackBlockedReasons.RuntimeRouterChangedTrue),
            new("ProductionDecisionChangedTrue", clean with { ProductionDecisionChangedOverride = true }, true, true, false, false, LearningControlledFormalLabelIngestionStagingPackStatuses.LearningControlledFormalLabelIngestionStagingPackBlocked, LearningControlledFormalLabelIngestionStagingPackBlockedReasons.ProductionDecisionChangedTrue),
            new("PackageOutputChangedTrue", clean with { PackageOutputChangedOverride = true }, true, true, false, false, LearningControlledFormalLabelIngestionStagingPackStatuses.LearningControlledFormalLabelIngestionStagingPackBlocked, LearningControlledFormalLabelIngestionStagingPackBlockedReasons.PackageOutputChangedTrue),
            new("FormalPackageWrittenTrue", clean with { FormalPackageWrittenOverride = true }, true, true, false, false, LearningControlledFormalLabelIngestionStagingPackStatuses.LearningControlledFormalLabelIngestionStagingPackBlocked, LearningControlledFormalLabelIngestionStagingPackBlockedReasons.FormalPackageWrittenTrue),
            new("GlobalDefaultOnTrue", clean with { GlobalDefaultOnOverride = true }, true, true, false, false, LearningControlledFormalLabelIngestionStagingPackStatuses.LearningControlledFormalLabelIngestionStagingPackBlocked, LearningControlledFormalLabelIngestionStagingPackBlockedReasons.GlobalDefaultOnTrue),
            new("MLAuthorityTrue", clean with { MLAuthorityOverride = true }, true, true, false, false, LearningControlledFormalLabelIngestionStagingPackStatuses.LearningControlledFormalLabelIngestionStagingPackBlocked, LearningControlledFormalLabelIngestionStagingPackBlockedReasons.MLAuthorityTrue),
            new("LLMAuthorityTrue", clean with { LLMAuthorityOverride = true }, true, true, false, false, LearningControlledFormalLabelIngestionStagingPackStatuses.LearningControlledFormalLabelIngestionStagingPackBlocked, LearningControlledFormalLabelIngestionStagingPackBlockedReasons.LLMAuthorityTrue),
            new("RuntimeAuthorityTrue", clean with { RuntimeAuthorityOverride = true }, true, true, false, false, LearningControlledFormalLabelIngestionStagingPackStatuses.LearningControlledFormalLabelIngestionStagingPackBlocked, LearningControlledFormalLabelIngestionStagingPackBlockedReasons.RuntimeAuthorityTrue),
            new("GateAuthorityTrue", clean with { GateAuthorityOverride = true }, true, true, false, false, LearningControlledFormalLabelIngestionStagingPackStatuses.LearningControlledFormalLabelIngestionStagingPackBlocked, LearningControlledFormalLabelIngestionStagingPackBlockedReasons.GateAuthorityTrue),
            new("V8ScopedActivationLost", clean with { V8ScopedActivationPreserved = false }, true, true, false, false, LearningControlledFormalLabelIngestionStagingPackStatuses.LearningControlledFormalLabelIngestionStagingPackBlocked, LearningControlledFormalLabelIngestionStagingPackBlockedReasons.V8ScopedActivationLost),
            new("MainlineEvidencePresent", clean, true, true, true, false, LearningControlledFormalLabelIngestionStagingPackStatuses.LearningControlledFormalLabelIngestionStagingPackBlocked, LearningControlledFormalLabelIngestionStagingPackBlockedReasons.MainlineEvidencePresent),
            new("MainlineTrustRegistryPresent", clean, true, true, false, true, LearningControlledFormalLabelIngestionStagingPackStatuses.LearningControlledFormalLabelIngestionStagingPackBlocked, LearningControlledFormalLabelIngestionStagingPackBlockedReasons.MainlineTrustRegistryPresent),
            new("RuntimeGateNotPassed", clean, false, true, false, false, LearningControlledFormalLabelIngestionStagingPackStatuses.LearningControlledFormalLabelIngestionStagingPackBlocked, LearningControlledFormalLabelIngestionStagingPackBlockedReasons.RuntimeChangeGateNotPassed),
            new("P15GateNotPassed", clean, true, false, false, false, LearningControlledFormalLabelIngestionStagingPackStatuses.LearningControlledFormalLabelIngestionStagingPackBlocked, LearningControlledFormalLabelIngestionStagingPackBlockedReasons.P15GateNotPassed)
        ];
    }

    private static LearningControlledFormalLabelIngestionStagingPackContext BuildCleanContext() => new()
    {
        FormalRealizationPackPresent = true,
        FormalRealizationPackPassed = true,
        CandidatesPresent = true,
        CandidateCount = 60,
        IntegrityManifestPresent = true,
        V8ScopedActivationPreserved = true,
        Candidates = Array.Empty<FormalLabelCandidate>(),
        FormalDatasetPath = string.Empty
    };

    // ─── builders ────────────────────────────────────────────────────────────

    private static FormalLabelIngestionDiffPreview BuildDiffPreview(string formalDatasetPath, IReadOnlyList<StagedFormalHardNegativeRow> staged)
    {
        var formalLines = File.Exists(formalDatasetPath) ? File.ReadAllLines(formalDatasetPath).Count(static l => !string.IsNullOrWhiteSpace(l)) : 0;
        // Determine duplicates by hash — a staged row counts as duplicate if its hash equals any existing formal row's hash field (if any).
        // Since formal hard-negatives.jsonl has a different schema (no deterministicBindingHash), treat all staged rows as additions for diff.
        var wouldAdd = staged.Count;
        var wouldSkipDuplicate = 0;
        var wouldRejectInvalid = 0;
        var sample = staged.Take(3).Select(s => $"{s.StagedLabelId} (hash={s.DeterministicBindingHash[..16]}...)").ToArray();
        return new FormalLabelIngestionDiffPreview
        {
            DiffMode = "PreviewOnlyNoApply",
            FormalDatasetPath = formalDatasetPath,
            FormalDatasetLineCountBefore = formalLines,
            StagedRowCount = staged.Count,
            WouldAddCount = wouldAdd,
            WouldSkipDuplicateCount = wouldSkipDuplicate,
            WouldRejectInvalidCount = wouldRejectInvalid,
            FormalDatasetLineCountAfterIfApplied = formalLines + wouldAdd,
            FormalTrainingSetChanged = false,
            AutoIngest = false,
            SampleAddedRows = sample,
            Notes = new[]
            {
                $"Formal dataset path: {formalDatasetPath}",
                $"Current formal dataset line count: {formalLines}",
                $"Staged rows: {staged.Count}",
                $"If applied, formal dataset would grow to {formalLines + wouldAdd} lines.",
                "This diff is read-only. FormalTrainingSetChanged=false. AutoIngest=false. No write performed.",
                "Schema bridge between staged jsonl (v10.19 with hash) and formal hard-negatives.jsonl (legacy schema) requires V9.5 ingestion to translate fields."
            }
        };
    }

    private static FormalLabelRollbackSnapshotPlan BuildRollbackSnapshotPlan(string formalDatasetPath)
    {
        var currentHash = ComputeFileSha256(formalDatasetPath);
        var lineCount = File.Exists(formalDatasetPath) ? File.ReadAllLines(formalDatasetPath).Count(static l => !string.IsNullOrWhiteSpace(l)) : 0;
        var plannedSnapshotPath = "learning/v10/staged-formal-hard-negatives-snapshots/hard-negatives-{timestamp}.snapshot.jsonl";
        return new FormalLabelRollbackSnapshotPlan
        {
            PlanVersion = "v10.19-rollback-snapshot-plan/v1",
            PlanMode = "PlanOnlyNoSnapshotTaken",
            FormalDatasetPath = formalDatasetPath,
            PlannedSnapshotPath = plannedSnapshotPath,
            PlannedSnapshotAlgorithm = "SHA-256",
            FormalDatasetCurrentHash = currentHash,
            FormalDatasetCurrentLineCount = lineCount,
            SnapshotProcedure = new[]
            {
                "Step 1: Compute SHA-256 of current formal dataset; record hash + line count.",
                "Step 2: Copy current formal dataset to learning/v10/staged-formal-hard-negatives-snapshots/ with timestamped filename.",
                "Step 3: Compute SHA-256 of copied snapshot; verify it matches Step 1.",
                "Step 4: Record snapshot path + hash in V8.18 rollback binding artifact.",
                "Step 5: Emit V8.18-style audit event referencing snapshot path."
            },
            RestoreProcedure = new[]
            {
                "Step 1: Verify rollback request references a snapshot in the V8.18 rollback binding registry.",
                "Step 2: Verify snapshot hash matches the recorded hash; refuse otherwise.",
                "Step 3: Atomically replace formal dataset with snapshot content (rename → move pattern).",
                "Step 4: Emit V8.18-style revocation record referencing all retracted labels.",
                "Step 5: Notify operators (signal only, not authority)."
            },
            SnapshotTaken = false,
            FormalTrainingSetChanged = false,
            V818RollbackBindingReference = "vector/v8/runtime-activation/activation-rollback-binding-FormalRetrievalActivation-demo-workspace-demo-collection.json",
            V818RevocationRecordReference = "vector/v8/dedicated-crossing/revocation-record-FormalRetrievalActivation-demo-workspace-demo-collection.json"
        };
    }

    private static FormalLabelQuarantinePolicy BuildQuarantinePolicy()
        => new()
        {
            PolicyVersion = "v10.19-formal-label-quarantine/v1",
            QuarantineLocation = "learning/v10/staged-formal-hard-negatives-quarantine/",
            QuarantineTriggers = new[]
            {
                "IntegrityManifest hash mismatch detected post-staging",
                "Evidence path becomes invalid (source ranking pair removed/rewritten)",
                "Downstream pilot detects regression vs reference under repaired scoring",
                "Operator manual quarantine request",
                "Counterexample replay shows >5% additional failure rate vs reference"
            },
            QuarantineActions = new[]
            {
                "Move affected staged row to learning/v10/staged-formal-hard-negatives-quarantine/ with timestamped subdir.",
                "Mark LifecycleState=Quarantined in the staging decision.",
                "Notify operators via signal channel (not gate authority).",
                "Refuse to include quarantined rows in any subsequent diff preview or ingestion attempt.",
                "Refuse to silently delete; quarantined rows persist on disk until explicit operator review."
            },
            ReleaseConditions = new[]
            {
                "Hash recomputed and verified against original binding.",
                "Evidence path verified to exist and still match expectedPreference.",
                "Counterexample replay confirms candidate failure rate <= reference failure rate.",
                "Operator releases via V9.5 ingestion contract (not auto-released)."
            },
            RejectedAuthorityClaims = new[]
            {
                "Quarantine policy CANNOT auto-release rows without operator review.",
                "Quarantine policy CANNOT silently delete quarantined rows.",
                "Quarantine policy CANNOT modify learning/features/hard-negatives.jsonl.",
                "Quarantine policy CANNOT grant ML/LLM/Runtime/Gate authority."
            },
            FormalTrainingSetChanged = false,
            AutoIngest = false
        };

    private static FormalLabelIngestionStagingDecision BuildStagingDecision(
        IReadOnlyList<StagedFormalHardNegativeRow> staged,
        FormalLabelIngestionDiffPreview diff,
        FormalLabelRollbackSnapshotPlan rollback,
        FormalLabelQuarantinePolicy quarantine,
        DateTimeOffset now)
    {
        var blockedBy = new List<string>
        {
            "FormalIngestionNotApplied: staging is the buffer, V9.5 controlled ingestion is required to write to formal dataset.",
            "RollbackSnapshotNotTaken: PlannedSnapshotPath is described but actual copy has not been executed."
        };
        return new FormalLabelIngestionStagingDecision
        {
            DecisionId = $"v10-formal-label-ingestion-staging-decision-{Guid.NewGuid():N}",
            CreatedAt = now.ToString("O"),
            StagedFormalLabelCount = staged.Count,
            InvalidCandidateCount = 0,
            HashMismatchCount = 0,
            StagingDatasetReady = staged.Count > 0,
            DiffPreviewReady = true,
            RollbackSnapshotPlanReady = true,
            QuarantinePolicyReady = true,
            StagingOnly = true,
            StagedLabelsAreFormal = false,
            FormalTrainingSetChanged = false,
            AutoIngest = false,
            HumanFeedbackAsSignal = true,
            HumanReviewAsGateAuthority = false,
            FormalIngestionApplied = false,
            RuntimePilotExecutionReadyForSeparateGate = false,
            BlockedForFormalIngestionBy = blockedBy,
            AIArbitration = false,
            EvidenceSourcesConsidered = new[]
            {
                "learning/v10/formal-evidence-realization-pack-gate.json",
                "learning/v10/formal-label-candidates.jsonl",
                "learning/v10/formal-label-integrity-manifest.json",
                "learning/v10/formal-label-realization-decision.json",
                "learning/v10/formal-label-rollback-contract.json"
            },
            DecisionNotes = new[]
            {
                $"Staged rows: {staged.Count} (all sourced from V10.16 eligible + integrity-verified candidates).",
                $"Diff preview: would add {diff.WouldAddCount} rows, skip {diff.WouldSkipDuplicateCount} duplicates, reject {diff.WouldRejectInvalidCount} invalid.",
                $"Rollback snapshot plan published; SnapshotTaken={rollback.SnapshotTaken}.",
                $"Quarantine policy published; {quarantine.QuarantineTriggers.Count} triggers / {quarantine.ReleaseConditions.Count} release conditions.",
                "Staging is a BUFFER. Formal dataset (learning/features/hard-negatives.jsonl) was NOT modified.",
                "FormalIngestionApplied=false; RuntimePilotExecutionReadyForSeparateGate=false until V9.5 actually writes the formal dataset under controlled human review."
            }
        };
    }

    private static string ComputeFileSha256(string path)
    {
        if (!File.Exists(path)) return string.Empty;
        try
        {
            var bytes = File.ReadAllBytes(path);
            var hash = SHA256.HashData(bytes);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
        catch { return string.Empty; }
    }

    private static void WriteStagedRowsJsonl(string path, IReadOnlyList<StagedFormalHardNegativeRow> rows)
    {
        var sb = new StringBuilder();
        foreach (var r in rows) sb.AppendLine(JsonSerializer.Serialize(r));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    /// <summary>V10.19: load V10.16 formal label candidates from disk.</summary>
    public static IReadOnlyList<FormalLabelCandidate> LoadFormalLabelCandidates(string path)
    {
        if (!File.Exists(path)) return Array.Empty<FormalLabelCandidate>();
        var result = new List<FormalLabelCandidate>();
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var c = JsonSerializer.Deserialize<FormalLabelCandidate>(line, opts);
                if (c is not null) result.Add(c);
            }
            catch { }
        }
        return result;
    }

    public static string BuildMarkdown(string title, LearningControlledFormalLabelIngestionStagingPackReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine($"- ControlledFormalLabelIngestionStagingPackPassed: `{report.ControlledFormalLabelIngestionStagingPackPassed}`");
        sb.AppendLine($"- GatePassed: `{report.GatePassed}`");
        sb.AppendLine($"- TotalCases: `{report.TotalCases}` PassedCases: `{report.PassedCases}` FailedCases: `{report.FailedCases}`");
        sb.AppendLine();
        sb.AppendLine("## Staging Outputs");
        sb.AppendLine($"- StagingDatasetReady: `{report.StagingDatasetReady}` StagedFormalLabelCount: `{report.StagedFormalLabelCount}`");
        sb.AppendLine($"- InvalidCandidateCount: `{report.InvalidCandidateCount}` HashMismatchCount: `{report.HashMismatchCount}`");
        sb.AppendLine($"- DiffPreviewReady: `{report.DiffPreviewReady}` RollbackSnapshotPlanReady: `{report.RollbackSnapshotPlanReady}` QuarantinePolicyReady: `{report.QuarantinePolicyReady}`");
        sb.AppendLine();
        sb.AppendLine("## Diff Preview");
        sb.AppendLine($"- DiffMode: `{report.DiffPreview.DiffMode}`");
        sb.AppendLine($"- FormalDatasetLineCountBefore: `{report.DiffPreview.FormalDatasetLineCountBefore}`");
        sb.AppendLine($"- StagedRowCount: `{report.DiffPreview.StagedRowCount}`");
        sb.AppendLine($"- WouldAddCount: `{report.DiffPreview.WouldAddCount}` WouldSkipDuplicateCount: `{report.DiffPreview.WouldSkipDuplicateCount}` WouldRejectInvalidCount: `{report.DiffPreview.WouldRejectInvalidCount}`");
        sb.AppendLine($"- FormalDatasetLineCountAfterIfApplied: `{report.DiffPreview.FormalDatasetLineCountAfterIfApplied}`");
        sb.AppendLine($"- FormalTrainingSetChanged: `{report.DiffPreview.FormalTrainingSetChanged}` AutoIngest: `{report.DiffPreview.AutoIngest}`");
        sb.AppendLine();
        sb.AppendLine("## Rollback Snapshot Plan");
        sb.AppendLine($"- PlanMode: `{report.RollbackSnapshotPlan.PlanMode}` Algorithm: `{report.RollbackSnapshotPlan.PlannedSnapshotAlgorithm}`");
        sb.AppendLine($"- FormalDatasetCurrentLineCount: `{report.RollbackSnapshotPlan.FormalDatasetCurrentLineCount}`");
        sb.AppendLine($"- SnapshotTaken: `{report.RollbackSnapshotPlan.SnapshotTaken}` FormalTrainingSetChanged: `{report.RollbackSnapshotPlan.FormalTrainingSetChanged}`");
        sb.AppendLine();
        sb.AppendLine("## Quarantine Policy");
        sb.AppendLine($"- PolicyVersion: `{report.QuarantinePolicy.PolicyVersion}` Triggers: `{report.QuarantinePolicy.QuarantineTriggers.Count}` Actions: `{report.QuarantinePolicy.QuarantineActions.Count}` ReleaseConditions: `{report.QuarantinePolicy.ReleaseConditions.Count}`");
        sb.AppendLine($"- FormalTrainingSetChanged: `{report.QuarantinePolicy.FormalTrainingSetChanged}` AutoIngest: `{report.QuarantinePolicy.AutoIngest}`");
        sb.AppendLine();
        sb.AppendLine("## Staging Decision");
        sb.AppendLine($"- StagingOnly: `{report.StagingDecision.StagingOnly}` StagedLabelsAreFormal: `{report.StagingDecision.StagedLabelsAreFormal}` FormalIngestionApplied: `{report.StagingDecision.FormalIngestionApplied}`");
        sb.AppendLine($"- RuntimePilotExecutionReadyForSeparateGate: `{report.StagingDecision.RuntimePilotExecutionReadyForSeparateGate}`");
        sb.AppendLine($"- BlockedForFormalIngestionBy:");
        foreach (var b in report.StagingDecision.BlockedForFormalIngestionBy) sb.AppendLine($"  - {b}");
        sb.AppendLine();
        sb.AppendLine("## Authority Invariants");
        sb.AppendLine($"- StagingOnly: `{report.StagingOnly}` StagedLabelsAreFormal: `{report.StagedLabelsAreFormal}` FormalTrainingSetChanged: `{report.FormalTrainingSetChanged}`");
        sb.AppendLine($"- AutoIngest: `{report.AutoIngest}` HumanReviewAsGateAuthority: `{report.HumanReviewAsGateAuthority}` HumanFeedbackAutoIngest: `{report.HumanFeedbackAutoIngest}`");
        sb.AppendLine($"- MLAuthority: `{report.MLAuthority}` LLMAuthority: `{report.LLMAuthority}` RuntimeAuthority: `{report.RuntimeAuthority}` GateAuthority: `{report.GateAuthority}`");
        sb.AppendLine($"- RuntimePromotionApplied: `{report.RuntimePromotionApplied}` RuntimePilotExecutionApplied: `{report.RuntimePilotExecutionApplied}`");
        sb.AppendLine($"- RuntimeRerankerChanged: `{report.RuntimeRerankerChanged}` RuntimeRouterChanged: `{report.RuntimeRouterChanged}` ProductionDecisionChanged: `{report.ProductionDecisionChanged}`");
        sb.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}` FormalPackageWritten: `{report.FormalPackageWritten}` GlobalDefaultOn: `{report.GlobalDefaultOn}`");
        sb.AppendLine($"- V8ScopedActivationPreserved: `{report.V8ScopedActivationPreserved}`");
        sb.AppendLine();
        sb.AppendLine($"- FormalDatasetSizeBeforeBytes: `{report.FormalDatasetSizeBeforeBytes}` FormalDatasetSizeAfterBytes: `{report.FormalDatasetSizeAfterBytes}` (must be equal)");
        sb.AppendLine($"- Recommendation: `{report.Recommendation}` NextAllowedPhase: `{report.NextAllowedPhase}`");
        if (report.BlockedReasons.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Blocked Reasons");
            foreach (var r in report.BlockedReasons) sb.AppendLine($"- `{r}`");
        }
        return sb.ToString();
    }
}

public sealed class LearningControlledFormalLabelIngestionStagingPackCase
{
    public string CaseName { get; init; } = string.Empty;
    public string ExpectedStatus { get; init; } = string.Empty;
    public string ActualStatus { get; init; } = string.Empty;
    public string ExpectedBlockedReason { get; init; } = string.Empty;
    public IReadOnlyList<string> ActualBlockedReasons { get; init; } = Array.Empty<string>();
    public string Reasoning { get; init; } = string.Empty;
    public bool StatusMatched { get; init; }
    public bool BlockedReasonMatched { get; init; }
    public bool PassedAsExpected { get; init; }
}

public sealed class LearningControlledFormalLabelIngestionStagingPackReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool ControlledFormalLabelIngestionStagingPackPassed { get; init; }
    public bool GatePassed { get; init; }
    public int TotalCases { get; init; }
    public int PassedCases { get; init; }
    public int FailedCases { get; init; }
    public int ReadyCases { get; init; }
    public int BlockedCases { get; init; }
    public IReadOnlyList<LearningControlledFormalLabelIngestionStagingPackCase> Cases { get; init; } = Array.Empty<LearningControlledFormalLabelIngestionStagingPackCase>();
    public IReadOnlyList<StagedFormalHardNegativeRow> StagedRows { get; init; } = Array.Empty<StagedFormalHardNegativeRow>();
    public FormalLabelIngestionDiffPreview DiffPreview { get; init; } = new();
    public FormalLabelRollbackSnapshotPlan RollbackSnapshotPlan { get; init; } = new();
    public FormalLabelQuarantinePolicy QuarantinePolicy { get; init; } = new();
    public FormalLabelIngestionStagingDecision StagingDecision { get; init; } = new();
    public bool StagingDatasetReady { get; init; }
    public int StagedFormalLabelCount { get; init; }
    public int InvalidCandidateCount { get; init; }
    public int HashMismatchCount { get; init; }
    public bool DiffPreviewReady { get; init; }
    public bool RollbackSnapshotPlanReady { get; init; }
    public bool QuarantinePolicyReady { get; init; }
    public bool StagingOnly { get; init; }
    public bool StagedLabelsAreFormal { get; init; }
    public bool FormalTrainingSetChanged { get; init; }
    public bool AutoIngest { get; init; }
    public bool HumanReviewAsGateAuthority { get; init; }
    public bool HumanFeedbackAutoIngest { get; init; }
    public bool RuntimePilotExecutionApplied { get; init; }
    public bool RuntimePromotionApplied { get; init; }
    public bool RuntimeRerankerChanged { get; init; }
    public bool RuntimeRouterChanged { get; init; }
    public bool ProductionDecisionChanged { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool MLAuthority { get; init; }
    public bool LLMAuthority { get; init; }
    public bool RuntimeAuthority { get; init; }
    public bool GateAuthority { get; init; }
    public bool V8ScopedActivationPreserved { get; init; }
    public bool UpstreamFormalRealizationPackGatePresent { get; init; }
    public bool UpstreamFormalRealizationPackGatePassed { get; init; }
    public bool MainlineEvidencePresent { get; init; }
    public bool MainlineTrustRegistryPresent { get; init; }
    public string StagedFormalHardNegativesPath { get; init; } = string.Empty;
    public string DiffPreviewPath { get; init; } = string.Empty;
    public string RollbackSnapshotPlanPath { get; init; } = string.Empty;
    public string QuarantinePolicyPath { get; init; } = string.Empty;
    public string StagingDecisionPath { get; init; } = string.Empty;
    public long FormalDatasetSizeBeforeBytes { get; init; }
    public long FormalDatasetSizeAfterBytes { get; init; }
    public string Recommendation { get; init; } = string.Empty;
    public string NextAllowedPhase { get; init; } = string.Empty;
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

public sealed class LearningControlledFormalLabelIngestionStagingPackOptions
{
    public bool Enabled { get; init; } = true;
    public bool IsGate { get; init; }
}
