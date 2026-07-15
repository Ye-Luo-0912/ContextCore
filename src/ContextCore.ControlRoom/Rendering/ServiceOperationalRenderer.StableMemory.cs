using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client;
using ContextCore.Core.Services;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Rendering;

public static partial class ServiceOperationalRenderer
{
    public static string RenderStableMemory(ServiceStableMemorySnapshot snapshot)
    {
        var view = snapshot.Snapshot;
        var diagnostics = snapshot.Diagnostics;
        var builder = new StringBuilder();
        AppendHeader(builder, "Service Stable Memory");
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
            AppendStringSection(builder, "Warnings", view.Warnings);
        }

        return builder.ToString();
    }

    public static string RenderStableReplacementChain(StableReplacementChainResponse chain)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Stable Replacement Chain");
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
            AppendStringSection(builder, "Warnings", chain.Warnings);
        }

        return builder.ToString();
    }

    public static string RenderStableMemoryDetail(StableMemoryRecord item)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Service Stable Memory Detail");
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
        AppendHeader(builder, "Service Stable Memory Explain");
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
            AppendStringSection(builder, "Warnings", explanation.Warnings);
        }

        return builder.ToString();
    }

    public static string RenderStableLifecycleReviewResult(StableLifecycleReviewResult result)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Stable Lifecycle Review Result");
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
        AppendStringSection(builder, "Warnings", result.Warnings);

        AppendStringSection(builder, "Errors", result.Errors);

        return builder.ToString();
    }

    public static string RenderStableLifecycleReviews(IReadOnlyList<StableLifecycleReviewRecord> reviews)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Stable Lifecycle Review History");
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
        AppendHeader(builder, "Service Global Context Detail");
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
}
