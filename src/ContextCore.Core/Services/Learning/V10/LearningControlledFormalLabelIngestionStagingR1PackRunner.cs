using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ContextCore.Core.Services;

public static class LearningControlledFormalLabelIngestionStagingR1PackStatuses
{
    public const string Ready = nameof(Ready);
    public const string Blocked = nameof(Blocked);
}

public static class LearningControlledFormalLabelIngestionStagingR1PackBlockedReasons
{
    public const string R1RealizationPackMissing = nameof(R1RealizationPackMissing);
    public const string R1CandidatesMissing = nameof(R1CandidatesMissing);
    public const string R1ManifestMissing = nameof(R1ManifestMissing);
    public const string LegacyStagingInvalidated = nameof(LegacyStagingInvalidated);
    public const string StagingSourceUsesLegacyHash = nameof(StagingSourceUsesLegacyHash);
    public const string CanonicalHashMismatch = nameof(CanonicalHashMismatch);
    public const string HumanReviewAsMainRecommendation = nameof(HumanReviewAsMainRecommendation);
    public const string P15GateNotPassed = nameof(P15GateNotPassed);
    public const string RuntimeChangeGateNotPassed = nameof(RuntimeChangeGateNotPassed);
}

public sealed class StagedFormalHardNegativeR1Row
{
    public string StagedLabelId { get; init; } = "";
    public string SourceCandidateLabelId { get; init; } = "";
    public string SourceShadowLabelId { get; init; } = "";
    public string SourceCandidateSpecId { get; init; } = "";
    public string EvidencePath { get; init; } = "";
    public string ExpectedPreference { get; init; } = "PositiveOverNegative";
    public string RankingPairRowHash { get; init; } = "";
    public string ShadowLabelHash { get; init; } = "";
    public string DeterministicBindingHashCanonical { get; init; } = "";
    public string LegacySpecBindingHash { get; init; } = "";
    public string HashInputVersion { get; init; } = "v10.16R/canonical-v1";
    public string LifecycleState { get; init; } = "Staged";
    public bool StagedLabelIsFormal { get; init; }
    public bool StagingOnly { get; init; } = true;
    public bool AutoIngest { get; init; }
    public string PolicyVersion { get; init; } = "v10.19R/staged-formal-hard-negative-r1/v1";
    public DateTimeOffset StagedAt { get; init; }
}

