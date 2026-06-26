using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

public sealed class FormalRetrievalPromotionExternalApprovalQuarantineNegativeMatrixRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public FormalRetrievalPromotionExternalApprovalQuarantineNegativeMatrixReport Run(
        bool rtPassed,
        bool p15Passed,
        FormalRetrievalPromotionExternalApprovalQuarantineMatrixOptions? opt = null)
    {
        opt ??= new FormalRetrievalPromotionExternalApprovalQuarantineMatrixOptions();
        var now = DateTimeOffset.UtcNow;
        var scanner = new FormalRetrievalPromotionExternalApprovalQuarantineScanRunner();
        var cases = new List<FormalRetrievalPromotionExternalApprovalQuarantineNegativeCase>();

        foreach (var scenario in BuildScenarios())
        {
            var evidenceJson = scenario.BuildEvidenceJson?.Invoke();
            var registryJson = scenario.BuildRegistryJson?.Invoke();

            var evidenceValidation = evidenceJson is null
                ? null
                : FormalRetrievalPromotionExternalApprovalQuarantineCandidateValidation.ValidateEvidenceJson(evidenceJson);
            var registryValidation = registryJson is null
                ? null
                : FormalRetrievalPromotionExternalApprovalQuarantineCandidateValidation.ValidateTrustRegistryJson(registryJson);

            var missingFields = new List<string>();
            var invalidFields = new List<string>();
            if (evidenceValidation is not null)
            {
                missingFields.AddRange(evidenceValidation.MissingFields);
                invalidFields.AddRange(evidenceValidation.InvalidFields);
            }

            if (registryValidation is not null)
            {
                missingFields.AddRange(registryValidation.MissingFields);
                invalidFields.AddRange(registryValidation.InvalidFields);
            }

            var report = scanner.RunGate(
                evidenceJson is not null,
                registryJson is not null,
                GetStatus(evidenceValidation),
                GetStatus(registryValidation),
                evidenceValidation?.CandidateValid ?? false,
                registryValidation?.CandidateValid ?? false,
                evidenceValidation?.SchemaValid ?? false,
                registryValidation?.SchemaValid ?? false,
                missingFields.Distinct(StringComparer.Ordinal).ToArray(),
                invalidFields.Distinct(StringComparer.Ordinal).ToArray(),
                false,
                false,
                scenario.CandidateFiles,
                rtPassed,
                p15Passed);

            var blockedReasonMatched = report.BlockedReasons.Any(r =>
                string.Equals(r, scenario.ExpectedBlockedReason, StringComparison.OrdinalIgnoreCase));
            var missingFieldMatched = scenario.ExpectedMissingField is null
                || report.CandidateValidationMissingFields.Contains(scenario.ExpectedMissingField, StringComparer.Ordinal);
            var invalidFieldMatched = scenario.ExpectedInvalidField is null
                || report.CandidateValidationInvalidFields.Contains(scenario.ExpectedInvalidField, StringComparer.Ordinal);
            var fieldMatched = missingFieldMatched && invalidFieldMatched;
            var failedAsExpected = !report.ScanPassed && blockedReasonMatched && fieldMatched;

            cases.Add(new FormalRetrievalPromotionExternalApprovalQuarantineNegativeCase
            {
                CaseName = scenario.CaseName,
                ExpectedBlockedReason = scenario.ExpectedBlockedReason,
                ActualBlockedReasons = report.BlockedReasons,
                ExpectedMissingField = scenario.ExpectedMissingField ?? string.Empty,
                ExpectedInvalidField = scenario.ExpectedInvalidField ?? string.Empty,
                ActualMissingFields = report.CandidateValidationMissingFields,
                ActualInvalidFields = report.CandidateValidationInvalidFields,
                MissingFieldMatched = missingFieldMatched,
                InvalidFieldMatched = invalidFieldMatched,
                FailedAsExpected = failedAsExpected
            });
        }

        var passedCases = cases.Count(static c => c.FailedAsExpected);
        var failedCases = cases.Count - passedCases;
        var matrixPassed = failedCases == 0;
        var gatePassed = opt.IsGate && matrixPassed;
        var blocked = new List<string>();
        if (!matrixPassed)
        {
            blocked.Add("QuarantineNegativeMatrixFailed");
        }

        return new FormalRetrievalPromotionExternalApprovalQuarantineNegativeMatrixReport
        {
            OperationId = $"frp-q-neg-matrix-{Guid.NewGuid():N}",
            CreatedAt = now,
            MatrixPassed = matrixPassed,
            GatePassed = gatePassed,
            TotalCases = cases.Count,
            PassedCases = passedCases,
            FailedCases = failedCases,
            Cases = cases,
            PromotionToMainlinePerformed = false,
            MainlineEvidencePresent = false,
            MainlineTrustRegistryPresent = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            FormalPackageWritten = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            VectorStoreBindingChanged = false,
            GlobalDefaultOn = false,
            ConfigPatchWritten = false,
            RuntimeActivation = false,
            NoRuntimeMutationInvariant = true,
            BlockedReasons = blocked,
            Diagnostics = new[]
            {
                $"total={cases.Count}",
                $"passed={passedCases}",
                $"failed={failedCases}",
                $"runtimeGate={rtPassed}",
                $"p15Gate={p15Passed}"
            }
        };
    }

    private static string GetStatus(FormalRetrievalPromotionExternalApprovalCandidateValidationResult? result)
    {
        if (result is null)
        {
            return QuarantineScanStatuses.Missing;
        }

        if (!result.CandidateValid || !result.SchemaValid)
        {
            return QuarantineScanStatuses.Invalid;
        }

        return QuarantineScanStatuses.ReadyForManualReview;
    }

    private static IReadOnlyList<QuarantineNegativeScenario> BuildScenarios() =>
    [
        new(
            "EvidenceMissingField",
            "EvidenceCandidateSchemaInvalid",
            () => RemoveRootField(BuildBaselineEvidenceJson(), "ApprovalId"),
            null,
            "ApprovalId",
            null,
            ["q/evidence.candidate.json"]),
        new(
            "EvidenceEmptyScopes",
            "EvidenceCandidateSchemaInvalid",
            () => SetRootArray(BuildBaselineEvidenceJson(), "ApprovalScopes", []),
            null,
            null,
            "ApprovalScopes",
            ["q/evidence.candidate.json"]),
        new(
            "EvidenceDefaultTime",
            "EvidenceCandidateSchemaInvalid",
            () => SetRootString(BuildBaselineEvidenceJson(), "ApprovalTimestamp", DateTimeOffset.MinValue.ToString("O")),
            null,
            null,
            "ApprovalTimestamp",
            ["q/evidence.candidate.json"]),
        new(
            "RegistryMissingRecords",
            "TrustRegistryCandidateInvalid",
            null,
            () => RemoveRootField(BuildBaselineRegistryJson(), "TrustedProvenanceRecords"),
            "TrustedProvenanceRecords",
            null,
            ["q/registry.candidate.json"]),
        new(
            "RegistryEmptySourceKinds",
            "TrustRegistryCandidateSchemaInvalid",
            null,
            () => SetRootArray(BuildBaselineRegistryJson(), "AllowedSourceKinds", []),
            null,
            "AllowedSourceKinds",
            ["q/registry.candidate.json"]),
        new(
            "RecordMissingChecksum",
            "TrustRegistryCandidateSchemaInvalid",
            null,
            () => RemoveRecordField(BuildBaselineRegistryJson(), 0, "ApprovalEvidenceChecksum"),
            "TrustedProvenanceRecords[0].ApprovalEvidenceChecksum",
            null,
            ["q/registry.candidate.json"]),
        new(
            "RecordMissingProvidedBy",
            "TrustRegistryCandidateSchemaInvalid",
            null,
            () => RemoveRecordField(BuildBaselineRegistryJson(), 1, "ApprovalEvidenceProvidedBy"),
            "TrustedProvenanceRecords[1].ApprovalEvidenceProvidedBy",
            null,
            ["q/registry.candidate.json"]),
        new(
            "RecordDefaultValidUntil",
            "TrustRegistryCandidateSchemaInvalid",
            null,
            () => SetRecordString(BuildBaselineRegistryJson(), 1, "ValidUntil", DateTimeOffset.MinValue.ToString("O")),
            null,
            "TrustedProvenanceRecords[1].ValidUntil",
            ["q/registry.candidate.json"])
    ];

    private static string BuildBaselineEvidenceJson()
    {
        var evidence = new FormalRetrievalPromotionApprovalEvidence
        {
            ApprovalEvidenceId = "fixture-evid-quarantine-001",
            ApprovedBy = "ReleaseManager",
            ApprovalId = "APPROVE-QUARANTINE-001",
            ApprovalScopes = ["demo-workspace/demo-collection"],
            ApprovalSource = "fixture-external-approval",
            ApprovalTimestamp = DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
            SourcePromotionPlanGateOperationId = "frp-plan-gate-39b807ebe543456c89166daebc7eb484",
            SourceReadinessGateOperationId = "frp-audit-gate-3ecc140a80b043249694e63dd8e461d8",
            SourceCloseoutGateOperationId = "arsp-closeout-gate-3df0efdbc9a540f3b5cdd03a2bd1b43a",
            OperatorStatement = "Fixture quarantine validation evidence.",
            EvidenceCreatedAt = DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
            ApprovalEvidenceSourceKind = "fixture",
            ApprovalEvidenceProvenanceId = "fixture-provenance-001",
            ApprovalEvidenceProvidedBy = "FixtureDryRunOperator",
            ApprovalEvidenceProvidedAt = DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
            ApprovalEvidenceTrustMode = "fixture-dry-run",
            ApprovalEvidenceIsExternal = true,
            IsFixture = true,
            ApprovalEvidenceChecksum = "fixture-computed-checksum-abc123",
            SourceApprovalRequestId = "frp-approval-20260626-5597b86de70d4ecb9949c2b99b23fce3",
            BoundPendingApprovalGateOperationId = "frp-approval-gate-38556bf32c094044af1934f4ab59beaa"
        };

        return JsonSerializer.Serialize(evidence, JsonOptions);
    }

    private static string BuildBaselineRegistryJson()
    {
        var registry = new FormalRetrievalPromotionApprovalTrustRegistry
        {
            RegistryId = "fixture-registry-quarantine-001",
            IsFixture = true,
            RegistryCreatedAt = DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
            AllowedSourceKinds = ["fixture", "registry-preview"],
            TrustedProvenanceRecords =
            [
                new FormalRetrievalPromotionApprovalTrustedProvenanceRecord
                {
                    ApprovalEvidenceProvenanceId = "fixture-provenance-001",
                    ApprovalEvidenceSourceKind = "fixture",
                    ApprovalEvidenceProvidedBy = "FixtureDryRunOperator",
                    ApprovalEvidenceChecksum = "fixture-computed-checksum-abc123",
                    SourceApprovalRequestId = "frp-approval-20260626-5597b86de70d4ecb9949c2b99b23fce3",
                    BoundPendingApprovalGateOperationId = "frp-approval-gate-38556bf32c094044af1934f4ab59beaa",
                    AllowedScopes = ["demo-workspace/demo-collection"],
                    TrustMode = "fixture-dry-run",
                    ValidUntil = DateTimeOffset.Parse("2027-12-31T00:00:00Z")
                },
                new FormalRetrievalPromotionApprovalTrustedProvenanceRecord
                {
                    ApprovalEvidenceProvenanceId = "fixture-provenance-002",
                    ApprovalEvidenceSourceKind = "registry-preview",
                    ApprovalEvidenceProvidedBy = "FixtureRegistryOperator",
                    ApprovalEvidenceChecksum = "fixture-computed-checksum-def456",
                    SourceApprovalRequestId = "frp-approval-20260626-5597b86de70d4ecb9949c2b99b23fce4",
                    BoundPendingApprovalGateOperationId = "frp-approval-gate-38556bf32c094044af1934f4ab59beab",
                    AllowedScopes = ["demo-workspace/demo-collection"],
                    TrustMode = "fixture-dry-run",
                    ValidUntil = DateTimeOffset.Parse("2027-12-31T00:00:00Z")
                }
            ]
        };

        return JsonSerializer.Serialize(registry, JsonOptions);
    }

    private static string RemoveRootField(string json, string fieldName)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        root.Remove(fieldName);
        return root.ToJsonString(JsonOptions);
    }

    private static string SetRootArray(string json, string fieldName, string[] values)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        root[fieldName] = array;
        return root.ToJsonString(JsonOptions);
    }

    private static string SetRootString(string json, string fieldName, string value)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        root[fieldName] = value;
        return root.ToJsonString(JsonOptions);
    }

    private static string RemoveRecordField(string json, int index, string fieldName)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var records = root["TrustedProvenanceRecords"]!.AsArray();
        records[index]!.AsObject().Remove(fieldName);
        return root.ToJsonString(JsonOptions);
    }

    private static string SetRecordString(string json, int index, string fieldName, string value)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var records = root["TrustedProvenanceRecords"]!.AsArray();
        records[index]![fieldName] = value;
        return root.ToJsonString(JsonOptions);
    }

    public static string BuildMarkdown(string title, FormalRetrievalPromotionExternalApprovalQuarantineNegativeMatrixReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}`");
        b.AppendLine($"操作: `{r.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Decision");
        b.AppendLine($"- MatrixPassed: `{r.MatrixPassed}` GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- Total: `{r.TotalCases}` Passed: `{r.PassedCases}` Failed: `{r.FailedCases}`");
        b.AppendLine($"- PromotionToMainlinePerformed: `{r.PromotionToMainlinePerformed}`");
        b.AppendLine($"- MainlineEvidencePresent: `{r.MainlineEvidencePresent}`");
        b.AppendLine($"- MainlineTrustRegistryPresent: `{r.MainlineTrustRegistryPresent}`");
        b.AppendLine();
        b.AppendLine("## Safety");
        b.AppendLine($"- FormalRetrievalAllowed: `{r.FormalRetrievalAllowed}`");
        b.AppendLine($"- RuntimeSwitchAllowed: `{r.RuntimeSwitchAllowed}`");
        b.AppendLine($"- FormalPackageWritten: `{r.FormalPackageWritten}`");
        b.AppendLine($"- PackageOutputChanged: `{r.PackageOutputChanged}`");
        b.AppendLine($"- PackingPolicyChanged: `{r.PackingPolicyChanged}`");
        b.AppendLine($"- VectorStoreBindingChanged: `{r.VectorStoreBindingChanged}`");
        b.AppendLine($"- GlobalDefaultOn: `{r.GlobalDefaultOn}`");
        b.AppendLine($"- ConfigPatchWritten: `{r.ConfigPatchWritten}`");
        b.AppendLine($"- RuntimeActivation: `{r.RuntimeActivation}`");
        b.AppendLine($"- NoRuntimeMutationInvariant: `{r.NoRuntimeMutationInvariant}`");
        b.AppendLine();
        b.AppendLine("## Negative Cases");
        foreach (var c in r.Cases)
        {
            b.AppendLine($"- `{c.CaseName}`: expectedReason=`{c.ExpectedBlockedReason}` failedAsExpected=`{c.FailedAsExpected}`");
            b.AppendLine($"  - expectedMissing=`{c.ExpectedMissingField}` matched=`{c.MissingFieldMatched}`");
            b.AppendLine($"  - expectedInvalid=`{c.ExpectedInvalidField}` matched=`{c.InvalidFieldMatched}`");
            b.AppendLine($"  - actualBlocked=`{string.Join(", ", c.ActualBlockedReasons)}`");
            b.AppendLine($"  - actualMissing=`{string.Join(", ", c.ActualMissingFields)}`");
            b.AppendLine($"  - actualInvalid=`{string.Join(", ", c.ActualInvalidFields)}`");
        }

        b.AppendLine();
        b.AppendLine("V8.9R quarantine negative matrix。真实 candidate JSON + 字段定位校验。");
        return b.ToString();
    }
}

