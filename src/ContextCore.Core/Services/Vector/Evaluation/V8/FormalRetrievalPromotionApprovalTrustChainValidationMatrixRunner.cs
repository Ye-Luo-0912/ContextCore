using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

public sealed class FormalRetrievalPromotionApprovalTrustChainValidationMatrixRunner
{
    public FormalRetrievalPromotionApprovalTrustChainValidationMatrixReport Run(
        bool rtPassed,
        bool p15Passed,
        bool mainlineEvidencePresent,
        bool mainlineRegistryPresent,
        FormalRetrievalPromotionApprovalTrustChainValidationMatrixOptions? opt = null)
    {
        opt ??= new FormalRetrievalPromotionApprovalTrustChainValidationMatrixOptions();
        var now = DateTimeOffset.UtcNow;
        var cases = new List<FormalRetrievalPromotionApprovalTrustChainValidationCase>();

        foreach (var scenario in BuildScenarios())
        {
            var evidence = scenario.BuildEvidence();
            var registry = scenario.BuildRegistry();
            var result = FormalRetrievalPromotionApprovalTrustChainValidator.Validate(evidence, registry);

            var statusMatched = string.Equals(scenario.ExpectedStatus, result.Status, StringComparison.Ordinal);
            var chainCompleteMatched = scenario.ExpectedChainComplete == result.ChainComplete;
            var reasonMatched = scenario.ExpectedMismatchReason is null
                || result.MismatchReasons.Contains(scenario.ExpectedMismatchReason, StringComparer.Ordinal);
            var fieldMatched = scenario.ExpectedMismatchField is null
                || result.MismatchFields.Contains(scenario.ExpectedMismatchField, StringComparer.Ordinal);

            // 正例必须 MismatchReasons 为空；反例必须包含期望的 reason。
            var reasonsShapeOk = scenario.ExpectedChainComplete
                ? result.MismatchReasons.Count == 0
                : result.MismatchReasons.Count >= 1;

            var passedAsExpected = statusMatched
                && chainCompleteMatched
                && reasonMatched
                && fieldMatched
                && reasonsShapeOk;

            cases.Add(new FormalRetrievalPromotionApprovalTrustChainValidationCase
            {
                CaseName = scenario.CaseName,
                ExpectedStatus = scenario.ExpectedStatus,
                ActualStatus = result.Status,
                ExpectedChainComplete = scenario.ExpectedChainComplete,
                ActualChainComplete = result.ChainComplete,
                ExpectedMismatchReason = scenario.ExpectedMismatchReason ?? string.Empty,
                ExpectedMismatchField = scenario.ExpectedMismatchField ?? string.Empty,
                ActualMismatchReasons = result.MismatchReasons,
                ActualMismatchFields = result.MismatchFields,
                MatchedProvenanceId = result.MatchedProvenanceId ?? string.Empty,
                MatchedRecordIndex = result.MatchedRecordIndex,
                StatusMatched = statusMatched,
                ChainCompleteMatched = chainCompleteMatched,
                MismatchReasonMatched = reasonMatched,
                MismatchFieldMatched = fieldMatched,
                PassedAsExpected = passedAsExpected
            });
        }

        var passedCases = cases.Count(static c => c.PassedAsExpected);
        var failedCases = cases.Count - passedCases;
        var positiveCases = cases.Count(static c => c.ExpectedChainComplete);
        var negativeCases = cases.Count - positiveCases;

        var blocked = new List<string>();
        if (cases.Count < 8)
        {
            blocked.Add("InsufficientTrustChainCases");
        }

        if (positiveCases < 1)
        {
            blocked.Add("MissingPositiveTrustChainCase");
        }

        if (negativeCases < 7)
        {
            blocked.Add("InsufficientNegativeTrustChainCases");
        }

        if (failedCases > 0)
        {
            blocked.Add("TrustChainValidationMatrixFailed");
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
        var matrixPassed = distinctBlocked.Length == 0;
        var gatePassed = opt.IsGate && matrixPassed;

        return new FormalRetrievalPromotionApprovalTrustChainValidationMatrixReport
        {
            OperationId = $"frp-trust-chain-matrix-{Guid.NewGuid():N}",
            CreatedAt = now,
            ChainValidationPassed = matrixPassed,
            GatePassed = gatePassed,
            TotalCases = cases.Count,
            PassedCases = passedCases,
            FailedCases = failedCases,
            PositiveCases = positiveCases,
            NegativeCases = negativeCases,
            Cases = cases,
            // V8.11 显式契约：trust chain validation 走机器判定；不是 approved、不要求人工审查、不写 mainline。
            ManualReviewRequired = false,
            ApprovalSealed = false,
            CapabilityGrantWritten = false,
            PromotionToMainlinePerformed = false,
            EvidenceCopiedToMainline = false,
            TrustRegistryCopiedToMainline = false,
            MainlineEvidencePresent = mainlineEvidencePresent,
            MainlineTrustRegistryPresent = mainlineRegistryPresent,
            // 安全不变量
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
                $"positive={positiveCases}",
                $"negative={negativeCases}",
                $"passed={passedCases}",
                $"failed={failedCases}",
                $"runtimeGate={rtPassed}",
                $"p15Gate={p15Passed}",
                $"mainlineEvidence={mainlineEvidencePresent}",
                $"mainlineRegistry={mainlineRegistryPresent}",
                "nextStage=PolicyAuthorityModel (not manual review, not approval seal)"
            }
        };
    }

