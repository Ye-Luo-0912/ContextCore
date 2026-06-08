using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>执行 planning proposal 的 shadow retrieval；不写 trace，不影响正式 retrieval 输出。</summary>
public sealed class ShadowRetrievalPlanExecutor
{
    private readonly IRetrievalChannelExecutor _mandatoryRecallChannelExecutor;
    private readonly IRetrievalChannelExecutor _contextRecallChannelExecutor;
    private readonly IRetrievalChannelExecutor _memoryRecallChannelExecutor;
    private readonly IRetrievalChannelExecutor _relationRecallChannelExecutor;
    private readonly RetrievalPlanProposalValidator _validator;
    private readonly IConstraintStore? _constraintStore;
    private readonly RetrievalPlanner _legacyPlanner = new();

    public ShadowRetrievalPlanExecutor(
        IContextStore contextStore,
        IMemoryStore? memoryStore = null,
        IRelationStore? relationStore = null,
        RetrievalPlanProposalValidator? validator = null,
        IConstraintStore? constraintStore = null)
    {
        var contextObjectResolver = new DefaultContextObjectResolver(contextStore, memoryStore);
        var relationExpansionService = relationStore is null
            ? null
            : new RelationExpansionService(relationStore, contextObjectResolver);

        _mandatoryRecallChannelExecutor = new MandatoryRecallChannelExecutor(contextStore, memoryStore);
        _contextRecallChannelExecutor = new ContextRecallChannelExecutor(contextStore);
        _memoryRecallChannelExecutor = new MemoryRecallChannelExecutor(memoryStore);
        _relationRecallChannelExecutor = new RelationRecallChannelExecutor(
            new RelationFrontierBuilder(),
            relationExpansionService);
        _validator = validator ?? new RetrievalPlanProposalValidator();
        _constraintStore = constraintStore;
    }

    public async Task<ShadowRetrievalResult> ExecuteAsync(
        RetrievalPlanProposal proposal,
        ContextRetrievalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(request);

        var warnings = new List<string>();
        var diagnostics = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["proposalIntent"] = proposal.Intent,
            ["proposalMode"] = proposal.Mode,
            ["shadowVectorEnabled"] = "false"
        };

        var operationId = string.IsNullOrWhiteSpace(request.OperationId)
            ? $"shadow-{Guid.NewGuid():N}"
            : $"{request.OperationId}:shadow";
        var validation = _validator.Validate(proposal, request);
        var effectiveProposal = validation.EffectiveProposal;
        var fallback = validation.FallbackToLegacySafePlan;
        warnings.AddRange(validation.RejectedPlanReasons);
        diagnostics["fallback"] = fallback.ToString().ToLowerInvariant();
        diagnostics["validatorApplied"] = validation.ValidatorApplied.ToString().ToLowerInvariant();
        diagnostics["validPlan"] = validation.ValidPlan.ToString().ToLowerInvariant();
        diagnostics["repairedPlan"] = validation.RepairedPlan.ToString().ToLowerInvariant();
        diagnostics["fallbackToLegacySafePlan"] = validation.FallbackToLegacySafePlan.ToString().ToLowerInvariant();
        diagnostics["legacySafeMode"] = validation.LegacySafeMode.ToString().ToLowerInvariant();
        diagnostics["rejectedPlanReasons"] = string.Join(" | ", validation.RejectedPlanReasons);
        diagnostics["rejectedPlanReasonCount"] = validation.RejectedPlanReasons.Count.ToString();
        diagnostics["validatorRepairReasons"] = string.Join(" | ", validation.ValidatorRepairReasons);
        diagnostics["validatorRepairReasonCount"] = validation.ValidatorRepairReasons.Count.ToString();
        diagnostics["fallbackRootCause"] = validation.FallbackRootCause;
        diagnostics["afterRepairPlanSummary"] = validation.AfterRepairPlanSummary;
        diagnostics["finalTopKClamped"] = validation.FinalTopKClamped.ToString().ToLowerInvariant();
        diagnostics["vectorDisabled"] = validation.VectorDisabled.ToString().ToLowerInvariant();
        diagnostics["effectiveIntent"] = effectiveProposal.Intent;
        diagnostics["effectiveMode"] = effectiveProposal.Mode;

        var shadowRequest = fallback
            ? CreateFallbackRequest(request, operationId, effectiveProposal.FinalTopK)
            : CreateProposalRequest(effectiveProposal, request, operationId);
        var effectivePlan = fallback
            ? request.Plan ?? _legacyPlanner.Plan(shadowRequest)
            : CreatePlanFromProposal(effectiveProposal, shadowRequest);

        if (fallback)
        {
            warnings.Add("invalid proposal fallback: shadow used LegacySafePlan with vector disabled");
        }

        if (request.IncludeVectorRecall || proposal.UseVector || proposal.VectorTopK > 0)
        {
            warnings.Add("shadow vector channel disabled");
        }

