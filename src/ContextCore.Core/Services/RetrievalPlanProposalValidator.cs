using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// Planning shadow 专用 proposal validator。它只影响 shadow execution，不参与正式 retrieval。
/// </summary>
public sealed class RetrievalPlanProposalValidator
{
    private const int DefaultSafeFinalTopK = 10;
    private readonly RetrievalPlanSafetyProfile _safetyProfile;

    public RetrievalPlanProposalValidator(RetrievalPlanSafetyProfile? safetyProfile = null)
    {
        _safetyProfile = safetyProfile ?? RetrievalPlanSafetyProfile.CreateDefault();
    }

    public RetrievalPlanProposalValidationResult Validate(
        RetrievalPlanProposal proposal,
        ContextRetrievalRequest request)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(request);

        var repairReasons = ExtractProposalServiceRepairReasons(proposal).ToList();
        var finalTopKClamped = repairReasons.Any(reason =>
            reason.Contains("finalTopK.clamped", StringComparison.OrdinalIgnoreCase));
        var vectorDisabled = false;
        var repaired = RepairProposal(
            proposal,
            request,
            repairReasons,
            ref finalTopKClamped,
            ref vectorDisabled);
        var invalidReasons = ValidateProposalShape(repaired, request);
        if (invalidReasons.Count > 0)
        {
            var safeFinalTopK = ResolveSafeFinalTopK(request);
            var legacySafePlan = CreateLegacySafePlan(proposal, request, safeFinalTopK);
            return new RetrievalPlanProposalValidationResult
            {
                ValidatorApplied = true,
                ValidPlan = false,
                RepairedPlan = repairReasons.Count > 0,
                FallbackToLegacySafePlan = true,
                LegacySafeMode = true,
                EffectiveProposal = legacySafePlan,
                RejectedPlanReasons = invalidReasons.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                ValidatorRepairReasons = repairReasons.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                FallbackRootCause = string.Join(" | ", invalidReasons),
                AfterRepairPlanSummary = BuildPlanSummary(legacySafePlan),
                FinalTopKClamped = finalTopKClamped,
                VectorDisabled = vectorDisabled
            };
        }