    private static IReadOnlyList<TrustChainScenario> BuildScenarios() =>
    [
        // 正例 — 干净的 evidence + registry 通过完整链校验。
        new(
            "ChainComplete",
            TrustChainValidationStatuses.TrustChainValidated,
            ExpectedChainComplete: true,
            ExpectedMismatchReason: null,
            ExpectedMismatchField: null,
            BuildEvidence: BuildBaselineEvidence,
            BuildRegistry: BuildBaselineRegistry),
        // 反例 1 — registry 找不到匹配的 provenance id。
        new(
            "ProvenanceIdNotInRegistry",
            TrustChainValidationStatuses.TrustChainBroken,
            ExpectedChainComplete: false,
            ExpectedMismatchReason: TrustChainMismatchReasons.EvidenceProvenanceNotFoundInRegistry,
            ExpectedMismatchField: nameof(FormalRetrievalPromotionApprovalEvidence.ApprovalEvidenceProvenanceId),
            BuildEvidence: () => CloneEvidence(BuildBaselineEvidence(), approvalEvidenceProvenanceId: "fixture-provenance-unknown-999"),
            BuildRegistry: BuildBaselineRegistry),
        // 反例 2 — record kind 与 evidence kind 不一致。
        new(
            "SourceKindMismatchWithRecord",
            TrustChainValidationStatuses.TrustChainBroken,
            ExpectedChainComplete: false,
            ExpectedMismatchReason: TrustChainMismatchReasons.EvidenceSourceKindMismatch,
            ExpectedMismatchField: nameof(FormalRetrievalPromotionApprovalEvidence.ApprovalEvidenceSourceKind),
            BuildEvidence: BuildBaselineEvidence,
            BuildRegistry: () => CloneRegistryWithRecord(BuildBaselineRegistry(), 0, sourceKind: "fixture-divergent-kind")),
        // 反例 3 — evidence kind 不在 AllowedSourceKinds 内。
        new(
            "SourceKindNotInAllowedKinds",
            TrustChainValidationStatuses.TrustChainBroken,
            ExpectedChainComplete: false,
            ExpectedMismatchReason: TrustChainMismatchReasons.EvidenceSourceKindNotAllowed,
            ExpectedMismatchField: nameof(FormalRetrievalPromotionApprovalEvidence.ApprovalEvidenceSourceKind),
            BuildEvidence: BuildBaselineEvidence,
            BuildRegistry: () => CloneRegistry(BuildBaselineRegistry(), allowedSourceKinds: new[] { "registry-preview" })),
        // 反例 4 — checksum 不一致。
        new(
            "ChecksumMismatch",
            TrustChainValidationStatuses.TrustChainBroken,
            ExpectedChainComplete: false,
            ExpectedMismatchReason: TrustChainMismatchReasons.EvidenceChecksumMismatch,
            ExpectedMismatchField: nameof(FormalRetrievalPromotionApprovalEvidence.ApprovalEvidenceChecksum),
            BuildEvidence: BuildBaselineEvidence,
            BuildRegistry: () => CloneRegistryWithRecord(BuildBaselineRegistry(), 0, checksum: "fixture-divergent-checksum-zzz")),
        // 反例 5 — ProvidedBy 不一致。
        new(
            "ProvidedByMismatch",
            TrustChainValidationStatuses.TrustChainBroken,
            ExpectedChainComplete: false,
            ExpectedMismatchReason: TrustChainMismatchReasons.EvidenceProvidedByMismatch,
            ExpectedMismatchField: nameof(FormalRetrievalPromotionApprovalEvidence.ApprovalEvidenceProvidedBy),
            BuildEvidence: BuildBaselineEvidence,
            BuildRegistry: () => CloneRegistryWithRecord(BuildBaselineRegistry(), 0, providedBy: "FixtureDivergentOperator")),
        // 反例 6 — SourceApprovalRequestId 不一致。
        new(
            "SourceApprovalRequestIdMismatch",
            TrustChainValidationStatuses.TrustChainBroken,
            ExpectedChainComplete: false,
            ExpectedMismatchReason: TrustChainMismatchReasons.EvidenceSourceApprovalRequestIdMismatch,
            ExpectedMismatchField: nameof(FormalRetrievalPromotionApprovalEvidence.SourceApprovalRequestId),
            BuildEvidence: BuildBaselineEvidence,
            BuildRegistry: () => CloneRegistryWithRecord(BuildBaselineRegistry(), 0, sourceApprovalRequestId: "frp-approval-divergent-999")),
        // 反例 7 — BoundPendingApprovalGateOperationId 不一致。
        new(
            "BoundPendingApprovalGateOperationIdMismatch",
            TrustChainValidationStatuses.TrustChainBroken,
            ExpectedChainComplete: false,
            ExpectedMismatchReason: TrustChainMismatchReasons.EvidenceBoundPendingApprovalGateOperationIdMismatch,
            ExpectedMismatchField: nameof(FormalRetrievalPromotionApprovalEvidence.BoundPendingApprovalGateOperationId),
            BuildEvidence: BuildBaselineEvidence,
            BuildRegistry: () => CloneRegistryWithRecord(BuildBaselineRegistry(), 0, boundPendingApprovalGateOperationId: "frp-approval-gate-divergent-999")),
        // 反例 8 — TrustMode 不一致。
        new(
            "TrustModeMismatch",
            TrustChainValidationStatuses.TrustChainBroken,
            ExpectedChainComplete: false,
            ExpectedMismatchReason: TrustChainMismatchReasons.EvidenceTrustModeMismatch,
            ExpectedMismatchField: nameof(FormalRetrievalPromotionApprovalEvidence.ApprovalEvidenceTrustMode),
            BuildEvidence: BuildBaselineEvidence,
            BuildRegistry: () => CloneRegistryWithRecord(BuildBaselineRegistry(), 0, trustMode: "fixture-divergent-trust-mode")),
        // 反例 9 — ApprovalScopes 不是 record.AllowedScopes 的子集。
        new(
            "ApprovalScopesNotSubsetOfRecord",
            TrustChainValidationStatuses.TrustChainBroken,
            ExpectedChainComplete: false,
            ExpectedMismatchReason: TrustChainMismatchReasons.EvidenceApprovalScopesNotSubsetOfRecord,
            ExpectedMismatchField: nameof(FormalRetrievalPromotionApprovalEvidence.ApprovalScopes),
            BuildEvidence: () => CloneEvidence(BuildBaselineEvidence(), approvalScopes: new[] { "demo-workspace/demo-collection", "out-of-scope/illegal-collection" }),
            BuildRegistry: BuildBaselineRegistry),
        // 反例 10 — ApprovalTimestamp 在 ValidUntil 之后（过期）。
        new(
            "ApprovalTimestampAfterValidUntil",
            TrustChainValidationStatuses.TrustChainBroken,
            ExpectedChainComplete: false,
            ExpectedMismatchReason: TrustChainMismatchReasons.EvidenceApprovalTimestampAfterRecordValidUntil,
            ExpectedMismatchField: nameof(FormalRetrievalPromotionApprovalEvidence.ApprovalTimestamp),
            BuildEvidence: () => CloneEvidence(BuildBaselineEvidence(), approvalTimestamp: DateTimeOffset.Parse("2028-01-01T00:00:00Z")),
            BuildRegistry: BuildBaselineRegistry)
    ];

