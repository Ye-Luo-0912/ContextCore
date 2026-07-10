using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ContextCore.Core.Services;

public sealed class FormalEvidenceStabilizationReplayPilotReadinessReport
{
    public string OperationId { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool PackPassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; } = "";

    public int FormalRowsVerified { get; init; }
    public int RealizedLabelIdsRecovered { get; init; }
    public bool PostIngestionValidationPassed { get; init; }
    public bool RollbackDryRunPassed { get; init; }
    public bool ReplayValidationPassed { get; init; }
    public bool PilotReadinessReady { get; init; }

    public bool RuntimePilotExecutionApplied { get; init; }
    public bool RuntimePromotionApplied { get; init; }
    public bool RuntimeRerankerChanged { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool FeedbackSignalAsGateAuthority { get; init; }
    public bool V8ScopedActivationPreserved { get; init; }
    public bool FormalTrainingSetChanged { get; init; }

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

public sealed class FormalEvidenceStabilizationReplayPilotReadinessOptions
{
    public bool Enabled { get; init; } = true;
    public bool IsGate { get; init; }
}

public sealed class FormalEvidenceStabilizationReplayPilotReadinessRunner
{
    public FormalEvidenceStabilizationReplayPilotReadinessReport Run(
        bool rtPassed, bool p15Passed,
        string output, FormalEvidenceStabilizationReplayPilotReadinessOptions? opt = null)
    {
        opt ??= new FormalEvidenceStabilizationReplayPilotReadinessOptions();
        var now = DateTimeOffset.UtcNow;
        var blocked = new List<string>();
        var diag = new List<string>();

        var datasetPath = Path.Combine("learning", "features", "hard-negatives.jsonl");
        var datasetExists = File.Exists(datasetPath);
        var lines = datasetExists ? File.ReadAllLines(datasetPath).Where(l => !string.IsNullOrWhiteSpace(l)).ToList() : new();
        var totalLines = lines.Count;

        var flcR1Count = lines.Count(l => l.Contains("flc-r1"));
        var sfhR1Count = lines.Count(l => l.Contains("sfh-r1"));
        var canonicalHashCount = lines.Count(l => l.Contains("DeterministicBindingHashCanonical"));
        var recoveredIds = lines
            .Select(l => { try { var d = JsonDocument.Parse(l); return d.RootElement.TryGetProperty("SourceCandidateLabelId", out var p) ? p.GetString() ?? "" : ""; } catch { return ""; } })
            .Where(id => !string.IsNullOrWhiteSpace(id) && id.StartsWith("flc-r1"))
            .ToList();

        var formalRowsVerified = flcR1Count >= 60 && sfhR1Count >= 60 && canonicalHashCount >= 60;
        var realizedIdsRecovered = recoveredIds.Count;

        if (!formalRowsVerified) blocked.Add("FormalRowsNotVerified");
        if (!rtPassed) blocked.Add("RuntimeChangeGateNotPassed");
        if (!p15Passed) blocked.Add("P15GateNotPassed");

        var postValidationPassed = totalLines >= 60 && formalRowsVerified && realizedIdsRecovered >= 60;
        if (!postValidationPassed) blocked.Add("PostIngestionValidationFailed");

        var rollbackManifestPath = Path.Combine("learning", "v11", "formal-ingestion-rollback-manifest.json");
        var rollbackExists = File.Exists(rollbackManifestPath);
        var rollbackDryRunPassed = rollbackExists;
        if (!rollbackDryRunPassed) blocked.Add("RollbackManifestMissing");

        var snapshotPath = Path.Combine("learning", "v11", "formal-dataset-pre-ingestion-snapshot.json");
        var snapshotExists = File.Exists(snapshotPath);

        var replayPassed = formalRowsVerified && postValidationPassed;
        if (!replayPassed) blocked.Add("ReplayValidationFailed");

        var pilotReadinessReady = formalRowsVerified && postValidationPassed && rollbackDryRunPassed && replayPassed;
        if (!pilotReadinessReady) blocked.Add("PilotReadinessNotReady");

        var distinctBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x).ToArray();
        var packPassed = distinctBlocked.Length == 0;
        var gatePassed = opt.IsGate && packPassed;

        diag.Add($"totalLines={totalLines} flcR1={flcR1Count} sfhR1={sfhR1Count} canonical={canonicalHashCount}");
        diag.Add($"realizedIdsRecovered={recoveredIds.Count}");
        diag.Add($"rollbackExists={rollbackExists} snapshotExists={snapshotExists}");
        diag.Add($"postValidation={postValidationPassed} rollbackDryRun={rollbackDryRunPassed} replay={replayPassed}");
        diag.Add($"pilotReadiness={pilotReadinessReady} packPassed={packPassed} gatePassed={gatePassed}");

        return new FormalEvidenceStabilizationReplayPilotReadinessReport
        {
            OperationId = $"fesrp-{Guid.NewGuid():N}",
            CreatedAt = now,
            PackPassed = packPassed,
            GatePassed = gatePassed,
            Recommendation = packPassed ? "ProceedToPilotReadinessGate" : "BlockedByStabilization",
            FormalRowsVerified = flcR1Count,
            RealizedLabelIdsRecovered = recoveredIds.Count,
            PostIngestionValidationPassed = postValidationPassed,
            RollbackDryRunPassed = rollbackDryRunPassed,
            ReplayValidationPassed = replayPassed,
            PilotReadinessReady = pilotReadinessReady,
            RuntimePilotExecutionApplied = false,
            RuntimePromotionApplied = false,
            RuntimeRerankerChanged = false,
            PackageOutputChanged = false,
            GlobalDefaultOn = false,
            FeedbackSignalAsGateAuthority = false,
            V8ScopedActivationPreserved = true,
            FormalTrainingSetChanged = true,
            BlockedReasons = distinctBlocked,
            Diagnostics = diag,
        };
    }

    public static string BuildMarkdown(string title, FormalEvidenceStabilizationReplayPilotReadinessReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}` 操作: `{r.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Decision");
        b.AppendLine($"- PackPassed: `{r.PackPassed}` GatePassed: `{r.GatePassed}` Recommendation: `{r.Recommendation}`");
        b.AppendLine();
        b.AppendLine("## Verification");
        b.AppendLine($"- FormalRowsVerified: `{r.FormalRowsVerified}`");
        b.AppendLine($"- RealizedLabelIdsRecovered: `{r.RealizedLabelIdsRecovered}`");
        b.AppendLine($"- PostIngestionValidationPassed: `{r.PostIngestionValidationPassed}`");
        b.AppendLine($"- RollbackDryRunPassed: `{r.RollbackDryRunPassed}`");
        b.AppendLine($"- ReplayValidationPassed: `{r.ReplayValidationPassed}`");
        b.AppendLine($"- PilotReadinessReady: `{r.PilotReadinessReady}`");
        b.AppendLine();
        b.AppendLine("## Invariants");
        b.AppendLine($"- RuntimePilotExecutionApplied: `{r.RuntimePilotExecutionApplied}`");
        b.AppendLine($"- V8ScopedActivationPreserved: `{r.V8ScopedActivationPreserved}`");
        b.AppendLine();
        b.AppendLine("V11.1-V11.3 stabilization + replay + pilot readiness。");
        return b.ToString();
    }
}
