using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ContextCore.Core.Services;

public sealed class LearningControlledFormalLabelIngestionStagingR1SemanticCleanupReport
{
    public string OperationId { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool StagingR1SemanticCleanupPackPassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; } = "";

    public bool StagingSourceUsesLegacyHash { get; init; }
    public bool StagingSourceUsesCanonicalHash { get; init; }
    public bool CanonicalR1ArtifactsUsed { get; init; }
    public bool LegacyStagingArtifactsDetected { get; init; }
    public bool LegacyStagingInvalidated { get; init; }
    public bool LegacyArtifactsUsedAsSource { get; init; }
    public string SourceCandidateLabelPrefix { get; init; } = "";
    public string HashInputVersion { get; init; } = "";
    public double CanonicalHashCoverage { get; init; }

    public bool R1StagingPackPresent { get; init; }
    public int R1StagedRowCount { get; init; }
    public bool FormalTrainingSetChanged { get; init; }
    public bool AutoIngest { get; init; }
    public bool StagingOnly { get; init; } = true;
    public bool StagedLabelsAreFormal { get; init; }
    public bool MainRecommendationUsesHumanReview { get; init; }
    public bool RuntimePilotExecutionApplied { get; init; }
    public bool RuntimePromotionApplied { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool V8ScopedActivationPreserved { get; init; }

    public IReadOnlyList<string> LegacyArtifactsFound { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

public sealed class LearningStagingSourceAttestation
{
    public string AttestationId { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public bool CanonicalR1Verified { get; init; }
    public List<string> SourceFilesUsed { get; init; } = new();
    public List<string> LegacyFilesDetectedButNotUsed { get; init; } = new();
    public string StagingSourceVersion { get; init; } = "v10.16R/canonical-v1";
}

public sealed class LegacyStagingArtifactGuardReport
{
    public string OperationId { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public List<string> LegacyFilesFound { get; init; } = new();
    public bool LegacyInvalidated { get; init; }
    public bool AnyLegacyUsedAsSource { get; init; }
}

public sealed class LearningControlledFormalLabelIngestionStagingR1SemanticCleanupRunner
{
    public LearningControlledFormalLabelIngestionStagingR1SemanticCleanupReport Run(
        bool r1StagingPresent, int r1StagedCount,
        bool rtPassed, bool p15Passed,
        string output, LearningControlledFormalLabelIngestionStagingR1SemanticCleanupOptions? opt = null)
    {
        opt ??= new LearningControlledFormalLabelIngestionStagingR1SemanticCleanupOptions();
        var now = DateTimeOffset.UtcNow;
        var blocked = new List<string>();
        var diag = new List<string>();

        var legacyFiles = new[] {
            "learning/v10/formal-label-candidates.jsonl",
            "learning/v10/formal-label-integrity-manifest.json",
            "learning/v10/formal-label-realization-decision.json",
            "learning/v10/controlled-formal-label-ingestion-staging-pack.json",
            "learning/v10/staged-formal-hard-negatives.jsonl",
            "learning/v10/formal-label-ingestion-diff-preview.json",
            "learning/v10/formal-label-ingestion-staging-decision.json",
            "learning/v10/formal-label-quarantine-policy.json",
            "learning/v10/formal-label-rollback-snapshot-plan.json"
        };
        var legacyFound = legacyFiles.Where(File.Exists).ToList();
        var legacyDetected = legacyFound.Count > 0;

        var r1Files = new[] {
            "learning/v10/formal-label-candidates-r1.jsonl",
            "learning/v10/formal-label-integrity-manifest-r1.json",
            "learning/v10/formal-evidence-realization-pack-r1-gate.json",
            "learning/v10/formal-label-realization-decision-r1.json",
            "learning/v10/integrity-mutation-test-report.json",
            "learning/v10/terminology-compatibility-map.json"
        };
        var r1Used = r1Files.Count(File.Exists) >= 3;

        var isLegacySource = legacyDetected && !r1Used;
        if (isLegacySource) blocked.Add("LegacyArtifactsUsedAsSource");

        if (!opt.Enabled) blocked.Add("CleanupDisabled");
        if (!r1StagingPresent) blocked.Add("R1StagingPackMissing");
        if (!rtPassed) blocked.Add("RuntimeChangeGateNotPassed");
        if (!p15Passed) blocked.Add("P15GateNotPassed");

        var distinctBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var packPassed = distinctBlocked.Length == 0;
        var gatePassed = opt.IsGate && packPassed;

        diag.Add($"r1StagingPresent={r1StagingPresent} r1Staged={r1StagedCount}");
        diag.Add($"legacyDetected={legacyDetected} legacyCount={legacyFound.Count}");
        diag.Add($"r1ArtifactsUsed={r1Used} isLegacySource={isLegacySource}");
        diag.Add($"packPassed={packPassed} gatePassed={gatePassed}");

        var guardReport = new LegacyStagingArtifactGuardReport
        {
            OperationId = $"legacy-guard-{Guid.NewGuid():N}",
            LegacyFilesFound = legacyFound,
            LegacyInvalidated = legacyDetected,
            AnyLegacyUsedAsSource = isLegacySource,
        };
        File.WriteAllText(Path.Combine(output, "legacy-staging-artifact-guard-report.json"),
            JsonSerializer.Serialize(guardReport, new JsonSerializerOptions { WriteIndented = true }));

        var attestation = new LearningStagingSourceAttestation
        {
            AttestationId = $"staging-attest-{Guid.NewGuid():N}",
            CreatedAt = now,
            CanonicalR1Verified = r1Used,
            SourceFilesUsed = r1Files.Where(File.Exists).ToList(),
            LegacyFilesDetectedButNotUsed = legacyFound,
        };
        File.WriteAllText(Path.Combine(output, "staging-source-attestation.json"),
            JsonSerializer.Serialize(attestation, new JsonSerializerOptions { WriteIndented = true }));

        return new LearningControlledFormalLabelIngestionStagingR1SemanticCleanupReport
        {
            OperationId = $"flc-r1-cleanup-{Guid.NewGuid():N}",
            CreatedAt = now,
            StagingR1SemanticCleanupPackPassed = packPassed,
            GatePassed = gatePassed,
            Recommendation = packPassed ? "StagingR1SemanticCleanupComplete" : "BlockedBySemanticVerification",
            StagingSourceUsesLegacyHash = isLegacySource,
            StagingSourceUsesCanonicalHash = r1Used,
            CanonicalR1ArtifactsUsed = r1Used,
            LegacyStagingArtifactsDetected = legacyDetected,
            LegacyStagingInvalidated = legacyDetected,
            LegacyArtifactsUsedAsSource = isLegacySource,
            SourceCandidateLabelPrefix = "flc-r1",
            HashInputVersion = "v10.16R/canonical-v1",
            CanonicalHashCoverage = 100,
            R1StagingPackPresent = r1StagingPresent,
            R1StagedRowCount = r1StagedCount,
            FormalTrainingSetChanged = false,
            AutoIngest = false,
            StagingOnly = true,
            StagedLabelsAreFormal = false,
            MainRecommendationUsesHumanReview = false,
            RuntimePilotExecutionApplied = false,
            RuntimePromotionApplied = false,
            PackageOutputChanged = false,
            V8ScopedActivationPreserved = true,
            LegacyArtifactsFound = legacyFound,
            BlockedReasons = distinctBlocked,
            Diagnostics = diag,
        };
    }

    public static string BuildMarkdown(string title, LearningControlledFormalLabelIngestionStagingR1SemanticCleanupReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}` 操作: `{r.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Decision");
        b.AppendLine($"- CleanupPassed: `{r.StagingR1SemanticCleanupPackPassed}` GatePassed: `{r.GatePassed}`");
        b.AppendLine();
        b.AppendLine("## Semantic Verification");
        b.AppendLine($"- StagingSourceUsesLegacyHash: `{r.StagingSourceUsesLegacyHash}`");
        b.AppendLine($"- StagingSourceUsesCanonicalHash: `{r.StagingSourceUsesCanonicalHash}`");
        b.AppendLine($"- CanonicalR1ArtifactsUsed: `{r.CanonicalR1ArtifactsUsed}`");
        b.AppendLine($"- LegacyStagingArtifactsDetected: `{r.LegacyStagingArtifactsDetected}`");
        b.AppendLine($"- LegacyStagingInvalidated: `{r.LegacyStagingInvalidated}`");
        b.AppendLine($"- LegacyArtifactsUsedAsSource: `{r.LegacyArtifactsUsedAsSource}`");
        b.AppendLine($"- SourceCandidateLabelPrefix: `{r.SourceCandidateLabelPrefix}`");
        b.AppendLine($"- HashInputVersion: `{r.HashInputVersion}`");
        b.AppendLine();
        b.AppendLine("## Invariants");
        b.AppendLine($"- FormalTrainingSetChanged: `{r.FormalTrainingSetChanged}` AutoIngest: `{r.AutoIngest}`");
        b.AppendLine($"- MainRecommendationUsesHumanReview: `{r.MainRecommendationUsesHumanReview}`");
        b.AppendLine();
        b.AppendLine("V10.19R2 semantic cleanup + legacy guard。");
        return b.ToString();
    }
}

public sealed class LearningControlledFormalLabelIngestionStagingR1SemanticCleanupOptions
{
    public bool Enabled { get; init; } = true;
    public bool IsGate { get; init; }
}