    private static FormalRetrievalPromotionApprovalEvidence BuildBaselineEvidence() => new()
    {
        ApprovalEvidenceId = "fixture-evid-trustchain-001",
        ApprovedBy = "ReleaseManager",
        ApprovalId = "APPROVE-TRUSTCHAIN-001",
        ApprovalScopes = new[] { "demo-workspace/demo-collection" },
        ApprovalSource = "fixture-external-approval",
        ApprovalTimestamp = DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
        SourcePromotionPlanGateOperationId = "frp-plan-gate-trustchain-1",
        SourceReadinessGateOperationId = "frp-audit-gate-trustchain-1",
        SourceCloseoutGateOperationId = "arsp-closeout-gate-trustchain-1",
        OperatorStatement = "Fixture trust chain validation evidence.",
        EvidenceCreatedAt = DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
        ApprovalEvidenceSourceKind = "fixture",
        ApprovalEvidenceProvenanceId = "fixture-provenance-trustchain-001",
        ApprovalEvidenceProvidedBy = "FixtureDryRunOperator",
        ApprovalEvidenceProvidedAt = DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
        ApprovalEvidenceTrustMode = "fixture-dry-run",
        ApprovalEvidenceIsExternal = true,
        IsFixture = true,
        ApprovalEvidenceChecksum = "fixture-checksum-trustchain-001",
        SourceApprovalRequestId = "frp-approval-trustchain-001",
        BoundPendingApprovalGateOperationId = "frp-approval-gate-trustchain-001"
    };

