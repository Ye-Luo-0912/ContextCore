using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Graph;
using ContextCore.Core.Services.Retrieval;
using ContextCore.Core.Services.Storage;
using ContextCore.Embedding;
using ContextCore.Embedding.Providers;
using ContextCore.ModelGateway;
using ContextCore.ModelGateway.Infrastructure;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.ControlRoom.Services;

public sealed partial class ControlRoomService
{

    public async Task<ServiceDashboardSnapshot> GetServiceDashboardSnapshotAsync(
        bool includeDeep = false,
        bool refreshDeep = false,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetServiceClient()
            .GetRuntimeSnapshotAsync(includeDeep, refreshDeep, cancellationToken)
            .ConfigureAwait(false);

        return new ServiceDashboardSnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            Snapshot = snapshot
        };
    }

    public async Task<ServiceJobsSnapshot> GetServiceJobsSnapshotAsync(
        ContextJobState? state = null,
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        var jobs = await GetServiceClient().QueryJobsAsync(
            new ContextJobQuery
            {
                WorkspaceId = _state.WorkspaceId,
                CollectionId = _state.CollectionId,
                State = state,
                Take = take
            },
            cancellationToken).ConfigureAwait(false);
        return new ServiceJobsSnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            Jobs = jobs
        };
    }

    public async Task<ServiceModelSnapshot> GetServiceModelSnapshotAsync(
        ContextCoreModelRouteResolveRequest? routeRequest = null,
        CancellationToken cancellationToken = default)
    {
        var modelStatus = await GetServiceClient().GetModelStatusAsync(cancellationToken).ConfigureAwait(false);
        var resolution = routeRequest is null
            ? null
            : await GetServiceClient().ResolveModelRouteAsync(routeRequest, cancellationToken).ConfigureAwait(false);

        return new ServiceModelSnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            ModelStatus = modelStatus,
            RouteResolution = resolution
        };
    }

    public async Task<ServiceAdminRuntimeSnapshot> GetServiceAdminRuntimeSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var runtime = await GetServiceClient()
            .GetRuntimeSnapshotAsync(includeDeep: false, refreshDeep: false, cancellationToken)
            .ConfigureAwait(false);
        var adminStatus = await GetServiceClient()
            .GetAdminStatusAsync(_state.WorkspaceId, _state.CollectionId, cancellationToken)
            .ConfigureAwait(false);
        var backupStatus = await GetServiceClient().GetBackupStatusAsync(cancellationToken).ConfigureAwait(false);
        var backupValidate = await GetServiceClient().ValidateBackupAsync(cancellationToken).ConfigureAwait(false);
        var postgresDiagnostics = await GetPostgresStorageDiagnosticsSafeAsync(cancellationToken).ConfigureAwait(false);
        var layoutRoot = string.IsNullOrWhiteSpace(adminStatus.Storage.RootPath)
            ? _state.RootPath
            : adminStatus.Storage.RootPath;

        return new ServiceAdminRuntimeSnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            Runtime = runtime,
            AdminStatus = adminStatus,
            BackupStatus = backupStatus,
            BackupValidate = backupValidate,
            FileLayoutStatus = BuildFileLayoutStatus(layoutRoot),
            MemoryLayoutDiagnostics = BuildMemoryLayoutDiagnostics(layoutRoot, _state.WorkspaceId, _state.CollectionId),
            TraceLayoutDiagnostics = BuildTraceLayoutDiagnostics(layoutRoot, _state.WorkspaceId, _state.CollectionId),
            ReportLayoutDiagnostics = BuildReportLayoutDiagnostics(layoutRoot),
            StorageBoundaryReport = BuildStorageBoundaryReport(),
            PostgresOperationalStoreDiagnostics = postgresDiagnostics,
        };
    }

    public async Task<ServiceMemorySnapshot> GetServiceMemorySnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var working = await GetServiceClient().QueryMemoryAsync(new ContextMemoryQuery
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            Layer = ContextMemoryLayer.Working,
            Take = 200
        }, cancellationToken).ConfigureAwait(false);
        var candidates = await GetServiceClient().QueryMemoryAsync(new ContextMemoryQuery
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            Status = ContextMemoryStatus.Candidate,
            Take = 200
        }, cancellationToken).ConfigureAwait(false);
        var stable = await GetServiceClient().QueryMemoryAsync(new ContextMemoryQuery
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            Layer = ContextMemoryLayer.Stable,
            Status = ContextMemoryStatus.Stable,
            Take = 200
        }, cancellationToken).ConfigureAwait(false);
        var globals = await GetServiceClient().QueryGlobalContextAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            take: 200,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return new ServiceMemorySnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            Working = working,
            Candidates = candidates,
            Stable = stable,
            Global = globals,
            MemoryLayoutDiagnostics = BuildMemoryLayoutDiagnostics(_state.RootPath, _state.WorkspaceId, _state.CollectionId)
        };
    }

    public async Task<ServiceCandidateMemorySnapshot> GetServiceCandidateMemoryPageSnapshotAsync(
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetServiceClient().GetCandidateMemorySnapshotAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            take,
            cancellationToken).ConfigureAwait(false);
        var diagnostics = await GetServiceClient().GetCandidateMemoryDiagnosticsAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            cancellationToken).ConfigureAwait(false);
        return new ServiceCandidateMemorySnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            Snapshot = snapshot,
            Diagnostics = diagnostics
        };
    }

    public async Task<ServiceStableMemorySnapshot> GetServiceStableMemoryPageSnapshotAsync(
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetServiceClient().GetStableMemorySnapshotAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            take,
            cancellationToken).ConfigureAwait(false);
        var diagnostics = await GetServiceClient().GetStableMemoryDiagnosticsAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            cancellationToken).ConfigureAwait(false);
        return new ServiceStableMemorySnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            Snapshot = snapshot,
            Diagnostics = diagnostics
        };
    }

    public async Task<ServiceConstraintsSnapshot> GetServiceConstraintsSnapshotAsync(
        ConstraintLevel? level = null,
        CancellationToken cancellationToken = default)
    {
        var constraints = await GetServiceClient().QueryConstraintsAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            level,
            200,
            cancellationToken).ConfigureAwait(false);
        return new ServiceConstraintsSnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            Constraints = constraints
        };
    }

    public async Task<ServiceConstraintGapsSnapshot> GetServiceConstraintGapsSnapshotAsync(
        string? status = ConstraintGapStatus.Pending,
        string? severity = null,
        int take = 20,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var gaps = await GetServiceClient().GetConstraintGapsAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            status: status,
            severity: severity,
            limit: take,
            offset: offset,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return new ServiceConstraintGapsSnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            Gaps = gaps,
            Status = status,
            Severity = severity,
            Limit = take,
            Offset = offset
        };
    }

    public async Task<ServiceCandidateConstraintsSnapshot> GetServiceCandidateConstraintsSnapshotAsync(
        ContextMemoryStatus? status = ContextMemoryStatus.Candidate,
        int take = 20,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var constraints = await GetServiceClient().GetCandidateConstraintsAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            status,
            take,
            offset,
            cancellationToken).ConfigureAwait(false);
        return new ServiceCandidateConstraintsSnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            Constraints = constraints,
            Status = status,
            Limit = take,
            Offset = offset
        };
    }

    public async Task<ServiceRelationsSnapshot> GetServiceRelationsSnapshotAsync(
        string? itemId = null,
        CancellationToken cancellationToken = default)
    {
        var types = await GetServiceClient().GetRelationTypesAsync(cancellationToken).ConfigureAwait(false);
        var diagnostics = await GetServiceClient().GetRelationDiagnosticsAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            cancellationToken).ConfigureAwait(false);
        ContextCoreRelationsResponse relations = new();
        RelationGraphDiagnosticsReport? itemDiagnostics = null;
        if (!string.IsNullOrWhiteSpace(itemId))
        {
            relations = await GetServiceClient().QueryRelationsAsync(
                itemId,
                _state.WorkspaceId,
                _state.CollectionId,
                cancellationToken).ConfigureAwait(false);
            itemDiagnostics = await GetServiceClient().GetItemRelationDiagnosticsAsync(
                itemId,
                _state.WorkspaceId,
                _state.CollectionId,
                cancellationToken).ConfigureAwait(false);
        }
        return new ServiceRelationsSnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            ItemId = itemId ?? string.Empty,
            Relations = relations,
            RelationTypes = types,
            Diagnostics = diagnostics,
            ItemDiagnostics = itemDiagnostics
        };
    }

    public async Task<ServicePolicySnapshot> GetServicePolicySnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var runtime = await GetServiceClient().GetRuntimeSnapshotAsync(false, false, cancellationToken).ConfigureAwait(false);
        var policies = await GetServiceClient().QueryPackagePoliciesAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return new ServicePolicySnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            Policies = policies,
            DefaultPolicy = CreateDefaultServicePolicy(),
            ProviderCapabilities = runtime.Status.Capabilities,
            LifecycleNotes =
            [
                "正常模式下 deprecated/rejected 内容默认不注入。",
                "deep probe 保持手动触发，不自动扩展。"
            ]
        };
    }

    public async Task<ServiceShortTermMemorySnapshot> GetServiceShortTermMemorySnapshotAsync(
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var runtime = await GetServiceClient()
            .GetRuntimeSnapshotAsync(includeDeep: false, refreshDeep: false, cancellationToken)
            .ConfigureAwait(false);
        var summary = await GetServiceClient().GetShortTermSummaryAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            sessionId,
            latestRawTake: 10,
            cancellationToken).ConfigureAwait(false);
        var rawEvents = await GetServiceClient().GetShortTermRawEventsAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            sessionId,
            take: 20,
            cancellationToken).ConfigureAwait(false);
        var archiveSummary = await GetServiceClient().GetShortTermArchiveSummaryAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            sessionId,
            cancellationToken).ConfigureAwait(false);
        var archiveItems = await GetServiceClient().GetShortTermArchiveItemsAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            sessionId,
            kind: null,
            limit: 10,
            cancellationToken).ConfigureAwait(false);
        var runs = await GetServiceClient().GetShortTermCompactionRunsAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            sessionId,
            trigger: null,
            take: 10,
            cancellationToken).ConfigureAwait(false);

        return new ServiceShortTermMemorySnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            Summary = summary,
            RawEvents = rawEvents,
            ArchiveSummary = archiveSummary,
            ArchiveItems = archiveItems,
            RecentRuns = runs,
            Maintenance = runtime.Status.ShortTermMaintenance ?? runtime.Readiness.ShortTermMaintenance
        };
    }

    public async Task<ServicePromotionCandidatesSnapshot> GetServicePromotionCandidatesSnapshotAsync(
        string? sessionId = null,
        PromotionCandidateStatus? status = null,
        string? kind = null,
        string? suggestedTargetLayer = null,
        double? minConfidence = null,
        double? minImportance = null,
        int take = 20,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var candidates = await GetServiceClient().QueryShortTermPromotionCandidatesAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            sessionId,
            status,
            kind,
            suggestedTargetLayer,
            minConfidence,
            minImportance,
            take,
            offset,
            cancellationToken).ConfigureAwait(false);
        return new ServicePromotionCandidatesSnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            Candidates = candidates,
            Status = status,
            Kind = kind,
            SuggestedTargetLayer = suggestedTargetLayer,
            MinConfidence = minConfidence,
            MinImportance = minImportance,
            Limit = take,
            Offset = offset
        };
    }

    public async Task<ServiceStableReviewCandidatesSnapshot> GetServiceStableReviewCandidatesSnapshotAsync(
        string? sessionId = null,
        string? status = null,
        string? validationStatus = null,
        string? kind = null,
        string? suggestedStableTarget = null,
        int take = 20,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var candidates = await GetServiceClient().GetStableReviewCandidatesAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            sessionId,
            status,
            validationStatus,
            kind,
            suggestedStableTarget,
            take,
            offset,
            cancellationToken).ConfigureAwait(false);

        return new ServiceStableReviewCandidatesSnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            Candidates = candidates,
            Status = status,
            ValidationStatus = validationStatus,
            Kind = kind,
            SuggestedStableTarget = suggestedStableTarget,
            Limit = take,
            Offset = offset
        };
    }

    public async Task<ServiceLearningSnapshot> GetServiceLearningSnapshotAsync(
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var records = await GetServiceClient().QueryLearningRecordsAsync(new ContextLearningRecordQuery
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            Limit = limit,
            Offset = offset
        }, cancellationToken).ConfigureAwait(false);
        var cases = await GetServiceClient().QueryLearningCasesAsync(new ContextLearningCaseQuery
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            Limit = limit,
            Offset = offset
        }, cancellationToken).ConfigureAwait(false);
        var feedback = await GetServiceClient().GetLearningFeedbackAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            limit: limit,
            offset: offset,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var summary = await GetServiceClient().GetLearningSummaryAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var regressionCases = await GetServiceClient().GetRegressionLearningCasesAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            limit: 20,
            offset: 0,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return new ServiceLearningSnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            Summary = summary,
            FeedbackSignals = feedback,
            Records = records,
            Cases = cases,
            RegressionCases = regressionCases,
            PositiveCount = records.Count(record => record.Signal == ContextFeedbackSignal.Positive),
            NegativeCount = records.Count(record => record.Signal == ContextFeedbackSignal.Negative),
            StaleCount = records.Count(record => record.Signal == ContextFeedbackSignal.Stale),
            FailureTypeSummary = records
                .GroupBy(record => record.FailureType)
                .ToDictionary(group => group.Key, group => group.Count())
        };
    }

    public async Task<ServiceVectorIndexSnapshot> GetServiceVectorIndexSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var client = GetServiceClient();
        var status = await client.GetVectorStatusAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            cancellationToken).ConfigureAwait(false);
        var diagnostics = await client.GetVectorDiagnosticsAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            cancellationToken).ConfigureAwait(false);
        var preview = await client.PreviewVectorReindexAsync(new VectorReindexPreviewRequest
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            Take = 50,
            IncludeContextItems = true,
            IncludeMemoryItems = true
        }, cancellationToken).ConfigureAwait(false);
        var plan = await client.CreateVectorReindexPlanAsync(new VectorReindexRequest
        {
            OperationId = $"vector-coverage-controlroom-{Guid.NewGuid():N}",
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            DryRun = true,
            Apply = false,
            MaxItems = 10_000,
            IncludeContextItems = true,
            IncludeMemoryItems = true,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["createdFrom"] = "controlroom_vector_coverage"
            }
        }, cancellationToken).ConfigureAwait(false);
        var coverage = VectorIndexCoverageReportBuilder.Build(plan, diagnostics, status);

        return new ServiceVectorIndexSnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            Status = status,
            Diagnostics = diagnostics,
            ReindexPreview = preview,
            Coverage = coverage
        };
    }

    public Task<VectorReindexPlan> CreateServiceVectorReindexPlanAsync(
        VectorReindexRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        request = NormalizeVectorReindexRequest(request, apply: false);
        return GetServiceClient().CreateVectorReindexPlanAsync(request, cancellationToken);
    }

    public Task<VectorReindexSubmitResponse> SubmitServiceVectorReindexAsync(
        VectorReindexRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        request = NormalizeVectorReindexRequest(request, apply: true);
        return GetServiceClient().SubmitVectorReindexAsync(request, cancellationToken);
    }

    public Task<VectorQueryPreviewResult> PreviewServiceVectorQueryAsync(
        VectorQueryPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = new VectorQueryPreviewRequest
        {
            OperationId = string.IsNullOrWhiteSpace(request.OperationId)
                ? $"vector-query-controlroom-{Guid.NewGuid():N}"
                : request.OperationId,
            WorkspaceId = string.IsNullOrWhiteSpace(request.WorkspaceId) ? _state.WorkspaceId : request.WorkspaceId,
            CollectionId = string.IsNullOrWhiteSpace(request.CollectionId) ? _state.CollectionId : request.CollectionId,
            QueryText = request.QueryText,
            TopK = request.TopK > 0 ? request.TopK : 10,
            ProfileId = string.IsNullOrWhiteSpace(request.ProfileId)
                ? VectorQueryProfileIds.NormalV1
                : request.ProfileId,
            Layer = request.Layer,
            ItemKind = request.ItemKind,
            MinSimilarity = request.MinSimilarity,
            IncludeVector = request.IncludeVector,
            Metadata = request.Metadata
        };

        return GetServiceClient().PreviewVectorQueryAsync(normalized, cancellationToken);
    }

    private VectorReindexRequest NormalizeVectorReindexRequest(
        VectorReindexRequest? request,
        bool apply)
    {
        request ??= new VectorReindexRequest();
        return new VectorReindexRequest
        {
            OperationId = string.IsNullOrWhiteSpace(request.OperationId)
                ? $"vector-reindex-controlroom-{Guid.NewGuid():N}"
                : request.OperationId,
            WorkspaceId = string.IsNullOrWhiteSpace(request.WorkspaceId) ? _state.WorkspaceId : request.WorkspaceId,
            CollectionId = string.IsNullOrWhiteSpace(request.CollectionId) ? _state.CollectionId : request.CollectionId,
            Layer = request.Layer,
            ItemKind = request.ItemKind,
            Layers = request.Layers,
            DryRun = !apply,
            Apply = apply,
            ConfirmApply = apply && request.ConfirmApply,
            Force = request.Force,
            BatchSize = request.BatchSize > 0 ? request.BatchSize : 50,
            MaxItems = request.MaxItems > 0 ? request.MaxItems : 200,
            IncludeContextItems = request.IncludeContextItems,
            IncludeMemoryItems = request.IncludeMemoryItems,
            Metadata = request.Metadata
        };
    }

    private static ContextLearningCaseStatusUpdateRequest CreateLearningCaseStatusRequest(string reason)
    {
        return new ContextLearningCaseStatusUpdateRequest
        {
            OperationId = Guid.NewGuid().ToString("N"),
            Reviewer = "controlroom",
            Reason = string.IsNullOrWhiteSpace(reason) ? "controlroom" : reason.Trim(),
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["source"] = "ControlRoom"
            }
        };
    }

    public string FormatServiceError(ContextCoreApiException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var builder = new StringBuilder();
        builder.AppendLine("Service 调用失败");
        builder.AppendLine($"状态码 : {(int)exception.StatusCode}");
        builder.AppendLine($"错误码 : {exception.ErrorResponse.ErrorCode}");
        builder.AppendLine($"目标   : {exception.ErrorResponse.Target}");
        builder.AppendLine($"消息   : {exception.ErrorResponse.Message}");
        builder.AppendLine($"操作   : {exception.ErrorResponse.OperationId}");
        builder.AppendLine($"Trace  : {exception.ErrorResponse.TraceId}");

        if (exception.ErrorResponse.Details.Count > 0)
        {
            builder.AppendLine("详情");
            foreach (var detail in exception.ErrorResponse.Details)
            {
                builder.AppendLine($"- [{detail.Code}] {detail.Field ?? detail.Target ?? "n/a"}: {detail.Message}");
            }
        }

        if (exception.ErrorResponse.Warnings.Count > 0)
        {
            builder.AppendLine("警告");
            foreach (var warning in exception.ErrorResponse.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private ContextPackagePolicy CreateDefaultServicePolicy()
    {
        return new ContextPackagePolicy
        {
            Id = "runtime-default",
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            Name = "Runtime Default Policy",
            Description = "ControlRoom Service Mode 默认只读展示策略。",
            TokenBudget = 1200,
            IncludeGlobalContext = true,
            IncludeHardConstraints = true,
            IncludeSoftConstraints = true,
            IncludeWorkingMemory = true,
            IncludeStableMemory = true,
            IncludeRecentRawContext = true,
            MaxRecentItems = 20
        };
    }
}
