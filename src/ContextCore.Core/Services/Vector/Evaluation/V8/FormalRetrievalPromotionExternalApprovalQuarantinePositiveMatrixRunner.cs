using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>V8.10 positive matrix candidate 状态语义。</summary>
public static class QuarantineCandidatePositiveStatuses
{
    public const string Missing = nameof(Missing);
    public const string Invalid = nameof(Invalid);

    /// <summary>结构合法且通过 schema validation；仅表示机器校验通过，不是 approved，也不要求人工介入。</summary>
    public const string MachineValidatedCandidate = nameof(MachineValidatedCandidate);
}

public sealed class FormalRetrievalPromotionExternalApprovalQuarantinePositiveMatrixRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public FormalRetrievalPromotionExternalApprovalQuarantinePositiveMatrixReport Run(
        bool rtPassed,
        bool p15Passed,
        bool mainlineEvidencePresent,
        bool mainlineRegistryPresent,
        FormalRetrievalPromotionExternalApprovalQuarantinePositiveMatrixOptions? opt = null)
    {
        opt ??= new FormalRetrievalPromotionExternalApprovalQuarantinePositiveMatrixOptions();
        var now = DateTimeOffset.UtcNow;
        var cases = new List<FormalRetrievalPromotionExternalApprovalQuarantinePositiveCase>();

        foreach (var scenario in BuildScenarios())
        {
            var evidenceJson = scenario.BuildEvidenceJson?.Invoke();
            var registryJson = scenario.BuildRegistryJson?.Invoke();

            // 走共用 validator —— 与 V8.9R 同一条 ValidateEvidenceJson / ValidateTrustRegistryJson。
            var evidenceValidation = evidenceJson is null
                ? null
                : FormalRetrievalPromotionExternalApprovalQuarantineCandidateValidation.ValidateEvidenceJson(evidenceJson);
            var registryValidation = registryJson is null
                ? null
                : FormalRetrievalPromotionExternalApprovalQuarantineCandidateValidation.ValidateTrustRegistryJson(registryJson);

            var evidenceStatus = ResolveStatus(evidenceValidation);
            var registryStatus = ResolveStatus(registryValidation);

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

            var distinctMissing = missingFields.Distinct(StringComparer.Ordinal).ToArray();
            var distinctInvalid = invalidFields.Distinct(StringComparer.Ordinal).ToArray();

            var evidenceCandidateValid = evidenceValidation?.CandidateValid ?? false;
            var evidenceSchemaValid = evidenceValidation?.SchemaValid ?? false;
            var registryCandidateValid = registryValidation?.CandidateValid ?? false;
            var registrySchemaValid = registryValidation?.SchemaValid ?? false;

            // 当任一候选存在时，要求该候选 CandidateValid && SchemaValid。
            var evidenceContractOk = evidenceJson is null || (evidenceCandidateValid && evidenceSchemaValid);
            var registryContractOk = registryJson is null || (registryCandidateValid && registrySchemaValid);
            var noMissingFields = distinctMissing.Length == 0;
            var noInvalidFields = distinctInvalid.Length == 0;

            var evidenceStatusMatched = string.Equals(scenario.ExpectedEvidenceStatus, evidenceStatus, StringComparison.Ordinal);
            var registryStatusMatched = string.Equals(scenario.ExpectedRegistryStatus, registryStatus, StringComparison.Ordinal);

            var passedAsExpected = evidenceContractOk
                && registryContractOk
                && noMissingFields
                && noInvalidFields
                && evidenceStatusMatched
                && registryStatusMatched;

            cases.Add(new FormalRetrievalPromotionExternalApprovalQuarantinePositiveCase
            {
                CaseName = scenario.CaseName,
                ExpectedEvidenceStatus = scenario.ExpectedEvidenceStatus,
                ExpectedRegistryStatus = scenario.ExpectedRegistryStatus,
                ActualEvidenceStatus = evidenceStatus,
                ActualRegistryStatus = registryStatus,
                EvidenceCandidateValid = evidenceCandidateValid,
                EvidenceSchemaValid = evidenceSchemaValid,
                TrustRegistryCandidateValid = registryCandidateValid,
                TrustRegistryCandidateSchemaValid = registrySchemaValid,
                MissingFields = distinctMissing,
                InvalidFields = distinctInvalid,
                EvidenceStatusMatched = evidenceStatusMatched,
                RegistryStatusMatched = registryStatusMatched,
                NoMissingFields = noMissingFields,
                NoInvalidFields = noInvalidFields,
                CandidateFiles = scenario.CandidateFiles,
                // 显式声明 “不是 approved / 不是 sealed / 不是 promoted”。
                NotApproved = true,
                NotSealed = true,
                NotPromoted = true,
                PassedAsExpected = passedAsExpected
            });
        }

        var passedCases = cases.Count(static c => c.PassedAsExpected);
        var failedCases = cases.Count - passedCases;
        var blocked = new List<string>();

        if (cases.Count < 4)
        {
            blocked.Add("InsufficientPositiveCases");
        }

        if (failedCases > 0)
        {
            blocked.Add("QuarantinePositiveMatrixFailed");
        }

        if (mainlineEvidencePresent)
        {
            blocked.Add("MainlineEvidencePresent");
        }

        if (mainlineRegistryPresent)
        {
            blocked.Add("MainlineTrustRegistryPresent");
        }

        if (!rtPassed)
        {
            blocked.Add("RuntimeChangeGateNotPassed");
        }

        if (!p15Passed)
        {
            blocked.Add("P15GateNotPassed");
        }

        var distinctBlocked = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var positiveMatrixPassed = distinctBlocked.Length == 0;
        var gatePassed = opt.IsGate && positiveMatrixPassed;

        return new FormalRetrievalPromotionExternalApprovalQuarantinePositiveMatrixReport
        {
            OperationId = $"frp-q-pos-matrix-{Guid.NewGuid():N}",
            CreatedAt = now,
            PositiveMatrixPassed = positiveMatrixPassed,
            GatePassed = gatePassed,
            TotalCases = cases.Count,
            PassedCases = passedCases,
            FailedCases = failedCases,
            Cases = cases,
            // V8.10 显式契约：machine validated ≠ approved；不需要人工审查、不 seal、不写 mainline。
            ManualReviewRequired = false,
            ApprovalSealed = false,
            CapabilityGrantWritten = false,
            PromotionToMainlinePerformed = false,
            EvidenceCopiedToMainline = false,
            TrustRegistryCopiedToMainline = false,
            MainlineEvidencePresent = mainlineEvidencePresent,
            MainlineTrustRegistryPresent = mainlineRegistryPresent,
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
            BlockedReasons = distinctBlocked,
            Diagnostics = new[]
            {
                $"total={cases.Count}",
                $"passed={passedCases}",
                $"failed={failedCases}",
                $"runtimeGate={rtPassed}",
                $"p15Gate={p15Passed}",
                $"mainlineEvidence={mainlineEvidencePresent}",
                $"mainlineRegistry={mainlineRegistryPresent}",
                "nextStage=TrustChainValidation/PolicyAuthorityModel (not manual review)"
            }
        };
    }

    private static string ResolveStatus(FormalRetrievalPromotionExternalApprovalCandidateValidationResult? result)
    {
        if (result is null)
        {
            return QuarantineCandidatePositiveStatuses.Missing;
        }

        if (!result.CandidateValid || !result.SchemaValid)
        {
            return QuarantineCandidatePositiveStatuses.Invalid;
        }

        return QuarantineCandidatePositiveStatuses.MachineValidatedCandidate;
    }

    private static IReadOnlyList<QuarantinePositiveScenario> BuildScenarios() =>
    [
        new(
            "ValidEvidenceCandidate",
            QuarantineCandidatePositiveStatuses.MachineValidatedCandidate,
            QuarantineCandidatePositiveStatuses.Missing,
            BuildBaselineEvidenceJson,
            null,
            ["q/evidence.candidate.json"]),
        new(
            "ValidTrustRegistryCandidate",
            QuarantineCandidatePositiveStatuses.Missing,
            QuarantineCandidatePositiveStatuses.MachineValidatedCandidate,
            null,
            BuildBaselineRegistryJson,
            ["q/registry.candidate.json"]),
        new(
            "ValidEvidenceAndRegistryPair",
            QuarantineCandidatePositiveStatuses.MachineValidatedCandidate,
            QuarantineCandidatePositiveStatuses.MachineValidatedCandidate,
            BuildBaselineEvidenceJson,
            BuildBaselineRegistryJson,
            ["q/evidence.candidate.json", "q/registry.candidate.json"]),
        new(
            "ValidMultiRecordTrustRegistry",
            QuarantineCandidatePositiveStatuses.Missing,
            QuarantineCandidatePositiveStatuses.MachineValidatedCandidate,
            null,
            BuildMultiRecordRegistryJson,
            ["q/registry.candidate.json"]),
        new(
            "ValidEvidenceWithMultiRecordRegistry",
            QuarantineCandidatePositiveStatuses.MachineValidatedCandidate,
            QuarantineCandidatePositiveStatuses.MachineValidatedCandidate,
            BuildBaselineEvidenceJson,
            BuildMultiRecordRegistryJson,
            ["q/evidence.candidate.json", "q/registry.candidate.json"]),
        new(
            "ValidRegistryWithFarFutureValidUntil",
            QuarantineCandidatePositiveStatuses.Missing,
            QuarantineCandidatePositiveStatuses.MachineValidatedCandidate,
            null,
            () => SetRecordString(BuildBaselineRegistryJson(), 0, "ValidUntil", "2099-12-31T00:00:00Z"),
            ["q/registry.candidate.json"])
    ];

    private static string BuildBaselineEvidenceJson()
    {
        var evidence = new FormalRetrievalPromotionApprovalEvidence
        {
            ApprovalEvidenceId = "fixture-evid-positive-001",
            ApprovedBy = "ReleaseManager",
            ApprovalId = "APPROVE-POSITIVE-001",
            ApprovalScopes = ["demo-workspace/demo-collection"],
            ApprovalSource = "fixture-external-approval",
            ApprovalTimestamp = DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
            SourcePromotionPlanGateOperationId = "frp-plan-gate-positive-1",
            SourceReadinessGateOperationId = "frp-audit-gate-positive-1",
            SourceCloseoutGateOperationId = "arsp-closeout-gate-positive-1",
            OperatorStatement = "Fixture positive matrix evidence.",
            EvidenceCreatedAt = DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
            ApprovalEvidenceSourceKind = "fixture",
            ApprovalEvidenceProvenanceId = "fixture-provenance-positive-001",
            ApprovalEvidenceProvidedBy = "FixtureDryRunOperator",
            ApprovalEvidenceProvidedAt = DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
            ApprovalEvidenceTrustMode = "fixture-dry-run",
            ApprovalEvidenceIsExternal = true,
            IsFixture = true,
            ApprovalEvidenceChecksum = "fixture-checksum-positive-abc",
            SourceApprovalRequestId = "frp-approval-positive-001",
            BoundPendingApprovalGateOperationId = "frp-approval-gate-positive-001"
        };

        return JsonSerializer.Serialize(evidence, JsonOptions);
    }

    private static string BuildBaselineRegistryJson()
    {
        var registry = new FormalRetrievalPromotionApprovalTrustRegistry
        {
            RegistryId = "fixture-registry-positive-001",
            IsFixture = true,
            RegistryCreatedAt = DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
            AllowedSourceKinds = ["fixture", "registry-preview"],
            TrustedProvenanceRecords =
            [
                new FormalRetrievalPromotionApprovalTrustedProvenanceRecord
                {
                    ApprovalEvidenceProvenanceId = "fixture-provenance-positive-001",
                    ApprovalEvidenceSourceKind = "fixture",
                    ApprovalEvidenceProvidedBy = "FixtureDryRunOperator",
                    ApprovalEvidenceChecksum = "fixture-checksum-positive-abc",
                    SourceApprovalRequestId = "frp-approval-positive-001",
                    BoundPendingApprovalGateOperationId = "frp-approval-gate-positive-001",
                    AllowedScopes = ["demo-workspace/demo-collection"],
                    TrustMode = "fixture-dry-run",
                    ValidUntil = DateTimeOffset.Parse("2027-12-31T00:00:00Z")
                }
            ]
        };

        return JsonSerializer.Serialize(registry, JsonOptions);
    }

    private static string BuildMultiRecordRegistryJson()
    {
        var registry = new FormalRetrievalPromotionApprovalTrustRegistry
        {
            RegistryId = "fixture-registry-positive-multi-001",
            IsFixture = true,
            RegistryCreatedAt = DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
            AllowedSourceKinds = ["fixture", "registry-preview", "fixture-multi"],
            TrustedProvenanceRecords =
            [
                new FormalRetrievalPromotionApprovalTrustedProvenanceRecord
                {
                    ApprovalEvidenceProvenanceId = "fixture-provenance-multi-001",
                    ApprovalEvidenceSourceKind = "fixture",
                    ApprovalEvidenceProvidedBy = "FixtureDryRunOperator",
                    ApprovalEvidenceChecksum = "fixture-checksum-multi-001",
                    SourceApprovalRequestId = "frp-approval-multi-001",
                    BoundPendingApprovalGateOperationId = "frp-approval-gate-multi-001",
                    AllowedScopes = ["demo-workspace/demo-collection"],
                    TrustMode = "fixture-dry-run",
                    ValidUntil = DateTimeOffset.Parse("2027-12-31T00:00:00Z")
                },
                new FormalRetrievalPromotionApprovalTrustedProvenanceRecord
                {
                    ApprovalEvidenceProvenanceId = "fixture-provenance-multi-002",
                    ApprovalEvidenceSourceKind = "registry-preview",
                    ApprovalEvidenceProvidedBy = "FixtureRegistryOperator",
                    ApprovalEvidenceChecksum = "fixture-checksum-multi-002",
                    SourceApprovalRequestId = "frp-approval-multi-002",
                    BoundPendingApprovalGateOperationId = "frp-approval-gate-multi-002",
                    AllowedScopes = ["demo-workspace/demo-collection"],
                    TrustMode = "fixture-dry-run",
                    ValidUntil = DateTimeOffset.Parse("2028-06-30T00:00:00Z")
                },
                new FormalRetrievalPromotionApprovalTrustedProvenanceRecord
                {
                    ApprovalEvidenceProvenanceId = "fixture-provenance-multi-003",
                    ApprovalEvidenceSourceKind = "fixture-multi",
                    ApprovalEvidenceProvidedBy = "FixtureMultiOperator",
                    ApprovalEvidenceChecksum = "fixture-checksum-multi-003",
                    SourceApprovalRequestId = "frp-approval-multi-003",
                    BoundPendingApprovalGateOperationId = "frp-approval-gate-multi-003",
                    AllowedScopes = ["demo-workspace/demo-collection"],
                    TrustMode = "fixture-dry-run",
                    ValidUntil = DateTimeOffset.Parse("2029-01-15T00:00:00Z")
                }
            ]
        };

        return JsonSerializer.Serialize(registry, JsonOptions);
    }

    private static string SetRecordString(string json, int index, string fieldName, string value)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var records = root["TrustedProvenanceRecords"]!.AsArray();
        records[index]![fieldName] = value;
        return root.ToJsonString(JsonOptions);
    }

    public static string BuildMarkdown(string title, FormalRetrievalPromotionExternalApprovalQuarantinePositiveMatrixReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}`");
        b.AppendLine($"操作: `{r.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Decision");
        b.AppendLine($"- PositiveMatrixPassed: `{r.PositiveMatrixPassed}` GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- Total: `{r.TotalCases}` Passed: `{r.PassedCases}` Failed: `{r.FailedCases}`");
        b.AppendLine();
        b.AppendLine("## No-Manual-Review Contract");
        b.AppendLine($"- ManualReviewRequired: `{r.ManualReviewRequired}`");
        b.AppendLine($"- ApprovalSealed: `{r.ApprovalSealed}`");
        b.AppendLine($"- CapabilityGrantWritten: `{r.CapabilityGrantWritten}`");
        b.AppendLine($"- PromotionToMainlinePerformed: `{r.PromotionToMainlinePerformed}`");
        b.AppendLine($"- EvidenceCopiedToMainline: `{r.EvidenceCopiedToMainline}`");
        b.AppendLine($"- TrustRegistryCopiedToMainline: `{r.TrustRegistryCopiedToMainline}`");
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
        b.AppendLine("## Positive Cases");
        foreach (var c in r.Cases)
        {
            b.AppendLine($"- `{c.CaseName}`: passedAsExpected=`{c.PassedAsExpected}`");
            b.AppendLine($"  - evidence: status expected=`{c.ExpectedEvidenceStatus}` actual=`{c.ActualEvidenceStatus}` matched=`{c.EvidenceStatusMatched}` candidateValid=`{c.EvidenceCandidateValid}` schemaValid=`{c.EvidenceSchemaValid}`");
            b.AppendLine($"  - registry: status expected=`{c.ExpectedRegistryStatus}` actual=`{c.ActualRegistryStatus}` matched=`{c.RegistryStatusMatched}` candidateValid=`{c.TrustRegistryCandidateValid}` schemaValid=`{c.TrustRegistryCandidateSchemaValid}`");
            b.AppendLine($"  - noMissingFields=`{c.NoMissingFields}` noInvalidFields=`{c.NoInvalidFields}`");
            b.AppendLine($"  - notApproved=`{c.NotApproved}` notSealed=`{c.NotSealed}` notPromoted=`{c.NotPromoted}`");
            if (c.MissingFields.Count > 0)
            {
                b.AppendLine($"  - missingFields=`{string.Join(", ", c.MissingFields)}`");
            }

            if (c.InvalidFields.Count > 0)
            {
                b.AppendLine($"  - invalidFields=`{string.Join(", ", c.InvalidFields)}`");
            }
        }

        b.AppendLine();
        if (r.BlockedReasons.Count > 0)
        {
            b.AppendLine("## Blocked Reasons");
            foreach (var br in r.BlockedReasons)
            {
                b.AppendLine($"- `{br}`");
            }
            b.AppendLine();
        }

        b.AppendLine("V8.10 quarantine positive matrix。结构合法 candidate 走机器校验得 MachineValidatedCandidate；机器校验不是 approved、不要求人工审查；下一步走 TrustChainValidation / PolicyAuthorityModel，不进入 manual review；不写 mainline、不 seal、不启用 formal retrieval。");
        return b.ToString();
    }
}