    private static FormalRetrievalPromotionApprovalTrustRegistry BuildBaselineRegistry() => new()
    {
        RegistryId = "fixture-registry-trustchain-001",
        IsFixture = true,
        RegistryCreatedAt = DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
        AllowedSourceKinds = new[] { "fixture", "registry-preview" },
        TrustedProvenanceRecords = new[]
        {
            new FormalRetrievalPromotionApprovalTrustedProvenanceRecord
            {
                ApprovalEvidenceProvenanceId = "fixture-provenance-trustchain-001",
                ApprovalEvidenceSourceKind = "fixture",
                ApprovalEvidenceProvidedBy = "FixtureDryRunOperator",
                ApprovalEvidenceChecksum = "fixture-checksum-trustchain-001",
                SourceApprovalRequestId = "frp-approval-trustchain-001",
                BoundPendingApprovalGateOperationId = "frp-approval-gate-trustchain-001",
                AllowedScopes = new[] { "demo-workspace/demo-collection" },
                TrustMode = "fixture-dry-run",
                ValidUntil = DateTimeOffset.Parse("2027-12-31T00:00:00Z")
            }
        }
    };

    private static FormalRetrievalPromotionApprovalEvidence CloneEvidence(
        FormalRetrievalPromotionApprovalEvidence b,
        string? approvalEvidenceProvenanceId = null,
        IReadOnlyList<string>? approvalScopes = null,
        DateTimeOffset? approvalTimestamp = null,
        string? approvalEvidenceSourceKind = null,
        string? approvalEvidenceProvidedBy = null,
        string? approvalEvidenceChecksum = null,
        string? sourceApprovalRequestId = null,
        string? boundPendingApprovalGateOperationId = null,
        string? approvalEvidenceTrustMode = null)
        => new()
        {
            ApprovalEvidenceId = b.ApprovalEvidenceId,
            ApprovedBy = b.ApprovedBy,
            ApprovalId = b.ApprovalId,
            ApprovalScopes = approvalScopes ?? b.ApprovalScopes,
            ApprovalSource = b.ApprovalSource,
            ApprovalTimestamp = approvalTimestamp ?? b.ApprovalTimestamp,
            SourcePromotionPlanGateOperationId = b.SourcePromotionPlanGateOperationId,
            SourceReadinessGateOperationId = b.SourceReadinessGateOperationId,
            SourceCloseoutGateOperationId = b.SourceCloseoutGateOperationId,
            OperatorStatement = b.OperatorStatement,
            EvidenceCreatedAt = b.EvidenceCreatedAt,
            ApprovalEvidenceSourceKind = approvalEvidenceSourceKind ?? b.ApprovalEvidenceSourceKind,
            ApprovalEvidenceProvenanceId = approvalEvidenceProvenanceId ?? b.ApprovalEvidenceProvenanceId,
            ApprovalEvidenceProvidedBy = approvalEvidenceProvidedBy ?? b.ApprovalEvidenceProvidedBy,
            ApprovalEvidenceProvidedAt = b.ApprovalEvidenceProvidedAt,
            ApprovalEvidenceTrustMode = approvalEvidenceTrustMode ?? b.ApprovalEvidenceTrustMode,
            ApprovalEvidenceIsExternal = b.ApprovalEvidenceIsExternal,
            IsFixture = b.IsFixture,
            ApprovalEvidenceChecksum = approvalEvidenceChecksum ?? b.ApprovalEvidenceChecksum,
            SourceApprovalRequestId = sourceApprovalRequestId ?? b.SourceApprovalRequestId,
            BoundPendingApprovalGateOperationId = boundPendingApprovalGateOperationId ?? b.BoundPendingApprovalGateOperationId
        };