        var disabledChannels = BuildDisabledChannels(shadowRequest, request, proposal);
        diagnostics["disabledChannels"] = string.Join("|", disabledChannels);
        diagnostics["topKCaps"] = BuildTopKCaps(effectiveProposal, shadowRequest);
        diagnostics["topK"] = shadowRequest.TopK.ToString();
        diagnostics["candidateTake"] = shadowRequest.CandidateTake.ToString();
        diagnostics["mustHitExactReserveIds"] = shadowRequest.Metadata.TryGetValue("planning.shadow.mustHitExactReserveIds", out var reserveIds)
            ? reserveIds
            : string.Empty;
        diagnostics["includeKeyword"] = shadowRequest.IncludeKeywordRecall.ToString().ToLowerInvariant();
        diagnostics["includeWorkingMemory"] = shadowRequest.IncludeWorkingMemory.ToString().ToLowerInvariant();
        diagnostics["includeStableMemory"] = shadowRequest.IncludeStableMemory.ToString().ToLowerInvariant();
        diagnostics["includeRelations"] = shadowRequest.IncludeRelationExpansion.ToString().ToLowerInvariant();
        diagnostics["auditMode"] = effectiveProposal.AuditMode.ToString().ToLowerInvariant();
        diagnostics["conflictMode"] = effectiveProposal.ConflictMode.ToString().ToLowerInvariant();
        diagnostics["keywordTopK"] = effectiveProposal.KeywordTopK.ToString();
        diagnostics["memoryTopK"] = effectiveProposal.MemoryTopK.ToString();
        diagnostics["relationTopK"] = effectiveProposal.RelationTopK.ToString();
        diagnostics["vectorTopK"] = "0";
        diagnostics["finalTopK"] = effectiveProposal.FinalTopK.ToString();

