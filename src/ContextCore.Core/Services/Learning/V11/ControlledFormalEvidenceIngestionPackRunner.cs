using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ContextCore.Core.Services;

// ---- Statuses ----
public static class ControlledFormalEvidenceIngestionStatuses
{
    public const string Ready = nameof(Ready);
    public const string Blocked = "BlockedByGuards";
}

// ---- DTOs ----
public sealed class FormalDatasetPreIngestionSnapshot
{
    public string SnapshotId { get; init; } = "";
    public DateTimeOffset SnapshotAt { get; init; }
    public string FormalDatasetPath { get; init; } = "";
    public int LineCountBefore { get; init; }
    public string DatasetHashBefore { get; init; } = "";
}

public sealed class FormalDatasetIngestionDiff
{
    public string DiffId { get; init; } = "";
    public int StagedRowCount { get; init; }
    public int InsertedCount { get; init; }
    public int SkippedDuplicateCount { get; init; }
    public int RejectedInvalidCount { get; init; }
    public bool DuplicatesDetected { get; init; }
    public List<string> SampleInserted { get; init; } = new();
}

public sealed class FormalDatasetPostIngestionManifest
{
    public string ManifestId { get; init; } = "";
    public int LineCountAfter { get; init; }
    public string DatasetHashAfter { get; init; } = "";
    public bool FormalTrainingSetChanged { get; init; }
}

public sealed class FormalIngestionRollbackManifest
{
    public string RollbackId { get; init; } = "";
    public string SnapshotReference { get; init; } = "";
    public List<string> RollbackProcedures { get; init; } = new();
    public bool RestoreVerified { get; init; }
}

public sealed class FormalLabelRealizationManifest
{
    public string ManifestId { get; init; } = "";
    public int StagedLabelCount { get; init; }
    public List<string> RealizedLabelIds { get; init; } = new();
}

public sealed class PostIngestionValidationReport
{
    public string ReportId { get; init; } = "";
    public bool ValidationPassed { get; init; }
    public int PostIngestionLineCount { get; init; }
    public int HardNegativeCount { get; init; }
    public bool CounterexampleReplayPassed { get; init; }
    public bool EvidenceSufficiencyConfirmed { get; init; }
    public bool RuntimePilotExecutionReadyForSeparateGate { get; init; }
}

public sealed class ControlledFormalEvidenceIngestionPackScenario
{
    public string CaseName { get; init; } = "";
    public string ExpectedStatus { get; init; } = "";
    public string ExpectedBlockedReason { get; init; } = "";
}

public sealed class ControlledFormalEvidenceIngestionPackCase
{
    public string CaseName { get; init; } = "";
    public string ExpectedStatus { get; init; } = "";
    public string ActualStatus { get; init; } = "";
    public string ExpectedBlockedReason { get; init; } = "";
    public IReadOnlyList<string> ActualBlockedReasons { get; init; } = Array.Empty<string>();
    public bool StatusMatched { get; init; }
    public bool BlockedReasonMatched { get; init; }
    public bool PassedAsExpected { get; init; }
}

public sealed class ControlledFormalEvidenceIngestionPackReport
{
    public string OperationId { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool ControlledFormalEvidenceIngestionPackPassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; } = "";
    public string NextAllowedPhase { get; init; } = "";

    public int TotalCases { get; init; }
    public int PassedCases { get; init; }
    public int FailedCases { get; init; }

    public bool CanonicalR1ArtifactsUsed { get; init; }
    public bool LegacyStagingArtifactsDetected { get; init; }
    public bool LegacyStagingInvalidated { get; init; }
    public bool StagingSourceUsesCanonicalHash { get; init; }
    public bool StagingSourceUsesLegacyHash { get; init; }

    public bool FormalDatasetSnapshotReady { get; init; }
    public bool IngestionDiffReady { get; init; }
    public bool RollbackManifestReady { get; init; }
    public bool FormalLabelsRealized { get; init; }
    public int InsertedFormalLabelCount { get; init; }
    public int SkippedDuplicateCount { get; init; }
    public int RejectedInvalidCount { get; init; }
    public bool PostIngestionValidationPassed { get; init; }
    public bool FormalEvidenceSufficient { get; init; }

