using ContextCore.Abstractions;

namespace ContextCore.Core.Services;

/// <summary>基于 planning snapshot 生成 retrieval plan proposal；只读预览，不执行检索。</summary>
public sealed class RetrievalPlanProposalService
{
    public const string PolicyVersion = "retrieval-plan-proposal-policy/v1";

    private readonly PlanningSnapshotService _snapshotService;
    private readonly PlanningIntentDetector _intentDetector;
    private readonly RetrievalPlanSafetyProfile _safetyProfile;

    public RetrievalPlanProposalService(
        PlanningSnapshotService snapshotService,
        PlanningIntentDetector intentDetector,
        RetrievalPlanSafetyProfile? safetyProfile = null)
    {
        _snapshotService = snapshotService;
        _intentDetector = intentDetector;
        _safetyProfile = safetyProfile ?? RetrievalPlanSafetyProfile.CreateDefault();
    }

    public async Task<RetrievalPlanProposal> ProposeAsync(
        ContextPlanningProposalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);

        var snapshot = await _snapshotService.GetSnapshotAsync(
            request.WorkspaceId,
            request.CollectionId,
            request.SessionId,
            cancellationToken).ConfigureAwait(false);

        return Propose(snapshot, request.CurrentInput, request.Mode);
    }

    public RetrievalPlanProposal Propose(
        ContextPlanningSnapshot snapshot,
        string? currentInput,
        string? requestedMode = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var detection = _intentDetector.Detect(snapshot, currentInput, requestedMode);
        var profile = ResolveProfile(detection.Intent, requestedMode, _safetyProfile);
        var reasons = new List<string>(detection.Reasons)
        {
            $"policy:{PolicyVersion}",
            BuildSnapshotCountReason(snapshot)
        };
        reasons.AddRange(BuildSnapshotReferenceReasons(snapshot));
        reasons.AddRange(BuildRecallReserveReasons(detection.Intent));

        var warnings = new List<string>(detection.Warnings)
        {
            "previewOnly: proposal does not execute retrieval or mutate retrieval output",
            "vector disabled by Phase P2 boundary"
        };

        if (snapshot.ActiveTasks.Count == 0)
        {
            warnings.Add("snapshot has no active tasks");
        }

        if (!_safetyProfile.AllowVector)
        {
            reasons.Add("safety.vector.disabled:UseVector=false;VectorTopK=0");
        }

        if (!profile.AuditMode && !profile.ConflictMode)
        {
            if (!_safetyProfile.AllowDeprecatedInNormalMode)
            {
                reasons.Add("safety.lifecycle.normalModeDeprecatedBlocked");
            }

            if (!_safetyProfile.AllowSupersededInNormalMode)
            {
                reasons.Add("safety.lifecycle.normalModeSupersededBlocked");
            }

            if (_safetyProfile.RequireLifecycleFilter)
            {
                reasons.Add("safety.lifecycle.normalModeFilterRequired");
            }
        }

        return new RetrievalPlanProposal
        {
            OperationId = Guid.NewGuid().ToString("N"),
            WorkspaceId = snapshot.WorkspaceId,
            CollectionId = snapshot.CollectionId,
            Intent = detection.Intent,
            Mode = profile.Mode,
            UseExact = profile.UseExact,
            UseKeyword = profile.UseKeyword,
            UseShortTermMemory = profile.UseShortTermMemory,
            UseWorkingMemory = profile.UseWorkingMemory,
            UseStableMemory = profile.UseStableMemory,
            UseRelations = profile.UseRelations,
            UseVector = false,
            AuditMode = profile.AuditMode,
            ConflictMode = profile.ConflictMode,
            KeywordTopK = profile.KeywordTopK,
            MemoryTopK = profile.MemoryTopK,
            RelationTopK = profile.RelationTopK,
            VectorTopK = 0,
            FinalTopK = profile.FinalTopK,
            Confidence = detection.Confidence,
            Reasons = reasons,
            Warnings = warnings
        };
    }

    private static ProposalProfile ResolveProfile(
        string intent,
        string? requestedMode,
        RetrievalPlanSafetyProfile safetyProfile)
    {
        return intent switch
        {
            PlanningIntentDetector.AuditDeprecated => new ProposalProfile
            {
                Mode = ResolveMode(requestedMode, "Audit"),
                UseExact = true,
                UseKeyword = true,
                UseShortTermMemory = true,
                UseWorkingMemory = true,
                UseStableMemory = true,
                UseRelations = true,
                AuditMode = true,
                ConflictMode = false,
                KeywordTopK = SafeTopK(24, safetyProfile.MaxKeywordTopK),
                MemoryTopK = SafeTopK(24, safetyProfile.MaxMemoryTopK),
                RelationTopK = SafeTopK(8, safetyProfile.MaxRelationTopK),
                FinalTopK = SafeTopK(10, safetyProfile.MaxFinalTopK)
            },
            PlanningIntentDetector.ConflictCheck => new ProposalProfile
            {
                Mode = ResolveMode(requestedMode, "Audit"),
                UseExact = true,
                UseKeyword = true,
                UseShortTermMemory = true,
                UseWorkingMemory = true,
                UseStableMemory = true,
                UseRelations = true,
                AuditMode = false,
                ConflictMode = true,
                KeywordTopK = SafeTopK(24, safetyProfile.MaxKeywordTopK),
                MemoryTopK = SafeTopK(24, safetyProfile.MaxMemoryTopK),
                RelationTopK = SafeTopK(8, safetyProfile.MaxRelationTopK),
                FinalTopK = SafeTopK(10, safetyProfile.MaxFinalTopK)
            },
            PlanningIntentDetector.AutomationRecovery => new ProposalProfile
            {
                Mode = ResolveMode(requestedMode, "Automation"),
                UseExact = true,
                UseKeyword = true,
                UseShortTermMemory = true,
                UseWorkingMemory = true,
                UseStableMemory = true,
                UseRelations = true,
                AuditMode = false,
                ConflictMode = false,
                KeywordTopK = SafeTopK(22, safetyProfile.MaxKeywordTopK),
                MemoryTopK = SafeTopK(22, safetyProfile.MaxMemoryTopK),
                RelationTopK = SafeTopK(8, safetyProfile.MaxRelationTopK),
                FinalTopK = SafeTopK(10, safetyProfile.MaxFinalTopK)
            },
            PlanningIntentDetector.NovelGeneration => new ProposalProfile
            {
                Mode = ResolveMode(requestedMode, "Novel"),
                UseExact = true,
                UseKeyword = true,
                UseShortTermMemory = true,
                UseWorkingMemory = true,
                UseStableMemory = true,
                UseRelations = true,
                AuditMode = false,
                ConflictMode = false,
                KeywordTopK = SafeTopK(22, safetyProfile.MaxKeywordTopK),
                MemoryTopK = SafeTopK(24, safetyProfile.MaxMemoryTopK),
                RelationTopK = SafeTopK(8, safetyProfile.MaxRelationTopK),
                FinalTopK = SafeTopK(10, safetyProfile.MaxFinalTopK)
            },
            PlanningIntentDetector.CodingTask => new ProposalProfile
            {
                Mode = ResolveMode(requestedMode, "Coding"),
                UseExact = true,
                UseKeyword = true,
                UseShortTermMemory = true,
                UseWorkingMemory = true,
                UseStableMemory = true,
                UseRelations = true,
                AuditMode = false,
                ConflictMode = false,
                KeywordTopK = SafeTopK(24, safetyProfile.MaxKeywordTopK),
                MemoryTopK = SafeTopK(24, safetyProfile.MaxMemoryTopK),
                RelationTopK = SafeTopK(8, safetyProfile.MaxRelationTopK),
                FinalTopK = SafeTopK(10, safetyProfile.MaxFinalTopK)
            },
            PlanningIntentDetector.LongTermPreference => new ProposalProfile
            {
                Mode = ResolveMode(requestedMode, "Chat"),
                UseExact = true,
                UseKeyword = true,
                UseShortTermMemory = false,
                UseWorkingMemory = true,
                UseStableMemory = true,
                UseRelations = false,
                AuditMode = false,
                ConflictMode = false,
                KeywordTopK = SafeTopK(18, safetyProfile.MaxKeywordTopK),
                MemoryTopK = SafeTopK(20, safetyProfile.MaxMemoryTopK),
                RelationTopK = 0,
                FinalTopK = SafeTopK(10, safetyProfile.MaxFinalTopK)
            },
            PlanningIntentDetector.CurrentTask => new ProposalProfile
            {
                Mode = ResolveMode(requestedMode, "Chat"),
                UseExact = true,
                UseKeyword = true,
                UseShortTermMemory = true,
                UseWorkingMemory = true,
                UseStableMemory = true,
                UseRelations = true,
                AuditMode = false,
                ConflictMode = false,
                KeywordTopK = SafeTopK(18, safetyProfile.MaxKeywordTopK),
                MemoryTopK = SafeTopK(20, safetyProfile.MaxMemoryTopK),
                RelationTopK = SafeTopK(8, safetyProfile.MaxRelationTopK),
                FinalTopK = SafeTopK(10, safetyProfile.MaxFinalTopK)
            },
            _ => new ProposalProfile
            {
                Mode = ResolveMode(requestedMode, "Chat"),
                UseExact = true,
                UseKeyword = true,
                UseShortTermMemory = true,
                UseWorkingMemory = true,
                UseStableMemory = true,
                UseRelations = true,
                AuditMode = false,
                ConflictMode = false,
                KeywordTopK = SafeTopK(22, safetyProfile.MaxKeywordTopK),
                MemoryTopK = SafeTopK(22, safetyProfile.MaxMemoryTopK),
                RelationTopK = SafeTopK(8, safetyProfile.MaxRelationTopK),
                FinalTopK = SafeTopK(10, safetyProfile.MaxFinalTopK)
            }
        };
    }

    private static int SafeTopK(int preferred, int max)
    {
        if (preferred <= 0)
        {
            return 0;
        }

        return max > 0 ? Math.Min(preferred, max) : preferred;
    }

    private static string ResolveMode(string? requestedMode, string fallback)
    {
        return string.IsNullOrWhiteSpace(requestedMode) ? fallback : requestedMode.Trim();
    }

    private static string BuildSnapshotCountReason(ContextPlanningSnapshot snapshot)
    {
        return string.Join(
            ';',
            $"snapshot:activeTasks={snapshot.ActiveTasks.Count}",
            $"recentDecisions={snapshot.RecentDecisions.Count}",
            $"openQuestions={snapshot.OpenQuestions.Count}",
            $"knownIssues={snapshot.KnownIssues.Count}",
            $"stableConstraints={snapshot.StableConstraints.Count}",
            $"stablePreferences={snapshot.StablePreferences.Count}",
            $"decisionRecords={snapshot.DecisionRecords.Count}",
            $"learningRecords={snapshot.LearningSignalsSummary.RecordCount}",
            $"learningCases={snapshot.LearningSignalsSummary.CaseCount}");
    }

    private static IEnumerable<string> BuildSnapshotReferenceReasons(ContextPlanningSnapshot snapshot)
    {
        foreach (var item in snapshot.ActiveTasks.Take(3))
        {
            yield return $"snapshot.activeTask:{item.ItemId}";
        }

        foreach (var item in snapshot.StableConstraints.Take(3))
        {
            yield return $"snapshot.stableConstraint:{item.Id}";
        }

        foreach (var item in snapshot.DecisionRecords.Take(3))
        {
            yield return $"snapshot.decisionRecord:{item.Id}";
        }

        foreach (var item in snapshot.StablePreferences.Take(3))
        {
            yield return $"snapshot.stablePreference:{item.Id}";
        }
    }

    private static IEnumerable<string> BuildRecallReserveReasons(string intent)
    {
        yield return "coverageFloor:highImportance,exactMatch,activeTask,stablePreference,relationEvidence";
        switch (intent)
        {
            case PlanningIntentDetector.CurrentTask:
                yield return "reserve.currentTask:shortTerm,working,relation";
                break;
            case PlanningIntentDetector.AutomationRecovery:
                yield return "reserve.automationRecovery:lastError,recoveryPoint,failedStep";
                break;
            case PlanningIntentDetector.NovelGeneration:
                yield return "reserve.novelGeneration:characterState,foreshadowing,worldConstraint,itemState";
                break;
            case PlanningIntentDetector.CodingTask:
                yield return "reserve.codingTask:exact,keyword,verification,relation";
                break;
            case PlanningIntentDetector.LongTermPreference:
                yield return "reserve.longTermPreference:stablePreference";
                break;
        }
    }

    private sealed class ProposalProfile
    {
        public string Mode { get; init; } = string.Empty;

        public bool UseExact { get; init; }

        public bool UseKeyword { get; init; }

        public bool UseShortTermMemory { get; init; }

        public bool UseWorkingMemory { get; init; }

        public bool UseStableMemory { get; init; }

        public bool UseRelations { get; init; }

        public bool AuditMode { get; init; }

        public bool ConflictMode { get; init; }

        public int KeywordTopK { get; init; }

        public int MemoryTopK { get; init; }

        public int RelationTopK { get; init; }

        public int FinalTopK { get; init; }
    }
}
