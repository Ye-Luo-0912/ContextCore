using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>V8.11 trust chain validation 状态语义。</summary>
public static class TrustChainValidationStatuses
{
    /// <summary>evidence 与 trust registry 通过 ApprovalEvidenceProvenanceId 关联且跨字段全部一致。</summary>
    public const string TrustChainValidated = nameof(TrustChainValidated);

    /// <summary>关联缺失或跨字段不一致；机器判定信任链不完整。</summary>
    public const string TrustChainBroken = nameof(TrustChainBroken);
}

/// <summary>V8.11 trust chain mismatch reason 常量。</summary>
public static class TrustChainMismatchReasons
{
    public const string EvidenceProvenanceNotFoundInRegistry = nameof(EvidenceProvenanceNotFoundInRegistry);
    public const string EvidenceSourceKindNotAllowed = nameof(EvidenceSourceKindNotAllowed);
    public const string EvidenceSourceKindMismatch = nameof(EvidenceSourceKindMismatch);
    public const string EvidenceChecksumMismatch = nameof(EvidenceChecksumMismatch);
    public const string EvidenceProvidedByMismatch = nameof(EvidenceProvidedByMismatch);
    public const string EvidenceSourceApprovalRequestIdMismatch = nameof(EvidenceSourceApprovalRequestIdMismatch);
    public const string EvidenceBoundPendingApprovalGateOperationIdMismatch = nameof(EvidenceBoundPendingApprovalGateOperationIdMismatch);
    public const string EvidenceTrustModeMismatch = nameof(EvidenceTrustModeMismatch);
    public const string EvidenceApprovalScopesNotSubsetOfRecord = nameof(EvidenceApprovalScopesNotSubsetOfRecord);
    public const string EvidenceApprovalTimestampAfterRecordValidUntil = nameof(EvidenceApprovalTimestampAfterRecordValidUntil);
}

/// <summary>V8.11 trust chain 校验结果。纯函数返回，不读 mainline / 不写 registry。</summary>
public sealed class TrustChainValidationResult
{
    public bool ChainComplete { get; init; }
    public string Status { get; init; } = TrustChainValidationStatuses.TrustChainBroken;
    public IReadOnlyList<string> MismatchReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> MismatchFields { get; init; } = Array.Empty<string>();
    public string? MatchedProvenanceId { get; init; }
    public int MatchedRecordIndex { get; init; } = -1;
}