public sealed class LearningControlledFormalLabelIngestionStagingR1PackReport
{
    public string OperationId { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool ControlledFormalLabelIngestionStagingR1PackPassed { get; init; }
    public bool GatePassed { get; init; }
    public int TotalCases { get; init; }
    public int PassedCases { get; init; }
    public int FailedCases { get; init; }
    public int StagedFormalLabelCount { get; init; }
    public int InvalidCandidateCount { get; init; }
    public int HashMismatchCount { get; init; }
    public string Recommendation { get; init; } = "KeepPreviewOnly";
    public string NextAllowedPhase { get; init; } = "KeepPreviewOnly";

    public bool StagingSourceUsesLegacyHash { get; init; }
    public bool LegacyStagingInvalidated { get; init; }
    public string SourceCandidateLabelPrefix { get; init; } = "";
    public double CanonicalHashCoverage { get; init; }
    public double RankingPairRowHashCoverage { get; init; }
    public double ShadowLabelHashCoverage { get; init; }
    public string HashInputVersion { get; init; } = "";
    public bool MainRecommendationUsesHumanReview { get; init; }
    public bool StagedLabelsAreFormal { get; init; }
    public bool FormalTrainingSetChanged { get; init; }
    public bool AutoIngest { get; init; }
    public bool StagingOnly { get; init; } = true;
    public bool RuntimePilotExecutionApplied { get; init; }
    public bool RuntimePromotionApplied { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool V8ScopedActivationPreserved { get; init; }
    public bool FormalRetrievalAllowed { get; init; }

    public IReadOnlyList<string> DecisionNotes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
    public IReadOnlyList<StagedFormalHardNegativeR1Row> StagedRows { get; init; } = Array.Empty<StagedFormalHardNegativeR1Row>();
}

public sealed class LearningControlledFormalLabelIngestionStagingR1PackOptions
{
    public bool Enabled { get; init; } = true;
    public bool IsGate { get; init; }
}

public sealed class LearningControlledFormalLabelIngestionStagingR1PackRunner
{
    public LearningControlledFormalLabelIngestionStagingR1PackReport Run(
        List<JsonDocument> r1Candidates,
        bool r1RealizationPresent, bool r1ManifestPresent,
        bool rtPassed, bool p15Passed,
        string output, LearningControlledFormalLabelIngestionStagingR1PackOptions? opt = null)
    {
        opt ??= new LearningControlledFormalLabelIngestionStagingR1PackOptions();
        var now = DateTimeOffset.UtcNow;
        var blocked = new List<string>();
        var diag = new List<string>();
        var notes = new List<string>();

        if (!opt.Enabled) blocked.Add("R1PackDisabled");
        if (!r1RealizationPresent) blocked.Add(LearningControlledFormalLabelIngestionStagingR1PackBlockedReasons.R1RealizationPackMissing);
        if (r1Candidates.Count == 0) blocked.Add(LearningControlledFormalLabelIngestionStagingR1PackBlockedReasons.R1CandidatesMissing);
        if (!r1ManifestPresent) blocked.Add(LearningControlledFormalLabelIngestionStagingR1PackBlockedReasons.R1ManifestMissing);
        if (!p15Passed) blocked.Add(LearningControlledFormalLabelIngestionStagingR1PackBlockedReasons.P15GateNotPassed);
        if (!rtPassed) blocked.Add(LearningControlledFormalLabelIngestionStagingR1PackBlockedReasons.RuntimeChangeGateNotPassed);

        var legacyStagingExists = File.Exists(Path.Combine("learning", "v10", "controlled-formal-label-ingestion-staging-pack.json"));
        var legacyCandidatesExist = File.Exists(Path.Combine("learning", "v10", "formal-label-candidates.jsonl"));
        var stagingSourceUsesLegacyHash = legacyStagingExists || legacyCandidatesExist;
        if (stagingSourceUsesLegacyHash)
        {
            notes.Add("Legacy staging pack found and invalidated. Use R1 artifacts only.");
        }

        var stagedRows = new List<StagedFormalHardNegativeR1Row>();
        var invalidCount = 0;
        var hashMismatchCount = 0;
        var canonicalHashCount = 0;
        var rankingHashCount = 0;
        var shadowHashCount = 0;
        var hasFlcR1Prefix = 0;

        foreach (var doc in r1Candidates)
        {
            var root = doc.RootElement;
            var canonicalHash = root.TryGetProperty("DeterministicBindingHashCanonical", out var ch) ? ch.GetString() ?? "" : "";
            var legacyHash = root.TryGetProperty("LegacySpecBindingHash", out var lh) ? lh.GetString() ?? "" : "";
            var rankingHash = root.TryGetProperty("RankingPairRowHash", out var rh) ? rh.GetString() ?? "" : "";
            var shadowHash = root.TryGetProperty("ShadowLabelHash", out var sh) ? sh.GetString() ?? "" : "";
            var candidateLabelId = root.TryGetProperty("CandidateLabelId", out var clid) ? clid.GetString() ?? "" : "";
            var shadowLabelId = root.TryGetProperty("SourceShadowLabelId", out var sl) ? sl.GetString() ?? "" : "";
            var specId = root.TryGetProperty("SourceCandidateSpecId", out var sc) ? sc.GetString() ?? "" : "";
            var evidencePath = root.TryGetProperty("EvidencePath", out var ep) ? ep.GetString() ?? "" : "";
            var eligible = root.TryGetProperty("PromotionEligibility", out var pe) && pe.GetString() == "Eligible";

            if (!string.IsNullOrWhiteSpace(canonicalHash)) canonicalHashCount++;
            if (!string.IsNullOrWhiteSpace(rankingHash)) rankingHashCount++;
            if (!string.IsNullOrWhiteSpace(shadowHash)) shadowHashCount++;
            if (candidateLabelId.StartsWith("flc-r1-", StringComparison.OrdinalIgnoreCase)) hasFlcR1Prefix++;

            if (!eligible || string.IsNullOrWhiteSpace(canonicalHash))
            {
                invalidCount++;
                continue;
            }

            var canonicalIntegrityRaw = root.TryGetProperty("CanonicalIntegrityVerified", out var civ);
            var integrityVerified = canonicalIntegrityRaw && civ.ValueKind == System.Text.Json.JsonValueKind.True;
            if (!integrityVerified)
            {
                hashMismatchCount++;
                continue;
            }

            stagedRows.Add(new StagedFormalHardNegativeR1Row
            {
                StagedLabelId = $"sfh-r1-{stagedRows.Count:D4}-{shadowLabelId}",
                SourceCandidateLabelId = candidateLabelId,
                SourceShadowLabelId = shadowLabelId,
                SourceCandidateSpecId = specId,
                EvidencePath = evidencePath,
                RankingPairRowHash = rankingHash,
                ShadowLabelHash = shadowHash,
                DeterministicBindingHashCanonical = canonicalHash,
                LegacySpecBindingHash = legacyHash,
                HashInputVersion = "v10.16R/canonical-v1",
                LifecycleState = "Staged",
                StagedLabelIsFormal = false,
                StagingOnly = true,
                AutoIngest = false,
                StagedAt = now,
            });
        }

        if (hashMismatchCount > 0)
            blocked.Add(LearningControlledFormalLabelIngestionStagingR1PackBlockedReasons.CanonicalHashMismatch);

        var stagedCount = stagedRows.Count;
        var canonicalCoverage = r1Candidates.Count > 0 ? (double)canonicalHashCount / r1Candidates.Count * 100.0 : 0;
        var rankingCoverage = r1Candidates.Count > 0 ? (double)rankingHashCount / r1Candidates.Count * 100.0 : 0;
        var shadowCoverage = r1Candidates.Count > 0 ? (double)shadowHashCount / r1Candidates.Count * 100.0 : 0;

        var distinctBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var packPassed = distinctBlocked.Length == 0;
        var gatePassed = opt.IsGate && packPassed;

        diag.Add($"r1Candidates={r1Candidates.Count}");
        diag.Add($"eligibleStaged={stagedCount} invalid={invalidCount} hashMismatch={hashMismatchCount}");
        diag.Add($"canonicalHashCoverage={canonicalCoverage:F0}% rankingHashCoverage={rankingCoverage:F0}% shadowHashCoverage={shadowCoverage:F0}%");
        diag.Add($"flcR1PrefixCount={hasFlcR1Prefix}/{r1Candidates.Count}");
        diag.Add($"legacyStagingExists={legacyStagingExists}");
        diag.Add($"packPassed={packPassed} gatePassed={gatePassed}");

        var stagingRowsPath = Path.Combine("learning", "v10", "staged-formal-hard-negatives-r1.jsonl");
        if (stagedRows.Count > 0 && packPassed)
            File.WriteAllLines(stagingRowsPath, stagedRows.Select(r =>
                JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = false })));

        return new LearningControlledFormalLabelIngestionStagingR1PackReport
        {
            OperationId = $"flc-staging-r1-{Guid.NewGuid():N}",
            CreatedAt = now,
            ControlledFormalLabelIngestionStagingR1PackPassed = packPassed,
            GatePassed = gatePassed,
            TotalCases = r1Candidates.Count,
            PassedCases = stagedCount,
            FailedCases = invalidCount + hashMismatchCount,
            StagedFormalLabelCount = stagedCount,
            InvalidCandidateCount = invalidCount,
            HashMismatchCount = hashMismatchCount,
            Recommendation = packPassed ? "ProceedToControlledFormalEvidenceIngestion" : "BlockedByR1StagingValidation",
            NextAllowedPhase = packPassed ? "ControlledFormalEvidenceIngestion-pending-canonical-hash-v2" : "KeepPreviewOnly",
            StagingSourceUsesLegacyHash = stagingSourceUsesLegacyHash,
            LegacyStagingInvalidated = stagingSourceUsesLegacyHash,
            SourceCandidateLabelPrefix = "flc-r1",
            CanonicalHashCoverage = canonicalCoverage,
            RankingPairRowHashCoverage = rankingCoverage,
            ShadowLabelHashCoverage = shadowCoverage,
            HashInputVersion = "v10.16R/canonical-v1",
            MainRecommendationUsesHumanReview = false,
            StagedLabelsAreFormal = false,
            FormalTrainingSetChanged = false,
            AutoIngest = false,
            StagingOnly = true,
            RuntimePilotExecutionApplied = false,
            RuntimePromotionApplied = false,
            PackageOutputChanged = false,
            V8ScopedActivationPreserved = true,
            FormalRetrievalAllowed = false,
            DecisionNotes = notes,
            BlockedReasons = distinctBlocked,
            Diagnostics = diag,
            StagedRows = stagedRows,
        };
    }