    private static FormalRetrievalPromotionApprovalTrustRegistry CloneRegistry(
        FormalRetrievalPromotionApprovalTrustRegistry b,
        IReadOnlyList<string>? allowedSourceKinds = null,
        IReadOnlyList<FormalRetrievalPromotionApprovalTrustedProvenanceRecord>? records = null)
        => new()
        {
            RegistryId = b.RegistryId,
            IsFixture = b.IsFixture,
            RegistryCreatedAt = b.RegistryCreatedAt,
            AllowedSourceKinds = allowedSourceKinds ?? b.AllowedSourceKinds,
            TrustedProvenanceRecords = records ?? b.TrustedProvenanceRecords
        };

    private static FormalRetrievalPromotionApprovalTrustRegistry CloneRegistryWithRecord(
        FormalRetrievalPromotionApprovalTrustRegistry b,
        int recordIndex,
        string? provenanceId = null,
        string? sourceKind = null,
        string? providedBy = null,
        string? checksum = null,
        string? sourceApprovalRequestId = null,
        string? boundPendingApprovalGateOperationId = null,
        IReadOnlyList<string>? allowedScopes = null,
        string? trustMode = null,
        DateTimeOffset? validUntil = null)
    {
        var records = b.TrustedProvenanceRecords.Select((r, i) =>
        {
            if (i != recordIndex) return r;
            return new FormalRetrievalPromotionApprovalTrustedProvenanceRecord
            {
                ApprovalEvidenceProvenanceId = provenanceId ?? r.ApprovalEvidenceProvenanceId,
                ApprovalEvidenceSourceKind = sourceKind ?? r.ApprovalEvidenceSourceKind,
                ApprovalEvidenceProvidedBy = providedBy ?? r.ApprovalEvidenceProvidedBy,
                ApprovalEvidenceChecksum = checksum ?? r.ApprovalEvidenceChecksum,
                SourceApprovalRequestId = sourceApprovalRequestId ?? r.SourceApprovalRequestId,
                BoundPendingApprovalGateOperationId = boundPendingApprovalGateOperationId ?? r.BoundPendingApprovalGateOperationId,
                AllowedScopes = allowedScopes ?? r.AllowedScopes,
                TrustMode = trustMode ?? r.TrustMode,
                ValidUntil = validUntil ?? r.ValidUntil
            };
        }).ToArray();
        return CloneRegistry(b, records: records);
    }

