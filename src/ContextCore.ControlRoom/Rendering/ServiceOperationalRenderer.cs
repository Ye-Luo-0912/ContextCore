using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Rendering;

/// <summary>渲染 Service 模式下的 jobs / model / admin-runtime 页面。</summary>
public static class ServiceOperationalRenderer
{
    public static string RenderJobs(ServiceJobsSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Jobs");
        builder.AppendLine("============");
        builder.AppendLine($"时间   : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务   : {snapshot.BaseUrl}");
        builder.AppendLine($"作业数 : {snapshot.Jobs.Count}");
        builder.AppendLine();

        foreach (var job in snapshot.Jobs)
        {
            var payload = TryParsePayload(job.PayloadJson);
            builder.AppendLine($"- {job.JobId} [{job.Kind}/{job.State}]");
            builder.AppendLine($"  OperationId : {payload.OperationId ?? job.JobId}");
            builder.AppendLine($"  CreatedAt   : {job.CreatedAt:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"  UpdatedAt   : {(job.CompletedAt ?? job.StartedAt ?? job.CreatedAt):yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"  RetryCount  : {job.RetryCount}/{job.MaxRetryCount}");
            builder.AppendLine($"  Warnings    : {(string.IsNullOrWhiteSpace(job.ErrorMessage) ? "无" : job.ErrorMessage)}");
            if (payload.Metadata.Count > 0)
            {
                builder.AppendLine($"  Metadata    : {string.Join(", ", payload.Metadata.Select(pair => $"{pair.Key}={pair.Value}"))}");
            }
        }

        return builder.ToString();
    }

    public static string RenderJobDetail(ContextJob job)
    {
        var payload = TryParsePayload(job.PayloadJson);
        var builder = new StringBuilder();
        builder.AppendLine("Service Job Detail");
        builder.AppendLine("==================");
        builder.AppendLine($"JobId       : {job.JobId}");
        builder.AppendLine($"Kind        : {job.Kind}");
        builder.AppendLine($"Status      : {job.State}");
        builder.AppendLine($"OperationId : {payload.OperationId ?? job.JobId}");
        builder.AppendLine($"CreatedAt   : {job.CreatedAt:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"UpdatedAt   : {(job.CompletedAt ?? job.StartedAt ?? job.CreatedAt):yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"RetryCount  : {job.RetryCount}/{job.MaxRetryCount}");
        builder.AppendLine($"Warnings    : {(string.IsNullOrWhiteSpace(job.ErrorMessage) ? "无" : job.ErrorMessage)}");
        if (payload.Metadata.Count > 0)
        {
            builder.AppendLine("Metadata");
            foreach (var pair in payload.Metadata)
            {
                builder.AppendLine($"- {pair.Key}={pair.Value}");
            }
        }

        return builder.ToString();
    }

    public static string RenderModel(ServiceModelSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Model Status");
        builder.AppendLine("====================");
        builder.AppendLine($"时间    : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务    : {snapshot.BaseUrl}");
        builder.AppendLine();
        builder.AppendLine("Providers");
        foreach (var provider in snapshot.ModelStatus.ApiProviders)
        {
            builder.AppendLine($"- {provider.Name} [{provider.Provider}] enabled={(provider.Enabled ? "yes" : "no")} endpoint={(provider.EndpointConfigured ? "configured" : "missing")}");
        }

        builder.AppendLine();
        builder.AppendLine("Routes");
        foreach (var route in snapshot.ModelStatus.Routes.Take(10))
        {
            builder.AppendLine($"- role={route.Role} task={route.TaskKind ?? "-"} mode={route.ThinkingMode ?? "-"} primary={route.Primary?.ModelName ?? route.PrimaryModelName ?? "-"} fallback={route.Fallback?.ModelName ?? route.FallbackModelName ?? "-"}");
        }

        if (snapshot.RouteResolution is not null)
        {
            builder.AppendLine();
            builder.AppendLine("Route Resolve");
            builder.AppendLine($"- role={snapshot.RouteResolution.Role}");
            builder.AppendLine($"- selected={snapshot.RouteResolution.Primary?.ModelName ?? "未命中"}");
            builder.AppendLine($"- fallback={snapshot.RouteResolution.Fallback?.ModelName ?? "无"}");
            builder.AppendLine($"- reason={snapshot.RouteResolution.RouteSource}");
        }

        return builder.ToString();
    }

    public static string RenderAdminRuntime(ServiceAdminRuntimeSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Admin / Runtime");
        builder.AppendLine("=======================");
        builder.AppendLine($"时间          : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务          : {snapshot.BaseUrl}");
        builder.AppendLine($"RuntimeStatus : {snapshot.Runtime.Status.Status}/{snapshot.Runtime.Readiness.Status}");
        builder.AppendLine($"Storage       : {snapshot.AdminStatus.Storage.Provider}");
        builder.AppendLine($"RootPath      : {snapshot.AdminStatus.Storage.RootPath ?? "未返回"}");
        builder.AppendLine($"Retrieval     : {snapshot.AdminStatus.RetrievalBaseline}");
        builder.AppendLine($"BackupRoot    : {snapshot.BackupStatus.Root ?? "无"}");
        builder.AppendLine($"BackupExists  : {snapshot.BackupStatus.Exists}");
        builder.AppendLine($"BackupHealthy : {snapshot.BackupValidate.Healthy}");
        builder.AppendLine($"BackupMessage : {snapshot.BackupValidate.Message ?? "无"}");
        return builder.ToString();
    }

    public static string RenderMemory(ServiceMemorySnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Memory");
        builder.AppendLine("==============");
        builder.AppendLine($"时间    : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务    : {snapshot.BaseUrl}");
        builder.AppendLine($"Working : {snapshot.Working.Count}");
        builder.AppendLine($"Candidate: {snapshot.Candidates.Count}");
        builder.AppendLine($"Stable  : {snapshot.Stable.Count}");
        builder.AppendLine($"Global  : {snapshot.Global.Count}");
        return builder.ToString();
    }

    public static string RenderMemoryDetail(ContextMemoryItem item)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Memory Detail");
        builder.AppendLine("=====================");
        builder.AppendLine($"Id         : {item.Id}");
        builder.AppendLine($"Layer      : {item.Layer}");
        builder.AppendLine($"Status     : {item.Status}");
        builder.AppendLine($"Type       : {item.Type}");
        builder.AppendLine($"Tags       : {string.Join(',', item.Tags)}");
        builder.AppendLine($"Refs       : {string.Join(',', item.RelationRefs)}");
        builder.AppendLine($"SourceRefs : {string.Join(',', item.SourceRefs)}");
        builder.AppendLine($"Importance : {item.Importance:0.00}");
        builder.AppendLine($"UpdatedAt  : {item.UpdatedAt:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"Content    : {item.Content}");
        return builder.ToString();
    }

    public static string RenderCandidateMemory(ServiceCandidateMemorySnapshot snapshot)
    {
        var view = snapshot.Snapshot;
        var diagnostics = snapshot.Diagnostics;
        var builder = new StringBuilder();
        builder.AppendLine("Service Candidate Memory");
        builder.AppendLine("========================");
        builder.AppendLine($"时间       : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务       : {snapshot.BaseUrl}");
        builder.AppendLine($"Workspace  : {view.WorkspaceId}");
        builder.AppendLine($"Collection : {view.CollectionId ?? "-"}");
        builder.AppendLine();
        builder.AppendLine("Snapshot");
        builder.AppendLine($"- CandidateMemoryCount        : {view.CandidateMemoryCount}");
        builder.AppendLine($"- CandidateConstraintCount    : {view.CandidateConstraintCount}");
        builder.AppendLine($"- CandidateDecisionCount      : {view.CandidateDecisionCount}");
        builder.AppendLine($"- PendingReviewCount          : {view.PendingReviewCount}");
        builder.AppendLine($"- AcceptedFromPromotionCount  : {view.AcceptedFromPromotionCount}");
        builder.AppendLine($"- ExpiredCandidateCount       : {view.ExpiredCandidateCount}");
        builder.AppendLine($"- DuplicateCandidateCount     : {view.DuplicateCandidateCount}");
        builder.AppendLine($"- ConflictCandidateCount      : {view.ConflictCandidateCount}");
        builder.AppendLine();
        builder.AppendLine("Recent Candidates");
        foreach (var candidate in view.RecentCandidates.Take(20))
        {
            builder.AppendLine($"- {candidate.Id} [{candidate.CandidateKind}/{candidate.Status}/{candidate.Lifecycle}] type={candidate.Type}");
            builder.AppendLine($"  title    : {candidate.Title}");
            builder.AppendLine($"  evidence : {(candidate.EvidenceRefs.Count == 0 ? "-" : string.Join(", ", candidate.EvidenceRefs))}");
            builder.AppendLine($"  source   : promotion={candidate.PromotionCandidateId ?? "-"} stable={candidate.StableReviewCandidateId ?? "-"} gap={candidate.ConstraintGapId ?? "-"}");
        }

        builder.AppendLine();
        builder.AppendLine("Diagnostics");
        builder.AppendLine($"- Total                 : {diagnostics.DiagnosticCount}");
        builder.AppendLine($"- Duplicate             : {diagnostics.DuplicateCandidateCount}");
        builder.AppendLine($"- Stale                 : {diagnostics.StaleCandidateCount}");
        builder.AppendLine($"- WithoutEvidence       : {diagnostics.CandidateWithoutEvidenceCount}");
        builder.AppendLine($"- RejectedSource        : {diagnostics.CandidateWithRejectedSourceCount}");
        builder.AppendLine($"- StableConflict        : {diagnostics.StableConflictCount}");
        builder.AppendLine($"- Superseded            : {diagnostics.SupersededCandidateCount}");
        foreach (var item in diagnostics.Diagnostics.Take(20))
        {
            builder.AppendLine($"  - {item.CandidateId} [{item.DiagnosticType}/{item.Severity}] {item.Reason}");
            builder.AppendLine($"    suggested: {item.SuggestedAction}");
            if (item.RelatedCandidateIds.Count > 0)
            {
                builder.AppendLine($"    related: {string.Join(", ", item.RelatedCandidateIds)}");
            }
        }

        if (view.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Warnings");
            foreach (var warning in view.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        return builder.ToString();
    }

    public static string RenderCandidateMemoryDetail(CandidateMemoryRecord candidate)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Candidate Memory Detail");
        builder.AppendLine("===============================");
        builder.AppendLine($"Id          : {candidate.Id}");
        builder.AppendLine($"Kind        : {candidate.CandidateKind}");
        builder.AppendLine($"Type        : {candidate.Type}");
        builder.AppendLine($"Status      : {candidate.Status}");
        builder.AppendLine($"Lifecycle   : {candidate.Lifecycle}");
        builder.AppendLine($"Importance  : {candidate.Importance:0.00}");
        builder.AppendLine($"Confidence  : {candidate.Confidence:0.00}");
        builder.AppendLine($"PromotionId : {candidate.PromotionCandidateId ?? "-"}");
        builder.AppendLine($"StableId    : {candidate.StableReviewCandidateId ?? "-"}");
        builder.AppendLine($"GapId       : {candidate.ConstraintGapId ?? "-"}");
        builder.AppendLine($"FeedbackId  : {candidate.FeedbackId ?? "-"}");
        builder.AppendLine($"LearningId  : {candidate.LearningCaseId ?? "-"}");
        builder.AppendLine($"Evidence    : {(candidate.EvidenceRefs.Count == 0 ? "-" : string.Join(", ", candidate.EvidenceRefs))}");
        builder.AppendLine($"SourceRefs  : {(candidate.SourceRefs.Count == 0 ? "-" : string.Join(", ", candidate.SourceRefs))}");
        builder.AppendLine($"UpdatedAt   : {candidate.UpdatedAt:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"Content     : {candidate.Content}");
        return builder.ToString();
    }

    public static string RenderCandidateMemoryExplanation(CandidateMemoryExplanation explanation)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Candidate Memory Explain");
        builder.AppendLine("================================");
        builder.AppendLine($"Candidate : {explanation.CandidateId}");
        builder.AppendLine($"Kind      : {explanation.Candidate.CandidateKind}");
        builder.AppendLine($"RiskFlags : {(explanation.RiskFlags.Count == 0 ? "-" : string.Join(", ", explanation.RiskFlags))}");
        builder.AppendLine($"Evidence  : {(explanation.EvidenceRefs.Count == 0 ? "-" : string.Join(", ", explanation.EvidenceRefs))}");
        builder.AppendLine();
        builder.AppendLine("Sources");
        builder.AppendLine($"- Promotion    : {explanation.SourcePromotionCandidate?.CandidateId ?? "-"}");
        builder.AppendLine($"- StableReview : {explanation.SourceStableReviewCandidate?.StableReviewCandidateId ?? "-"}");
        builder.AppendLine($"- ConstraintGap: {explanation.SourceConstraintGap?.GapId ?? "-"}");
        builder.AppendLine($"- Feedback     : {explanation.SourceFeedbackSignal?.FeedbackId ?? "-"}");
        builder.AppendLine($"- LearningCase : {explanation.SourceLearningCase?.CaseId ?? "-"}");
        builder.AppendLine();
        builder.AppendLine("Review History");
        builder.AppendLine($"- Promotion reviews       : {explanation.PromotionReviewHistory.Count}");
        builder.AppendLine($"- Stable reviews          : {explanation.StableReviewHistory.Count}");
        builder.AppendLine($"- Constraint gap reviews  : {explanation.ConstraintGapReviewHistory.Count}");
        builder.AppendLine($"- Candidate constraint reviews: {explanation.CandidateConstraintReviewHistory.Count}");
        builder.AppendLine($"- Candidate memory reviews    : {explanation.CandidateMemoryReviewHistory.Count}");
        builder.AppendLine();
        builder.AppendLine("Provenance Chain");
        foreach (var link in explanation.ProvenanceChain)
        {
            builder.AppendLine($"- {link.SourceType}:{link.SourceId} relation={link.Relation} status={link.Status}");
        }

        if (explanation.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Warnings");
            foreach (var warning in explanation.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        return builder.ToString();
    }

    public static string RenderCandidateMemoryReviewResult(CandidateMemoryReviewResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Candidate Memory Review Result");
        builder.AppendLine("==============================");
        builder.AppendLine($"OperationId : {result.OperationId}");
        builder.AppendLine($"CandidateId : {result.CandidateId}");
        builder.AppendLine($"Kind        : {result.CandidateKind}");
        builder.AppendLine($"Action      : {result.Action}");
        builder.AppendLine($"Status      : {result.FromStatus} -> {result.ToStatus}");
        builder.AppendLine($"ReviewId    : {result.ReviewId}");
        builder.AppendLine($"Reviewer    : {result.Reviewer}");
        builder.AppendLine($"Reason      : {result.Reason}");
        builder.AppendLine($"ReviewedAt  : {result.ReviewedAt:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"Supersedes  : {result.SupersedeTargetCandidateId ?? "-"}");
        if (result.Warnings.Count > 0)
        {
            builder.AppendLine("Warnings");
            foreach (var warning in result.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        if (result.Errors.Count > 0)
        {
            builder.AppendLine("Errors");
            foreach (var error in result.Errors)
            {
                builder.AppendLine($"- {error}");
            }
        }

        return builder.ToString();
    }

    public static string RenderCandidateMemoryReviews(IReadOnlyList<CandidateMemoryReviewRecord> reviews)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Candidate Memory Review History");
        builder.AppendLine("===============================");
        builder.AppendLine($"Count: {reviews.Count}");
        foreach (var review in reviews.Take(50))
        {
            builder.AppendLine($"- {review.ReviewId} {review.Action} {review.FromStatus}->{review.ToStatus}");
            builder.AppendLine($"  reviewer={review.Reviewer} reason={review.Reason} reviewedAt={review.ReviewedAt:yyyy-MM-dd HH:mm:ss}");
            if (!string.IsNullOrWhiteSpace(review.SupersedeTargetCandidateId))
            {
                builder.AppendLine($"  supersedeTarget={review.SupersedeTargetCandidateId}");
            }

            if (review.Warnings.Count > 0)
            {
                builder.AppendLine($"  warnings={string.Join("; ", review.Warnings)}");
            }
        }

        return builder.ToString();
    }

    public static string RenderStableMemory(ServiceStableMemorySnapshot snapshot)
    {
        var view = snapshot.Snapshot;
        var diagnostics = snapshot.Diagnostics;
        var builder = new StringBuilder();
        builder.AppendLine("Service Stable Memory");
        builder.AppendLine("=====================");
        builder.AppendLine($"时间       : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务       : {snapshot.BaseUrl}");
        builder.AppendLine($"Workspace  : {view.WorkspaceId}");
        builder.AppendLine($"Collection : {view.CollectionId ?? "-"}");
        builder.AppendLine();
        builder.AppendLine("Snapshot");
        builder.AppendLine($"- StableMemoryCount        : {view.StableMemoryCount}");
        builder.AppendLine($"- StableConstraintCount    : {view.StableConstraintCount}");
        builder.AppendLine($"- DecisionRecordCount      : {view.DecisionRecordCount}");
        builder.AppendLine($"- GlobalMemoryCount        : {view.GlobalMemoryCount}");
        builder.AppendLine($"- ActiveCount              : {view.ActiveCount}");
        builder.AppendLine($"- SupersededCount          : {view.SupersededCount}");
        builder.AppendLine($"- DeprecatedCount          : {view.DeprecatedCount}");
        builder.AppendLine($"- RejectedCount            : {view.RejectedCount}");
        builder.AppendLine($"- MissingProvenanceCount   : {view.MissingProvenanceCount}");
        builder.AppendLine($"- DuplicateCandidateCount  : {view.DuplicateCandidateCount}");
        builder.AppendLine($"- ConflictCandidateCount   : {view.ConflictCandidateCount}");
        builder.AppendLine($"- WeakEvidenceCount        : {view.WeakEvidenceCount}");
        builder.AppendLine();
        builder.AppendLine("Recent Stable Items");
        foreach (var item in view.RecentStableItems.Take(20))
        {
            builder.AppendLine($"- {item.Id} [{item.StableKind}/{item.Status}/{item.Lifecycle}] type={item.Type}");
            builder.AppendLine($"  title    : {item.Title}");
            builder.AppendLine($"  evidence : {(item.EvidenceRefs.Count == 0 ? "-" : string.Join(", ", item.EvidenceRefs))}");
            builder.AppendLine($"  source   : stableReview={item.StableReviewCandidateId ?? "-"} promotion={item.PromotionCandidateId ?? "-"} learning={item.LearningCaseId ?? "-"}");
        }

        builder.AppendLine();
        builder.AppendLine("Diagnostics");
        builder.AppendLine($"- Total                         : {diagnostics.DiagnosticCount}");
        builder.AppendLine($"- DuplicateStableMemory         : {diagnostics.DuplicateStableMemoryCount}");
        builder.AppendLine($"- PossibleConflict              : {diagnostics.PossibleConflictCount}");
        builder.AppendLine($"- MissingProvenance             : {diagnostics.MissingProvenanceCount}");
        builder.AppendLine($"- MissingEvidenceRefs           : {diagnostics.MissingEvidenceRefsCount}");
        builder.AppendLine($"- StableWithoutReviewSource     : {diagnostics.StableWithoutReviewSourceCount}");
        builder.AppendLine($"- StableConstraintWithoutScope  : {diagnostics.StableConstraintWithoutScopeCount}");
        builder.AppendLine($"- DecisionRecordWithoutSource   : {diagnostics.DecisionRecordWithoutSourceCount}");
        builder.AppendLine($"- DeprecatedStillActive         : {diagnostics.DeprecatedStillActiveCount}");
        builder.AppendLine($"- SupersededWithoutReplacement  : {diagnostics.SupersededWithoutReplacementCount}");
        builder.AppendLine($"- GlobalMemoryScopeRisk         : {diagnostics.GlobalMemoryScopeRiskCount}");
        builder.AppendLine($"- SupersededWithoutRelation     : {diagnostics.SupersededWithoutRelationCount}");
        builder.AppendLine($"- MetadataRelationMismatch      : {diagnostics.MetadataRelationMismatchCount}");
        builder.AppendLine($"- BrokenReplacementLink         : {diagnostics.BrokenReplacementLinkCount}");
        builder.AppendLine($"- ReplacementTargetMissing      : {diagnostics.ReplacementTargetMissingCount}");
        builder.AppendLine($"- ReplacementTargetInactive     : {diagnostics.ReplacementTargetInactiveCount}");
        builder.AppendLine($"- ReplacementCycle              : {diagnostics.ReplacementCycleCount}");
        builder.AppendLine($"- MultipleActiveReplacements    : {diagnostics.MultipleActiveReplacementsCount}");
        builder.AppendLine($"- ScopeMismatchInReplacement    : {diagnostics.ScopeMismatchInReplacementCount}");
        foreach (var item in diagnostics.Diagnostics.Take(20))
        {
            builder.AppendLine($"  - {item.StableItemId} [{item.StableKind}/{item.DiagnosticType}/{item.Severity}] {item.Reason}");
            if (item.RelatedStableItemIds.Count > 0)
            {
                builder.AppendLine($"    related: {string.Join(", ", item.RelatedStableItemIds)}");
            }
        }

        if (view.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Warnings");
            foreach (var warning in view.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        return builder.ToString();
    }

    public static string RenderStableReplacementChain(StableReplacementChainResponse chain)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Stable Replacement Chain");
        builder.AppendLine("========================");
        builder.AppendLine($"Item       : {chain.ItemId}");
        builder.AppendLine($"Current    : {chain.CurrentItem.Id} [{chain.CurrentItem.Status}/{chain.CurrentItem.Lifecycle}]");
        builder.AppendLine($"Root       : {chain.RootItem?.Id ?? "-"}");
        builder.AppendLine($"Latest     : {chain.LatestItem?.Id ?? "-"} [{chain.LatestItem?.Status.ToString() ?? "-"} / {chain.LatestItem?.Lifecycle ?? "-"}]");
        builder.AppendLine();
        builder.AppendLine("Previous Items");
        if (chain.PreviousItems.Count == 0)
        {
            builder.AppendLine("- none");
        }
        else
        {
            foreach (var item in chain.PreviousItems)
            {
                builder.AppendLine($"- {item.Id} [{item.StableKind}/{item.Status}/{item.Lifecycle}] {item.Title}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Next Items");
        if (chain.NextItems.Count == 0)
        {
            builder.AppendLine("- none");
        }
        else
        {
            foreach (var item in chain.NextItems)
            {
                builder.AppendLine($"- {item.Id} [{item.StableKind}/{item.Status}/{item.Lifecycle}] {item.Title}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Relations");
        if (chain.Relations.Count == 0)
        {
            builder.AppendLine("- none");
        }
        else
        {
            foreach (var relation in chain.Relations)
            {
                builder.AppendLine($"- {relation.SourceId} --{relation.RelationType}--> {relation.TargetId} confidence={relation.Confidence:0.00}");
                builder.AppendLine($"  reviewId={relation.Metadata.GetValueOrDefault("reviewId", "-")} lifecycle={relation.Metadata.GetValueOrDefault("lifecycle", "-")} source={relation.Metadata.GetValueOrDefault("source", "-")}");
            }
        }

        if (chain.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Warnings");
            foreach (var warning in chain.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        return builder.ToString();
    }

    public static string RenderStableMemoryDetail(StableMemoryRecord item)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Stable Memory Detail");
        builder.AppendLine("============================");
        builder.AppendLine($"Id          : {item.Id}");
        builder.AppendLine($"Kind        : {item.StableKind}");
        builder.AppendLine($"Type        : {item.Type}");
        builder.AppendLine($"Status      : {item.Status}");
        builder.AppendLine($"Lifecycle   : {item.Lifecycle}");
        builder.AppendLine($"Scope       : {item.Scope?.ToString() ?? "-"}");
        builder.AppendLine($"Level       : {item.ConstraintLevel?.ToString() ?? "-"}");
        builder.AppendLine($"Importance  : {item.Importance:0.00}");
        builder.AppendLine($"Confidence  : {item.Confidence:0.00}");
        builder.AppendLine($"StableId    : {item.StableReviewCandidateId ?? "-"}");
        builder.AppendLine($"PromotionId : {item.PromotionCandidateId ?? "-"}");
        builder.AppendLine($"FeedbackId  : {item.FeedbackId ?? "-"}");
        builder.AppendLine($"LearningId  : {item.LearningCaseId ?? "-"}");
        builder.AppendLine($"WorkingId   : {item.WorkingItemId ?? "-"}");
        builder.AppendLine($"Evidence    : {(item.EvidenceRefs.Count == 0 ? "-" : string.Join(", ", item.EvidenceRefs))}");
        builder.AppendLine($"SourceRefs  : {(item.SourceRefs.Count == 0 ? "-" : string.Join(", ", item.SourceRefs))}");
        builder.AppendLine($"UpdatedAt   : {item.UpdatedAt:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"Content     : {item.Content}");
        return builder.ToString();
    }

    public static string RenderStableMemoryExplanation(StableMemoryExplanation explanation)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Stable Memory Explain");
        builder.AppendLine("=============================");
        builder.AppendLine($"StableItem : {explanation.StableItemId}");
        builder.AppendLine($"Kind       : {explanation.StableItem.StableKind}");
        builder.AppendLine($"Evidence   : {(explanation.EvidenceRefs.Count == 0 ? "-" : string.Join(", ", explanation.EvidenceRefs))}");
        builder.AppendLine();
        builder.AppendLine("Source Refs");
        builder.AppendLine($"- StableReview : {explanation.StableItem.StableReviewCandidateId ?? "-"}");
        builder.AppendLine($"- Promotion    : {explanation.StableItem.PromotionCandidateId ?? "-"}");
        builder.AppendLine($"- Feedback     : {explanation.StableItem.FeedbackId ?? "-"}");
        builder.AppendLine($"- LearningCase : {explanation.StableItem.LearningCaseId ?? "-"}");
        builder.AppendLine($"- WorkingItem  : {explanation.StableItem.WorkingItemId ?? "-"}");
        if (explanation.Provenance is not null)
        {
            builder.AppendLine();
            builder.AppendLine("Provenance");
            builder.AppendLine($"- targetKind={explanation.Provenance.TargetItemKind}");
            builder.AppendLine($"- stableReview={explanation.Provenance.StableReviewCandidate?.StableReviewCandidateId ?? "-"}");
            builder.AppendLine($"- promotion={explanation.Provenance.PromotionCandidate?.CandidateId ?? "-"}");
            builder.AppendLine($"- feedback={explanation.Provenance.FeedbackSignal?.FeedbackId ?? "-"}");
            builder.AppendLine($"- learningCase={explanation.Provenance.LearningCase?.CaseId ?? "-"}");
            builder.AppendLine($"- sourceWorkingItem={explanation.Provenance.SourceWorkingItem?.ItemId ?? "-"}");
            builder.AppendLine($"- missingLinks={(explanation.Provenance.MissingLinks.Count == 0 ? "-" : string.Join(", ", explanation.Provenance.MissingLinks))}");
        }

        if (explanation.Diagnostics.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Diagnostics");
            foreach (var diagnostic in explanation.Diagnostics)
            {
                builder.AppendLine($"- {diagnostic.DiagnosticType} [{diagnostic.Severity}] {diagnostic.Reason}");
            }
        }

        if (explanation.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Warnings");
            foreach (var warning in explanation.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        return builder.ToString();
    }

    public static string RenderStableLifecycleReviewResult(StableLifecycleReviewResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Stable Lifecycle Review Result");
        builder.AppendLine("==============================");
        builder.AppendLine($"OperationId : {result.OperationId}");
        builder.AppendLine($"StableItem  : {result.StableItemId}");
        builder.AppendLine($"Kind        : {result.StableKind}");
        builder.AppendLine($"Action      : {result.Action}");
        builder.AppendLine($"Status      : {result.FromStatus} -> {result.ToStatus}");
        builder.AppendLine($"Lifecycle   : {result.FromLifecycle} -> {result.ToLifecycle}");
        builder.AppendLine($"ReviewId    : {result.ReviewId}");
        builder.AppendLine($"Reviewer    : {result.Reviewer}");
        builder.AppendLine($"Reason      : {result.Reason}");
        builder.AppendLine($"Replacement : {result.ReplacementItemId ?? "-"}");
        builder.AppendLine($"ReviewedAt  : {result.ReviewedAt:yyyy-MM-dd HH:mm:ss}");
        if (result.Warnings.Count > 0)
        {
            builder.AppendLine("Warnings");
            foreach (var warning in result.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        if (result.Errors.Count > 0)
        {
            builder.AppendLine("Errors");
            foreach (var error in result.Errors)
            {
                builder.AppendLine($"- {error}");
            }
        }

        return builder.ToString();
    }

    public static string RenderStableLifecycleReviews(IReadOnlyList<StableLifecycleReviewRecord> reviews)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Stable Lifecycle Review History");
        builder.AppendLine("===============================");
        builder.AppendLine($"Count: {reviews.Count}");
        foreach (var review in reviews.Take(50))
        {
            builder.AppendLine($"- {review.ReviewId} {review.Action} {review.FromStatus}->{review.ToStatus} {review.FromLifecycle}->{review.ToLifecycle}");
            builder.AppendLine($"  reviewer={review.Reviewer} reason={review.Reason} reviewedAt={review.ReviewedAt:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"  replacement={review.ReplacementItemId ?? "-"}");
            if (review.Warnings.Count > 0)
            {
                builder.AppendLine($"  warnings={string.Join("; ", review.Warnings)}");
            }
        }

        return builder.ToString();
    }

    public static string RenderGlobalMemoryDetail(ContextGlobalItem item)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Global Context Detail");
        builder.AppendLine("=============================");
        builder.AppendLine($"Id         : {item.Id}");
        builder.AppendLine($"Scope      : {item.Scope}");
        builder.AppendLine($"Type       : {item.Type}");
        builder.AppendLine($"Tags       : {string.Join(',', item.Tags)}");
        builder.AppendLine($"SourceRefs : {string.Join(',', item.SourceRefs)}");
        builder.AppendLine($"Importance : {item.Importance:0.00}");
        builder.AppendLine($"UpdatedAt  : {item.UpdatedAt:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"Content    : {item.Content}");
        return builder.ToString();
    }

    public static string RenderConstraints(ServiceConstraintsSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Constraints");
        builder.AppendLine("===================");
        builder.AppendLine($"Count: {snapshot.Constraints.Count}");
        foreach (var item in snapshot.Constraints.Take(20))
        {
            builder.AppendLine($"- {item.Id} [{item.Level}/{item.Status}] scope={item.Scope} appliesTo={string.Join(',', item.AppliesToRefs)}");
        }
        return builder.ToString();
    }

    public static string RenderConstraintDetail(ContextConstraint item)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Constraint Detail");
        builder.AppendLine("=========================");
        builder.AppendLine($"Id         : {item.Id}");
        builder.AppendLine($"Scope      : {item.Scope}");
        builder.AppendLine($"Type       : {item.Level}");
        builder.AppendLine($"Severity   : {item.Level}");
        builder.AppendLine($"Status     : {item.Status}");
        builder.AppendLine($"AppliesTo  : {string.Join(',', item.AppliesToRefs)}");
        builder.AppendLine($"SourceRefs : {string.Join(',', item.SourceRefs)}");
        builder.AppendLine($"Content    : {item.Content}");
        return builder.ToString();
    }

    public static string RenderConstraintGaps(ServiceConstraintGapsSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Constraint Gaps");
        builder.AppendLine("=======================");
        builder.AppendLine($"时间    : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务    : {snapshot.BaseUrl}");
        builder.AppendLine($"Count   : {snapshot.Gaps.Count}");
        builder.AppendLine($"Filter  : status={snapshot.Status ?? "-"} severity={snapshot.Severity ?? "-"} limit={snapshot.Limit} offset={snapshot.Offset}");
        foreach (var gap in snapshot.Gaps.Take(20))
        {
            builder.AppendLine($"- {gap.GapId} [{gap.Status}/{gap.Severity}] sample={gap.SourceSampleId} source={gap.Source}");
            builder.AppendLine($"  expected : {gap.ExpectedConstraintText}");
            builder.AppendLine($"  suggest  : scope={gap.SuggestedConstraintScope} type={gap.SuggestedConstraintType} title={gap.SuggestedConstraintTitle}");
            builder.AppendLine($"  evidence : {string.Join(", ", gap.EvidenceRefs)}");
        }

        return builder.ToString();
    }

    public static string RenderConstraintGapDetail(ConstraintGapCandidate gap)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Constraint Gap Detail");
        builder.AppendLine("=============================");
        builder.AppendLine($"GapId                  : {gap.GapId}");
        builder.AppendLine($"Status                 : {gap.Status}");
        builder.AppendLine($"Severity               : {gap.Severity}");
        builder.AppendLine($"Source                 : {gap.Source}");
        builder.AppendLine($"SourceSampleId         : {gap.SourceSampleId}");
        builder.AppendLine($"SourceOperationId      : {gap.SourceOperationId}");
        builder.AppendLine($"ExpectedConstraintText : {gap.ExpectedConstraintText}");
        builder.AppendLine($"MatchedConstraintIds   : {(gap.MatchedConstraintIds.Count == 0 ? "-" : string.Join(", ", gap.MatchedConstraintIds))}");
        builder.AppendLine($"SuggestedTitle         : {gap.SuggestedConstraintTitle}");
        builder.AppendLine($"SuggestedScope         : {gap.SuggestedConstraintScope}");
        builder.AppendLine($"SuggestedType          : {gap.SuggestedConstraintType}");
        builder.AppendLine($"Reason                 : {gap.Reason}");
        builder.AppendLine($"EvidenceRefs           : {string.Join(", ", gap.EvidenceRefs)}");
        if (gap.Metadata.Count > 0)
        {
            builder.AppendLine("Metadata");
            foreach (var pair in gap.Metadata.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"- {pair.Key}={pair.Value}");
            }
        }

        return builder.ToString();
    }

    public static string RenderConstraintGapReviewResult(ConstraintGapReviewResult response)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Constraint Gap Review Result");
        builder.AppendLine("============================");
        builder.AppendLine($"OperationId         : {response.OperationId}");
        builder.AppendLine($"GapId               : {response.GapId}");
        builder.AppendLine($"Action              : {response.Action}");
        builder.AppendLine($"Status              : {response.Status}");
        builder.AppendLine($"ReviewId            : {response.ReviewId}");
        builder.AppendLine($"Reviewer            : {response.Reviewer}");
        builder.AppendLine($"Reason              : {response.Reason}");
        builder.AppendLine($"ReviewedAt          : {(response.ReviewedAt == default ? "-" : response.ReviewedAt.ToString("yyyy-MM-dd HH:mm:ss"))}");
        builder.AppendLine($"CreatedConstraintId : {response.CreatedConstraintId ?? response.TargetItemId ?? "-"}");
        builder.AppendLine($"TargetKind          : {response.TargetItemKind ?? "-"}");
        builder.AppendLine($"TargetLayer         : {response.TargetLayer ?? "-"}");
        if (response.Warnings.Count > 0)
        {
            builder.AppendLine("Warnings");
            foreach (var warning in response.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        if (response.Errors.Count > 0)
        {
            builder.AppendLine("Errors");
            foreach (var error in response.Errors)
            {
                builder.AppendLine($"- {error}");
            }
        }

        return builder.ToString();
    }

    public static string RenderConstraintGapReviews(IReadOnlyList<ConstraintGapReviewRecord> reviews)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Constraint Gap Review History");
        builder.AppendLine("=============================");
        if (reviews.Count == 0)
        {
            builder.AppendLine("(empty)");
            return builder.ToString();
        }

        foreach (var review in reviews)
        {
            builder.AppendLine($"- {review.ReviewId} [{review.Action}] {review.FromStatus} -> {review.ToStatus}");
            builder.AppendLine($"  reviewer            : {review.Reviewer}");
            builder.AppendLine($"  reason              : {review.Reason}");
            builder.AppendLine($"  createdConstraintId : {review.CreatedConstraintId ?? "-"}");
            builder.AppendLine($"  source              : sample={review.SourceSampleId} operation={review.SourceOperationId}");
            builder.AppendLine($"  expected            : {review.ExpectedConstraintText}");
            builder.AppendLine($"  evidenceRefs        : {string.Join(", ", review.EvidenceRefs)}");
            builder.AppendLine($"  reviewedAt          : {(review.ReviewedAt == default ? review.CreatedAt : review.ReviewedAt):yyyy-MM-dd HH:mm:ss}");
        }

        return builder.ToString();
    }

    public static string RenderCandidateConstraints(ServiceCandidateConstraintsSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Candidate Constraints");
        builder.AppendLine("=============================");
        builder.AppendLine($"时间    : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务    : {snapshot.BaseUrl}");
        builder.AppendLine($"Count   : {snapshot.Constraints.Count}");
        builder.AppendLine($"Filter  : status={snapshot.Status?.ToString() ?? "-"} limit={snapshot.Limit} offset={snapshot.Offset}");
        foreach (var item in snapshot.Constraints.Take(20))
        {
            builder.AppendLine($"- {item.Id} [{item.Level}/{item.Status}] scope={item.Scope}");
            builder.AppendLine($"  source   : gap={ReadMetadata(item, "sourceConstraintGapId")} sample={ReadMetadata(item, "sourceSampleId")}");
            builder.AppendLine($"  evidence : {ReadMetadata(item, "evidenceRefs")}");
            builder.AppendLine($"  content  : {item.Content}");
        }

        return builder.ToString();
    }

    public static string RenderCandidateConstraintDetail(ContextConstraint item)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Candidate Constraint Detail");
        builder.AppendLine("===================================");
        builder.AppendLine($"Id         : {item.Id}");
        builder.AppendLine($"Scope      : {item.Scope}");
        builder.AppendLine($"Level      : {item.Level}");
        builder.AppendLine($"Status     : {item.Status}");
        builder.AppendLine($"Confidence : {item.Confidence:0.###}");
        builder.AppendLine($"SourceRefs : {string.Join(", ", item.SourceRefs)}");
        builder.AppendLine($"Content    : {item.Content}");
        if (item.Metadata.Count > 0)
        {
            builder.AppendLine("Metadata");
            foreach (var pair in item.Metadata.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"- {pair.Key}={pair.Value}");
            }
        }

        return builder.ToString();
    }

    public static string RenderCandidateConstraintReviewResult(CandidateConstraintReviewResult response)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Candidate Constraint Review Result");
        builder.AppendLine("==================================");
        builder.AppendLine($"OperationId           : {response.OperationId}");
        builder.AppendLine($"ConstraintId          : {response.ConstraintId}");
        builder.AppendLine($"Action                : {response.Action}");
        builder.AppendLine($"Status                : {response.Status}");
        builder.AppendLine($"ReviewId              : {response.ReviewId}");
        builder.AppendLine($"Reviewer              : {response.Reviewer}");
        builder.AppendLine($"Reason                : {response.Reason}");
        builder.AppendLine($"ReviewedAt            : {(response.ReviewedAt == default ? "-" : response.ReviewedAt.ToString("yyyy-MM-dd HH:mm:ss"))}");
        builder.AppendLine($"ActivatedConstraintId : {response.ActivatedConstraintId ?? "-"}");
        builder.AppendLine($"TargetLayer           : {response.TargetLayer ?? "-"}");
        if (response.Warnings.Count > 0)
        {
            builder.AppendLine("Warnings");
            foreach (var warning in response.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        if (response.Errors.Count > 0)
        {
            builder.AppendLine("Errors");
            foreach (var error in response.Errors)
            {
                builder.AppendLine($"- {error}");
            }
        }

        return builder.ToString();
    }

    public static string RenderCandidateConstraintReviews(IReadOnlyList<CandidateConstraintReviewRecord> reviews)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Candidate Constraint Review History");
        builder.AppendLine("===================================");
        if (reviews.Count == 0)
        {
            builder.AppendLine("(empty)");
            return builder.ToString();
        }

        foreach (var review in reviews)
        {
            builder.AppendLine($"- {review.ReviewId} [{review.Action}] {review.FromStatus} -> {review.ToStatus}");
            builder.AppendLine($"  reviewer            : {review.Reviewer}");
            builder.AppendLine($"  reason              : {review.Reason}");
            builder.AppendLine($"  activatedConstraint : {review.ActivatedConstraintId ?? "-"}");
            builder.AppendLine($"  source              : gap={review.SourceConstraintGapId} sample={review.SourceSampleId} operation={review.SourceOperationId}");
            builder.AppendLine($"  evidenceRefs        : {string.Join(", ", review.EvidenceRefs)}");
            builder.AppendLine($"  reviewedAt          : {(review.ReviewedAt == default ? review.CreatedAt : review.ReviewedAt):yyyy-MM-dd HH:mm:ss}");
        }

        return builder.ToString();
    }

    public static string RenderProvenance(ContextProvenanceResponse provenance)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Provenance");
        builder.AppendLine("==================");
        builder.AppendLine($"ItemId     : {provenance.ItemId}");
        builder.AppendLine($"TargetKind : {(string.IsNullOrWhiteSpace(provenance.TargetItemKind) ? "-" : provenance.TargetItemKind)}");
        if (provenance.TargetMemoryItem is not null)
        {
            builder.AppendLine("Target Memory");
            builder.AppendLine($"- {provenance.TargetMemoryItem.Id} [{provenance.TargetMemoryItem.Layer}/{provenance.TargetMemoryItem.Status}/{provenance.TargetMemoryItem.Type}]");
            builder.AppendLine($"  sourceRefs : {string.Join(", ", provenance.TargetMemoryItem.SourceRefs)}");
        }

        if (provenance.TargetConstraint is not null)
        {
            builder.AppendLine("Target Constraint");
            builder.AppendLine($"- {provenance.TargetConstraint.Id} [{provenance.TargetConstraint.Level}/{provenance.TargetConstraint.Status}]");
            builder.AppendLine($"  sourceRefs : {string.Join(", ", provenance.TargetConstraint.SourceRefs)}");
        }

        if (provenance.StableReviewCandidate is not null)
        {
            builder.AppendLine("Stable Review Candidate");
            builder.AppendLine($"- {provenance.StableReviewCandidate.StableReviewCandidateId} [{provenance.StableReviewCandidate.Status}/{provenance.StableReviewCandidate.ValidationStatus}]");
            builder.AppendLine($"  source     : promotion={provenance.StableReviewCandidate.SourceCandidateId} target={provenance.StableReviewCandidate.SourceTargetItemId} learningCase={provenance.StableReviewCandidate.SourceLearningCaseId ?? "-"}");
        }

        if (provenance.PromotionCandidate is not null)
        {
            builder.AppendLine("Promotion Candidate");
            builder.AppendLine($"- {provenance.PromotionCandidate.CandidateId} [{provenance.PromotionCandidate.Kind}/{provenance.PromotionCandidate.Status}] target={provenance.PromotionCandidate.SuggestedTargetLayer}");
            builder.AppendLine($"  workingItem: {provenance.PromotionCandidate.SourceWorkingItemId}");
        }

        if (provenance.FeedbackSignal is not null)
        {
            builder.AppendLine("Feedback Signal");
            builder.AppendLine($"- {provenance.FeedbackSignal.FeedbackId} [{provenance.FeedbackSignal.Action}] reviewer={provenance.FeedbackSignal.Reviewer}");
        }

        if (provenance.LearningCase is not null)
        {
            builder.AppendLine("Learning Case");
            builder.AppendLine($"- {provenance.LearningCase.CaseId} [{provenance.LearningCase.CaseKind}/{provenance.LearningCase.Signal}/{provenance.LearningCase.Status}]");
        }

        if (provenance.SourceWorkingItem is not null)
        {
            builder.AppendLine("Source Working Item");
            builder.AppendLine($"- {provenance.SourceWorkingItem.ItemId} [{provenance.SourceWorkingItem.Kind}/{provenance.SourceWorkingItem.Status}] {provenance.SourceWorkingItem.Summary}");
        }

        builder.AppendLine($"EvidenceRefs : {(provenance.EvidenceRefs.Count == 0 ? "-" : string.Join(", ", provenance.EvidenceRefs))}");
        builder.AppendLine($"StableReviews: {provenance.StableReviewHistory.Count}");
        foreach (var review in provenance.StableReviewHistory.Take(5))
        {
            builder.AppendLine($"- {review.ReviewId} [{review.Action}] {review.FromStatus}->{review.ToStatus} target={review.StableTargetItemId ?? "-"}");
        }

        builder.AppendLine($"PromotionReviews: {provenance.PromotionReviewHistory.Count}");
        foreach (var review in provenance.PromotionReviewHistory.Take(5))
        {
            builder.AppendLine($"- {review.ReviewId} [{review.Action}] {review.FromStatus}->{review.ToStatus} target={review.TargetItemId ?? "-"}");
        }

        if (provenance.Diagnostics.Count > 0)
        {
            builder.AppendLine("Diagnostics");
            foreach (var diagnostic in provenance.Diagnostics)
            {
                builder.AppendLine($"- {diagnostic.Code} [{diagnostic.Severity}] {diagnostic.Message}");
            }
        }

        if (provenance.MissingLinks.Count > 0)
        {
            builder.AppendLine($"MissingLinks: {string.Join(", ", provenance.MissingLinks)}");
        }

        if (provenance.Warnings.Count > 0)
        {
            builder.AppendLine("Warnings");
            foreach (var warning in provenance.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        return builder.ToString();
    }

    public static string RenderRelations(ServiceRelationsSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Relations");
        builder.AppendLine("=================");
        builder.AppendLine($"时间       : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务       : {snapshot.BaseUrl}");
        builder.AppendLine();
        builder.AppendLine("Relation Types");
        builder.AppendLine($"Count: {snapshot.RelationTypes.Count}");
        foreach (var type in snapshot.RelationTypes.Take(20))
        {
            builder.AppendLine($"- {type.Type} directional={type.IsDirectional} inverse={type.InverseType ?? "-"} weight={type.DefaultWeight:0.00} evidence={(type.RequiresEvidence ? "yes" : "no")} normalExpansion={(type.AllowsNormalExpansion ? "yes" : "no")}");
        }

        AppendRelationDiagnostics(builder, "Global Relation Diagnostics", snapshot.Diagnostics);

        if (!string.IsNullOrWhiteSpace(snapshot.ItemId))
        {
            builder.AppendLine();
            builder.AppendLine("Item Relations");
            builder.AppendLine($"ItemId   : {snapshot.ItemId}");
            builder.AppendLine($"Outgoing : {snapshot.Relations.Outgoing.Count}");
            foreach (var relation in snapshot.Relations.Outgoing)
            {
                builder.AppendLine($"- OUT {relation.SourceId} -> {relation.TargetId} type={relation.RelationType} weight={relation.Weight:0.00} confidence={relation.Confidence:0.00}");
            }

            builder.AppendLine($"Incoming : {snapshot.Relations.Incoming.Count}");
            foreach (var relation in snapshot.Relations.Incoming)
            {
                builder.AppendLine($"- IN  {relation.SourceId} -> {relation.TargetId} type={relation.RelationType} weight={relation.Weight:0.00} confidence={relation.Confidence:0.00}");
            }

            if (snapshot.ItemDiagnostics is not null)
            {
                AppendRelationDiagnostics(builder, "Item Relation Diagnostics", snapshot.ItemDiagnostics);
            }
        }

        return builder.ToString();
    }

    public static string RenderRelationExplain(RelationExplainResponse explain)
    {
        var relation = explain.Relation;
        var builder = new StringBuilder();
        builder.AppendLine("Service Relation Explain");
        builder.AppendLine("========================");
        builder.AppendLine($"RelationId : {explain.RelationId}");
        builder.AppendLine($"Type       : {relation?.RelationType ?? explain.TypeDefinition?.Type ?? "-"}");
        builder.AppendLine($"Source     : {relation?.SourceId ?? "-"} ({explain.SourceItem?.Kind ?? "unknown"}, lifecycle={explain.SourceItem?.Lifecycle ?? "-"})");
        builder.AppendLine($"Target     : {relation?.TargetId ?? "-"} ({explain.TargetItem?.Kind ?? "unknown"}, lifecycle={explain.TargetItem?.Lifecycle ?? "-"})");
        builder.AppendLine($"Inverse    : {explain.InverseRelation?.Id ?? "-"}");
        builder.AppendLine($"Confidence : {explain.Confidence:0.00} reason={BlankDash(explain.ConfidenceReason)}");
        builder.AppendLine($"Lifecycle  : {BlankDash(explain.Lifecycle)}");
        builder.AppendLine($"Review     : {BlankDash(explain.ReviewStatus)}");
        builder.AppendLine();
        builder.AppendLine("Evidence");
        builder.AppendLine($"EvidenceRefs: {string.Join(", ", explain.EvidenceRefs.DefaultIfEmpty("-"))}");
        builder.AppendLine($"SourceRefs  : {string.Join(", ", explain.SourceRefs.DefaultIfEmpty("-"))}");
        foreach (var evidence in explain.Evidence.Take(10))
        {
            builder.AppendLine($"- {evidence.EvidenceId} kind={BlankDash(evidence.EvidenceKind)} sourceOperation={BlankDash(evidence.SourceOperationId)} sourceItem={BlankDash(evidence.SourceItemId)}");
            if (!string.IsNullOrWhiteSpace(evidence.EvidenceText))
            {
                builder.AppendLine($"  text: {evidence.EvidenceText}");
            }
        }

        if (explain.TypeDefinition is not null)
        {
            builder.AppendLine();
            builder.AppendLine("Type Definition");
            builder.AppendLine($"- directional={explain.TypeDefinition.IsDirectional} inverse={explain.TypeDefinition.InverseType ?? "-"} requiresEvidence={explain.TypeDefinition.RequiresEvidence} normalExpansion={explain.TypeDefinition.AllowsNormalExpansion}");
        }

        if (explain.Diagnostics.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Diagnostics");
            foreach (var diagnostic in explain.Diagnostics.Take(20))
            {
                builder.AppendLine($"- {diagnostic.DiagnosticType} [{diagnostic.Severity}] {diagnostic.Reason}");
            }
        }

        if (explain.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Warnings");
            foreach (var warning in explain.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        return builder.ToString();
    }

    private static void AppendRelationDiagnostics(
        StringBuilder builder,
        string title,
        RelationGraphDiagnosticsReport report)
    {
        builder.AppendLine();
        builder.AppendLine(title);
        builder.AppendLine($"Relations={report.RelationCount} Diagnostics={report.DiagnosticCount}");
        foreach (var diagnostic in report.Diagnostics.Take(20))
        {
            builder.AppendLine($"- {diagnostic.DiagnosticType} [{diagnostic.Severity}] relation={diagnostic.RelationId ?? "-"} {diagnostic.SourceId ?? "-"} --{diagnostic.RelationType ?? "-"}--> {diagnostic.TargetId ?? "-"}");
            builder.AppendLine($"  reason: {diagnostic.Reason}");
            if (diagnostic.RelatedItemIds.Count > 0)
            {
                builder.AppendLine($"  items : {string.Join(", ", diagnostic.RelatedItemIds)}");
            }
        }

        if (report.Warnings.Count > 0)
        {
            builder.AppendLine("Warnings");
            foreach (var warning in report.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }
    }

    private static string BlankDash(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value;
    }

    public static string RenderPolicy(ServicePolicySnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Policy");
        builder.AppendLine("==============");
        builder.AppendLine($"PersistedPolicies : {snapshot.Policies.Count}");
        builder.AppendLine($"DefaultPolicy     : {snapshot.DefaultPolicy.Name}");
        builder.AppendLine($"TokenBudget       : {snapshot.DefaultPolicy.TokenBudget}");
        builder.AppendLine($"SectionPriorities : {(snapshot.DefaultPolicy.SectionPriorities.Count == 0 ? "(default)" : string.Join(',', snapshot.DefaultPolicy.SectionPriorities.Select(p => $"{p.Key}={p.Value}")))}");
        builder.AppendLine("LifecyclePolicy");
        foreach (var note in snapshot.LifecycleNotes)
        {
            builder.AppendLine($"- {note}");
        }
        builder.AppendLine("ProviderCapabilities");
        foreach (var capability in snapshot.ProviderCapabilities)
        {
            builder.AppendLine($"- {capability.Name} [{capability.State}] active={(capability.Active ? "yes" : "no")}");
        }
        return builder.ToString();
    }

    public static string RenderShortTermMemory(ServiceShortTermMemorySnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Short-Term Memory");
        builder.AppendLine("=========================");
        builder.AppendLine($"RawEventCount    : {snapshot.Summary.RawEventCount}");
        builder.AppendLine($"WorkingItemCount : {snapshot.Summary.WorkingItemCount}");
        builder.AppendLine($"ActiveTasks      : {snapshot.Summary.ActiveTaskCount}");
        builder.AppendLine($"RecentDecisions  : {snapshot.Summary.RecentDecisionCount}");
        builder.AppendLine($"OpenQuestions    : {snapshot.Summary.OpenQuestionCount}");
        builder.AppendLine($"KnownIssues      : {snapshot.Summary.KnownIssueCount}");
        builder.AppendLine($"RecentWarnings   : {snapshot.Summary.RecentWarningCount}");
        AppendMaintenanceSection(builder, snapshot.Maintenance);
        AppendWorkingSection(builder, "ActiveTasks", snapshot.Summary.ActiveTasks);
        AppendWorkingSection(builder, "RecentDecisions", snapshot.Summary.RecentDecisions);
        AppendWorkingSection(builder, "OpenQuestions", snapshot.Summary.OpenQuestions);
        AppendWorkingSection(builder, "KnownIssues", snapshot.Summary.KnownIssues);
        AppendWorkingSection(builder, "RecentWarnings", snapshot.Summary.RecentWarnings);
        builder.AppendLine("LatestRawEvents");
        foreach (var item in snapshot.RawEvents)
        {
            builder.AppendLine($"- {item.EventId} [{item.EventKind}] seq={item.SequenceId} source={item.Source} tags={string.Join(',', item.Tags)}");
        }
        builder.AppendLine();
        builder.AppendLine(RenderShortTermArchiveSummary(snapshot.ArchiveSummary));
        builder.AppendLine();
        builder.AppendLine(RenderShortTermArchiveItems(snapshot.ArchiveItems));
        builder.AppendLine();
        builder.AppendLine(RenderShortTermCompactionRuns(snapshot.RecentRuns));
        return builder.ToString();
    }

    public static string RenderShortTermCompactionResult(ShortTermMemoryCompactionResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Short-Term Compaction Result");
        builder.AppendLine("===========================");
        builder.AppendLine($"Scope                  : {result.WorkspaceId}/{result.CollectionId} session={result.SessionId ?? "-"}");
        builder.AppendLine($"ActiveRawEvents        : {result.ActiveRawEventCountBefore} -> {result.ActiveRawEventCountAfter}");
        builder.AppendLine($"ActiveWorkingItems     : {result.ActiveWorkingItemCountBefore} -> {result.ActiveWorkingItemCountAfter}");
        builder.AppendLine($"MergedWorkingItems     : {result.MergedWorkingItems}");
        builder.AppendLine($"MergedByWorkingKey     : {result.MergedByWorkingKeyGroups}");
        builder.AppendLine($"MergedByTitle          : {result.MergedByTitleGroups}");
        builder.AppendLine($"ArchivedRawEvents      : {result.ArchivedRawEventCount}");
        builder.AppendLine($"ArchivedWorkingItems   : {result.ArchivedWorkingItemCount}");
        builder.AppendLine($"ArchivedResolvedItems  : {result.ArchivedResolvedWorkingItemCount}");
        builder.AppendLine($"EvidenceRefsTrimmed    : {result.EvidenceRefsTrimmed}");
        builder.AppendLine($"CompletedAt            : {result.CompletedAt:yyyy-MM-dd HH:mm:ss}");
        return builder.ToString();
    }

    public static string RenderShortTermArchiveSummary(ShortTermArchiveSummary summary)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Short-Term Archive Summary");
        builder.AppendLine("==========================");
        builder.AppendLine($"Scope                   : {summary.WorkspaceId}/{summary.CollectionId ?? "-"} session={summary.SessionId ?? "-"}");
        builder.AppendLine($"ArchivedRawEvents       : {summary.ArchivedRawEventCount}");
        builder.AppendLine($"ArchivedWorkingItems    : {summary.ArchivedWorkingItemCount}");
        builder.AppendLine($"ArchivedResolvedItems   : {summary.ArchivedResolvedWorkingItemCount}");
        builder.AppendLine($"ArchivedActiveTasks     : {summary.ArchivedActiveTaskCount}");
        builder.AppendLine($"ArchivedDecisions       : {summary.ArchivedRecentDecisionCount}");
        builder.AppendLine($"ArchivedOpenQuestions   : {summary.ArchivedOpenQuestionCount}");
        builder.AppendLine($"ArchivedKnownIssues     : {summary.ArchivedKnownIssueCount}");
        builder.AppendLine($"ArchivedRecentWarnings  : {summary.ArchivedRecentWarningCount}");
        builder.AppendLine($"LatestArchivedAt        : {(summary.LatestArchivedAt is null ? "-" : summary.LatestArchivedAt.Value.ToString("yyyy-MM-dd HH:mm:ss"))}");
        return builder.ToString();
    }

    public static string RenderShortTermArchiveItems(ShortTermArchiveItemsResponse response)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Short-Term Archive Items");
        builder.AppendLine("========================");
        builder.AppendLine($"ArchivedRawCount        : {response.RawEvents.Count}");
        foreach (var item in response.RawEvents)
        {
            builder.AppendLine($"- RAW {item.EventId} [{item.EventKind}] {item.Source}");
        }

        builder.AppendLine($"ArchivedWorkingCount    : {response.WorkingItems.Count}");
        foreach (var item in response.WorkingItems)
        {
            builder.AppendLine($"- WORK {item.ItemId} [{item.Kind}/{item.Status}] {item.Summary}");
        }

        return builder.ToString();
    }

    public static string RenderShortTermCompactionRuns(IReadOnlyList<ShortTermCompactionRun> runs)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Short-Term Compaction Runs");
        builder.AppendLine("==========================");
        if (runs.Count == 0)
        {
            builder.AppendLine("(empty)");
            return builder.ToString();
        }

        foreach (var run in runs)
        {
            builder.AppendLine($"- {run.RunId} [{run.Trigger}] {run.StartedAt:yyyy-MM-dd HH:mm:ss} dup={run.RemovedDuplicates} archiveRaw={run.ArchivedRawEvents} archiveWorking={run.ArchivedWorkingItems}");
        }

        return builder.ToString();
    }

    public static string RenderPromotionCandidates(ServicePromotionCandidatesSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Promotion Candidates");
        builder.AppendLine("============================");
        builder.AppendLine($"时间        : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务        : {snapshot.BaseUrl}");
        builder.AppendLine($"Candidates  : {snapshot.Candidates.Count}");
        builder.AppendLine($"Filters     : status={snapshot.Status?.ToString() ?? "-"} kind={snapshot.Kind ?? "-"} target={snapshot.SuggestedTargetLayer ?? "-"} minConf={snapshot.MinConfidence?.ToString("0.00") ?? "-"} minImp={snapshot.MinImportance?.ToString("0.00") ?? "-"} limit={snapshot.Limit} offset={snapshot.Offset}");
        if (snapshot.Candidates.Count == 0)
        {
            builder.AppendLine("(empty)");
            return builder.ToString();
        }

        foreach (var candidate in snapshot.Candidates)
        {
            builder.AppendLine($"- {candidate.CandidateId} [{candidate.Kind}/{candidate.Status}]");
            builder.AppendLine($"  title        : {candidate.Title}");
            builder.AppendLine($"  target       : {candidate.SuggestedTargetLayer}");
            builder.AppendLine($"  confidence   : {candidate.Confidence:0.00}");
            builder.AppendLine($"  importance   : {candidate.Importance:0.00}");
            builder.AppendLine($"  reason       : {candidate.Reason}");
            builder.AppendLine($"  evidenceRefs : {string.Join(", ", candidate.EvidenceRefs)}");
        }

        return builder.ToString();
    }

    public static string RenderPromotionCandidateDetail(ShortTermPromotionCandidate candidate)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Promotion Candidate Detail");
        builder.AppendLine("==========================");
        builder.AppendLine($"CandidateId      : {candidate.CandidateId}");
        builder.AppendLine($"SourceWorkingId  : {candidate.SourceWorkingItemId}");
        builder.AppendLine($"Kind             : {candidate.Kind}");
        builder.AppendLine($"Title            : {candidate.Title}");
        builder.AppendLine($"TargetLayer      : {candidate.SuggestedTargetLayer}");
        builder.AppendLine($"Status           : {candidate.Status}");
        builder.AppendLine($"Confidence       : {candidate.Confidence:0.00}");
        builder.AppendLine($"Importance       : {candidate.Importance:0.00}");
        builder.AppendLine($"Reason           : {candidate.Reason}");
        builder.AppendLine($"DedupeKey        : {candidate.DedupeKey}");
        builder.AppendLine($"SourceFingerprint: {candidate.SourceFingerprint}");
        builder.AppendLine($"GeneratedBy      : {candidate.GeneratedBy}");
        builder.AppendLine($"PolicyVersion    : {candidate.PolicyVersion}");
        builder.AppendLine($"Rule             : {candidate.RuleName} ({candidate.RuleVersion})");
        builder.AppendLine($"EvidenceRefs     : {string.Join(", ", candidate.EvidenceRefs)}");
        return builder.ToString();
    }

    public static string RenderPromotionCandidateExplanation(ShortTermPromotionCandidateExplanation explanation)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Promotion Candidate Explain");
        builder.AppendLine("===========================");
        builder.AppendLine($"CandidateId      : {explanation.CandidateId}");
        builder.AppendLine($"TargetLayer      : {explanation.SuggestedTargetLayer}");
        builder.AppendLine($"Confidence       : {explanation.Confidence:0.00}");
        builder.AppendLine($"Importance       : {explanation.Importance:0.00}");
        builder.AppendLine($"Reason           : {explanation.Reason}");
        builder.AppendLine($"Rule             : {explanation.RuleName} ({explanation.RuleVersion})");
        builder.AppendLine($"PolicyVersion    : {explanation.PolicyVersion}");
        builder.AppendLine($"GeneratedBy      : {explanation.GeneratedBy}");
        builder.AppendLine($"DedupeKey        : {explanation.DedupeKey}");
        builder.AppendLine($"SourceFingerprint: {explanation.SourceFingerprint}");
        builder.AppendLine("SourceWorkingItem");
        builder.AppendLine($"- {explanation.SourceWorkingItem.ItemId} [{explanation.SourceWorkingItem.Kind}/{explanation.SourceWorkingItem.Status}] {explanation.SourceWorkingItem.Summary}");
        builder.AppendLine($"EvidenceRefs     : {string.Join(", ", explanation.EvidenceRefs)}");
        builder.AppendLine($"SourceRawEvents  : {explanation.SourceRawEvents.Count}");
        foreach (var item in explanation.SourceRawEvents)
        {
            builder.AppendLine($"- {item.EventId} [{item.EventKind}] {item.Source}");
        }
        if (explanation.Warnings.Count > 0)
        {
            builder.AppendLine("Warnings");
            foreach (var warning in explanation.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        return builder.ToString();
    }

    public static string RenderPromotionCandidateReviewResult(PromotionCandidateReviewResult response)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Promotion Candidate Review Result");
        builder.AppendLine("=================================");
        builder.AppendLine($"OperationId : {response.OperationId}");
        builder.AppendLine($"CandidateId : {response.CandidateId}");
        builder.AppendLine($"Action      : {response.Action}");
        builder.AppendLine($"Status      : {response.Status}");
        builder.AppendLine($"ReviewId    : {response.ReviewId}");
        builder.AppendLine($"Reviewer    : {response.Reviewer}");
        builder.AppendLine($"Reason      : {response.Reason}");
        builder.AppendLine($"ReviewedAt  : {(response.ReviewedAt == default ? "-" : response.ReviewedAt.ToString("yyyy-MM-dd HH:mm:ss"))}");
        builder.AppendLine($"TargetId    : {response.CreatedTargetItemId ?? response.TargetItemId ?? "-"}");
        builder.AppendLine($"TargetKind  : {response.TargetItemKind ?? "-"}");
        builder.AppendLine($"TargetLayer : {response.TargetLayer ?? "-"}");
        if (response.Warnings.Count > 0)
        {
            builder.AppendLine("Warnings");
            foreach (var warning in response.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        if (response.Errors.Count > 0)
        {
            builder.AppendLine("Errors");
            foreach (var error in response.Errors)
            {
                builder.AppendLine($"- {error}");
            }
        }

        return builder.ToString();
    }

    public static string RenderPromotionCandidateReviews(IReadOnlyList<PromotionCandidateReviewRecord> reviews)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Promotion Candidate Review History");
        builder.AppendLine("==================================");
        if (reviews.Count == 0)
        {
            builder.AppendLine("(empty)");
            return builder.ToString();
        }

        foreach (var review in reviews)
        {
            builder.AppendLine($"- {review.ReviewId} [{review.Action}] {review.FromStatus} -> {review.ToStatus}");
            builder.AppendLine($"  reviewer    : {review.Reviewer}");
            builder.AppendLine($"  reason      : {review.Reason}");
            builder.AppendLine($"  target      : {review.TargetItemKind ?? "-"} {review.TargetItemId ?? "-"} layer={review.TargetLayer ?? "-"}");
            builder.AppendLine($"  evidenceRefs: {string.Join(", ", review.EvidenceRefs)}");
            builder.AppendLine($"  reviewedAt  : {(review.ReviewedAt == default ? review.CreatedAt : review.ReviewedAt):yyyy-MM-dd HH:mm:ss}");
        }

        return builder.ToString();
    }

    public static string RenderStableReviewCandidates(ServiceStableReviewCandidatesSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Stable Review Candidates");
        builder.AppendLine("================================");
        builder.AppendLine($"时间        : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务        : {snapshot.BaseUrl}");
        builder.AppendLine($"Candidates  : {snapshot.Candidates.Count}");
        builder.AppendLine($"Filters     : status={snapshot.Status ?? "-"} validation={snapshot.ValidationStatus ?? "-"} kind={snapshot.Kind ?? "-"} target={snapshot.SuggestedStableTarget ?? "-"} limit={snapshot.Limit} offset={snapshot.Offset}");
        if (snapshot.Candidates.Count == 0)
        {
            builder.AppendLine("(empty)");
            return builder.ToString();
        }

        foreach (var candidate in snapshot.Candidates)
        {
            builder.AppendLine($"- {candidate.StableReviewCandidateId} [{candidate.Kind}/{candidate.Status}/{candidate.ValidationStatus}]");
            builder.AppendLine($"  title        : {candidate.Title}");
            builder.AppendLine($"  stableTarget : {candidate.SuggestedStableTarget}");
            builder.AppendLine($"  source       : candidate={candidate.SourceCandidateId} target={candidate.SourceTargetItemId} learningCase={candidate.SourceLearningCaseId ?? "-"}");
            builder.AppendLine($"  confidence   : {candidate.Confidence:0.00}");
            builder.AppendLine($"  importance   : {candidate.Importance:0.00}");
            builder.AppendLine($"  riskFlags    : {(candidate.RiskFlags.Count == 0 ? "-" : string.Join(", ", candidate.RiskFlags))}");
            builder.AppendLine($"  evidenceRefs : {string.Join(", ", candidate.EvidenceRefs)}");
        }

        return builder.ToString();
    }

    public static string RenderStableReviewCandidateDetail(StableReviewCandidate candidate)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Stable Review Candidate Detail");
        builder.AppendLine("==============================");
        builder.AppendLine($"StableReviewCandidateId : {candidate.StableReviewCandidateId}");
        builder.AppendLine($"SourceCandidateId       : {candidate.SourceCandidateId}");
        builder.AppendLine($"SourceTargetItemId      : {candidate.SourceTargetItemId}");
        builder.AppendLine($"SourceLearningCaseId    : {candidate.SourceLearningCaseId ?? "-"}");
        builder.AppendLine($"Kind                    : {candidate.Kind}");
        builder.AppendLine($"SuggestedStableTarget   : {candidate.SuggestedStableTarget}");
        builder.AppendLine($"Status                  : {candidate.Status}");
        builder.AppendLine($"ValidationStatus        : {candidate.ValidationStatus}");
        builder.AppendLine($"RiskFlags               : {(candidate.RiskFlags.Count == 0 ? "-" : string.Join(", ", candidate.RiskFlags))}");
        builder.AppendLine($"Confidence              : {candidate.Confidence:0.00}");
        builder.AppendLine($"Importance              : {candidate.Importance:0.00}");
        builder.AppendLine($"Reason                  : {candidate.Reason}");
        builder.AppendLine($"EvidenceRefs            : {string.Join(", ", candidate.EvidenceRefs)}");
        return builder.ToString();
    }

    public static string RenderStableReviewCandidateExplanation(StableReviewCandidateExplanation explanation)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Stable Review Candidate Explain");
        builder.AppendLine("===============================");
        builder.AppendLine($"StableReviewCandidateId : {explanation.StableReviewCandidateId}");
        builder.AppendLine($"ValidationStatus        : {explanation.ValidationStatus}");
        builder.AppendLine($"RiskFlags               : {(explanation.RiskFlags.Count == 0 ? "-" : string.Join(", ", explanation.RiskFlags))}");
        builder.AppendLine($"Reason                  : {explanation.Reason}");
        builder.AppendLine("Source Promotion Candidate");
        builder.AppendLine($"- {explanation.SourceCandidate.CandidateId} [{explanation.SourceCandidate.Kind}/{explanation.SourceCandidate.Status}] target={explanation.SourceCandidate.SuggestedTargetLayer}");
        builder.AppendLine($"  title    : {explanation.SourceCandidate.Title}");
        builder.AppendLine($"  evidence : {string.Join(", ", explanation.SourceCandidate.EvidenceRefs)}");
        if (explanation.SourceLearningCase is not null)
        {
            builder.AppendLine("Source Learning Case");
            builder.AppendLine($"- {explanation.SourceLearningCase.CaseId} [{explanation.SourceLearningCase.CaseKind}/{explanation.SourceLearningCase.Status}]");
            builder.AppendLine($"  evidence : {string.Join(", ", explanation.SourceLearningCase.EvidenceRefs)}");
        }

        if (explanation.SourceMemoryTarget is not null)
        {
            builder.AppendLine("Source Target Memory");
            builder.AppendLine($"- {explanation.SourceMemoryTarget.Id} [{explanation.SourceMemoryTarget.Layer}/{explanation.SourceMemoryTarget.Status}/{explanation.SourceMemoryTarget.Type}]");
            builder.AppendLine($"  sourceRefs: {string.Join(", ", explanation.SourceMemoryTarget.SourceRefs)}");
        }

        if (explanation.SourceConstraintTarget is not null)
        {
            builder.AppendLine("Source Target Constraint");
            builder.AppendLine($"- {explanation.SourceConstraintTarget.Id} [{explanation.SourceConstraintTarget.Level}/{explanation.SourceConstraintTarget.Status}]");
            builder.AppendLine($"  sourceRefs: {string.Join(", ", explanation.SourceConstraintTarget.SourceRefs)}");
        }

        builder.AppendLine($"EvidenceRefs            : {string.Join(", ", explanation.EvidenceRefs)}");
        if (explanation.Warnings.Count > 0)
        {
            builder.AppendLine("Warnings");
            foreach (var warning in explanation.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        return builder.ToString();
    }

    public static string RenderStableReviewDecisionResult(StableReviewDecisionResult response)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Stable Review Decision Result");
        builder.AppendLine("=============================");
        builder.AppendLine($"OperationId             : {response.OperationId}");
        builder.AppendLine($"StableReviewCandidateId : {response.StableReviewCandidateId}");
        builder.AppendLine($"Action                  : {response.Action}");
        builder.AppendLine($"Status                  : {response.Status}");
        builder.AppendLine($"ValidationStatus        : {response.ValidationStatus}");
        builder.AppendLine($"ReviewId                : {response.ReviewId}");
        builder.AppendLine($"Reviewer                : {response.Reviewer}");
        builder.AppendLine($"Reason                  : {response.Reason}");
        builder.AppendLine($"ReviewedAt              : {(response.ReviewedAt == default ? "-" : response.ReviewedAt.ToString("yyyy-MM-dd HH:mm:ss"))}");
        builder.AppendLine($"StableTargetId          : {response.CreatedStableTargetItemId ?? response.CreatedTargetItemId ?? "-"}");
        builder.AppendLine($"StableTargetKind        : {response.StableTargetItemKind ?? "-"}");
        builder.AppendLine($"TargetLayer             : {response.TargetLayer ?? "-"}");
        if (response.Warnings.Count > 0)
        {
            builder.AppendLine("Warnings");
            foreach (var warning in response.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        if (response.Errors.Count > 0)
        {
            builder.AppendLine("Errors");
            foreach (var error in response.Errors)
            {
                builder.AppendLine($"- {error}");
            }
        }

        return builder.ToString();
    }

    public static string RenderStableReviewCandidateReviews(IReadOnlyList<StableReviewRecord> reviews)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Stable Review Decision History");
        builder.AppendLine("==============================");
        if (reviews.Count == 0)
        {
            builder.AppendLine("(empty)");
            return builder.ToString();
        }

        foreach (var review in reviews)
        {
            builder.AppendLine($"- {review.ReviewId} [{review.Action}] {review.FromStatus} -> {review.ToStatus}");
            builder.AppendLine($"  reviewer       : {review.Reviewer}");
            builder.AppendLine($"  reason         : {review.Reason}");
            builder.AppendLine($"  validation     : {review.ValidationStatus}");
            builder.AppendLine($"  riskFlags      : {(review.RiskFlags.Count == 0 ? "-" : string.Join(", ", review.RiskFlags))}");
            builder.AppendLine($"  stableTarget   : {review.StableTargetItemKind ?? "-"} {review.StableTargetItemId ?? "-"} layer={review.TargetLayer ?? "-"}");
            builder.AppendLine($"  source         : promotion={review.SourcePromotionCandidateId} target={review.SourceTargetItemId} learningCase={review.SourceLearningCaseId ?? "-"}");
            builder.AppendLine($"  evidenceRefs   : {string.Join(", ", review.EvidenceRefs)}");
            builder.AppendLine($"  reviewedAt     : {(review.ReviewedAt == default ? review.CreatedAt : review.ReviewedAt):yyyy-MM-dd HH:mm:ss}");
        }

        return builder.ToString();
    }

    public static string RenderLearning(ServiceLearningSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Context Learning");
        builder.AppendLine("========================");
        builder.AppendLine($"时间     : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务     : {snapshot.BaseUrl}");
        builder.AppendLine($"Feedback : {snapshot.FeedbackSignals.Count}");
        builder.AppendLine($"Records  : {snapshot.Records.Count}");
        builder.AppendLine($"Cases    : {snapshot.Cases.Count}");
        builder.AppendLine($"Signals  : positive={snapshot.PositiveCount} negative={snapshot.NegativeCount} stale={snapshot.StaleCount}");
        if (snapshot.Summary is not null)
        {
            builder.AppendLine($"Summary  : records={snapshot.Summary.RecordCount} cases={snapshot.Summary.CaseCount}");
            builder.AppendLine($"Statuses : draft={snapshot.Summary.DraftCaseCount} candidate={snapshot.Summary.CandidateCaseCount} activeRegression={snapshot.Summary.ActiveRegressionCaseCount} archived={snapshot.Summary.ArchivedCaseCount} rejected={snapshot.Summary.RejectedCaseCount}");
        }

        if (snapshot.LastGeneration is not null)
        {
            builder.AppendLine($"Generation: scanned={snapshot.LastGeneration.RecordsScanned} created={snapshot.LastGeneration.Created} existing={snapshot.LastGeneration.Existing}");
        }

        if (snapshot.LastStatusUpdate is not null)
        {
            builder.AppendLine($"LastUpdate: {snapshot.LastStatusUpdate.CaseId} -> {snapshot.LastStatusUpdate.Status} op={snapshot.LastStatusUpdate.OperationId}");
        }

        builder.AppendLine();
        builder.AppendLine("Failure Types");
        var failureTypes = snapshot.Summary?.FailureTypeCounts ?? snapshot.FailureTypeSummary;
        if (failureTypes.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var pair in failureTypes.OrderBy(pair => pair.Key.ToString(), StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"- {pair.Key}: {pair.Value}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Case Kinds");
        if (snapshot.Summary is null || snapshot.Summary.CaseKindCounts.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var pair in snapshot.Summary.CaseKindCounts.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"- {pair.Key}: {pair.Value}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Active Regression Cases");
        if (snapshot.RegressionCases.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var learningCase in snapshot.RegressionCases.Take(10))
            {
                builder.AppendLine($"- {learningCase.CaseId} [{learningCase.CaseKind}] {learningCase.Title}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Promotion Feedback Signals");
        if (snapshot.FeedbackSignals.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var feedback in snapshot.FeedbackSignals.Take(20))
            {
                builder.AppendLine($"- {feedback.FeedbackId} [{feedback.Action}] candidate={feedback.CandidateId}");
                builder.AppendLine($"  reviewer : {feedback.Reviewer}");
                builder.AppendLine($"  target   : suggested={feedback.SuggestedTargetLayer} actual={feedback.ActualTargetLayer ?? "-"} created={feedback.CreatedTargetItemId ?? "-"}");
                builder.AppendLine($"  reason   : {feedback.Reason}");
                builder.AppendLine($"  evidence : {string.Join(", ", feedback.EvidenceRefs)}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Recent Feedback");
        if (snapshot.Records.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var record in snapshot.Records.Take(20))
            {
                builder.AppendLine($"- {record.RecordId} [{record.Signal}/{record.FailureType}] {record.EventKind}");
                builder.AppendLine($"  source   : {record.SourceKind}/{record.SourceId}");
                builder.AppendLine($"  candidate: {record.CandidateId ?? "-"} review={record.ReviewId ?? "-"}");
                builder.AppendLine($"  reason   : {record.Reason}");
                builder.AppendLine($"  evidence : {string.Join(", ", record.EvidenceRefs)}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Learning Cases");
        if (snapshot.Cases.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var learningCase in snapshot.Cases.Take(20))
            {
                builder.AppendLine($"- {learningCase.CaseId} [{learningCase.CaseKind}/{learningCase.Signal}/{learningCase.FailureType}/{learningCase.Status}]");
                builder.AppendLine($"  title    : {learningCase.Title}");
                builder.AppendLine($"  source   : {learningCase.SourceKind}/{learningCase.SourceId} record={learningCase.SourceRecordId}");
                builder.AppendLine($"  evidence : {string.Join(", ", learningCase.EvidenceRefs)}");
            }
        }

        return builder.ToString();
    }

    public static string RenderPolicyFeedbackDataset(ServicePolicyFeedbackDatasetSnapshot snapshot)
    {
        var dataset = snapshot.Dataset;
        var builder = new StringBuilder();
        builder.AppendLine("Service Policy Feedback Dataset");
        builder.AppendLine("================================");
        builder.AppendLine($"时间       : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务       : {snapshot.BaseUrl}");
        builder.AppendLine($"Dataset    : {dataset.DatasetId}");
        builder.AppendLine($"Name       : {dataset.Name}");
        builder.AppendLine($"Scope      : {dataset.Scope}");
        builder.AppendLine($"Policy     : {dataset.PolicyVersion}");
        builder.AppendLine($"Baseline   : {dataset.EvalBaselineRef}");
        builder.AppendLine($"Page       : offset={snapshot.Offset} limit={snapshot.Limit} records={dataset.Records.Count}");
        builder.AppendLine($"Labels     : positive={dataset.PositiveCount} negative={dataset.NegativeCount} neutral={dataset.NeutralCount}");

        builder.AppendLine();
        builder.AppendLine("Source Types");
        if (dataset.SourceTypes.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var pair in dataset.SourceTypes.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"- {pair.Key}: {pair.Value}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Recent Policy Feedback Records");
        if (dataset.Records.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var record in dataset.Records.Take(20))
            {
                builder.AppendLine($"- {record.FeedbackRecordId} [{record.Label}/{record.Action}] {record.SourceType}/{record.SourceId}");
                builder.AppendLine($"  workspace : {record.WorkspaceId} collection={record.CollectionId} session={record.SessionId ?? "-"}");
                builder.AppendLine($"  reviewer  : {record.Reviewer}");
                builder.AppendLine($"  target    : {record.TargetLayer}");
                builder.AppendLine($"  reason    : {record.Reason}");
                builder.AppendLine($"  positive  : {string.Join(", ", record.PositiveRefs)}");
                builder.AppendLine($"  negative  : {string.Join(", ", record.NegativeRefs)}");
                builder.AppendLine($"  evidence  : {string.Join(", ", record.EvidenceRefs)}");
                builder.AppendLine($"  createdAt : {record.CreatedAt:yyyy-MM-dd HH:mm:ss}");
            }
        }

        return builder.ToString();
    }

    public static string RenderLearningFeatures(ServiceLearningFeaturesSnapshot snapshot)
    {
        var dataset = snapshot.Dataset;
        var builder = new StringBuilder();
        builder.AppendLine("Service Learning Features");
        builder.AppendLine("=========================");
        builder.AppendLine($"时间       : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务       : {snapshot.BaseUrl}");
        builder.AppendLine($"Dataset    : {dataset.DatasetId}");
        builder.AppendLine($"Policy     : {dataset.PolicyVersion}");
        builder.AppendLine($"Page       : offset={snapshot.Offset} limit={snapshot.Limit} records={dataset.FeatureExamples.Count}");
        builder.AppendLine($"Counts     : features={dataset.FeatureCount} rankingPairs={dataset.RankingPairCount} routerIntent={dataset.RouterIntentExampleCount}");
        builder.AppendLine($"LatestExport: {(string.IsNullOrWhiteSpace(dataset.LatestExportPath) ? "-" : dataset.LatestExportPath)}");

        var quality = snapshot.QualityReport;
        builder.AppendLine();
        builder.AppendLine("Dataset Quality");
        builder.AppendLine($"- counts : policy={quality.PolicyFeedbackFeatureCount} rankingPairs={quality.RankingPairCount} routerIntent={quality.RouterIntentExampleCount}");
        builder.AppendLine($"- labels : positive={quality.PositiveCount} negative={quality.NegativeCount} neutral={quality.NeutralCount}");
        builder.AppendLine($"- risks  : {(quality.DataRisks.Count == 0 ? "-" : string.Join(", ", quality.DataRisks))}");
        builder.AppendLine($"- next   : {(string.IsNullOrWhiteSpace(quality.RecommendedNextAction) ? "-" : quality.RecommendedNextAction)}");
        builder.AppendLine("Task Readiness");
        if (quality.TaskReadiness.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var item in quality.TaskReadiness.Values.OrderBy(item => item.TaskName, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"- {item.TaskName}: {item.Status} ready={(item.Ready ? "yes" : "no")}");
                builder.AppendLine($"  next    : {item.RecommendedNextAction}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Label Distribution");
        if (dataset.LabelDistribution.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var pair in dataset.LabelDistribution.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"- {pair.Key}: {pair.Value}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Source Type Distribution");
        if (dataset.SourceTypeDistribution.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var pair in dataset.SourceTypeDistribution.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"- {pair.Key}: {pair.Value}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Recent Feature Examples");
        if (dataset.FeatureExamples.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var example in dataset.FeatureExamples.Take(20))
            {
                builder.AppendLine($"- {example.ExampleId} [{example.TaskKind}/{example.Label}] {example.SourceType}/{example.SourceId}");
                builder.AppendLine($"  candidate : {example.CandidateId} kind={example.CandidateKind} layer={example.CandidateLayer} status={example.CandidateStatus}");
                builder.AppendLine($"  accepted  : {example.Accepted} rejected={example.Rejected} selected={example.Selected}");
                builder.AppendLine($"  evidence  : {string.Join(", ", example.EvidenceRefs)}");
            }
        }

        return builder.ToString();
    }

    public static string RenderPlanningSnapshot(ServicePlanningSnapshot snapshot)
    {
        var planning = snapshot.Snapshot;
        var builder = new StringBuilder();
        builder.AppendLine("Service Planning Snapshot");
        builder.AppendLine("=========================");
        builder.AppendLine($"时间      : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务      : {snapshot.BaseUrl}");
        builder.AppendLine($"Workspace : {planning.WorkspaceId}");
        builder.AppendLine($"Collection: {planning.CollectionId ?? "-"}");
        builder.AppendLine($"Session   : {planning.SessionId ?? "-"}");
        builder.AppendLine($"Policy    : {planning.PolicyVersion}");
        builder.AppendLine($"CreatedAt : {planning.CreatedAt:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine();
        builder.AppendLine($"Counts    : tasks={planning.ActiveTasks.Count} decisions={planning.RecentDecisions.Count} questions={planning.OpenQuestions.Count} issues={planning.KnownIssues.Count} constraints={planning.StableConstraints.Count} preferences={planning.StablePreferences.Count} decisionRecords={planning.DecisionRecords.Count}");

        builder.AppendLine();
        AppendWorkingItems(builder, "Active Tasks", planning.ActiveTasks);
        AppendWorkingItems(builder, "Recent Decisions", planning.RecentDecisions);
        AppendWorkingItems(builder, "Open Questions", planning.OpenQuestions);
        AppendWorkingItems(builder, "Known Issues", planning.KnownIssues);
        AppendConstraints(builder, "Stable Constraints", planning.StableConstraints);
        AppendMemoryItems(builder, "Stable Preferences", planning.StablePreferences);
        AppendMemoryItems(builder, "Decision Records", planning.DecisionRecords);

        builder.AppendLine();
        builder.AppendLine("Learning Signals Summary");
        builder.AppendLine("------------------------");
        builder.AppendLine($"records={planning.LearningSignalsSummary.RecordCount} cases={planning.LearningSignalsSummary.CaseCount} positive={planning.LearningSignalsSummary.PositiveCount} negative={planning.LearningSignalsSummary.NegativeCount} stale={planning.LearningSignalsSummary.StaleCount}");
        builder.AppendLine($"caseStatus draft={planning.LearningSignalsSummary.DraftCaseCount} candidate={planning.LearningSignalsSummary.CandidateCaseCount} activeRegression={planning.LearningSignalsSummary.ActiveRegressionCaseCount} archived={planning.LearningSignalsSummary.ArchivedCaseCount} rejected={planning.LearningSignalsSummary.RejectedCaseCount}");
        if (planning.LearningSignalsSummary.FailureTypeCounts.Count > 0)
        {
            builder.AppendLine("failureTypes");
            foreach (var pair in planning.LearningSignalsSummary.FailureTypeCounts.OrderBy(pair => pair.Key.ToString(), StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"- {pair.Key}: {pair.Value}");
            }
        }

        return builder.ToString();
    }

    public static string RenderPlanningProposal(ServicePlanningProposalSnapshot snapshot)
    {
        var proposal = snapshot.Proposal;
        var builder = new StringBuilder();
        builder.AppendLine("Service Planning Proposal");
        builder.AppendLine("=========================");
        builder.AppendLine($"时间       : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务       : {snapshot.BaseUrl}");
        builder.AppendLine($"Input      : {Compact(snapshot.CurrentInput, 180)}");
        builder.AppendLine($"Operation  : {proposal.OperationId}");
        builder.AppendLine($"Workspace  : {proposal.WorkspaceId}");
        builder.AppendLine($"Collection : {proposal.CollectionId ?? "-"}");
        builder.AppendLine($"Intent     : {proposal.Intent}");
        builder.AppendLine($"Mode       : {proposal.Mode}");
        builder.AppendLine($"Confidence : {proposal.Confidence:0.00}");
        builder.AppendLine($"AuditMode  : {proposal.AuditMode}");
        builder.AppendLine($"Conflict   : {proposal.ConflictMode}");
        builder.AppendLine();
        builder.AppendLine("Channels");
        builder.AppendLine("--------");
        builder.AppendLine($"Exact={proposal.UseExact} Keyword={proposal.UseKeyword} ShortTerm={proposal.UseShortTermMemory} Working={proposal.UseWorkingMemory} Stable={proposal.UseStableMemory} Relations={proposal.UseRelations} Vector={proposal.UseVector}");
        builder.AppendLine();
        builder.AppendLine("TopK");
        builder.AppendLine("----");
        builder.AppendLine($"Keyword={proposal.KeywordTopK} Memory={proposal.MemoryTopK} Relation={proposal.RelationTopK} Vector={proposal.VectorTopK} Final={proposal.FinalTopK}");
        AppendStringList(builder, "Reasons", proposal.Reasons);
        AppendStringList(builder, "Warnings", proposal.Warnings);

        return builder.ToString();
    }

    public static string RenderRankerShadowDebug(ServiceRankerShadowDebugSnapshot snapshot)
    {
        var response = snapshot.Response;
        var builder = new StringBuilder();
        builder.AppendLine("Service Ranker Shadow Debug");
        builder.AppendLine("===========================");
        builder.AppendLine($"时间       : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务       : {snapshot.BaseUrl}");
        builder.AppendLine($"Query      : {Compact(response.Query, 180)}");
        builder.AppendLine($"Operation  : {response.OperationId}");
        builder.AppendLine($"Retrieval  : {response.RetrievalOperationId}");
        builder.AppendLine($"Workspace  : {response.WorkspaceId}");
        builder.AppendLine($"Collection : {response.CollectionId}");
        builder.AppendLine($"Mode       : {response.Mode}");
        builder.AppendLine($"Profile    : {response.RankerShadowProfile}");
        builder.AppendLine($"DebugOnly  : {response.Metadata.GetValueOrDefault("debugOnly", "true")}");
        builder.AppendLine($"FormalChanged : {response.FormalOutputChanged}");
        builder.AppendLine($"SelectedChanged: {response.SelectedSetChanged}");
        builder.AppendLine($"Selected   : {string.Join(", ", response.LegacySelectedIds.Take(12))}");
        builder.AppendLine();
        builder.AppendLine("Candidate Score Comparison");
        builder.AppendLine("--------------------------");
        if (response.CandidateScores.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var item in response.CandidateScores
                .OrderBy(static item => item.LegacyRank)
                .ThenBy(static item => item.CandidateId, StringComparer.OrdinalIgnoreCase)
                .Take(30))
            {
                builder.AppendLine(
                    $"- {item.CandidateId} selected={item.Selected} rank={item.LegacyRank}->{item.ShadowRank} legacy={item.LegacyScore:0.00} lifecycle={item.LifecycleAwareScore:0.00} delta={item.ScoreDelta:+0.00;-0.00;0.00}");
                builder.AppendLine($"  kind={item.Kind}/{item.Type} section={item.SectionName} reason={item.Reason}");
                var features = item.LifecycleFeatures;
                if (features.IsDeprecated || features.IsSuperseded || features.IsHistorical || features.IsRejected || features.IsCurrentVersion)
                {
                    builder.AppendLine($"  lifecycle deprecated={features.IsDeprecated} superseded={features.IsSuperseded} historical={features.IsHistorical} rejected={features.IsRejected} current={features.IsCurrentVersion} confidence={features.LifecycleConfidence:0.00}");
                }
            }
        }

        AppendShadowScoreList(builder, "Deprecated / Historical Demotions", response.DeprecatedDemotions.Concat(response.HistoricalDemotions).DistinctBy(static item => item.CandidateId).ToArray());
        AppendShadowScoreList(builder, "Current / Active Promotions", response.CurrentActivePromotions);
        AppendShadowScoreList(builder, "Version Conflict Fixes", response.VersionConflictFixes);
        AppendShadowScoreList(builder, "Must-hit Demotions", response.MustHitDemotions);
        AppendShadowScoreList(builder, "Must-not-hit Promotions", response.MustNotHitPromotions);
        AppendRankerShadowTraceQualitySummary(builder, snapshot.TraceQualitySummary);
        AppendRecentRankerShadowTraces(builder, snapshot.RecentShadowTraces);

        return builder.ToString();
    }

    public static string RenderError(ContextCoreApiException exception)
    {
        return ServiceOperationRenderer.RenderError(exception);
    }

    private static void AppendShadowScoreList(
        StringBuilder builder,
        string title,
        IReadOnlyList<LifecycleAwareRankerShadowCandidateScore> items)
    {
        builder.AppendLine();
        builder.AppendLine(title);
        builder.AppendLine(new string('-', title.Length));
        if (items.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return;
        }

        foreach (var item in items.Take(12))
        {
            builder.AppendLine($"- {item.CandidateId} delta={item.ScoreDelta:+0.00;-0.00;0.00} rank={item.LegacyRank}->{item.ShadowRank} reason={item.Reason}");
        }
    }

    private static void AppendRankerShadowTraceQualitySummary(
        StringBuilder builder,
        RankerShadowTraceQualityReport report)
    {
        builder.AppendLine();
        builder.AppendLine("Trace Quality Summary");
        builder.AppendLine("---------------------");
        builder.AppendLine($"- traces={report.TraceCount} candidates={report.CandidateScoreCount} deprecated={report.DeprecatedDemotionCount} historical={report.HistoricalDemotionCount} versionFixes={report.VersionConflictFixCount}");
        builder.AppendLine($"- currentPromotions={report.CurrentVersionPromotionCount} avgDelta={report.AverageScoreDelta:0.00} maxPositive={report.MaxPositiveDelta:0.00} maxNegative={report.MaxNegativeDelta:0.00}");
        builder.AppendLine($"- risks mustHitDemoted={report.MustHitDemotedCount} mustNotHitPromoted={report.MustNotHitPromotedCount}");
        builder.AppendLine($"- next={report.RecommendedNextStep}");
    }

    private static void AppendRecentRankerShadowTraces(
        StringBuilder builder,
        IReadOnlyList<LifecycleAwareRankerShadowTraceRecord> traces)
    {
        builder.AppendLine();
        builder.AppendLine("Recent Shadow Traces");
        builder.AppendLine("--------------------");
        if (traces.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return;
        }

        foreach (var trace in traces.Take(5))
        {
            builder.AppendLine($"- {trace.RetrievalId} {trace.CreatedAt:yyyy-MM-dd HH:mm:ss} profile={trace.Profile} candidates={trace.CandidateScores.Count} demotions={trace.DeprecatedDemotions.Count}");
            builder.AppendLine($"  query: {Compact(trace.Query, 140)}");
        }
    }

    private static void AppendStringList(StringBuilder builder, string title, IReadOnlyList<string> values)
    {
        builder.AppendLine();
        builder.AppendLine(title);
        builder.AppendLine(new string('-', title.Length));
        if (values.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return;
        }

        foreach (var value in values.Take(20))
        {
            builder.AppendLine($"- {value}");
        }
    }

    private static void AppendWorkingItems(StringBuilder builder, string title, IReadOnlyList<ShortTermWorkingItem> items)
    {
        builder.AppendLine(title);
        builder.AppendLine(new string('-', title.Length));
        if (items.Count == 0)
        {
            builder.AppendLine("- (empty)");
            builder.AppendLine();
            return;
        }

        foreach (var item in items.Take(10))
        {
            builder.AppendLine($"- {item.ItemId} [{item.Kind}/{item.Status}/{item.Lifecycle}] importance={item.Importance:0.00}");
            builder.AppendLine($"  title   : {item.Title}");
            builder.AppendLine($"  summary : {Compact(item.Summary, 160)}");
            builder.AppendLine($"  refs    : {string.Join(", ", item.SourceRefs.Concat(item.Refs).Distinct(StringComparer.OrdinalIgnoreCase).Take(8))}");
        }

        builder.AppendLine();
    }

    private static void AppendConstraints(StringBuilder builder, string title, IReadOnlyList<ContextConstraint> items)
    {
        builder.AppendLine(title);
        builder.AppendLine(new string('-', title.Length));
        if (items.Count == 0)
        {
            builder.AppendLine("- (empty)");
            builder.AppendLine();
            return;
        }

        foreach (var item in items.Take(10))
        {
            builder.AppendLine($"- {item.Id} [{item.Level}/{item.Status}/{item.Scope}] confidence={item.Confidence:0.00}");
            builder.AppendLine($"  content : {Compact(item.Content, 160)}");
            builder.AppendLine($"  refs    : {string.Join(", ", item.SourceRefs.Take(8))}");
        }

        builder.AppendLine();
    }

    private static void AppendMemoryItems(StringBuilder builder, string title, IReadOnlyList<ContextMemoryItem> items)
    {
        builder.AppendLine(title);
        builder.AppendLine(new string('-', title.Length));
        if (items.Count == 0)
        {
            builder.AppendLine("- (empty)");
            builder.AppendLine();
            return;
        }

        foreach (var item in items.Take(10))
        {
            builder.AppendLine($"- {item.Id} [{item.Type}/{item.Status}] importance={item.Importance:0.00}");
            builder.AppendLine($"  content : {Compact(item.Content, 160)}");
            builder.AppendLine($"  refs    : {string.Join(", ", item.SourceRefs.Take(8))}");
        }

        builder.AppendLine();
    }

    private static string Compact(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }

        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "...";
    }

    private static JobPayloadInfo TryParsePayload(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return new JobPayloadInfo();
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? operationId = null;

            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.NameEquals("OperationId") || property.NameEquals("operationId"))
                    {
                        operationId = property.Value.GetString();
                    }

                    if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                    {
                        metadata[property.Name] = property.Value.ToString();
                    }
                }
            }

            return new JobPayloadInfo
            {
                OperationId = operationId,
                Metadata = metadata
            };
        }
        catch
        {
            return new JobPayloadInfo();
        }
    }

    private sealed class JobPayloadInfo
    {
        public string? OperationId { get; init; }

        public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
    }

    private static void AppendWorkingSection(
        StringBuilder builder,
        string title,
        IReadOnlyList<ShortTermWorkingItem> items)
    {
        builder.AppendLine(title);
        if (items.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return;
        }

        foreach (var item in items)
        {
            builder.AppendLine($"- {item.ItemId} [{item.Kind}/{item.Status}] {item.Summary}");
        }
    }

    private static void AppendMaintenanceSection(
        StringBuilder builder,
        ShortTermMaintenanceStatusResponse? maintenance)
    {
        builder.AppendLine("Maintenance");
        if (maintenance is null)
        {
            builder.AppendLine("- (unavailable)");
            return;
        }

        builder.AppendLine($"- Enabled       : {maintenance.Enabled}");
        builder.AppendLine($"- Running       : {maintenance.IsRunning}");
        builder.AppendLine($"- RunOnStartup  : {maintenance.RunOnStartup}");
        builder.AppendLine($"- IntervalSec   : {maintenance.IntervalSeconds}");
        builder.AppendLine($"- LastError     : {maintenance.LastError ?? "none"}");
        builder.AppendLine($"- LastRun       : {maintenance.LastRun?.RunId ?? "none"}");
    }

    private static string ReadMetadata(ContextConstraint item, string key)
    {
        return item.Metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : "-";
    }
}