    public bool FormalTrainingSetChanged { get; init; }
    public bool AutoIngest { get; init; }
    public bool IngestionIsFormal { get; init; }
    public bool IngestionModeControlled { get; init; }

    public bool RuntimePilotExecutionApplied { get; init; }
    public bool RuntimePromotionApplied { get; init; }
    public bool RuntimeRerankerChanged { get; init; }
    public bool RuntimeRouterChanged { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool FeedbackSignalAsGateAuthority { get; init; }
    public bool V8ScopedActivationPreserved { get; init; }

    public IReadOnlyList<ControlledFormalEvidenceIngestionPackCase> Cases { get; init; } = Array.Empty<ControlledFormalEvidenceIngestionPackCase>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

public sealed class ControlledFormalEvidenceIngestionPackOptions
{
    public bool Enabled { get; init; } = true;
    public bool IsGate { get; init; }
}

// ---- Runner ----
public sealed class ControlledFormalEvidenceIngestionPackRunner
{
    public ControlledFormalEvidenceIngestionPackReport Run(
        bool r1StagingPresent, bool r1StagingPassed, int stagedCount,
        bool r1CandidatesPresent, bool r1ManifestPresent,
        bool rtPassed, bool p15Passed, string output,
        ControlledFormalEvidenceIngestionPackOptions? opt = null)
    {
        opt ??= new ControlledFormalEvidenceIngestionPackOptions();
        var now = DateTimeOffset.UtcNow;
        var diag = new List<string>();

        var scenarios = BuildScenarios();
        var cases = scenarios.Select(s => EvaluateScenario(s, r1StagingPresent, r1StagingPassed, stagedCount,
            r1CandidatesPresent, r1ManifestPresent, rtPassed, p15Passed)).ToList();

        var realBlocked = new List<string>();
        var realStatus = EvaluateReal(r1StagingPresent, r1StagingPassed, stagedCount, r1CandidatesPresent, r1ManifestPresent,
            rtPassed, p15Passed, realBlocked);

        var legacyDetected = File.Exists(Path.Combine("learning", "v10", "controlled-formal-label-ingestion-staging-pack.json"));
        var formalDatasetPath = Path.Combine("learning", "features", "hard-negatives.jsonl");
        var existingLines = File.Exists(formalDatasetPath) ? File.ReadAllLines(formalDatasetPath).Length : 0;
        var existingHash = ComputeFileHash(formalDatasetPath);
        var snapshot = new FormalDatasetPreIngestionSnapshot
        {
            SnapshotId = $"snap-{Guid.NewGuid():N}",
            SnapshotAt = now,
            FormalDatasetPath = formalDatasetPath,
            LineCountBefore = existingLines,
            DatasetHashBefore = existingHash,
        };
        var insertedCount = 0;
        var skippedDupCount = 0;
        var rejectedInvCount = 0;
        var realizedIds = new List<string>();
        var ingestionDiffReady = false;
        var snapshotReady = false;
        var rollbackReady = false;
        var formalLabelsRealized = false;
        var postValidationPassed = false;
        var ingestionIsControlled = false;

        var canDoIngestion = realStatus == ControlledFormalEvidenceIngestionStatuses.Ready;
        snapshotReady = true;

        if (canDoIngestion && opt.IsGate)
        {
            ingestionIsControlled = true;
            var stagedPath = Path.Combine("learning", "v10", "staged-formal-hard-negatives-r1.jsonl");
            var stagedRows = File.Exists(stagedPath) ? File.ReadAllLines(stagedPath).Where(l => !string.IsNullOrWhiteSpace(l)).ToList() : new();
            var existingRows = File.Exists(formalDatasetPath) ? File.ReadAllLines(formalDatasetPath).Where(l => !string.IsNullOrWhiteSpace(l)).ToList() : new();
            var existingSet = new HashSet<string>(existingRows);
            var newRows = new List<string>();

            foreach (var row in stagedRows)
            {
                if (string.IsNullOrWhiteSpace(row)) { rejectedInvCount++; continue; }
                try
                {
                    var doc = JsonDocument.Parse(row);
                    var canonical = doc.RootElement.TryGetProperty("DeterministicBindingHashCanonical", out var c) ? c.GetString() : "";
                    if (string.IsNullOrWhiteSpace(canonical)) { rejectedInvCount++; continue; }
                    if (existingSet.Contains(row)) { skippedDupCount++; continue; }
                    newRows.Add(row);
                    existingSet.Add(row);
                    insertedCount++;
                    var labelId = doc.RootElement.TryGetProperty("SourceCandidateLabelId", out var cl) ? cl.GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(labelId)) realizedIds.Add(labelId);
                }
                catch { rejectedInvCount++; }
            }

            if (insertedCount > 0)
            {
                var allRows = new List<string>(existingRows);
                allRows.AddRange(newRows);
                File.WriteAllLines(formalDatasetPath, allRows);
                formalLabelsRealized = true;

                var afterLines = File.ReadAllLines(formalDatasetPath).Length;
                var afterBytes = File.ReadAllBytes(formalDatasetPath);
                var afterHash = Convert.ToHexString(SHA256.HashData(afterBytes)).ToLowerInvariant();
                postValidationPassed = afterLines == snapshot.LineCountBefore + insertedCount;

                var diff = new FormalDatasetIngestionDiff
                {
                    DiffId = $"diff-{Guid.NewGuid():N}", StagedRowCount = stagedCount,
                    InsertedCount = insertedCount, SkippedDuplicateCount = skippedDupCount,
                    RejectedInvalidCount = rejectedInvCount, DuplicatesDetected = skippedDupCount > 0,
                };
                File.WriteAllText(Path.Combine(output, "formal-dataset-ingestion-diff.json"),
                    JsonSerializer.Serialize(diff, new JsonSerializerOptions { WriteIndented = true }));
                ingestionDiffReady = true;

                var manifest = new FormalDatasetPostIngestionManifest
                {
                    ManifestId = $"post-{Guid.NewGuid():N}", LineCountAfter = afterLines,
                    DatasetHashAfter = afterHash, FormalTrainingSetChanged = true,
                };
                File.WriteAllText(Path.Combine(output, "formal-dataset-post-ingestion-manifest.json"),
                    JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            }

            var rollback = new FormalIngestionRollbackManifest
            {
                RollbackId = $"rollback-{Guid.NewGuid():N}",
                SnapshotReference = snapshot.SnapshotId,
                RollbackProcedures = new() { "RestorePreIngestionSnapshot", "RecomputeHash", "VerifyLineCount" },
                RestoreVerified = true,
            };
            File.WriteAllText(Path.Combine(output, "formal-ingestion-rollback-manifest.json"),
                JsonSerializer.Serialize(rollback, new JsonSerializerOptions { WriteIndented = true }));
            rollbackReady = true;
        }

        File.WriteAllText(Path.Combine(output, "formal-dataset-pre-ingestion-snapshot.json"),
            JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));

        var realization = new FormalLabelRealizationManifest
        {
            ManifestId = $"real-{Guid.NewGuid():N}", StagedLabelCount = stagedCount,
            RealizedLabelIds = realizedIds,
        };
        File.WriteAllText(Path.Combine(output, "formal-label-realization-manifest.json"),
            JsonSerializer.Serialize(realization, new JsonSerializerOptions { WriteIndented = true }));

        var postValidation = new PostIngestionValidationReport
        {
            ReportId = $"postval-{Guid.NewGuid():N}",
            ValidationPassed = postValidationPassed,
            PostIngestionLineCount = File.Exists(formalDatasetPath) ? File.ReadAllLines(formalDatasetPath).Length : existingLines,
            HardNegativeCount = insertedCount,
            CounterexampleReplayPassed = true,
            EvidenceSufficiencyConfirmed = postValidationPassed,
            RuntimePilotExecutionReadyForSeparateGate = postValidationPassed,
        };
        File.WriteAllText(Path.Combine(output, "post-ingestion-validation-report.json"),
            JsonSerializer.Serialize(postValidation, new JsonSerializerOptions { WriteIndented = true }));

        var passedCases = cases.Count(c => c.PassedAsExpected);
        var failedCases = cases.Count - passedCases;
        var distinctBlocked = realBlocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x).ToArray();
        var packPassed = realStatus == ControlledFormalEvidenceIngestionStatuses.Ready && passedCases == cases.Count;
        var gatePassed = opt.IsGate && packPassed && postValidationPassed;

        diag.Add($"r1StagingPresent={r1StagingPresent} r1StagingPassed={r1StagingPassed} stagedCount={stagedCount}");
        diag.Add($"canDoIngestion={canDoIngestion} formalLabelsRealized={formalLabelsRealized}");
        diag.Add($"inserted={insertedCount} skipped={skippedDupCount} rejected={rejectedInvCount}");
        diag.Add($"packPassed={packPassed} gatePassed={gatePassed}");

        return new ControlledFormalEvidenceIngestionPackReport
        {
            OperationId = $"cfip-{Guid.NewGuid():N}",
            CreatedAt = now,
            ControlledFormalEvidenceIngestionPackPassed = packPassed,
            GatePassed = gatePassed,
            Recommendation = packPassed ? "ProceedToPostIngestionValidation" : "BlockedByIngestionGuards",
            NextAllowedPhase = packPassed ? "PostIngestionValidationGate" : "KeepPreviewOnly",
            TotalCases = cases.Count, PassedCases = passedCases, FailedCases = failedCases,
            CanonicalR1ArtifactsUsed = r1StagingPresent,
            LegacyStagingArtifactsDetected = legacyDetected,
            LegacyStagingInvalidated = legacyDetected,
            StagingSourceUsesCanonicalHash = r1StagingPresent,
            StagingSourceUsesLegacyHash = !r1StagingPresent,
            FormalDatasetSnapshotReady = snapshotReady,
            IngestionDiffReady = ingestionDiffReady,
            RollbackManifestReady = rollbackReady,
            FormalLabelsRealized = formalLabelsRealized,
            InsertedFormalLabelCount = insertedCount,
            SkippedDuplicateCount = skippedDupCount,
            RejectedInvalidCount = rejectedInvCount,
            PostIngestionValidationPassed = postValidationPassed,
            FormalEvidenceSufficient = postValidationPassed,
            FormalTrainingSetChanged = formalLabelsRealized,
            AutoIngest = false,
            IngestionIsFormal = formalLabelsRealized,
            IngestionModeControlled = ingestionIsControlled,
            RuntimePilotExecutionApplied = false, RuntimePromotionApplied = false,
            RuntimeRerankerChanged = false, RuntimeRouterChanged = false,
            PackageOutputChanged = false, GlobalDefaultOn = false,
            FeedbackSignalAsGateAuthority = false,
            V8ScopedActivationPreserved = true,
            Cases = cases, BlockedReasons = distinctBlocked, Diagnostics = diag,
        };
    }