    public static string BuildMarkdown(string title, FormalRetrievalPromotionApprovalTrustChainValidationMatrixReport r)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}`");
        b.AppendLine($"操作: `{r.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Decision");
        b.AppendLine($"- ChainValidationPassed: `{r.ChainValidationPassed}` GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- Total: `{r.TotalCases}` Positive: `{r.PositiveCases}` Negative: `{r.NegativeCases}`");
        b.AppendLine($"- Passed: `{r.PassedCases}` Failed: `{r.FailedCases}`");
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
        b.AppendLine("## Trust Chain Cases");
        foreach (var c in r.Cases)
        {
            b.AppendLine($"- `{c.CaseName}`: passedAsExpected=`{c.PassedAsExpected}`");
            b.AppendLine($"  - status expected=`{c.ExpectedStatus}` actual=`{c.ActualStatus}` matched=`{c.StatusMatched}`");
            b.AppendLine($"  - chainComplete expected=`{c.ExpectedChainComplete}` actual=`{c.ActualChainComplete}` matched=`{c.ChainCompleteMatched}`");
            if (!string.IsNullOrEmpty(c.ExpectedMismatchReason))
            {
                b.AppendLine($"  - expectedReason=`{c.ExpectedMismatchReason}` reasonMatched=`{c.MismatchReasonMatched}`");
            }
            if (!string.IsNullOrEmpty(c.ExpectedMismatchField))
            {
                b.AppendLine($"  - expectedField=`{c.ExpectedMismatchField}` fieldMatched=`{c.MismatchFieldMatched}`");
            }
            if (c.ActualMismatchReasons.Count > 0)
            {
                b.AppendLine($"  - actualReasons=`{string.Join(", ", c.ActualMismatchReasons)}`");
            }
            if (c.ActualMismatchFields.Count > 0)
            {
                b.AppendLine($"  - actualFields=`{string.Join(", ", c.ActualMismatchFields)}`");
            }
            if (!string.IsNullOrEmpty(c.MatchedProvenanceId))
            {
                b.AppendLine($"  - matchedProvenanceId=`{c.MatchedProvenanceId}` matchedRecordIndex=`{c.MatchedRecordIndex}`");
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

        b.AppendLine("V8.11 trust chain validation matrix。evidence 与 trust registry 跨字段链路校验；机器判定不是 approved，不要求人工审查；下一阶段走 PolicyAuthorityModel；不写 mainline、不 seal、不启用 formal retrieval。");
        return b.ToString();
    }
}

public sealed record TrustChainScenario(
    string CaseName,
    string ExpectedStatus,
    bool ExpectedChainComplete,
    string? ExpectedMismatchReason,
    string? ExpectedMismatchField,
    Func<FormalRetrievalPromotionApprovalEvidence> BuildEvidence,
    Func<FormalRetrievalPromotionApprovalTrustRegistry> BuildRegistry);

public sealed class FormalRetrievalPromotionApprovalTrustChainValidationCase
{
    public string CaseName { get; init; } = string.Empty;
    public string ExpectedStatus { get; init; } = string.Empty;
    public string ActualStatus { get; init; } = string.Empty;
    public bool ExpectedChainComplete { get; init; }
    public bool ActualChainComplete { get; init; }
    public string ExpectedMismatchReason { get; init; } = string.Empty;
    public string ExpectedMismatchField { get; init; } = string.Empty;
    public IReadOnlyList<string> ActualMismatchReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ActualMismatchFields { get; init; } = Array.Empty<string>();
    public string MatchedProvenanceId { get; init; } = string.Empty;
    public int MatchedRecordIndex { get; init; } = -1;
    public bool StatusMatched { get; init; }
    public bool ChainCompleteMatched { get; init; }
    public bool MismatchReasonMatched { get; init; }
    public bool MismatchFieldMatched { get; init; }
    public bool PassedAsExpected { get; init; }
}

public sealed class FormalRetrievalPromotionApprovalTrustChainValidationMatrixReport
{
    public string OperationId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool ChainValidationPassed { get; init; }
    public bool GatePassed { get; init; }
    public int TotalCases { get; init; }
    public int PassedCases { get; init; }
    public int FailedCases { get; init; }
    public int PositiveCases { get; init; }
    public int NegativeCases { get; init; }
    public IReadOnlyList<FormalRetrievalPromotionApprovalTrustChainValidationCase> Cases { get; init; } = Array.Empty<FormalRetrievalPromotionApprovalTrustChainValidationCase>();

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

public sealed class FormalRetrievalPromotionApprovalTrustChainValidationMatrixOptions
{
    public bool Enabled { get; init; } = true;
    public bool IsGate { get; init; }
}