    private static string ComputeSha256(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static List<JsonDocument> LoadR1Candidates(string path)
    {
        var candidates = new List<JsonDocument>();
        if (!File.Exists(path)) return candidates;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { candidates.Add(JsonDocument.Parse(line)); }
            catch { }
        }
        return candidates;
    }

    public static string BuildMarkdown(string title, LearningControlledFormalLabelIngestionStagingR1PackReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}` 操作: `{r.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Decision");
        b.AppendLine($"- R1PackPassed: `{r.ControlledFormalLabelIngestionStagingR1PackPassed}` GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- Recommendation: `{r.Recommendation}` NextPhase: `{r.NextAllowedPhase}`");
        b.AppendLine();
        b.AppendLine("## Staging Summary");
        b.AppendLine($"- Total: `{r.TotalCases}` Staged: `{r.StagedFormalLabelCount}` Invalid: `{r.InvalidCandidateCount}` HashMismatch: `{r.HashMismatchCount}`");
        b.AppendLine($"- CanonicalHashCoverage: `{r.CanonicalHashCoverage:F0}%` RankingPairHashCoverage: `{r.RankingPairRowHashCoverage:F0}%` ShadowLabelHashCoverage: `{r.ShadowLabelHashCoverage:F0}%`");
        b.AppendLine($"- SourceCandidateLabelPrefix: `{r.SourceCandidateLabelPrefix}` HashInputVersion: `{r.HashInputVersion}`");
        b.AppendLine($"- LegacyStagingInvalidated: `{r.LegacyStagingInvalidated}` StagingSourceUsesLegacyHash: `{r.StagingSourceUsesLegacyHash}`");
        b.AppendLine($"- MainRecommendationUsesHumanReview: `{r.MainRecommendationUsesHumanReview}`");
        b.AppendLine();
        b.AppendLine("## Invariants");
        b.AppendLine($"- StagedLabelsAreFormal: `{r.StagedLabelsAreFormal}` StagingOnly: `{r.StagingOnly}` AutoIngest: `{r.AutoIngest}`");
        b.AppendLine($"- FormalTrainingSetChanged: `{r.FormalTrainingSetChanged}` RuntimePilotExecutionApplied: `{r.RuntimePilotExecutionApplied}`");
        b.AppendLine($"- V8ScopedActivationPreserved: `{r.V8ScopedActivationPreserved}`");
        b.AppendLine();
        b.AppendLine("V10.19R R1 staging pack regeneration。");
        return b.ToString();
    }
}