public sealed record QuarantinePositiveScenario(
    string CaseName,
    string ExpectedEvidenceStatus,
    string ExpectedRegistryStatus,
    Func<string>? BuildEvidenceJson,
    Func<string>? BuildRegistryJson,
    IReadOnlyList<string> CandidateFiles);

public sealed class FormalRetrievalPromotionExternalApprovalQuarantinePositiveCase
{
    public string CaseName { get; init; } = string.Empty;
    public string ExpectedEvidenceStatus { get; init; } = string.Empty;
    public string ExpectedRegistryStatus { get; init; } = string.Empty;
    public string ActualEvidenceStatus { get; init; } = string.Empty;
    public string ActualRegistryStatus { get; init; } = string.Empty;
    public bool EvidenceCandidateValid { get; init; }
    public bool EvidenceSchemaValid { get; init; }
    public bool TrustRegistryCandidateValid { get; init; }
    public bool TrustRegistryCandidateSchemaValid { get; init; }
    public IReadOnlyList<string> MissingFields { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> InvalidFields { get; init; } = Array.Empty<string>();
    public bool EvidenceStatusMatched { get; init; }
    public bool RegistryStatusMatched { get; init; }
    public bool NoMissingFields { get; init; }
    public bool NoInvalidFields { get; init; }
    public IReadOnlyList<string> CandidateFiles { get; init; } = Array.Empty<string>();

    /// <summary>机器校验通过不等于 approved。</summary>
    public bool NotApproved { get; init; }

    /// <summary>机器校验通过不等于 capability sealed。</summary>
    public bool NotSealed { get; init; }

    /// <summary>机器校验通过不等于 mainline promoted。</summary>
    public bool NotPromoted { get; init; }

    public bool PassedAsExpected { get; init; }
}

public sealed class FormalRetrievalPromotionExternalApprovalQuarantinePositiveMatrixReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool PositiveMatrixPassed { get; init; }
    public bool GatePassed { get; init; }
    public int TotalCases { get; init; }
    public int PassedCases { get; init; }
    public int FailedCases { get; init; }
    public IReadOnlyList<FormalRetrievalPromotionExternalApprovalQuarantinePositiveCase> Cases { get; init; } = Array.Empty<FormalRetrievalPromotionExternalApprovalQuarantinePositiveCase>();

    // No-Manual-Review Contract
    public bool ManualReviewRequired { get; init; }
    public bool ApprovalSealed { get; init; }
    public bool CapabilityGrantWritten { get; init; }
    public bool PromotionToMainlinePerformed { get; init; }
    public bool EvidenceCopiedToMainline { get; init; }
    public bool TrustRegistryCopiedToMainline { get; init; }
    public bool MainlineEvidencePresent { get; init; }
    public bool MainlineTrustRegistryPresent { get; init; }

    // Safety invariants
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

public sealed class FormalRetrievalPromotionExternalApprovalQuarantinePositiveMatrixOptions
{
    public bool Enabled { get; init; } = true;
    public bool IsGate { get; init; }
}