/// <summary>
/// V8.11 trust chain validator。给定一对 evidence + trust registry，
/// 通过 ApprovalEvidenceProvenanceId 关联记录后，逐字段比较。
/// 不写 mainline、不 seal、不 approve、不启用 formal retrieval。
/// </summary>
public static class FormalRetrievalPromotionApprovalTrustChainValidator
{
    public static TrustChainValidationResult Validate(
        FormalRetrievalPromotionApprovalEvidence evidence,
        FormalRetrievalPromotionApprovalTrustRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(registry);

        var reasons = new List<string>();
        var fields = new List<string>();

        // step 1: 通过 ApprovalEvidenceProvenanceId 关联记录。
        var matchedIndex = -1;
        FormalRetrievalPromotionApprovalTrustedProvenanceRecord? matched = null;
        for (var i = 0; i < registry.TrustedProvenanceRecords.Count; i++)
        {
            var record = registry.TrustedProvenanceRecords[i];
            if (string.Equals(record.ApprovalEvidenceProvenanceId, evidence.ApprovalEvidenceProvenanceId, StringComparison.Ordinal))
            {
                matched = record;
                matchedIndex = i;
                break;
            }
        }

        if (matched is null)
        {
            reasons.Add(TrustChainMismatchReasons.EvidenceProvenanceNotFoundInRegistry);
            fields.Add(nameof(FormalRetrievalPromotionApprovalEvidence.ApprovalEvidenceProvenanceId));
            return new TrustChainValidationResult
            {
                ChainComplete = false,
                Status = TrustChainValidationStatuses.TrustChainBroken,
                MismatchReasons = reasons.ToArray(),
                MismatchFields = fields.ToArray(),
                MatchedProvenanceId = null,
                MatchedRecordIndex = -1
            };
        }

        // step 2: AllowedSourceKinds 注册表层成员关系。
        var isKindAllowed = registry.AllowedSourceKinds.Any(k =>
            string.Equals(k, evidence.ApprovalEvidenceSourceKind, StringComparison.Ordinal));
        if (!isKindAllowed)
        {
            reasons.Add(TrustChainMismatchReasons.EvidenceSourceKindNotAllowed);
            fields.Add(nameof(FormalRetrievalPromotionApprovalEvidence.ApprovalEvidenceSourceKind));
        }

        // step 3: 跨字段逐项比较 — 全部用 Ordinal 字符串等同。
        AddIfNotEqual(reasons, fields,
            evidence.ApprovalEvidenceSourceKind, matched.ApprovalEvidenceSourceKind,
            TrustChainMismatchReasons.EvidenceSourceKindMismatch,
            nameof(FormalRetrievalPromotionApprovalEvidence.ApprovalEvidenceSourceKind));

        AddIfNotEqual(reasons, fields,
            evidence.ApprovalEvidenceChecksum, matched.ApprovalEvidenceChecksum,
            TrustChainMismatchReasons.EvidenceChecksumMismatch,
            nameof(FormalRetrievalPromotionApprovalEvidence.ApprovalEvidenceChecksum));

        AddIfNotEqual(reasons, fields,
            evidence.ApprovalEvidenceProvidedBy, matched.ApprovalEvidenceProvidedBy,
            TrustChainMismatchReasons.EvidenceProvidedByMismatch,
            nameof(FormalRetrievalPromotionApprovalEvidence.ApprovalEvidenceProvidedBy));

        AddIfNotEqual(reasons, fields,
            evidence.SourceApprovalRequestId, matched.SourceApprovalRequestId,
            TrustChainMismatchReasons.EvidenceSourceApprovalRequestIdMismatch,
            nameof(FormalRetrievalPromotionApprovalEvidence.SourceApprovalRequestId));

        AddIfNotEqual(reasons, fields,
            evidence.BoundPendingApprovalGateOperationId, matched.BoundPendingApprovalGateOperationId,
            TrustChainMismatchReasons.EvidenceBoundPendingApprovalGateOperationIdMismatch,
            nameof(FormalRetrievalPromotionApprovalEvidence.BoundPendingApprovalGateOperationId));

        AddIfNotEqual(reasons, fields,
            evidence.ApprovalEvidenceTrustMode, matched.TrustMode,
            TrustChainMismatchReasons.EvidenceTrustModeMismatch,
            nameof(FormalRetrievalPromotionApprovalEvidence.ApprovalEvidenceTrustMode));

        // step 4: ApprovalScopes ⊆ record.AllowedScopes（按字符串集合）。
        var allowedScopeSet = new HashSet<string>(matched.AllowedScopes, StringComparer.Ordinal);
        var hasOutOfScope = evidence.ApprovalScopes.Any(s => !allowedScopeSet.Contains(s));
        if (hasOutOfScope)
        {
            reasons.Add(TrustChainMismatchReasons.EvidenceApprovalScopesNotSubsetOfRecord);
            fields.Add(nameof(FormalRetrievalPromotionApprovalEvidence.ApprovalScopes));
        }

        // step 5: 有效期 — evidence.ApprovalTimestamp 必须早于 record.ValidUntil。
        if (evidence.ApprovalTimestamp > matched.ValidUntil)
        {
            reasons.Add(TrustChainMismatchReasons.EvidenceApprovalTimestampAfterRecordValidUntil);
            fields.Add(nameof(FormalRetrievalPromotionApprovalEvidence.ApprovalTimestamp));
        }

        var chainComplete = reasons.Count == 0;
        return new TrustChainValidationResult
        {
            ChainComplete = chainComplete,
            Status = chainComplete
                ? TrustChainValidationStatuses.TrustChainValidated
                : TrustChainValidationStatuses.TrustChainBroken,
            MismatchReasons = reasons.Distinct(StringComparer.Ordinal).ToArray(),
            MismatchFields = fields.Distinct(StringComparer.Ordinal).ToArray(),
            MatchedProvenanceId = matched.ApprovalEvidenceProvenanceId,
            MatchedRecordIndex = matchedIndex
        };
    }

    private static void AddIfNotEqual(
        List<string> reasons,
        List<string> fields,
        string left,
        string right,
        string reason,
        string fieldName)
    {
        if (!string.Equals(left, right, StringComparison.Ordinal))
        {
            reasons.Add(reason);
            fields.Add(fieldName);
        }
    }
}