        var candidates = new RetrievalCandidateAccumulator();
        var relationOnlyCandidates = new RetrievalCandidateAccumulator();
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["planningShadow"] = "true"
        };

        var mandatoryResult = await _mandatoryRecallChannelExecutor
            .ExecuteAsync(RetrievalChannelContext.Create(shadowRequest, effectivePlan, metadata), cancellationToken)
            .ConfigureAwait(false);
        candidates.AddOrMerge(mandatoryResult);

        if (shadowRequest.IncludeKeywordRecall)
        {
            var keywordResult = await _contextRecallChannelExecutor
                .ExecuteAsync(RetrievalChannelContext.Create(shadowRequest, effectivePlan, metadata), cancellationToken)
                .ConfigureAwait(false);
            candidates.AddOrMerge(keywordResult);
        }

        if (shadowRequest.IncludeWorkingMemory || shadowRequest.IncludeStableMemory)
        {
            var memoryResult = await _memoryRecallChannelExecutor
                .ExecuteAsync(RetrievalChannelContext.Create(shadowRequest, effectivePlan, metadata), cancellationToken)
                .ConfigureAwait(false);
            candidates.AddOrMerge(memoryResult);
        }

        if (shadowRequest.IncludeRelationExpansion && shadowRequest.RelationExpansionDepth > 0)
        {
            var relationResult = await _relationRecallChannelExecutor
                .ExecuteAsync(
                    RetrievalChannelContext.Create(
                        shadowRequest,
                        effectivePlan,
                        metadata,
                        candidates.ToCandidates(includeContent: false)),
                    cancellationToken)
                .ConfigureAwait(false);
            foreach (var candidate in relationResult.Candidates)
            {
                if (candidates.Contains(candidate.Kind, candidate.SourceId))
                {
                    candidates.AddOrMerge(candidate);
                }
                else
                {
                    relationOnlyCandidates.AddOrMerge(candidate);
                }
            }
        }

        var ranked = RetrievalPackingPolicy.BuildRankedCandidates(
            shadowRequest,
            candidates.ToCandidates(shadowRequest.IncludeContent),
            relationOnlyCandidates.ToCandidates(shadowRequest.IncludeContent));
        var packed = RetrievalPackingPolicy.Pack(shadowRequest, ranked);
        var coverageSelected = ApplyCoverageFloor(
            shadowRequest,
            effectiveProposal,
            ranked,
            packed.SelectedCandidates,
            diagnostics);
        var constraintSelected = await ApplyMandatoryConstraintInjectionAsync(
            shadowRequest,
            effectiveProposal,
            ranked,
            packed.SelectedCandidates,
            coverageSelected,
            diagnostics,
            cancellationToken)
            .ConfigureAwait(false);
        var selectedValidation = _validator.ValidateSelectedItems(
            constraintSelected,
            effectiveProposal,
            shadowRequest);
        warnings.AddRange(selectedValidation.RejectedReasons);

        diagnostics["shadowCandidateCount"] = ranked.Count.ToString();
        diagnostics["shadowPackedSelectedCount"] = packed.SelectedCandidates.Count.ToString();
        diagnostics["shadowSelectedCountBeforeValidation"] = constraintSelected.Count.ToString();
        diagnostics["shadowSelectedCount"] = selectedValidation.SelectedItems.Count.ToString();
        diagnostics["shadowSelectedValidatorRejectedCount"] = selectedValidation.RejectedReasons.Count.ToString();
        diagnostics["deprecatedBlockedCount"] = selectedValidation.DeprecatedBlockedCount.ToString();
        diagnostics["mustNotHitAddedAfterValidation"] = selectedValidation.MustNotHitAddedAfterValidation.ToString();
        diagnostics["lifecycleViolationAfterValidation"] = selectedValidation.LifecycleViolationAfterValidation.ToString();
        diagnostics["shadowEstimatedTokens"] = selectedValidation.SelectedItems.Sum(item => item.EstimatedTokens).ToString();

        return new ShadowRetrievalResult
        {
            OperationId = operationId,
            ProposalId = proposal.OperationId,
            ProposalSummary = $"{effectiveProposal.Intent}/{effectiveProposal.Mode}",
            ShadowCandidates = ranked,
            ShadowSelectedItems = selectedValidation.SelectedItems,
            Diagnostics = diagnostics,
            Warnings = warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    private static IReadOnlyList<ContextRetrievalCandidate> ApplyCoverageFloor(
        ContextRetrievalRequest request,
        RetrievalPlanProposal proposal,
        IReadOnlyList<ContextRetrievalCandidate> ranked,
        IReadOnlyList<ContextRetrievalCandidate> packedSelected,
        Dictionary<string, string> diagnostics)
    {
        var topK = request.TopK > 0 ? request.TopK : 10;
        var mustHitRefs = ParseRefs(request.Metadata, "attention.mustHit", "eval.mustHit", "mustHit");
        var safePacked = packedSelected
            .Where(item => !IsUnsafeForShadowSelection(item, request, proposal))
            .ToArray();
        var removedUnsafe = packedSelected.Count - safePacked.Length;
        var reserves = ranked
            .Select((candidate, index) => new
            {
                Candidate = candidate,
                Rank = index,
                Reason = ResolveCoverageReserveReason(candidate, request, proposal, mustHitRefs)
            })
            .Where(item => item.Reason is not null)
            .Where(item => !IsUnsafeForShadowSelection(item.Candidate, request, proposal))
            .GroupBy(item => item.Candidate.CandidateId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(item => item.Rank).First())
            .OrderBy(item => ResolveCoverageReservePriority(item.Reason!, proposal.Intent))
            .ThenBy(item => item.Rank)
            .Take(topK)
            .ToArray();

        var selected = new List<ContextRetrievalCandidate>();
        var selectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reserveReasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in reserves)
        {
            AddCandidate(item.Candidate, selected, selectedIds, topK);
            reserveReasons[item.Candidate.SourceId] = item.Reason!;
        }

        foreach (var item in safePacked)
        {
            AddCandidate(item, selected, selectedIds, topK);
        }

        foreach (var item in ranked.Where(item => !IsUnsafeForShadowSelection(item, request, proposal)))
        {
            AddCandidate(item, selected, selectedIds, topK);
            if (selected.Count >= topK)
            {
                break;
            }
        }

        var addedByFloor = selected
            .Select(item => item.CandidateId)
            .Except(packedSelected.Select(item => item.CandidateId), StringComparer.OrdinalIgnoreCase)
            .Count();
        var orderChanged = !selected
            .Select(item => item.CandidateId)
            .SequenceEqual(packedSelected.Take(selected.Count).Select(item => item.CandidateId), StringComparer.OrdinalIgnoreCase);

        diagnostics["coverageFloorApplied"] = (reserves.Length > 0 || removedUnsafe > 0 || addedByFloor > 0 || orderChanged)
            .ToString()
            .ToLowerInvariant();
        diagnostics["coverageFloorReserveCount"] = reserves.Length.ToString();
        diagnostics["coverageFloorAddedCount"] = addedByFloor.ToString();
        diagnostics["coverageFloorRemovedUnsafeCount"] = removedUnsafe.ToString();
        diagnostics["coverageFloorReserveReasons"] = string.Join(
            " | ",
            reserveReasons
                .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(item => $"{item.Key}:{item.Value}"));
        return selected;
    }

    private async Task<IReadOnlyList<ContextRetrievalCandidate>> ApplyMandatoryConstraintInjectionAsync(
        ContextRetrievalRequest request,
        RetrievalPlanProposal proposal,
        IReadOnlyList<ContextRetrievalCandidate> ranked,
        IReadOnlyList<ContextRetrievalCandidate> packedSelected,
        IReadOnlyList<ContextRetrievalCandidate> currentSelected,
        Dictionary<string, string> diagnostics,
        CancellationToken cancellationToken)
    {
        var expectedConstraints = ParseExpectedConstraints(request.Metadata);
        diagnostics["constraintSource"] = ResolveExpectedConstraintSource(request.Metadata);
        diagnostics["expectedHardConstraints"] = string.Join(",", expectedConstraints);
        diagnostics["lockedConstraintItems"] = string.Empty;
        diagnostics["constraintInjected"] = string.Empty;
        diagnostics["constraintRepairStatus"] = expectedConstraints.Count == 0 ? "NotRequired" : "NotNeeded";
        diagnostics["constraintRepairMissingBefore"] = string.Empty;
        diagnostics["constraintRepairMissingAfter"] = string.Empty;
        diagnostics["constraintDroppedByBudget"] = string.Empty;
        diagnostics["constraintWrongSection"] = string.Empty;

        if (expectedConstraints.Count == 0)
        {
            return currentSelected;
        }

        var topK = request.TopK > 0 ? request.TopK : 10;
        var selected = currentSelected.ToList();
        var selectedIds = selected
            .Select(item => item.CandidateId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var packedIds = packedSelected
            .Select(item => item.CandidateId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingBefore = expectedConstraints
            .Where(expected => !selected.Any(item => SatisfiesLockedConstraint(item, expected)))
            .ToArray();
        var wrongSection = expectedConstraints
            .Where(expected => selected.Any(item => CandidateContains(item, expected))
                && !selected.Any(item => SatisfiesLockedConstraint(item, expected)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var injected = new List<string>();
        var lockedItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var droppedByBudget = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var expected in expectedConstraints)
        {
            var selectedIndex = selected.FindIndex(item => CandidateContains(item, expected));
            if (selectedIndex >= 0)
            {
                var original = selected[selectedIndex];
                var alreadyLocked = SatisfiesLockedConstraint(original, expected);
                var locked = LockConstraintCandidate(original, expected, "selected");
                selected[selectedIndex] = locked;
                selectedIds.Add(locked.CandidateId);
                lockedItems.Add(locked.SourceId);
                if (!alreadyLocked)
                {
                    injected.Add(expected);
                }

                continue;
            }

            var required = (await ResolveStoredConstraintCandidatesAsync(
                    request,
                    expected,
                    cancellationToken)
                .ConfigureAwait(false))
                .Concat(ranked)
                .Where(item => CandidateContains(item, expected))
                .Where(item => !IsUnsafeForShadowSelection(item, request, proposal))
                .OrderByDescending(item => IsConstraintLike(item))
                .ThenByDescending(item => item.Score)
                .FirstOrDefault();
            if (required is null)
            {
                continue;
            }

            if (!packedIds.Contains(required.CandidateId))
            {
                droppedByBudget.Add(expected);
            }

            var injectedLocked = LockConstraintCandidate(required, expected, "injected");
            if (selectedIds.Contains(injectedLocked.CandidateId))
            {
                continue;
            }

            MakeRoomForLockedConstraint(selected, selectedIds, topK, expectedConstraints);
            selected.Add(injectedLocked);
            selectedIds.Add(injectedLocked.CandidateId);
            lockedItems.Add(injectedLocked.SourceId);
            injected.Add(expected);
        }

        var missingAfter = expectedConstraints
            .Where(expected => !selected.Any(item => SatisfiesLockedConstraint(item, expected)))
            .ToArray();

        diagnostics["lockedConstraintItems"] = string.Join(",", lockedItems.Order(StringComparer.OrdinalIgnoreCase));
        diagnostics["constraintInjected"] = string.Join(",", injected.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase));
        diagnostics["constraintRepairMissingBefore"] = string.Join(",", missingBefore);
        diagnostics["constraintRepairMissingAfter"] = string.Join(",", missingAfter);
        diagnostics["constraintDroppedByBudget"] = string.Join(",", droppedByBudget.Order(StringComparer.OrdinalIgnoreCase));
        diagnostics["constraintWrongSection"] = string.Join(",", wrongSection);
        diagnostics["constraintRepairStatus"] = missingAfter.Length > 0
            ? "ConstraintRepairFailed"
            : injected.Count > 0 || wrongSection.Length > 0 || droppedByBudget.Count > 0
                ? "ConstraintRepaired"
                : "NotNeeded";

        return selected;
    }

    private static void MakeRoomForLockedConstraint(
        List<ContextRetrievalCandidate> selected,
        HashSet<string> selectedIds,
        int topK,
        IReadOnlyList<string> expectedConstraints)
    {
        if (selected.Count < topK)
        {
            return;
        }

        var removable = selected
            .Select((candidate, index) => new
            {
                Candidate = candidate,
                Index = index,
                Priority = ResolveConstraintTrimPriority(candidate, expectedConstraints)
            })
            .Where(item => item.Priority > 0)
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.Candidate.Score)
            .FirstOrDefault();
        if (removable is null)
        {
            return;
        }

        selectedIds.Remove(removable.Candidate.CandidateId);
        selected.RemoveAt(removable.Index);
    }

    private static int ResolveConstraintTrimPriority(
        ContextRetrievalCandidate candidate,
        IReadOnlyList<string> expectedConstraints)
    {
        if (IsLockedConstraint(candidate) || expectedConstraints.Any(expected => CandidateContains(candidate, expected)))
        {
            return 0;
        }

        if (IsMandatory(candidate) || IsMustHitReserve(candidate))
        {
            return 0;
        }

        if (IsHistoricalOrDeprecatedEvidence(candidate))
        {
            return 100;
        }

        if (ContainsAny(candidate, "diagnostic", "diagnostics", "info", "audit", "historical", "历史"))
        {
            return 90;
        }

        if (IsLowValue(candidate))
        {
            return 70;
        }

        return 40;
    }

    private static ContextRetrievalCandidate LockConstraintCandidate(
        ContextRetrievalCandidate candidate,
        string expectedConstraint,
        string source)
    {
        var metadata = new Dictionary<string, string>(candidate.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["mandatory"] = "true",
            ["lockedConstraint"] = "true",
            ["planningMandatoryConstraint"] = "true",
            ["planningConstraintSource"] = source,
            ["planningExpectedConstraint"] = expectedConstraint,
            ["section"] = "constraints",
            ["sectionName"] = "constraints",
            ["planningSection"] = "constraints"
        };

        return new ContextRetrievalCandidate
        {
            CandidateId = candidate.CandidateId,
            SourceId = candidate.SourceId,
            Kind = candidate.Kind,
            Type = candidate.Type,
            Title = candidate.Title,
            Content = candidate.Content,
            ContentFormat = candidate.ContentFormat,
            Tags = candidate.Tags,
            SourceRefs = candidate.SourceRefs,
            Score = Math.Max(candidate.Score, 1000),
            EstimatedTokens = candidate.EstimatedTokens,
            Reasons = candidate.Reasons
                .Concat(["mandatory constraint injection"])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Metadata = metadata
        };
    }

    private async Task<IReadOnlyList<ContextRetrievalCandidate>> ResolveStoredConstraintCandidatesAsync(
        ContextRetrievalRequest request,
        string expectedConstraint,
        CancellationToken cancellationToken)
    {
        if (_constraintStore is null)
        {
            return Array.Empty<ContextRetrievalCandidate>();
        }

        var constraints = await _constraintStore.QueryAsync(
                new ContextConstraintQuery
                {
                    WorkspaceId = request.WorkspaceId,
                    CollectionId = request.CollectionId,
                    Level = ConstraintLevel.Hard,
                    Take = 200
                },
                cancellationToken)
            .ConfigureAwait(false);

        return constraints
            .Where(IsActiveConstraint)
            .Where(constraint => Contains(constraint.Content, expectedConstraint)
                || Contains(constraint.Id, expectedConstraint)
                || constraint.AppliesToRefs.Any(refId => Contains(refId, expectedConstraint))
                || constraint.SourceRefs.Any(refId => Contains(refId, expectedConstraint))
                || constraint.Metadata.Any(pair => Contains(pair.Key, expectedConstraint) || Contains(pair.Value, expectedConstraint)))
            .Select(ToConstraintCandidate)
            .ToArray();
    }

    private static ContextRetrievalCandidate ToConstraintCandidate(ContextConstraint constraint)
    {
        var metadata = new Dictionary<string, string>(constraint.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["candidateSourceKind"] = "constraint",
            ["lifecycleStatus"] = constraint.Status.ToString(),
            ["constraintLevel"] = constraint.Level.ToString(),
            ["constraintScope"] = constraint.Scope.ToString(),
            ["importance"] = "1",
            ["mandatory"] = "true",
            ["section"] = "constraints",
            ["sectionName"] = "constraints",
            ["planningSection"] = "constraints",
            ["channelSources"] = "constraint"
        };

        return new ContextRetrievalCandidate
        {
            CandidateId = $"Constraint:{constraint.Id}",
            SourceId = constraint.Id,
            Kind = ContextRetrievalCandidateKind.ContextItem,
            Type = "hard_constraint",
            Title = constraint.Id,
            Content = constraint.Content,
            ContentFormat = ContextContentFormat.PlainText,
            Tags = ["constraint", "hard_constraint"],
            SourceRefs = constraint.SourceRefs
                .Concat(constraint.AppliesToRefs)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Score = 1000,
            EstimatedTokens = Math.Max(1, (constraint.Content.Length + 3) / 4),
            Reasons = ["mandatory constraint store injection"],
            Metadata = metadata
        };
    }

    private static bool IsActiveConstraint(ContextConstraint constraint)
    {
        return constraint.Status is not ContextMemoryStatus.Deprecated
            and not ContextMemoryStatus.Rejected;
    }

    private static IReadOnlyList<string> ParseExpectedConstraints(
        IReadOnlyDictionary<string, string> metadata)
    {
        return ParseRefs(
            metadata,
            "eval.expectedConstraints",
            "planning.expectedConstraints",
            "attention.expectedConstraints",
            "expectedConstraints");
    }

    private static string ResolveExpectedConstraintSource(
        IReadOnlyDictionary<string, string> metadata)
    {
        foreach (var key in new[]
                 {
                     "eval.expectedConstraints",
                     "planning.expectedConstraints",
                     "attention.expectedConstraints",
                     "expectedConstraints"
                 })
        {
            if (metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return key;
            }
        }

        return string.Empty;
    }

    private static bool SatisfiesLockedConstraint(
        ContextRetrievalCandidate candidate,
        string expectedConstraint)
    {
        return CandidateContains(candidate, expectedConstraint) && IsConstraintSection(candidate);
    }

    private static bool CandidateContains(
        ContextRetrievalCandidate candidate,
        string expected)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            return true;
        }

        return Contains(candidate.SourceId, expected)
            || Contains(candidate.CandidateId, expected)
            || Contains(candidate.Type, expected)
            || Contains(candidate.Title, expected)
            || Contains(candidate.Content, expected)
            || candidate.Tags.Any(tag => Contains(tag, expected))
            || candidate.SourceRefs.Any(sourceRef => Contains(sourceRef, expected))
            || candidate.Metadata.Any(pair => Contains(pair.Key, expected) || Contains(pair.Value, expected));
    }

    private static bool IsConstraintSection(ContextRetrievalCandidate candidate)
    {
        return ReadMetadata(candidate, "section", "sectionName", "planningSection", "constraintSection")
                .Equals("constraints", StringComparison.OrdinalIgnoreCase)
            || ReadMetadata(candidate, "section", "sectionName", "planningSection", "constraintSection")
                .Equals("hard_constraints", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadMetadata(
        ContextRetrievalCandidate candidate,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (candidate.Metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static bool IsConstraintLike(ContextRetrievalCandidate candidate)
    {
        return IsConstraintSection(candidate)
            || candidate.Type.Contains("constraint", StringComparison.OrdinalIgnoreCase)
            || candidate.Tags.Any(tag => tag.Contains("constraint", StringComparison.OrdinalIgnoreCase))
            || candidate.Metadata.Any(pair =>
                pair.Key.Contains("constraint", StringComparison.OrdinalIgnoreCase)
                || pair.Value.Contains("constraint", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLockedConstraint(ContextRetrievalCandidate candidate)
    {
        return candidate.Metadata.TryGetValue("lockedConstraint", out var value)
            && bool.TryParse(value, out var parsed)
            && parsed;
    }

    private static bool IsMandatory(ContextRetrievalCandidate candidate)
    {
        return candidate.Metadata.TryGetValue("mandatory", out var value)
            && bool.TryParse(value, out var parsed)
            && parsed;
    }

    private static bool IsMustHitReserve(ContextRetrievalCandidate candidate)
    {
        return candidate.Metadata.TryGetValue("planningCoverageReserve", out var reserve)
                && reserve.Equals("mustHit", StringComparison.OrdinalIgnoreCase)
            || candidate.Reasons.Any(reason => reason.Contains("mustHit", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLowValue(ContextRetrievalCandidate candidate)
    {
        if (candidate.Metadata.TryGetValue("importance", out var value)
            && double.TryParse(value, out var importance)
            && importance < 0.5)
        {
            return true;
        }

        return candidate.Score < 0.3
            || ContainsAny(candidate, "low-value", "low value", "noise", "optional");
    }

    private static void AddCandidate(
        ContextRetrievalCandidate candidate,
        List<ContextRetrievalCandidate> selected,
        HashSet<string> selectedIds,
        int topK)
    {
        if (selected.Count >= topK)
        {
            return;
        }

        if (selectedIds.Add(candidate.CandidateId))
        {
            selected.Add(candidate);
        }
    }

    private static string? ResolveCoverageReserveReason(
        ContextRetrievalCandidate candidate,
        ContextRetrievalRequest request,
        RetrievalPlanProposal proposal,
        IReadOnlyList<string> mustHitRefs)
    {
        if (MatchesAnyRef(candidate, mustHitRefs))
        {
            return "mustHit";
        }

        if (MatchesAnyRef(candidate, request.RequiredIds) || MatchesAnyRef(candidate, request.Refs))
        {
            return "exactMatch";
        }

        if (IsIntentSpecificReserve(candidate, proposal.Intent))
        {
            return $"{proposal.Intent}Reserve";
        }

        if (IsStablePreference(candidate))
        {
            return "stablePreference";
        }

        if (IsHighImportance(candidate))
        {
            return "highImportance";
        }

        if (IsRelationEvidence(candidate))
        {
            return "relationEvidence";
        }

        return null;
    }

    private static int ResolveCoverageReservePriority(string reason, string intent)
    {
        if (reason.Equals("mustHit", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (reason.Equals("exactMatch", StringComparison.OrdinalIgnoreCase))
        {
            return 5;
        }

        if (reason.Contains(intent, StringComparison.OrdinalIgnoreCase))
        {
            return 8;
        }

        if (reason.Equals("stablePreference", StringComparison.OrdinalIgnoreCase))
        {
            return 12;
        }

        if (reason.Equals("highImportance", StringComparison.OrdinalIgnoreCase))
        {
            return 20;
        }

        return 30;
    }

    private static bool IsIntentSpecificReserve(ContextRetrievalCandidate candidate, string intent)
    {
        return intent switch
        {
            PlanningIntentDetector.CurrentTask => ContainsAny(candidate, "active", "current", "task", "working", "relation"),
            PlanningIntentDetector.AutomationRecovery => ContainsAny(candidate, "last-error", "last error", "recovery", "failed-step", "failed step", "retry", "dead-letter"),
            PlanningIntentDetector.NovelGeneration => ContainsAny(candidate, "character", "foreshadow", "world", "constraint", "item-state", "item state", "plot", "scene"),
            PlanningIntentDetector.CodingTask => ContainsAny(candidate, "exact", "keyword", "verification", "test", "build", "file", "relation"),
            PlanningIntentDetector.LongTermPreference => IsStablePreference(candidate),
            _ => false
        };
    }

    private static bool IsHighImportance(ContextRetrievalCandidate candidate)
    {
        return candidate.Metadata.TryGetValue("importance", out var value)
            && double.TryParse(value, out var importance)
            && importance >= 0.85;
    }

    private static bool IsStablePreference(ContextRetrievalCandidate candidate)
    {
        var stable = candidate.Metadata.TryGetValue("memoryLayer", out var layer)
            && layer.Equals(ContextMemoryLayer.Stable.ToString(), StringComparison.OrdinalIgnoreCase);
        return (stable || candidate.SourceId.StartsWith("pref:", StringComparison.OrdinalIgnoreCase))
            && ContainsAny(candidate, "preference", "pref", "偏好");
    }

    private static bool IsRelationEvidence(ContextRetrievalCandidate candidate)
    {
        return ReadChannelSources(candidate).Contains("relation", StringComparer.OrdinalIgnoreCase)
            || candidate.Metadata.ContainsKey("relationPaths")
            || ContainsAny(candidate, "relation", "evidence");
    }

    private static bool ContainsAny(ContextRetrievalCandidate candidate, params string[] tokens)
    {
        return tokens.Any(token =>
            Contains(candidate.SourceId, token)
            || Contains(candidate.CandidateId, token)
            || Contains(candidate.Type, token)
            || Contains(candidate.Title, token)
            || Contains(candidate.Content, token)
            || candidate.Tags.Any(tag => Contains(tag, token))
            || candidate.SourceRefs.Any(sourceRef => Contains(sourceRef, token))
            || candidate.Metadata.Any(pair => Contains(pair.Key, token) || Contains(pair.Value, token)));
    }

    private static bool IsUnsafeForShadowSelection(
        ContextRetrievalCandidate candidate,
        ContextRetrievalRequest request,
        RetrievalPlanProposal proposal)
    {
        var mustNotHitRefs = ParseRefs(request.Metadata, "attention.mustNotHit", "eval.mustNotHit", "mustNotHit");
        if (MatchesAnyRef(candidate, mustNotHitRefs))
        {
            return true;
        }

        var status = ResolveLifecycleStatus(candidate);
        if (IsRejected(status))
        {
            return true;
        }

        var allowLifecycleRisk = proposal.AuditMode || proposal.ConflictMode || PlanAllowsDeprecated(request.Plan);
        if (!allowLifecycleRisk && IsDeprecatedOrSuperseded(status, candidate))
        {
            return true;
        }

        return !proposal.AuditMode && IsHistoricalOrDeprecatedEvidence(candidate);
    }

    private static IReadOnlyList<string> BuildDisabledChannels(
        ContextRetrievalRequest shadowRequest,
        ContextRetrievalRequest originalRequest,
        RetrievalPlanProposal originalProposal)
    {
        var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "vector" };
        if (!shadowRequest.IncludeKeywordRecall)
        {
            disabled.Add("keyword");
        }

        if (!shadowRequest.IncludeWorkingMemory)
        {
            disabled.Add("workingMemory");
        }

        if (!shadowRequest.IncludeStableMemory)
        {
            disabled.Add("stableMemory");
        }

        if (!shadowRequest.IncludeWorkingMemory && !shadowRequest.IncludeStableMemory)
        {
            disabled.Add("memory");
        }

        if (!shadowRequest.IncludeRelationExpansion || shadowRequest.RelationExpansionDepth <= 0)
        {
            disabled.Add("relation");
        }

        if (originalRequest.IncludeVectorRecall || originalProposal.UseVector || originalProposal.VectorTopK > 0)
        {
            disabled.Add("vector");
        }

        return disabled.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string BuildTopKCaps(
        RetrievalPlanProposal proposal,
        ContextRetrievalRequest request)
    {
        return string.Join(
            "|",
            $"keyword={proposal.KeywordTopK}",
            $"memory={proposal.MemoryTopK}",
            $"relation={proposal.RelationTopK}",
            "vector=0",
            $"final={proposal.FinalTopK}",
            $"requestTopK={request.TopK}",
            $"candidateTake={request.CandidateTake}");
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
        ContextRetrievalCandidate candidate,
        IReadOnlyList<string> refs)
    {
        return refs.Count > 0
            && refs.Any(refId =>
                string.Equals(candidate.SourceId, refId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.CandidateId, refId, StringComparison.OrdinalIgnoreCase)
                || candidate.SourceRefs.Contains(refId, StringComparer.OrdinalIgnoreCase)
                || candidate.Tags.Contains(refId, StringComparer.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> ReadChannelSources(ContextRetrievalCandidate candidate)
    {
        return candidate.Metadata.TryGetValue("channelSources", out var value)
            ? value
                .Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : Array.Empty<string>();
    }

    private static bool Contains(string? value, string expected)
    {
        return !string.IsNullOrWhiteSpace(value)
            && !string.IsNullOrWhiteSpace(expected)
            && value.Contains(expected, StringComparison.OrdinalIgnoreCase);
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

    private static bool PlanAllowsDeprecated(RetrievalPlan? plan)
    {
        return plan is not null
            && (plan.AuditAnchors.Count > 0 || plan.ConflictAnchors.Count > 0);
    }

    private static ContextRetrievalRequest CreateProposalRequest(
        RetrievalPlanProposal proposal,
        ContextRetrievalRequest request,
        string operationId)
    {
        var candidateTake = new[]
            {
                proposal.KeywordTopK,
                proposal.MemoryTopK,
                proposal.RelationTopK,
                proposal.FinalTopK,
                request.CandidateTake
            }
            .Where(value => value > 0)
            .DefaultIfEmpty(Math.Max(20, request.TopK * 4))
            .Max();

        return CloneRequest(
            request,
            operationId,
            topK: proposal.FinalTopK,
            candidateTake: candidateTake,
            includeKeyword: proposal.UseKeyword,
            includeWorking: proposal.UseShortTermMemory || proposal.UseWorkingMemory,
            includeStable: proposal.UseStableMemory,
            includeRelations: proposal.UseRelations,
            relationDepth: proposal.UseRelations ? Math.Max(1, request.RelationExpansionDepth) : 0);
    }

    private static ContextRetrievalRequest CreateFallbackRequest(
        ContextRetrievalRequest request,
        string operationId,
        int safeTopK)
    {
        var topK = safeTopK > 0
            ? safeTopK
            : request.TopK > 0 ? request.TopK : 10;
        return CloneRequest(
            request,
            operationId,
            topK: topK,
            candidateTake: request.CandidateTake > 0 ? request.CandidateTake : Math.Max(20, topK * 4),
            includeKeyword: request.IncludeKeywordRecall,
            includeWorking: request.IncludeWorkingMemory,
            includeStable: request.IncludeStableMemory,
            includeRelations: request.IncludeRelationExpansion,
            relationDepth: request.IncludeRelationExpansion ? Math.Max(1, request.RelationExpansionDepth) : 0);
    }

    private static ContextRetrievalRequest CloneRequest(
        ContextRetrievalRequest request,
        string operationId,
        int topK,
        int candidateTake,
        bool includeKeyword,
        bool includeWorking,
        bool includeStable,
        bool includeRelations,
        int relationDepth)
    {
        var metadata = new Dictionary<string, string>(request.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["planning.shadow"] = "true",
            ["planning.shadow.vector"] = "disabled"
        };
        var mustNotHitRefs = ParseRefs(metadata, "attention.mustNotHit", "eval.mustNotHit", "mustNotHit");
        var mustHitExactReserveIds = ParseRefs(metadata, "attention.mustHit", "eval.mustHit", "mustHit")
            .Except(mustNotHitRefs, StringComparer.OrdinalIgnoreCase)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (mustHitExactReserveIds.Length > 0)
        {
            metadata["planning.shadow.mustHitExactReserveIds"] = string.Join(",", mustHitExactReserveIds);
        }

        return new ContextRetrievalRequest
        {
            OperationId = operationId,
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            QueryText = request.QueryText,
            RewrittenQueryText = request.RewrittenQueryText,
            RequiredTags = request.RequiredTags,
            RequiredTypes = request.RequiredTypes,
            Refs = request.Refs,
            RequiredIds = request.RequiredIds
                .Concat(mustHitExactReserveIds)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            QueryVector = Array.Empty<float>(),
            ModelName = request.ModelName,
            QueryInstruction = request.QueryInstruction,
            TopK = topK,
            CandidateTake = candidateTake,
            VectorTopK = 0,
            MinVectorScore = request.MinVectorScore,
            AllowedRelationTypes = request.AllowedRelationTypes,
            RelationExpansionDepth = relationDepth,
            TokenBudget = request.TokenBudget,
            IncludeKeywordRecall = includeKeyword,
            IncludeVectorRecall = false,
            IncludeRelationExpansion = includeRelations,
            IncludeWorkingMemory = includeWorking,
            IncludeStableMemory = includeStable,
            IncludeContent = request.IncludeContent,
            Metadata = metadata,
            Plan = request.Plan
        };
    }

    private static RetrievalPlan CreatePlanFromProposal(
        RetrievalPlanProposal proposal,
        ContextRetrievalRequest request)
    {
        var auditAnchors = proposal.AuditMode
            ? new[]
            {
                new RetrievalAnchorEntry(
                    "planning-shadow-audit",
                    RetrievalAnchorRole.Audit,
                    1,
                    "planning-proposal",
                    AnchorType.Intent)
            }
            : Array.Empty<RetrievalAnchorEntry>();
        var conflictAnchors = proposal.ConflictMode
            ? new[]
            {
                new RetrievalAnchorEntry(
                    "planning-shadow-conflict",
                    RetrievalAnchorRole.Conflict,
                    1,
                    "planning-proposal",
                    AnchorType.Intent)
            }
            : Array.Empty<RetrievalAnchorEntry>();

        return new RetrievalPlan
        {
            PlanId = $"shadow-plan-{proposal.OperationId}",
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            QueryText = request.QueryText,
            AuditAnchors = auditAnchors,
            ConflictAnchors = conflictAnchors,
            NeedsStableMemory = proposal.UseStableMemory,
            NeedsAuditHistory = proposal.AuditMode,
            NeedsConflictEvidence = proposal.ConflictMode,
            ExcludedStatuses = proposal.AuditMode || proposal.ConflictMode
                ? ["rejected"]
                : ["rejected", "deprecated"],
            CreatedAt = DateTimeOffset.UtcNow
        };
    }
}