public sealed record QuarantineNegativeScenario(
    string CaseName,
    string ExpectedBlockedReason,
    Func<string>? BuildEvidenceJson,
    Func<string>? BuildRegistryJson,
    string? ExpectedMissingField,
    string? ExpectedInvalidField,
    IReadOnlyList<string> CandidateFiles);

public sealed class FormalRetrievalPromotionExternalApprovalQuarantineNegativeCase
{
    public string CaseName { get; init; } = string.Empty;
    public string ExpectedBlockedReason { get; init; } = string.Empty;
    public IReadOnlyList<string> ActualBlockedReasons { get; init; } = Array.Empty<string>();
    public string ExpectedMissingField { get; init; } = string.Empty;
    public string ExpectedInvalidField { get; init; } = string.Empty;
    public IReadOnlyList<string> ActualMissingFields { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ActualInvalidFields { get; init; } = Array.Empty<string>();
    public bool MissingFieldMatched { get; init; }
    public bool InvalidFieldMatched { get; init; }
    public bool FailedAsExpected { get; init; }
}

public sealed class FormalRetrievalPromotionExternalApprovalQuarantineNegativeMatrixReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool MatrixPassed { get; init; }
    public bool GatePassed { get; init; }
    public int TotalCases { get; init; }
    public int PassedCases { get; init; }
    public int FailedCases { get; init; }
    public IReadOnlyList<FormalRetrievalPromotionExternalApprovalQuarantineNegativeCase> Cases { get; init; } = Array.Empty<FormalRetrievalPromotionExternalApprovalQuarantineNegativeCase>();
    public bool PromotionToMainlinePerformed { get; init; }
    public bool MainlineEvidencePresent { get; init; }
    public bool MainlineTrustRegistryPresent { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool ConfigPatchWritten { get; init; }
    public bool RuntimeActivation { get; init; }
    public bool NoRuntimeMutationInvariant { get; init; }
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

public sealed class FormalRetrievalPromotionExternalApprovalQuarantineMatrixOptions
{
    public bool Enabled { get; init; } = true;
    public bool IsGate { get; init; }
}