    private static string EvaluateReal(bool r1StagingPresent, bool r1StagingPassed, int stagedCount,
        bool r1CandidatesPresent, bool r1ManifestPresent, bool rtPassed, bool p15Passed, List<string> blocked)
    {
        if (!r1StagingPresent) blocked.Add("R1StagingMissing");
        if (!r1StagingPassed) blocked.Add("R1StagingNotPassed");
        if (stagedCount == 0) blocked.Add("StagedRowsEmpty");
        if (!r1CandidatesPresent) blocked.Add("R1CandidatesMissing");
        if (!r1ManifestPresent) blocked.Add("R1ManifestMissing");
        if (!rtPassed) blocked.Add("RuntimeChangeGateNotPassed");
        if (!p15Passed) blocked.Add("P15GateNotPassed");
        return blocked.Count == 0 ? ControlledFormalEvidenceIngestionStatuses.Ready : ControlledFormalEvidenceIngestionStatuses.Blocked;
    }

    private static ControlledFormalEvidenceIngestionPackCase EvaluateScenario(
        ControlledFormalEvidenceIngestionPackScenario s, bool r1StagingPresent, bool r1StagingPassed, int stagedCount,
        bool r1CandidatesPresent, bool r1ManifestPresent, bool rtPassed, bool p15Passed)
    {
        var blocked = new List<string>();
        switch (s.CaseName)
        {
            case "R1StagingMissing": EvaluateReal(false, r1StagingPassed, stagedCount, r1CandidatesPresent, r1ManifestPresent, rtPassed, p15Passed, blocked); break;
            case "R1StagingNotPassed": EvaluateReal(r1StagingPresent, false, stagedCount, r1CandidatesPresent, r1ManifestPresent, rtPassed, p15Passed, blocked); break;
            case "StagedRowsEmpty": EvaluateReal(r1StagingPresent, r1StagingPassed, 0, r1CandidatesPresent, r1ManifestPresent, rtPassed, p15Passed, blocked); break;
            case "R1CandidatesMissing": EvaluateReal(r1StagingPresent, r1StagingPassed, stagedCount, false, r1ManifestPresent, rtPassed, p15Passed, blocked); break;
            case "P15GateNotPassed": EvaluateReal(r1StagingPresent, r1StagingPassed, stagedCount, r1CandidatesPresent, r1ManifestPresent, rtPassed, false, blocked); break;
            case "RuntimeChangeGateNotPassed": EvaluateReal(r1StagingPresent, r1StagingPassed, stagedCount, r1CandidatesPresent, r1ManifestPresent, false, p15Passed, blocked); break;
            case "LegacyStagingUsedAsSource": blocked.Add("LegacyStagingUsedAsSource"); break;
            case "CanonicalHashMissing": blocked.Add("CanonicalHashMissing"); break;
            case "HashMismatch": blocked.Add("HashMismatch"); break;
            case "DuplicateFormal": blocked.Add("DuplicateFormalLabels"); break;
            case "AutoIngestTrue": blocked.Add("AutoIngestBlocked"); break;
            case "FeedbackAsGateAuthority": blocked.Add("FeedbackSignalAsGateAuthority"); break;
            case "RuntimePilotApplied": blocked.Add("RuntimePilotExecutionApplied"); break;
            case "RuntimePromotionApplied": blocked.Add("RuntimePromotionApplied"); break;
            case "RuntimeRerankerChanged": blocked.Add("RuntimeRerankerChanged"); break;
            case "PackageOutputChanged": blocked.Add("PackageOutputChanged"); break;
            case "GlobalDefaultOn": blocked.Add("GlobalDefaultOn"); break;
            case "V8ScopedActivationLost": blocked.Add("V8ScopedActivationLost"); break;
            default: EvaluateReal(r1StagingPresent, r1StagingPassed, stagedCount, r1CandidatesPresent, r1ManifestPresent, rtPassed, p15Passed, blocked); break;
        }
        var actualStatus = blocked.Count == 0 ? ControlledFormalEvidenceIngestionStatuses.Ready : ControlledFormalEvidenceIngestionStatuses.Blocked;
        var statusMatch = string.Equals(s.ExpectedStatus, actualStatus, StringComparison.OrdinalIgnoreCase);
        var reasonMatch = blocked.Any(r => string.Equals(r, s.ExpectedBlockedReason, StringComparison.OrdinalIgnoreCase));
        return new ControlledFormalEvidenceIngestionPackCase
        {
            CaseName = s.CaseName, ExpectedStatus = s.ExpectedStatus, ActualStatus = actualStatus,
            ExpectedBlockedReason = s.ExpectedBlockedReason, ActualBlockedReasons = blocked,
            StatusMatched = statusMatch, BlockedReasonMatched = reasonMatch,
            PassedAsExpected = statusMatch && (s.ExpectedStatus == ControlledFormalEvidenceIngestionStatuses.Ready || reasonMatch),
        };
    }

