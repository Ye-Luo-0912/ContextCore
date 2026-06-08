using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>只读聚合 planning 输入快照；不写入存储，也不影响 retrieval/package。</summary>
public sealed class PlanningSnapshotService
{
    public const string PolicyVersion = "context-planning-snapshot-policy/v1";

    private readonly IShortTermMemoryStore _shortTermMemoryStore;
    private readonly IMemoryStore _memoryStore;
    private readonly IConstraintStore _constraintStore;
    private readonly IContextLearningStore _learningStore;

    public PlanningSnapshotService(
        IShortTermMemoryStore shortTermMemoryStore,
        IMemoryStore memoryStore,
        IConstraintStore constraintStore,
        IContextLearningStore learningStore)
    {
        _shortTermMemoryStore = shortTermMemoryStore;
        _memoryStore = memoryStore;
        _constraintStore = constraintStore;
        _learningStore = learningStore;
    }

    public async Task<ContextPlanningSnapshot> GetSnapshotAsync(
        string workspaceId,
        string? collectionId,
        string? sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var summaryTask = _shortTermMemoryStore.GetSummaryAsync(new ShortTermSummaryQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            SessionId = sessionId,
            LatestRawTake = 0
        }, cancellationToken);
        var stableMemoryTask = _memoryStore.QueryAsync(new ContextMemoryQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Layer = ContextMemoryLayer.Stable,
            Status = ContextMemoryStatus.Stable,
            Take = 500
        }, cancellationToken);
        var stableConstraintsTask = _constraintStore.QueryAsync(new ContextConstraintQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Status = ContextMemoryStatus.Stable,
            Take = 200
        }, cancellationToken);
        var learningSummaryTask = BuildLearningSummaryAsync(workspaceId, collectionId, sessionId, cancellationToken);

        await Task.WhenAll(summaryTask, stableMemoryTask, stableConstraintsTask, learningSummaryTask)
            .ConfigureAwait(false);

        var summary = await summaryTask.ConfigureAwait(false);
        var stableMemory = await stableMemoryTask.ConfigureAwait(false);
        var stableConstraints = await stableConstraintsTask.ConfigureAwait(false);
        var learningSummary = await learningSummaryTask.ConfigureAwait(false);

        return new ContextPlanningSnapshot
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            SessionId = sessionId,
            ActiveTasks = OrderWorkingItems(summary.ActiveTasks),
            RecentDecisions = OrderWorkingItems(summary.RecentDecisions),
            OpenQuestions = OrderWorkingItems(summary.OpenQuestions),
            KnownIssues = OrderWorkingItems(summary.KnownIssues),
            StableConstraints = stableConstraints
                .OrderByDescending(item => item.Level == ConstraintLevel.Hard)
                .ThenByDescending(item => item.UpdatedAt == default ? item.CreatedAt : item.UpdatedAt)
                .ToArray(),
            StablePreferences = stableMemory
                .Where(IsStablePreference)
                .OrderByDescending(item => item.Importance)
                .ThenByDescending(item => item.UpdatedAt == default ? item.CreatedAt : item.UpdatedAt)
                .ToArray(),
            DecisionRecords = stableMemory
                .Where(IsDecisionRecord)
                .OrderByDescending(item => item.UpdatedAt == default ? item.CreatedAt : item.UpdatedAt)
                .ToArray(),
            LearningSignalsSummary = learningSummary,
            PolicyVersion = PolicyVersion,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private async Task<ContextLearningSummary> BuildLearningSummaryAsync(
        string workspaceId,
        string? collectionId,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        var recordsTask = _learningStore.QueryRecordsAsync(new ContextLearningRecordQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            SessionId = sessionId,
            Limit = int.MaxValue
        }, cancellationToken);
        var casesTask = _learningStore.QueryCasesAsync(new ContextLearningCaseQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            SessionId = sessionId,
            Limit = int.MaxValue
        }, cancellationToken);

        await Task.WhenAll(recordsTask, casesTask).ConfigureAwait(false);
        var records = await recordsTask.ConfigureAwait(false);
        var cases = await casesTask.ConfigureAwait(false);

        return new ContextLearningSummary
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            RecordCount = records.Count,
            CaseCount = cases.Count,
            PositiveCount = records.Count(record => record.Signal == ContextFeedbackSignal.Positive),
            NegativeCount = records.Count(record => record.Signal == ContextFeedbackSignal.Negative),
            StaleCount = records.Count(record => record.Signal == ContextFeedbackSignal.Stale),
            DraftCaseCount = cases.Count(item => item.Status == ContextLearningCaseStatus.Draft),
            CandidateCaseCount = cases.Count(item => item.Status == ContextLearningCaseStatus.Candidate),
            ActiveRegressionCaseCount = cases.Count(item => item.Status == ContextLearningCaseStatus.ActiveRegression),
            ArchivedCaseCount = cases.Count(item => item.Status == ContextLearningCaseStatus.Archived),
            RejectedCaseCount = cases.Count(item => item.Status == ContextLearningCaseStatus.Rejected),
            FailureTypeCounts = records
                .GroupBy(static record => record.FailureType)
                .ToDictionary(static group => group.Key, static group => group.Count()),
            CaseKindCounts = cases
                .Where(static item => !string.IsNullOrWhiteSpace(item.CaseKind))
                .GroupBy(static item => item.CaseKind, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase)
        };
    }

    private static IReadOnlyList<ShortTermWorkingItem> OrderWorkingItems(IReadOnlyList<ShortTermWorkingItem> items)
    {
        return items
            .OrderByDescending(item => item.Importance)
            .ThenByDescending(item => item.UpdatedAt == default ? item.CreatedAt : item.UpdatedAt)
            .ToArray();
    }

    private static bool IsStablePreference(ContextMemoryItem item)
    {
        return item.Type.Contains("preference", StringComparison.OrdinalIgnoreCase)
            || item.Tags.Contains("preference", StringComparer.OrdinalIgnoreCase)
            || item.Metadata.Values.Any(value => value.Contains("preference", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsDecisionRecord(ContextMemoryItem item)
    {
        return string.Equals(item.Type, "decision", StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Type, "decision-record", StringComparison.OrdinalIgnoreCase)
            || item.Tags.Contains("decision", StringComparer.OrdinalIgnoreCase)
            || item.Tags.Contains("DecisionRecord", StringComparer.OrdinalIgnoreCase)
            || item.Metadata.Values.Any(value => value.Contains("decision", StringComparison.OrdinalIgnoreCase));
    }
}
