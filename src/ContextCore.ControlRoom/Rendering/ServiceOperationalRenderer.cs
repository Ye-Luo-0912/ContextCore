using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client;
using ContextCore.Core.Services;
using ContextCore.ControlRoom.Services;
using ContextCore.ControlRoom.Models;

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
        builder.AppendLine();
        builder.AppendLine("File Layout Status");
        builder.AppendLine($"DataRoot      : {snapshot.FileLayoutStatus.DataRoot}");
        builder.AppendLine($"Categories    : {snapshot.FileLayoutStatus.ArtifactCategories.Count}");
        builder.AppendLine($"ManifestCount : {snapshot.FileLayoutStatus.ManifestCount}");
        builder.AppendLine($"ReportCount   : {snapshot.FileLayoutStatus.ReportCount}");
        foreach (var sample in snapshot.FileLayoutStatus.ResolvedPathSamples.Take(4))
        {
            builder.AppendLine($"- {sample.Descriptor.Kind}/{sample.Descriptor.CapabilityId}: {sample.RelativePath}");
        }

        if (snapshot.FileLayoutStatus.Diagnostics.Count > 0)
        {
            builder.AppendLine($"Diagnostics   : {string.Join(", ", snapshot.FileLayoutStatus.Diagnostics)}");
        }

        builder.AppendLine();
        builder.AppendLine("Memory Layout Status");
        AppendMemoryLayoutStatus(builder, snapshot.MemoryLayoutDiagnostics);

        builder.AppendLine();
        builder.AppendLine("Trace Layout Status");
        AppendTraceLayoutStatus(builder, snapshot.TraceLayoutDiagnostics);

        builder.AppendLine();
        builder.AppendLine("Report Layout Status");
        AppendReportLayoutStatus(builder, snapshot.ReportLayoutDiagnostics);

        builder.AppendLine();
        builder.AppendLine("Storage Boundary Status");
        AppendStorageBoundaryStatus(builder, snapshot.StorageBoundaryReport);

        builder.AppendLine();
        builder.AppendLine("Postgres Operational Store Status");
        AppendPostgresOperationalStoreStatus(builder, snapshot.PostgresOperationalStoreDiagnostics);

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
        builder.AppendLine();
        builder.AppendLine("Memory Layout Status");
        AppendMemoryLayoutStatus(builder, snapshot.MemoryLayoutDiagnostics);
        return builder.ToString();
    }

    private static void AppendMemoryLayoutStatus(StringBuilder builder, MemoryLayoutDiagnostics diagnostics)
    {
        builder.AppendLine($"DataRoot      : {diagnostics.DataRoot}");
        builder.AppendLine($"ShortTerm     : {diagnostics.ShortTermArtifactCount}");
        builder.AppendLine($"Candidate     : {diagnostics.CandidateArtifactCount}");
        builder.AppendLine($"Stable        : {diagnostics.StableArtifactCount}");
        builder.AppendLine($"TemporalReady : {diagnostics.TemporalPlaceholderReady}");
        builder.AppendLine($"LegacyFallback: {diagnostics.LegacyFallbackCount}");
        builder.AppendLine($"MissingDirs   : {diagnostics.MissingDirectoryCount}");
        foreach (var path in diagnostics.MemoryLayerPaths.Take(6))
        {
            builder.AppendLine($"- {path.Key}: {path.Value}");
        }

        if (diagnostics.Diagnostics.Count > 0)
        {
            builder.AppendLine($"Diagnostics   : {string.Join(", ", diagnostics.Diagnostics)}");
        }
    }

    private static void AppendTraceLayoutStatus(StringBuilder builder, TraceLayoutDiagnostics diagnostics)
    {
        builder.AppendLine($"TraceRoot     : {diagnostics.TraceRoot}");
        builder.AppendLine($"Retrieval     : {diagnostics.RetrievalTraceCount}");
        builder.AppendLine($"ToolCallReady : {diagnostics.ToolCallPlaceholderReady}");
        builder.AppendLine($"LegacyFallback: {diagnostics.LegacyFallbackCount}");
        foreach (var path in diagnostics.TraceCategoryPaths.Take(6))
        {
            builder.AppendLine($"- {path.Key}: {path.Value}");
        }

        if (diagnostics.Diagnostics.Count > 0)
        {
            builder.AppendLine($"Diagnostics   : {string.Join(", ", diagnostics.Diagnostics)}");
        }
    }

    private static void AppendReportLayoutStatus(StringBuilder builder, ReportLayoutDiagnostics diagnostics)
    {
        builder.AppendLine($"DataRoot       : {diagnostics.DataRoot}");
        builder.AppendLine($"ManifestCount  : {diagnostics.ManifestCount}");
        builder.AppendLine($"LatestReports  : {diagnostics.LatestReportCount}");
        builder.AppendLine($"LegacyMirrored : {diagnostics.LegacyMirroredCount}");
        builder.AppendLine($"MissingStandard: {diagnostics.MissingStandardArtifactCount}");
        builder.AppendLine($"MissingLegacy  : {diagnostics.MissingLegacyArtifactCount}");
        builder.AppendLine($"DuplicateHash  : {diagnostics.DuplicateContentHashCount}");
        foreach (var count in diagnostics.ReportCountByKind.OrderBy(item => item.Key).Take(8))
        {
            builder.AppendLine($"- {count.Key}: {count.Value}");
        }

        foreach (var sample in diagnostics.ResolvedPathSamples.Take(4))
        {
            builder.AppendLine($"sample {sample.ArtifactKind}/{sample.CapabilityId}: {sample.RelativePath}");
        }

        if (diagnostics.LargestReports.Count > 0)
        {
            builder.AppendLine("LargestReports");
            foreach (var report in diagnostics.LargestReports.Take(3))
            {
                builder.AppendLine($"- {report.RelativePath} ({report.SizeBytes} bytes)");
            }
        }

        if (diagnostics.Diagnostics.Count > 0)
        {
            builder.AppendLine($"Diagnostics    : {string.Join(", ", diagnostics.Diagnostics)}");
        }
    }

    private static void AppendStorageBoundaryStatus(StringBuilder builder, StorageBoundaryReport report)
    {
        builder.AppendLine($"ArtifactKinds : {report.TotalArtifactKinds}");
        builder.AppendLine($"ArtifactOnly  : {report.ArtifactOnlyCount}");
        builder.AppendLine($"Operational   : {report.OperationalStateCount}");
        builder.AppendLine($"IndexState    : {report.IndexStateCount}");
        builder.AppendLine($"DbRecommended : {report.DatabaseRecommendedCount}");
        builder.AppendLine($"FsPreferred   : {report.FileSystemPreferredCount}");
        builder.AppendLine($"Migrations    : {report.MigrationCandidates.Count}");
        builder.AppendLine($"HighPriority  : {report.HighPriorityMigrationCandidates.Count}");
        foreach (var candidate in report.HighPriorityMigrationCandidates.Take(6))
        {
            builder.AppendLine(
                $"- {candidate.SubjectId}: {candidate.Responsibility}/{candidate.PreferredProvider}, risk={candidate.MigrationRisk}");
        }

        if (report.RecommendedNextPhases.Count > 0)
        {
            builder.AppendLine("NextPhases");
            foreach (var phase in report.RecommendedNextPhases.Take(4))
            {
                builder.AppendLine($"- {phase}");
            }
        }

        if (report.Diagnostics.Count > 0)
        {
            builder.AppendLine($"Diagnostics   : {string.Join(", ", report.Diagnostics)}");
        }
    }

    private static void AppendPostgresOperationalStoreStatus(
        StringBuilder builder,
        PostgresOperationalStoreDiagnostics diagnostics)
    {
        builder.AppendLine($"Enabled       : {diagnostics.ProviderEnabled}");
        builder.AppendLine($"ProviderId    : {diagnostics.ProviderId}");
        builder.AppendLine($"Status        : {diagnostics.Status}");
        builder.AppendLine($"Connection    : {diagnostics.ConnectionAvailable}");
        builder.AppendLine($"SchemaVersion : {diagnostics.CurrentSchemaVersion ?? "未应用"}");
        builder.AppendLine($"Pending       : {diagnostics.PendingMigrations}");
        builder.AppendLine($"TableCount    : {diagnostics.TableCount}");
        builder.AppendLine($"MissingTables : {diagnostics.RequiredTableMissingCount}");
        builder.AppendLine($"Capability    : {diagnostics.ProviderCapabilityStatus}");
        if (diagnostics.SchemaVerification is not null)
        {
            var verification = diagnostics.SchemaVerification;
            builder.AppendLine("Schema Verification");
            builder.AppendLine($"- SchemaName     : {(string.IsNullOrWhiteSpace(verification.SchemaName) ? "default" : verification.SchemaName)}");
            builder.AppendLine($"- SchemaVersion  : {verification.CurrentSchemaVersion ?? "未应用"}");
            builder.AppendLine($"- RequiredTables : {verification.RequiredTableCount}");
            builder.AppendLine($"- MissingTables  : {verification.MissingRequiredTableCount}");
            builder.AppendLine($"- RequiredIndexes: {verification.RequiredIndexCount}");
            builder.AppendLine($"- MissingIndexes : {verification.MissingIndexCount}");
            builder.AppendLine($"- Recommendation : {verification.Recommendation}");
        }

        if (diagnostics.MissingRequiredTables.Count > 0)
        {
            builder.AppendLine("MissingRequiredTables");
            foreach (var table in diagnostics.MissingRequiredTables.Take(6))
            {
                builder.AppendLine($"- {table}");
            }
        }

        if (diagnostics.Diagnostics.Count > 0)
        {
            builder.AppendLine($"Diagnostics   : {string.Join(", ", diagnostics.Diagnostics)}");
        }
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

    private static string FormatTargetSections(IReadOnlyDictionary<string, int> sections)
    {
        return sections.Count == 0
            ? "-"
            : string.Join(", ", sections
                .OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static item => $"{item.Key}={item.Value}"));
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

    public static string RenderRelationExpansionProfiles(IReadOnlyList<RelationExpansionProfile> profiles)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Relation Expansion Profiles");
        builder.AppendLine("===================================");
        builder.AppendLine($"Count: {profiles.Count}");
        foreach (var profile in profiles.OrderBy(item => item.ProfileId, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"- {profile.ProfileId} mode={profile.Mode} intent={profile.Intent} depth={profile.MaxDepth} fanout={profile.MaxFanout} minConfidence={profile.MinConfidence:0.00}");
            builder.AppendLine($"  allowed={string.Join(", ", profile.AllowedRelationTypes.DefaultIfEmpty("-"))}");
            builder.AppendLine($"  blocked={string.Join(", ", profile.BlockedRelationTypes.DefaultIfEmpty("-"))}");
            builder.AppendLine($"  lifecycle={BlankDash(profile.LifecyclePolicy)} candidate={profile.AllowCandidateRelations} deprecated={profile.AllowDeprecatedRelations} rejected={profile.AllowRejectedRelations} requireEvidence={profile.RequireEvidence}");
        }

        return builder.ToString();
    }

    public static string RenderRelationExpansionPreview(RelationExpansionPreviewResponse preview)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Relation Expansion Preview");
        builder.AppendLine("==================================");
        builder.AppendLine($"Operation : {preview.OperationId}");
        builder.AppendLine($"ItemId    : {preview.ItemId}");
        builder.AppendLine($"Profile   : {preview.Profile.ProfileId} ({preview.Profile.Mode}/{preview.Profile.Intent})");
        builder.AppendLine($"Accepted  : {preview.AcceptedCount}");
        builder.AppendLine($"Blocked   : {preview.BlockedCount}");
        builder.AppendLine();
        builder.AppendLine("Accepted Relations");
        foreach (var relation in preview.AcceptedRelations.Take(20))
        {
            builder.AppendLine($"- {relation.RelationId} depth={relation.Depth} {relation.SourceId} --{relation.RelationType}--> {relation.TargetId} confidence={relation.Confidence:0.00} weight={relation.Weight:0.00}");
            builder.AppendLine($"  section={BlankDash(relation.TargetSection)} reason={BlankDash(relation.SectionReason)} riskNormal={relation.RiskIfNormalSelected} riskAfterRouting={relation.RiskAfterSectionRouting}");
        }

        if (preview.AcceptedRelations.Count == 0)
        {
            builder.AppendLine("- none");
        }

        builder.AppendLine();
        builder.AppendLine("Blocked Relations");
        foreach (var relation in preview.BlockedRelations.Take(30))
        {
            builder.AppendLine($"- {relation.RelationId} depth={relation.Depth} {relation.SourceId} --{relation.RelationType}--> {relation.TargetId} reasons={string.Join(",", relation.Reasons.DefaultIfEmpty("-"))}");
            builder.AppendLine($"  section={BlankDash(relation.TargetSection)} reason={BlankDash(relation.SectionReason)} riskNormal={relation.RiskIfNormalSelected} riskAfterRouting={relation.RiskAfterSectionRouting}");
        }

        if (preview.BlockedRelations.Count == 0)
        {
            builder.AppendLine("- none");
        }

        if (preview.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Warnings");
            foreach (var warning in preview.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        return builder.ToString();
    }

    public static string RenderRelationReviewResult(RelationReviewResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Relation Review Result");
        builder.AppendLine("==============================");
        builder.AppendLine($"Operation  : {result.OperationId}");
        builder.AppendLine($"RelationId : {result.RelationId}");
        builder.AppendLine($"Action     : {result.Action}");
        builder.AppendLine($"Lifecycle  : {BlankDash(result.FromLifecycle)} -> {BlankDash(result.ToLifecycle)}");
        builder.AppendLine($"Review     : {BlankDash(result.FromReviewStatus)} -> {BlankDash(result.ToReviewStatus)}");
        builder.AppendLine($"Reviewer   : {BlankDash(result.Reviewer)}");
        builder.AppendLine($"Reason     : {BlankDash(result.Reason)}");
        builder.AppendLine($"ReviewedAt : {result.ReviewedAt:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"Relation   : {result.Relation.SourceId} --{result.Relation.RelationType}--> {result.Relation.TargetId}");
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

    public static string RenderRelationReviews(IReadOnlyList<RelationReviewRecord> reviews)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Service Relation Review History");
        builder.AppendLine("===============================");
        builder.AppendLine($"Count: {reviews.Count}");
        foreach (var review in reviews.Take(20))
        {
            builder.AppendLine($"- {review.ReviewId} [{review.Action}] {BlankDash(review.FromLifecycle)}->{BlankDash(review.ToLifecycle)} review={BlankDash(review.FromReviewStatus)}->{BlankDash(review.ToReviewStatus)}");
            builder.AppendLine($"  relation={review.RelationId} {review.SourceId} --{review.RelationType}--> {review.TargetId}");
            builder.AppendLine($"  reviewer={BlankDash(review.Reviewer)} reason={Compact(review.Reason, 160)} at={review.ReviewedAt:yyyy-MM-dd HH:mm:ss}");
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

    private static void AppendStatusLine(StringBuilder builder, string value)
    {
        AppendLabeledLine(builder, "status", value);
    }

    private static void AppendMetricLine(StringBuilder builder, string label, string value)
    {
        AppendLabeledLine(builder, label, value);
    }

    private static void AppendBooleanInvariantLine(StringBuilder builder, string label, bool value)
    {
        AppendLabeledLine(builder, label, value.ToString());
    }

    private static void AppendRecommendationLine(StringBuilder builder, string? value)
    {
        AppendLabeledLine(builder, "recommendation", BlankDash(value));
    }

    private static void AppendBlockedLine(StringBuilder builder, IReadOnlyList<string> blockedReasons, string label = "blocked")
    {
        AppendLabeledLine(
            builder,
            label,
            blockedReasons.Count == 0 ? "-" : string.Join(", ", blockedReasons));
    }

    private static void AppendMissingSummaryState(StringBuilder builder, string status, string action)
    {
        AppendStatusLine(builder, status);
        AppendMetricLine(builder, "action", action);
    }

    private static bool TryBeginReportSection(StringBuilder builder, ControlRoomReportDescriptor descriptor, string? sourcePath)
    {
        builder.AppendLine();
        builder.AppendLine(descriptor.DisplayTitle);
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            AppendMissingSummaryState(builder, descriptor.DefaultMissingStatus(), descriptor.DefaultEvalCommand());
            return false;
        }
        return true;
    }

    private static bool TryBeginReportSection(StringBuilder builder, ControlRoomReportDescriptor descriptor, string? sourcePath1, string? sourcePath2)
    {
        builder.AppendLine();
        builder.AppendLine(descriptor.DisplayTitle);
        if (string.IsNullOrWhiteSpace(sourcePath1) && string.IsNullOrWhiteSpace(sourcePath2))
        {
            AppendMissingSummaryState(builder, descriptor.DefaultMissingStatus(), descriptor.DefaultEvalCommand());
            return false;
        }
        return true;
    }

    private static void AppendLabeledLine(StringBuilder builder, string label, string value)
    {
        builder.AppendLine($"- {label,-14}: {value}");
    }

    private static string BlankDash(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value;
    }

    private static string FormatList(IReadOnlyList<string> values)
    {
        return values.Count == 0 ? "-" : string.Join(", ", values);
    }

    private static string FormatMap(IReadOnlyDictionary<string, string> values, int maxItems = 6)
    {
        if (values.Count == 0)
        {
            return "-";
        }

        return string.Join("; ", values
            .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maxItems))
            .Select(static pair => $"{pair.Key}={pair.Value}"));
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

    public static string RenderVectorIndex(ServiceVectorIndexSnapshot snapshot)
    {
        var status = snapshot.Status;
        var diagnostics = snapshot.Diagnostics;
        var preview = snapshot.ReindexPreview;
        var builder = new StringBuilder();
        builder.AppendLine("Service Vector Index");
        builder.AppendLine("====================");
        builder.AppendLine($"时间       : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务       : {snapshot.BaseUrl}");
        builder.AppendLine($"Workspace  : {status.WorkspaceId}");
        builder.AppendLine($"Collection : {status.CollectionId}");
        builder.AppendLine($"Provider   : {(string.IsNullOrWhiteSpace(status.Provider) ? "-" : status.Provider)}");
        builder.AppendLine($"Model      : {(string.IsNullOrWhiteSpace(status.Model) ? "-" : status.Model)}");
        builder.AppendLine($"Dimension  : {status.Dimension}");
        builder.AppendLine($"Available  : store={(status.StoreAvailable ? "yes" : "no")} generator={(status.GeneratorAvailable ? "yes" : "no")}");
        builder.AppendLine($"Counts     : indexed={status.IndexedCount} stale={status.StaleCount} missing={status.MissingCount} duplicate={status.DuplicateCount} orphan={status.OrphanCount}");
        builder.AppendLine();
        builder.AppendLine("Coverage Summary");
        builder.AppendLine($"- source items : {snapshot.Coverage.TotalSourceItems}");
        builder.AppendLine($"- indexed      : {snapshot.Coverage.IndexedItems}");
        builder.AppendLine($"- coverage     : {snapshot.Coverage.CoverageRate:P2}");
        builder.AppendLine($"- missing      : {snapshot.Coverage.MissingByLayer.Values.Sum()}");
        builder.AppendLine($"- stale        : {snapshot.Coverage.StaleByLayer.Values.Sum()}");
        builder.AppendLine($"- duplicate    : {snapshot.Coverage.DuplicateCount}");
        builder.AppendLine($"- orphan       : {snapshot.Coverage.OrphanCount}");
        builder.AppendLine($"- recommendation: {snapshot.Coverage.Recommendation}");
        builder.AppendLine();
        builder.AppendLine("Shadow Quality Summary");
        if (!snapshot.ShadowQuality.Available)
        {
            builder.AppendLine($"- status        : {snapshot.ShadowQuality.CurrentRecommendation}");
            builder.AppendLine("- action        : run eval vector-query-profile-sweep");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.SourcePath}");
            builder.AppendLine($"- recommendation: {snapshot.ShadowQuality.CurrentRecommendation}");
            builder.AppendLine($"- best profile  : {snapshot.ShadowQuality.BestProfile}");
            builder.AppendLine($"- best topK     : {snapshot.ShadowQuality.BestTopK}");
            builder.AppendLine($"- best minSim   : {snapshot.ShadowQuality.BestMinSimilarity:F2}");
            builder.AppendLine($"- riskAfter     : {snapshot.ShadowQuality.RiskAfterPolicy}");
            builder.AppendLine($"- separation    : {snapshot.ShadowQuality.SimilaritySeparation:F4}");
        }

        builder.AppendLine();
        builder.AppendLine("Residual Risk Summary");
        if (snapshot.ShadowQuality.ResidualRiskCount == 0)
        {
            builder.AppendLine("- residualRisk  : 0");
        }
        else
        {
            builder.AppendLine($"- source        : {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.ResidualRiskSourcePath) ? "-" : snapshot.ShadowQuality.ResidualRiskSourcePath)}");
            builder.AppendLine($"- residualRisk  : {snapshot.ShadowQuality.ResidualRiskCount}");
            foreach (var pair in snapshot.ShadowQuality.TopResidualRiskTypes
                         .OrderByDescending(pair => pair.Value)
                         .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                         .Take(5))
            {
                builder.AppendLine($"- riskType      : {pair.Key} = {pair.Value}");
            }

            foreach (var reason in snapshot.ShadowQuality.TopWhyPolicyAllowed.Take(3))
            {
                builder.AppendLine($"- whyAllowed    : {reason}");
            }

            foreach (var action in snapshot.ShadowQuality.TopExpectedActions.Take(3))
            {
                builder.AppendLine($"- expectedAction: {action}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Lifecycle Metadata Coverage");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.LifecycleMetadataCoverageSourcePath))
        {
            builder.AppendLine("- status        : NoCoverageReport");
            builder.AppendLine("- action        : run eval vector-lifecycle-metadata-coverage");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.LifecycleMetadataCoverageSourcePath}");
            builder.AppendLine($"- coverage      : {snapshot.ShadowQuality.LifecycleMetadataCoverageRate:P2}");
            builder.AppendLine($"- unknown       : {snapshot.ShadowQuality.UnknownLifecycleCount}");
            builder.AppendLine($"- missingReview : {snapshot.ShadowQuality.MissingReviewStatusCount}");
            builder.AppendLine($"- missingReplace: {snapshot.ShadowQuality.MissingReplacementInfoCount}");
            builder.AppendLine($"- blockedByGate : {snapshot.ShadowQuality.BlockedByLifecycleMetadataGate}");
        }

        builder.AppendLine();
        builder.AppendLine("Provider Comparison Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.ProviderComparisonSourcePath))
        {
            builder.AppendLine("- status        : NoProviderComparisonReport");
            builder.AppendLine("- action        : run eval vector-provider-comparison --providers current,qwen3");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.ProviderComparisonSourcePath}");
            foreach (var provider in snapshot.ShadowQuality.ProviderComparisonResults.Take(4))
            {
                builder.AppendLine(
                    $"- provider      : {provider.ProviderId} dim={provider.Dimension} indexed={provider.IndexedEntryCount} a3={provider.A3RecallAfterPolicy:P2}/{provider.A3MrrAfterPolicy:F4} extended={provider.ExtendedRecallAfterPolicy:P2}/{provider.ExtendedMrrAfterPolicy:F4} risk={provider.A3RiskAfterPolicy + provider.ExtendedRiskAfterPolicy} pgParity={provider.PgVectorParityPassed} rec={provider.Recommendation}");
            }

            builder.AppendLine($"- qwen3 gate    : {snapshot.ShadowQuality.Qwen3ReadinessGatePassed}");
            builder.AppendLine($"- qwen3 rec     : {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.Qwen3Recommendation) ? "-" : snapshot.ShadowQuality.Qwen3Recommendation)}");
            foreach (var reason in snapshot.ShadowQuality.Qwen3BlockedReasons.Take(5))
            {
                builder.AppendLine($"- qwen3 blocked : {reason}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Provider Promotion Status");
        builder.AppendLine($"- current       : KeepCurrentPreviewProvider");
        builder.AppendLine($"- qwen3         : {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.ProviderPromotionStatus) ? "NoFreezeReport" : snapshot.ShadowQuality.ProviderPromotionStatus)}");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.ProviderComparisonFreezeSourcePath))
        {
            builder.AppendLine("- freeze action : run eval vector-provider-comparison-freeze");
        }
        else
        {
            builder.AppendLine($"- freeze source : {snapshot.ShadowQuality.ProviderComparisonFreezeSourcePath}");
            builder.AppendLine($"- comparison    : {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.ProviderComparisonStatus) ? "-" : snapshot.ShadowQuality.ProviderComparisonStatus)}");
            builder.AppendLine($"- sanity passed : {snapshot.ShadowQuality.ProviderConfigurationSanityPassed}");
            builder.AppendLine($"- v4 recheck    : {snapshot.ShadowQuality.VectorV4RecheckAllowed}");
            foreach (var reason in snapshot.ShadowQuality.ProviderPromotionBlockedReasons.Take(5))
            {
                builder.AppendLine($"- promotion blocked : {reason}");
            }
        }

        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.V4ReadinessGateSourcePath))
        {
            builder.AppendLine("- gate          : NoReadinessGateReport");
            builder.AppendLine("- gate action   : run eval vector-retrieval-shadow-readiness-gate");
        }
        else
        {
            builder.AppendLine($"- gate source   : {snapshot.ShadowQuality.V4ReadinessGateSourcePath}");
            builder.AppendLine($"- gate passed   : {snapshot.ShadowQuality.V4ReadinessGatePassed}");
            foreach (var reason in snapshot.ShadowQuality.V4ReadinessGateFailReasons.Take(6))
            {
                builder.AppendLine($"- gate fail     : {reason}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Hybrid Retrieval Preview Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.HybridPreviewSourcePath))
        {
            builder.AppendLine("- status        : NoHybridPreviewReport");
            builder.AppendLine("- action        : run eval vector-hybrid-preview");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.HybridPreviewSourcePath}");
            builder.AppendLine($"- a3 recall     : {snapshot.ShadowQuality.HybridFullA3Recall}");
            builder.AppendLine($"- ext recall    : {snapshot.ShadowQuality.HybridFullExtendedRecall}");
            builder.AppendLine($"- risk          : {snapshot.ShadowQuality.HybridFullRiskAfterPolicy}");
            builder.AppendLine($"- recommendation: {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.HybridReadinessRecommendation) ? "-" : snapshot.ShadowQuality.HybridReadinessRecommendation)}");
            builder.AppendLine($"- gate passed   : {snapshot.ShadowQuality.HybridReadinessGatePassed}");
        }

        builder.AppendLine();
        builder.AppendLine("Hybrid Recall Regression Audit Summary");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.HybridAuditSourcePath))
        {
            builder.AppendLine("- status        : NoAuditReport");
            builder.AppendLine("- action        : run eval vector-hybrid-recall-regression-audit");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.HybridAuditSourcePath}");
            builder.AppendLine($"- passed        : {snapshot.ShadowQuality.HybridAuditPassed}");
            builder.AppendLine($"- dense dropped : {snapshot.ShadowQuality.HybridAuditDenseDroppedCount}");
            builder.AppendLine($"- elig mismatch : {snapshot.ShadowQuality.HybridAuditEligibilityMismatchCount}");
            builder.AppendLine($"- dedup overwrite: {snapshot.ShadowQuality.HybridAuditDedupOverwriteCount}");
            builder.AppendLine($"- recommendation: {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.HybridAuditRecommendation) ? "-" : snapshot.ShadowQuality.HybridAuditRecommendation)}");
        }

        builder.AppendLine();
        builder.AppendLine("Hybrid Retrieval Freeze Status");
        if (string.IsNullOrWhiteSpace(snapshot.ShadowQuality.HybridFreezeSourcePath))
        {
            builder.AppendLine("- status        : NoFreezeReport");
            builder.AppendLine("- action        : run eval vector-hybrid-freeze-gate");
        }
        else
        {
            builder.AppendLine($"- source        : {snapshot.ShadowQuality.HybridFreezeSourcePath}");
            builder.AppendLine($"- freeze passed : {snapshot.ShadowQuality.HybridFreezePassed}");
            builder.AppendLine($"- status        : {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.HybridFreezeStatus) ? "-" : snapshot.ShadowQuality.HybridFreezeStatus)}");
            builder.AppendLine($"- recommendation: {(string.IsNullOrWhiteSpace(snapshot.ShadowQuality.HybridFreezeRecommendation) ? "-" : snapshot.ShadowQuality.HybridFreezeRecommendation)}");
            builder.AppendLine($"- v4 recheck    : {snapshot.ShadowQuality.HybridV4RecheckAllowed}");
            foreach (var reason in snapshot.ShadowQuality.HybridFreezeBlockedReasons.Take(5))
            {
                builder.AppendLine($"- freeze blocked: {reason}");
            }
        }

        if (snapshot.ShadowQuality.OperationalReports.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Historical Evaluation Reports");
            foreach (var report in snapshot.ShadowQuality.OperationalReports)
            {
                builder.AppendLine($"- {report.DisplayTitle}");
                builder.AppendLine($"  source        : {report.SourcePath}");
                builder.AppendLine($"  passed/gate   : {report.Passed} / {report.GatePassed}");
                if (report.KeyMetrics.Count > 0)
                {
                    builder.AppendLine($"  key metrics   : {string.Join(", ", report.KeyMetrics.Select(kv => $"{kv.Key}={kv.Value}"))}");
                }
                if (!string.IsNullOrEmpty(report.Recommendation))
                {
                    builder.AppendLine($"  recommendation: {report.Recommendation}");
                }
                if (report.BlockedReasons.Count > 0)
                {
                    builder.AppendLine($"  blocked       : {string.Join("; ", report.BlockedReasons)}");
                }
            }
        }

        if (status.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Warnings");
            foreach (var warning in status.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Diagnostics");
        builder.AppendLine($"- total          : {diagnostics.Diagnostics.Count}");
        builder.AppendLine($"- dimensionMismatch: {diagnostics.DimensionMismatchCount}");
        builder.AppendLine($"- unsupportedModel : {diagnostics.UnsupportedModelCount}");
        builder.AppendLine($"- providerUnavailable: {diagnostics.ProviderUnavailableCount}");
        if (diagnostics.CountsByType.Count > 0)
        {
            foreach (var pair in diagnostics.CountsByType.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"- {pair.Key}: {pair.Value}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Recent Diagnostics");
        if (diagnostics.Diagnostics.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var item in diagnostics.Diagnostics.Take(20))
            {
                builder.AppendLine($"- {item.Type} [{item.Severity}] item={item.ItemId} entry={item.EntryId ?? "-"}");
                builder.AppendLine($"  message : {item.Message}");
                builder.AppendLine($"  action  : {item.SuggestedAction}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Reindex Preview");
        builder.AppendLine($"- sources : {preview.SourceItemCount}");
        builder.AppendLine($"- create  : {preview.WouldCreateCount}");
        builder.AppendLine($"- update  : {preview.WouldUpdateCount}");
        builder.AppendLine($"- current : {preview.AlreadyCurrentCount}");
        builder.AppendLine($"- orphan  : {preview.WouldDeleteOrphanCount}");
        builder.AppendLine();
        builder.AppendLine("Actions");
        builder.AppendLine("- P Reindex Plan");
        builder.AppendLine("- A Apply Reindex (requires YES)");
        builder.AppendLine("- R Reindex Reports");
        builder.AppendLine("- Q Query Preview");
        builder.AppendLine("- D Diagnostics");
        if (preview.Warnings.Count > 0)
        {
            foreach (var warning in preview.Warnings)
            {
                builder.AppendLine($"- warning : {warning}");
            }
        }

        foreach (var item in preview.Items.Take(20))
        {
            builder.AppendLine($"- {item.Action,-12} {item.ItemId} kind={item.ItemKind} layer={item.Layer}");
            builder.AppendLine($"  reason : {item.Reason}");
        }

        return builder.ToString();
    }

    public static string RenderVectorQueryPreview(VectorQueryPreviewResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Vector Query Preview");
        builder.AppendLine("====================");
        builder.AppendLine($"Operation  : {result.OperationId}");
        builder.AppendLine($"Workspace  : {result.WorkspaceId}");
        builder.AppendLine($"Collection : {result.CollectionId}");
        builder.AppendLine($"Query      : {result.QueryText}");
        builder.AppendLine($"TopK       : {result.TopK}");
        builder.AppendLine($"Profile    : {result.ProfileId}");
        builder.AppendLine($"Layer      : {result.Layer ?? "-"}");
        builder.AppendLine($"ItemKind   : {result.ItemKind ?? "-"}");
        builder.AppendLine($"MinSim     : {result.MinSimilarity?.ToString("F3") ?? "-"}");
        builder.AppendLine();
        builder.AppendLine("Diagnostics");
        builder.AppendLine($"- indexed={result.Diagnostics.IndexedCount} duplicate={result.Diagnostics.DuplicateCount} stale={result.Diagnostics.StaleCount} orphan={result.Diagnostics.OrphanCount}");
        builder.AppendLine($"- store={result.Diagnostics.StoreAvailable} generator={result.Diagnostics.GeneratorAvailable} indexEmpty={result.Diagnostics.IndexEmpty}");

        if (result.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Warnings");
            foreach (var warning in result.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Candidates");
        if (result.Candidates.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return builder.ToString();
        }

        foreach (var candidate in result.Candidates.Take(30))
        {
            var flags = new List<string>();
            if (candidate.IsDuplicate) flags.Add("duplicate");
            if (candidate.IsStale) flags.Add("stale");
            if (candidate.IsOrphan) flags.Add("orphan");
            if (candidate.IsLifecycleRisk) flags.Add("lifecycle-risk");
            builder.AppendLine($"- #{candidate.Rank} raw=#{candidate.RawRank} {candidate.ItemId} sim={candidate.Similarity:F4} status={candidate.EligibilityStatus} target={candidate.TargetSection}");
            builder.AppendLine($"  kind={candidate.ItemKind} layer={candidate.Layer} riskBefore={candidate.RiskIfNormalSelected} riskAfter={candidate.RiskAfterPolicy}");
            builder.AppendLine($"  entry={candidate.EntryId} model={candidate.EmbeddingModel} provider={candidate.EmbeddingProvider}");
            if (flags.Count > 0)
            {
                builder.AppendLine($"  flags={string.Join(",", flags)}");
            }

            if (candidate.BlockedReasons.Count > 0)
            {
                builder.AppendLine($"  blocked={string.Join(",", candidate.BlockedReasons)}");
            }

            if (candidate.Diagnostics.Count > 0)
            {
                builder.AppendLine($"  diagnostics={string.Join(",", candidate.Diagnostics)}");
            }
        }

        return builder.ToString();
    }

    public static string RenderVectorReindexPlan(VectorReindexPlan plan)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Vector Reindex Plan");
        builder.AppendLine("===================");
        builder.AppendLine($"PlanId     : {plan.PlanId}");
        builder.AppendLine($"Workspace  : {plan.WorkspaceId}");
        builder.AppendLine($"Collection : {plan.CollectionId}");
        builder.AppendLine($"DryRun     : {plan.DryRun}");
        builder.AppendLine($"Candidates : total={plan.TotalCandidates} create={plan.ToCreate} update={plan.ToUpdate} skip={plan.ToSkip} orphan={plan.ToDeleteOrphan}");
        builder.AppendLine($"Signals    : stale={plan.StaleItems.Count} missing={plan.MissingItems.Count} duplicate={plan.DuplicateItems.Count} orphan={plan.OrphanItems.Count} estimatedEmbedding={plan.EstimatedEmbeddingCount}");

        if (plan.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Warnings");
            foreach (var warning in plan.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Plan Items");
        if (plan.Items.Count == 0)
        {
            builder.AppendLine("- (empty)");
        }
        else
        {
            foreach (var item in plan.Items.Take(30))
            {
                builder.AppendLine($"- {item.Action,-12} {item.ItemId} kind={item.ItemKind} layer={item.Layer}");
                builder.AppendLine($"  reason : {item.Reason}");
            }
        }

        return builder.ToString();
    }

    public static string RenderVectorReindexSubmit(VectorReindexSubmitResponse response)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Vector Reindex Submit");
        builder.AppendLine("=====================");
        builder.AppendLine($"JobId      : {response.Job.JobId}");
        builder.AppendLine($"State      : {response.Job.State}");
        builder.AppendLine($"Kind       : {response.Job.Kind}");
        builder.AppendLine($"Workspace  : {response.Job.WorkspaceId}");
        builder.AppendLine($"Collection : {response.Job.CollectionId}");
        builder.AppendLine();
        builder.AppendLine($"Plan       : create={response.Plan.ToCreate} update={response.Plan.ToUpdate} skip={response.Plan.ToSkip} orphan={response.Plan.ToDeleteOrphan} duplicate={response.Plan.DuplicateItems.Count}");
        builder.AppendLine("Apply 已提交为后台 job；正式 retrieval/package 输出不会被 vector reindex 修改。");
        return builder.ToString();
    }

    public static string RenderVectorReindexReports(VectorReindexReportQueryResponse response)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Vector Reindex Reports");
        builder.AppendLine("======================");
        builder.AppendLine($"Count: {response.Count}");
        if (response.Reports.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return builder.ToString();
        }

        foreach (var report in response.Reports.Take(20))
        {
            builder.AppendLine($"- {report.ReportId} op={report.OperationId} job={report.JobId ?? "-"} dryRun={report.Summary.DryRun} applied={report.Summary.Applied}");
            builder.AppendLine($"  summary: create={report.Summary.Created} update={report.Summary.Updated} skip={report.Summary.Skipped} failed={report.Summary.Failed} duplicate={report.Summary.Duplicate} orphan={report.Summary.Orphan}");
        }

        return builder.ToString();
    }

    public static string RenderError(ContextCoreApiException exception)
    {
        return ServiceOperationRenderer.RenderError(exception);
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

    private static string FormatDictionaryCompact(IReadOnlyDictionary<string, int> values)
    {
        return values.Count == 0
            ? "-"
            : string.Join(", ", values
                .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .Select(static pair => $"{pair.Key}={pair.Value}"));
    }

    private static string TrimHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }

        return value.Length <= 16 ? value : value[..16];
    }

    private static string FormatEmpty(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value;
    }

    private static string ReadMetadata(ContextConstraint item, string key)
    {
        return item.Metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : "-";
    }
}