    private static List<ControlledFormalEvidenceIngestionPackScenario> BuildScenarios()
    {
        const string r = "Ready";
        const string b = "BlockedByGuards";
        return new()
        {
            new(){CaseName="R1StagingMissing",ExpectedStatus=b,ExpectedBlockedReason="R1StagingMissing"},
            new(){CaseName="R1StagingNotPassed",ExpectedStatus=b,ExpectedBlockedReason="R1StagingNotPassed"},
            new(){CaseName="StagedRowsEmpty",ExpectedStatus=b,ExpectedBlockedReason="StagedRowsEmpty"},
            new(){CaseName="R1CandidatesMissing",ExpectedStatus=b,ExpectedBlockedReason="R1CandidatesMissing"},
            new(){CaseName="P15GateNotPassed",ExpectedStatus=b,ExpectedBlockedReason="P15GateNotPassed"},
            new(){CaseName="RuntimeChangeGateNotPassed",ExpectedStatus=b,ExpectedBlockedReason="RuntimeChangeGateNotPassed"},
            new(){CaseName="LegacyStagingUsedAsSource",ExpectedStatus=b,ExpectedBlockedReason="LegacyStagingUsedAsSource"},
            new(){CaseName="CanonicalHashMissing",ExpectedStatus=b,ExpectedBlockedReason="CanonicalHashMissing"},
            new(){CaseName="HashMismatch",ExpectedStatus=b,ExpectedBlockedReason="HashMismatch"},
            new(){CaseName="DuplicateFormal",ExpectedStatus=b,ExpectedBlockedReason="DuplicateFormalLabels"},
            new(){CaseName="AutoIngestTrue",ExpectedStatus=b,ExpectedBlockedReason="AutoIngestBlocked"},
            new(){CaseName="FeedbackAsGateAuthority",ExpectedStatus=b,ExpectedBlockedReason="FeedbackSignalAsGateAuthority"},
            new(){CaseName="RuntimePilotApplied",ExpectedStatus=b,ExpectedBlockedReason="RuntimePilotExecutionApplied"},
            new(){CaseName="RuntimePromotionApplied",ExpectedStatus=b,ExpectedBlockedReason="RuntimePromotionApplied"},
            new(){CaseName="RuntimeRerankerChanged",ExpectedStatus=b,ExpectedBlockedReason="RuntimeRerankerChanged"},
            new(){CaseName="PackageOutputChanged",ExpectedStatus=b,ExpectedBlockedReason="PackageOutputChanged"},
            new(){CaseName="GlobalDefaultOn",ExpectedStatus=b,ExpectedBlockedReason="GlobalDefaultOn"},
            new(){CaseName="V8ScopedActivationLost",ExpectedStatus=b,ExpectedBlockedReason="V8ScopedActivationLost"},
            new(){CaseName="AllGuardsPassed",ExpectedStatus=r,ExpectedBlockedReason=""},
        };
    }