        return new RetrievalPlanProposalValidationResult
        {
            ValidatorApplied = true,
            ValidPlan = true,
            RepairedPlan = repairReasons.Count > 0,
            FallbackToLegacySafePlan = false,
            LegacySafeMode = false,
            EffectiveProposal = repaired,
            RejectedPlanReasons = Array.Empty<string>(),
            ValidatorRepairReasons = repairReasons.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            FallbackRootCause = string.Empty,
            AfterRepairPlanSummary = BuildPlanSummary(repaired),
            FinalTopKClamped = finalTopKClamped,
            VectorDisabled = vectorDisabled
        };
    }

    public ShadowSelectedItemValidationResult ValidateSelectedItems(
        IReadOnlyList<ContextRetrievalCandidate> selectedItems,
        RetrievalPlanProposal proposal,
        ContextRetrievalRequest request)
    {
        ArgumentNullException.ThrowIfNull(selectedItems);
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(request);

        var mustNotHitRefs = ParseRefs(request.Metadata, "attention.mustNotHit", "eval.mustNotHit", "mustNotHit");
        var allowLifecycleRisk = proposal.AuditMode || proposal.ConflictMode || PlanAllowsDeprecated(request.Plan);
        var kept = new List<ContextRetrievalCandidate>();
        var rejectedReasons = new List<string>();
        var deprecatedBlockedCount = 0;

        foreach (var item in selectedItems)
        {
            if (MatchesAnyRef(item, mustNotHitRefs))
            {
                rejectedReasons.Add($"must_not_hit_shadow_blocked:{item.SourceId}");
                continue;
            }

            var status = ResolveLifecycleStatus(item);
            if (IsRejected(status))
            {
                rejectedReasons.Add($"rejected_lifecycle_blocked:{item.SourceId}");
                continue;
            }

            if (!allowLifecycleRisk && IsDeprecatedOrSuperseded(status, item))
            {
                rejectedReasons.Add($"non_audit_lifecycle_blocked:{item.SourceId}:{NormalizeStatus(status)}");
                deprecatedBlockedCount++;
                continue;
            }

            if (!proposal.AuditMode && IsHistoricalOrDeprecatedEvidence(item))
            {
                rejectedReasons.Add($"normal_path_historical_evidence_blocked:{item.SourceId}");
                deprecatedBlockedCount++;
                continue;
            }

            kept.Add(item);
        }

        return new ShadowSelectedItemValidationResult
        {
            SelectedItems = kept,
            RejectedReasons = rejectedReasons.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            DeprecatedBlockedCount = deprecatedBlockedCount,
            MustNotHitAddedAfterValidation = kept.Count(item => MatchesAnyRef(item, mustNotHitRefs)),
            LifecycleViolationAfterValidation = kept.Count(item => IsLifecycleViolationAfterValidation(item, allowLifecycleRisk))
        };
    }

    private RetrievalPlanProposal RepairProposal(
        RetrievalPlanProposal proposal,
        ContextRetrievalRequest request,
        List<string> repairReasons,
        ref bool finalTopKClamped,
        ref bool vectorDisabled)
    {
        var finalTopK = proposal.FinalTopK;
        var keywordTopK = proposal.KeywordTopK;
        var memoryTopK = proposal.MemoryTopK;
        var relationTopK = proposal.RelationTopK;
        var vectorTopK = proposal.VectorTopK;
        var useVector = proposal.UseVector;

        var maxFinalTopK = ResolveSafeFinalTopK(request);
        if (finalTopK > maxFinalTopK)
        {
            repairReasons.Add($"validator.finalTopK.clamped:{finalTopK}->{maxFinalTopK}");
            finalTopK = maxFinalTopK;
            finalTopKClamped = true;
        }

        keywordTopK = ClampChannelTopK(
            "keywordTopK",
            keywordTopK,
            _safetyProfile.MaxKeywordTopK,
            repairReasons);
        memoryTopK = ClampChannelTopK(
            "memoryTopK",
            memoryTopK,
            _safetyProfile.MaxMemoryTopK,
            repairReasons);
        relationTopK = ClampChannelTopK(
            "relationTopK",
            relationTopK,
            _safetyProfile.MaxRelationTopK,
            repairReasons);

        if (!_safetyProfile.AllowVector)
        {
            if (useVector || vectorTopK != 0)
            {
                repairReasons.Add($"validator.vector.disabled:UseVector={useVector};VectorTopK={vectorTopK}->0");
            }

            useVector = false;
            vectorTopK = 0;
            vectorDisabled = true;
        }
        else
        {
            vectorTopK = ClampChannelTopK(
                "vectorTopK",
                vectorTopK,
                _safetyProfile.MaxVectorTopK,
                repairReasons);
        }

        var validatorReasons = repairReasons
            .Where(reason => reason.StartsWith("validator.", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return new RetrievalPlanProposal
        {
            OperationId = proposal.OperationId,
            WorkspaceId = proposal.WorkspaceId,
            CollectionId = proposal.CollectionId,
            Intent = proposal.Intent,
            Mode = proposal.Mode,
            UseExact = proposal.UseExact,
            UseKeyword = proposal.UseKeyword,
            UseShortTermMemory = proposal.UseShortTermMemory,
            UseWorkingMemory = proposal.UseWorkingMemory,
            UseStableMemory = proposal.UseStableMemory,
            UseRelations = proposal.UseRelations,
            UseVector = useVector,
            AuditMode = proposal.AuditMode,
            ConflictMode = proposal.ConflictMode,
            KeywordTopK = keywordTopK,
            MemoryTopK = memoryTopK,
            RelationTopK = relationTopK,
            VectorTopK = vectorTopK,
            FinalTopK = finalTopK,
            Confidence = proposal.Confidence,
            Reasons = proposal.Reasons
                .Concat(validatorReasons)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Warnings = proposal.Warnings
                .Concat(validatorReasons.Select(reason => $"validator repair: {reason}"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    private IReadOnlyList<string> ValidateProposalShape(
        RetrievalPlanProposal proposal,
        ContextRetrievalRequest request)
    {
        var reasons = new List<string>();
        if (string.IsNullOrWhiteSpace(proposal.OperationId))
        {
            reasons.Add("invalid proposal: operation id is empty");
        }

        if (string.IsNullOrWhiteSpace(proposal.Intent))
        {
            reasons.Add("invalid proposal: intent is empty");
        }

        if (!string.Equals(proposal.WorkspaceId, request.WorkspaceId, StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("invalid proposal: workspace mismatch");
        }

        if (!string.IsNullOrWhiteSpace(proposal.CollectionId)
            && !string.Equals(proposal.CollectionId, request.CollectionId, StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("invalid proposal: collection mismatch");
        }

        if (proposal.FinalTopK <= 0)
        {
            reasons.Add("invalid proposal: final topK must be positive");
        }

        if (proposal.KeywordTopK < 0 || proposal.MemoryTopK < 0 || proposal.RelationTopK < 0 || proposal.VectorTopK < 0)
        {
            reasons.Add("invalid proposal: topK cannot be negative");
        }

        if (proposal.UseVector && !_safetyProfile.AllowVector)
        {
            reasons.Add("invalid proposal: vector channel is disabled in shadow");
        }

        if (!proposal.UseExact
            && !proposal.UseKeyword
            && !proposal.UseShortTermMemory
            && !proposal.UseWorkingMemory
            && !proposal.UseStableMemory
            && !proposal.UseRelations)
        {
            reasons.Add("invalid proposal: no retrieval channel enabled");
        }

        return reasons;
    }

    private int ResolveSafeFinalTopK(ContextRetrievalRequest request)
    {
        var profileTopK = _safetyProfile.MaxFinalTopK > 0
            ? _safetyProfile.MaxFinalTopK
            : DefaultSafeFinalTopK;
        var requestTopK = request.TopK > 0 ? request.TopK : profileTopK;
        return Math.Min(profileTopK, requestTopK);
    }

    private static int ClampChannelTopK(
        string name,
        int value,
        int maxValue,
        List<string> repairReasons)
    {
        if (maxValue < 0 || value <= maxValue)
        {
            return value;
        }

        repairReasons.Add($"validator.{name}.clamped:{value}->{maxValue}");
        return maxValue;
    }

    private static IEnumerable<string> ExtractProposalServiceRepairReasons(RetrievalPlanProposal proposal)
    {
        return proposal.Reasons
            .Where(reason =>
                reason.StartsWith("safety.", StringComparison.OrdinalIgnoreCase)
                && reason.Contains(".clamped:", StringComparison.OrdinalIgnoreCase))
            .Select(reason => $"proposalService:{reason}");
    }

    private static RetrievalPlanProposal CreateLegacySafePlan(
        RetrievalPlanProposal proposal,
        ContextRetrievalRequest request,
        int safeFinalTopK)
    {
        var plan = request.Plan;
        var auditMode = (plan?.AuditAnchors.Count ?? 0) > 0;
        var conflictMode = (plan?.ConflictAnchors.Count ?? 0) > 0;
        var candidateTake = request.CandidateTake > 0
            ? request.CandidateTake
            : Math.Max(20, safeFinalTopK * 4);

        return new RetrievalPlanProposal
        {
            OperationId = string.IsNullOrWhiteSpace(proposal.OperationId)
                ? $"legacy-safe-{Guid.NewGuid():N}"
                : proposal.OperationId,
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            Intent = "LegacySafePlan",
            Mode = string.IsNullOrWhiteSpace(proposal.Mode) ? "LegacySafe" : proposal.Mode,
            UseExact = true,
            UseKeyword = request.IncludeKeywordRecall,
            UseShortTermMemory = request.IncludeWorkingMemory,
            UseWorkingMemory = request.IncludeWorkingMemory,
            UseStableMemory = request.IncludeStableMemory,
            UseRelations = request.IncludeRelationExpansion,
            UseVector = false,
            AuditMode = auditMode,
            ConflictMode = conflictMode,
            KeywordTopK = request.IncludeKeywordRecall ? candidateTake : 0,
            MemoryTopK = request.IncludeWorkingMemory || request.IncludeStableMemory ? candidateTake : 0,
            RelationTopK = request.IncludeRelationExpansion ? Math.Min(candidateTake, Math.Max(2, safeFinalTopK / 3)) : 0,
            VectorTopK = 0,
            FinalTopK = safeFinalTopK,
            Confidence = proposal.Confidence,
            Reasons =
            [
                "validator:LegacySafePlan",
                "inherits legacy lifecycle restrictions",
                "inherits legacy relation quota reserve",
                "inherits legacy packing safety caps"
            ],
            Warnings = proposal.Warnings
        };
    }

    private static string BuildPlanSummary(RetrievalPlanProposal proposal)
    {
        return string.Join(
            ';',
            $"intent={proposal.Intent}",
            $"mode={proposal.Mode}",
            $"keywordTopK={proposal.KeywordTopK}",
            $"memoryTopK={proposal.MemoryTopK}",
            $"relationTopK={proposal.RelationTopK}",
            $"vectorTopK={proposal.VectorTopK}",
            $"finalTopK={proposal.FinalTopK}",
            $"useVector={proposal.UseVector.ToString().ToLowerInvariant()}",
            $"audit={proposal.AuditMode.ToString().ToLowerInvariant()}",
            $"conflict={proposal.ConflictMode.ToString().ToLowerInvariant()}");
    }

    private static bool PlanAllowsDeprecated(RetrievalPlan? plan)
    {
        return plan is not null
            && (plan.AuditAnchors.Count > 0 || plan.ConflictAnchors.Count > 0);
    }

    private static IReadOnlyList<string> ParseRefs(
        IReadOnlyDictionary<string, string> metadata,
        params string[] keys)
    {
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            if (!metadata.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            foreach (var part in value.Split([',', ';', '|', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = part.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    refs.Add(trimmed);
                }
            }
        }

        return refs.ToArray();
    }

    private static bool MatchesAnyRef(
        ContextRetrievalCandidate item,
        IReadOnlyList<string> refs)
    {
        return refs.Count > 0 && refs.Any(refId =>
            string.Equals(item.SourceId, refId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.CandidateId, refId, StringComparison.OrdinalIgnoreCase)
            || item.SourceRefs.Contains(refId, StringComparer.OrdinalIgnoreCase)
            || item.Tags.Contains(refId, StringComparer.OrdinalIgnoreCase));
    }

    private static string ResolveLifecycleStatus(ContextRetrievalCandidate item)
    {
        if (item.Metadata.TryGetValue("lifecycleStatus", out var lifecycleStatus)
            && !string.IsNullOrWhiteSpace(lifecycleStatus))
        {
            return lifecycleStatus;
        }

        if (item.Metadata.TryGetValue("status", out var status)
            && !string.IsNullOrWhiteSpace(status))
        {
            return status;
        }

        if (item.Metadata.TryGetValue("processState", out var processState)
            && !string.IsNullOrWhiteSpace(processState))
        {
            return processState;
        }

        return string.Empty;
    }

    private static bool IsRejected(string status)
    {
        return status.Equals(ContextMemoryStatus.Rejected.ToString(), StringComparison.OrdinalIgnoreCase)
            || status.Equals("rejected", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDeprecatedOrSuperseded(string status, ContextRetrievalCandidate item)
    {
        return status.Equals(ContextMemoryStatus.Deprecated.ToString(), StringComparison.OrdinalIgnoreCase)
            || status.Equals("deprecated", StringComparison.OrdinalIgnoreCase)
            || status.Equals("superseded", StringComparison.OrdinalIgnoreCase)
            || item.Metadata.ContainsKey("supersededBy")
            || item.Metadata.TryGetValue("isSuperseded", out var isSuperseded)
                && bool.TryParse(isSuperseded, out var parsed)
                && parsed;
    }

    private static bool IsHistoricalOrDeprecatedEvidence(ContextRetrievalCandidate item)
    {
        return ContainsLifecycleEvidenceMarker(item.Type)
            || item.Tags.Any(ContainsLifecycleEvidenceMarker)
            || item.SourceRefs.Any(ContainsLifecycleEvidenceMarker)
            || item.Metadata.Any(pair =>
                ContainsLifecycleEvidenceMarker(pair.Key)
                || ContainsLifecycleEvidenceMarker(pair.Value));
    }

    private static bool ContainsLifecycleEvidenceMarker(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && (value.Contains("historical_context", StringComparison.OrdinalIgnoreCase)
                || value.Contains("deprecated_evidence", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLifecycleViolationAfterValidation(ContextRetrievalCandidate item, bool allowLifecycleRisk)
    {
        var status = ResolveLifecycleStatus(item);
        return IsRejected(status)
            || (!allowLifecycleRisk && IsDeprecatedOrSuperseded(status, item));
    }

    private static string NormalizeStatus(string status)
    {
        return string.IsNullOrWhiteSpace(status) ? "unknown" : status.Trim();
    }
}