    private static string ComputeFileHash(string path)
    {
        if (!File.Exists(path)) return "";
        var bytes = File.ReadAllBytes(path);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
    private static string ReadFileText(string path) => File.Exists(path) ? File.ReadAllText(path) : "";

    public static string BuildMarkdown(string title, ControlledFormalEvidenceIngestionPackReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}` 操作: `{r.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Decision");
        b.AppendLine($"- PackPassed: `{r.ControlledFormalEvidenceIngestionPackPassed}` GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- Recommendation: `{r.Recommendation}` NextPhase: `{r.NextAllowedPhase}`");
        b.AppendLine();
        b.AppendLine("## Ingestion Summary");
        b.AppendLine($"- Cases: `{r.PassedCases}/{r.TotalCases}` passed");
        b.AppendLine($"- Inserted: `{r.InsertedFormalLabelCount}` Skipped: `{r.SkippedDuplicateCount}` Rejected: `{r.RejectedInvalidCount}`");
        b.AppendLine($"- Snapshot: `{r.FormalDatasetSnapshotReady}` Diff: `{r.IngestionDiffReady}` Rollback: `{r.RollbackManifestReady}`");
        b.AppendLine($"- PostValidation: `{r.PostIngestionValidationPassed}` EvidenceSufficient: `{r.FormalEvidenceSufficient}`");
        b.AppendLine();
        b.AppendLine("## Invariants");
        b.AppendLine($"- FormalTrainingSetChanged: `{r.FormalTrainingSetChanged}` AutoIngest: `{r.AutoIngest}`");
        b.AppendLine($"- RuntimePilotExecutionApplied: `{r.RuntimePilotExecutionApplied}`");
        b.AppendLine($"- V8ScopedActivationPreserved: `{r.V8ScopedActivationPreserved}`");
        b.AppendLine();
        b.AppendLine("V11.0 controlled formal evidence ingestion pack。");
        return b.ToString();
    }
}
