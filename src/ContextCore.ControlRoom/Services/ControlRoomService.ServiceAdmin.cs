using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.ControlRoom.Models;
using ContextCore.Client;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Attention;
using ContextCore.Core.Services.Graph;
using ContextCore.Core.Services.Planning;
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
        var jobs = await QueryServiceJobsAsync(state, take, cancellationToken).ConfigureAwait(false);
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
        var modelStatus = await GetServiceModelStatusAsync(cancellationToken).ConfigureAwait(false);
        var resolution = routeRequest is null
            ? null
            : await ResolveServiceModelRouteAsync(routeRequest, cancellationToken).ConfigureAwait(false);

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
        var adminStatus = await GetServiceAdminStatusAsync(cancellationToken).ConfigureAwait(false);
        var backupStatus = await GetServiceBackupStatusAsync(cancellationToken).ConfigureAwait(false);
        var backupValidate = await ValidateServiceBackupAsync(cancellationToken).ConfigureAwait(false);
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

    public Task<ContextInputIngestionResult> IngestServiceAsync(
        ContextInputCommand command,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().IngestAsync(command, cancellationToken);
    }

    public Task<ContextQueryResponse> QueryServiceAsync(
        ContextQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().QueryContextAsync(request, cancellationToken);
    }

    public Task<IReadOnlyList<ContextMemoryItem>> QueryServiceMemoryAsync(
        ContextMemoryQuery query,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().QueryMemoryAsync(query, cancellationToken);
    }

    public Task<CandidateMemorySnapshot> GetServiceCandidateMemorySnapshotAsync(
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetCandidateMemorySnapshotAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            take,
            cancellationToken);
    }

    public Task<StableMemorySnapshot> GetServiceStableMemorySnapshotAsync(
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetStableMemorySnapshotAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            take,
            cancellationToken);
    }

    public Task<StableMemoryDiagnosticsReport> GetServiceStableMemoryDiagnosticsAsync(
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetStableMemoryDiagnosticsAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            cancellationToken);
    }

    public Task<StableMemoryExplanation> ExplainServiceStableMemoryAsync(
        string itemId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().ExplainStableMemoryAsync(
            itemId,
            _state.WorkspaceId,
            _state.CollectionId,
            cancellationToken);
    }

    public Task<StableReplacementChainResponse> GetServiceStableReplacementChainAsync(
        string itemId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetStableReplacementChainAsync(
            itemId,
            _state.WorkspaceId,
            _state.CollectionId,
            cancellationToken);
    }

    public Task<StableLifecycleReviewResult> DeprecateServiceStableMemoryAsync(
        string itemId,
        StableLifecycleReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().DeprecateStableMemoryAsync(itemId, request, cancellationToken);
    }

    public Task<StableLifecycleReviewResult> SupersedeServiceStableMemoryAsync(
        string itemId,
        StableLifecycleReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().SupersedeStableMemoryAsync(itemId, request, cancellationToken);
    }

    public Task<StableLifecycleReviewResult> RejectServiceStableMemoryAsync(
        string itemId,
        StableLifecycleReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().RejectStableMemoryAsync(itemId, request, cancellationToken);
    }

    public Task<IReadOnlyList<StableLifecycleReviewRecord>> GetServiceStableMemoryReviewsAsync(
        string itemId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetStableMemoryReviewsAsync(itemId, cancellationToken);
    }

    public Task<CandidateMemoryRecord> GetServiceCandidateMemoryAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetCandidateMemoryAsync(
            candidateId,
            _state.WorkspaceId,
            _state.CollectionId,
            cancellationToken);
    }

    public Task<CandidateMemoryExplanation> ExplainServiceCandidateMemoryAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().ExplainCandidateMemoryAsync(
            candidateId,
            _state.WorkspaceId,
            _state.CollectionId,
            cancellationToken);
    }

    public Task<CandidateMemoryDiagnosticsReport> GetServiceCandidateMemoryDiagnosticsAsync(
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetCandidateMemoryDiagnosticsAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            cancellationToken);
    }

    public Task<CandidateMemoryReviewResult> MarkServiceCandidateMemoryReadyForStableReviewAsync(
        string candidateId,
        CandidateMemoryReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().MarkCandidateMemoryReadyForStableReviewAsync(candidateId, request, cancellationToken);
    }

    public Task<CandidateMemoryReviewResult> MarkServiceCandidateMemoryNeedsMoreEvidenceAsync(
        string candidateId,
        CandidateMemoryReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().MarkCandidateMemoryNeedsMoreEvidenceAsync(candidateId, request, cancellationToken);
    }

    public Task<CandidateMemoryReviewResult> RejectServiceCandidateMemoryAsync(
        string candidateId,
        CandidateMemoryReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().RejectCandidateMemoryAsync(candidateId, request, cancellationToken);
    }

    public Task<CandidateMemoryReviewResult> ExpireServiceCandidateMemoryAsync(
        string candidateId,
        CandidateMemoryReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().ExpireCandidateMemoryAsync(candidateId, request, cancellationToken);
    }

    public Task<CandidateMemoryReviewResult> SupersedeServiceCandidateMemoryAsync(
        string candidateId,
        CandidateMemoryReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().SupersedeCandidateMemoryAsync(candidateId, request, cancellationToken);
    }

    public Task<IReadOnlyList<CandidateMemoryReviewRecord>> GetServiceCandidateMemoryReviewsAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetCandidateMemoryReviewsAsync(candidateId, cancellationToken);
    }

    public Task<IReadOnlyList<ContextGlobalItem>> QueryServiceGlobalContextAsync(
        ContextScope? scope = null,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().QueryGlobalContextAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            scope,
            take,
            cancellationToken);
    }

    public Task<ContextPackageBuildResult> BuildServicePackageAsync(
        ContextPackageRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().BuildPackageDetailedAsync(request, cancellationToken);
    }

    public Task<IReadOnlyList<ContextConstraint>> QueryServiceConstraintsAsync(
        ConstraintLevel? level = null,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().QueryConstraintsAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            level,
            take,
            cancellationToken);
    }

    public Task<IReadOnlyList<ContextConstraint>> QueryServiceCandidateConstraintsAsync(
        ContextMemoryStatus? status = ContextMemoryStatus.Candidate,
        int take = 20,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetCandidateConstraintsAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            status,
            take,
            offset,
            cancellationToken);
    }

    public Task<ContextConstraint> GetServiceCandidateConstraintAsync(
        string constraintId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetCandidateConstraintAsync(constraintId, cancellationToken);
    }

    public Task<CandidateConstraintReviewResult> ActivateServiceCandidateConstraintAsync(
        string constraintId,
        CandidateConstraintReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().ActivateCandidateConstraintAsync(constraintId, request, cancellationToken);
    }

    public Task<CandidateConstraintReviewResult> RejectServiceCandidateConstraintAsync(
        string constraintId,
        CandidateConstraintReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().RejectCandidateConstraintAsync(constraintId, request, cancellationToken);
    }

    public Task<IReadOnlyList<CandidateConstraintReviewRecord>> GetServiceCandidateConstraintReviewsAsync(
        string constraintId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetCandidateConstraintReviewsAsync(constraintId, cancellationToken);
    }

    public Task<IReadOnlyList<ConstraintGapCandidate>> QueryServiceConstraintGapsAsync(
        string? status = null,
        string? severity = null,
        int take = 20,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetConstraintGapsAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            status: status,
            severity: severity,
            limit: take,
            offset: offset,
            cancellationToken: cancellationToken);
    }

    public Task<ConstraintGapCandidate> GetServiceConstraintGapAsync(
        string gapId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetConstraintGapAsync(gapId, cancellationToken);
    }

    public Task<ConstraintGapReviewResult> AcceptServiceConstraintGapAsync(
        string gapId,
        ConstraintGapReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().AcceptConstraintGapAsync(gapId, request, cancellationToken);
    }

    public Task<ConstraintGapReviewResult> RejectServiceConstraintGapAsync(
        string gapId,
        ConstraintGapReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().RejectConstraintGapAsync(gapId, request, cancellationToken);
    }

    public Task<IReadOnlyList<ConstraintGapReviewRecord>> GetServiceConstraintGapReviewsAsync(
        string gapId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetConstraintGapReviewsAsync(gapId, cancellationToken);
    }

    public Task<ContextProvenanceResponse> GetServiceProvenanceAsync(
        string itemId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetProvenanceAsync(
            itemId,
            _state.WorkspaceId,
            _state.CollectionId,
            cancellationToken);
    }

    public Task<ContextCoreRelationsResponse> QueryServiceRelationsAsync(
        string itemId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().QueryRelationsAsync(
            itemId,
            _state.WorkspaceId,
            _state.CollectionId,
            cancellationToken);
    }

    public Task<IReadOnlyList<RelationTypeDefinition>> GetServiceRelationTypesAsync(
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetRelationTypesAsync(cancellationToken);
    }

    public Task<IReadOnlyList<RelationExpansionProfile>> GetServiceRelationExpansionProfilesAsync(
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetRelationExpansionProfilesAsync(cancellationToken);
    }

    public Task<RelationExpansionPreviewResponse> PreviewServiceRelationExpansionAsync(
        string itemId,
        string profileId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().PreviewRelationExpansionAsync(new RelationExpansionPreviewRequest
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            ItemId = itemId,
            ProfileId = profileId
        }, cancellationToken);
    }

    public Task<IReadOnlyList<GraphExpansionShadowTraceRecord>> GetServiceGraphExpansionShadowTracesAsync(
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetGraphExpansionShadowTracesAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            take,
            cancellationToken);
    }

    public Task<RelationGraphDiagnosticsReport> GetServiceRelationDiagnosticsAsync(
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetRelationDiagnosticsAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            cancellationToken);
    }

    public Task<RelationGraphDiagnosticsReport> GetServiceItemRelationDiagnosticsAsync(
        string itemId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetItemRelationDiagnosticsAsync(
            itemId,
            _state.WorkspaceId,
            _state.CollectionId,
            cancellationToken);
    }

    public Task<RelationExplainResponse> ExplainServiceRelationAsync(
        string relationId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().ExplainRelationAsync(
            relationId,
            _state.WorkspaceId,
            _state.CollectionId,
            cancellationToken);
    }

    public Task<RelationReviewResult> ReviewServiceRelationAsync(
        string relationId,
        RelationReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().ReviewRelationAsync(relationId, request, cancellationToken);
    }

    public Task<RelationReviewResult> RejectServiceRelationAsync(
        string relationId,
        RelationReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().RejectRelationAsync(relationId, request, cancellationToken);
    }

    public Task<RelationReviewResult> DeprecateServiceRelationAsync(
        string relationId,
        RelationReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().DeprecateRelationAsync(relationId, request, cancellationToken);
    }

    public Task<RelationReviewResult> MarkServiceRelationNeedsEvidenceAsync(
        string relationId,
        RelationReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().MarkRelationNeedsEvidenceAsync(relationId, request, cancellationToken);
    }

    public Task<IReadOnlyList<RelationReviewRecord>> GetServiceRelationReviewsAsync(
        string relationId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetRelationReviewsAsync(relationId, cancellationToken);
    }

    public Task<IReadOnlyList<ContextJob>> QueryServiceJobsAsync(
        ContextJobState? state = null,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().QueryJobsAsync(new ContextJobQuery
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            State = state,
            Take = take
        }, cancellationToken);
    }

    public Task<ContextJob> GetServiceJobAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetJobAsync(jobId, cancellationToken);
    }

    public Task<ContextCoreRequeueJobResponse> RequeueServiceJobAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().RequeueJobAsync(jobId, cancellationToken);
    }

    public Task<ContextCoreModelStatusResponse> GetServiceModelStatusAsync(CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetModelStatusAsync(cancellationToken);
    }

    public Task<ContextCoreModelRouteResolveResponse> ResolveServiceModelRouteAsync(
        ContextCoreModelRouteResolveRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().ResolveModelRouteAsync(request, cancellationToken);
    }

    public Task<ContextCoreAdminStatusResponse> GetServiceAdminStatusAsync(CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetAdminStatusAsync(_state.WorkspaceId, _state.CollectionId, cancellationToken);
    }

    public Task<ContextCoreBackupStatusResponse> GetServiceBackupStatusAsync(CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetBackupStatusAsync(cancellationToken);
    }

    public Task<ContextCoreBackupValidateResponse> ValidateServiceBackupAsync(CancellationToken cancellationToken = default)
    {
        return GetServiceClient().ValidateBackupAsync(cancellationToken);
    }

    public Task<IReadOnlyList<ContextPackagePolicy>> QueryServicePoliciesAsync(
        string? queryText = null,
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().QueryPackagePoliciesAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            queryText,
            take,
            cancellationToken);
    }

    public Task<ContextPackagePolicy> GetServicePolicyAsync(
        string policyId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetPackagePolicyAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            policyId,
            cancellationToken);
    }

    public async Task<ServiceMemorySnapshot> GetServiceMemorySnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var working = await QueryServiceMemoryAsync(new ContextMemoryQuery
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            Layer = ContextMemoryLayer.Working,
            Take = 200
        }, cancellationToken).ConfigureAwait(false);
        var candidates = await QueryServiceMemoryAsync(new ContextMemoryQuery
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            Status = ContextMemoryStatus.Candidate,
            Take = 200
        }, cancellationToken).ConfigureAwait(false);
        var stable = await QueryServiceMemoryAsync(new ContextMemoryQuery
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            Layer = ContextMemoryLayer.Stable,
            Status = ContextMemoryStatus.Stable,
            Take = 200
        }, cancellationToken).ConfigureAwait(false);
        var globals = await QueryServiceGlobalContextAsync(take: 200, cancellationToken: cancellationToken).ConfigureAwait(false);

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
        var snapshot = await GetServiceCandidateMemorySnapshotAsync(take, cancellationToken).ConfigureAwait(false);
        var diagnostics = await GetServiceCandidateMemoryDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
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
        var snapshot = await GetServiceStableMemorySnapshotAsync(take, cancellationToken).ConfigureAwait(false);
        var diagnostics = await GetServiceStableMemoryDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
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
        var constraints = await QueryServiceConstraintsAsync(level, 200, cancellationToken).ConfigureAwait(false);
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
        var gaps = await QueryServiceConstraintGapsAsync(status, severity, take, offset, cancellationToken).ConfigureAwait(false);
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
        var constraints = await QueryServiceCandidateConstraintsAsync(
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
        var types = await GetServiceRelationTypesAsync(cancellationToken).ConfigureAwait(false);
        var diagnostics = await GetServiceRelationDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
        ContextCoreRelationsResponse relations = new();
        RelationGraphDiagnosticsReport? itemDiagnostics = null;
        if (!string.IsNullOrWhiteSpace(itemId))
        {
            relations = await QueryServiceRelationsAsync(itemId, cancellationToken).ConfigureAwait(false);
            itemDiagnostics = await GetServiceItemRelationDiagnosticsAsync(itemId, cancellationToken).ConfigureAwait(false);
        }
        var graphShadowTraces = await GetServiceGraphExpansionShadowTracesAsync(50, cancellationToken).ConfigureAwait(false);
        var graphShadowQuality = new GraphExpansionShadowTraceQualityReportBuilder()
            .Build(graphShadowTraces, _state.WorkspaceId, _state.CollectionId);

        return new ServiceRelationsSnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            ItemId = itemId ?? string.Empty,
            Relations = relations,
            RelationTypes = types,
            Diagnostics = diagnostics,
            ItemDiagnostics = itemDiagnostics,
            GraphShadowTraceQualitySummary = graphShadowQuality,
            RecentGraphShadowTraces = graphShadowTraces.Take(5).ToArray()
        };
    }

    public async Task<ServicePolicySnapshot> GetServicePolicySnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var runtime = await GetServiceClient().GetRuntimeSnapshotAsync(false, false, cancellationToken).ConfigureAwait(false);
        var policies = await QueryServicePoliciesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

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

    public Task<ShortTermMemoryCompactionResult> CompactServiceShortTermMemoryAsync(
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().CompactShortTermMemoryAsync(new ShortTermMemoryCompactionRequest
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            SessionId = sessionId
        }, cancellationToken);
    }

    public Task<ShortTermArchiveSummary> GetServiceShortTermArchiveSummaryAsync(
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetShortTermArchiveSummaryAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            sessionId,
            cancellationToken);
    }

    public Task<ShortTermArchiveItemsResponse> GetServiceShortTermArchiveItemsAsync(
        string? sessionId = null,
        string? kind = null,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetShortTermArchiveItemsAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            sessionId,
            kind,
            limit,
            cancellationToken);
    }

    public Task<IReadOnlyList<ShortTermCompactionRun>> GetServiceShortTermCompactionRunsAsync(
        string? sessionId = null,
        string? trigger = null,
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetShortTermCompactionRunsAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            sessionId,
            trigger,
            take,
            cancellationToken);
    }

    public Task<ShortTermCompactionRun> GetServiceShortTermCompactionRunAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetShortTermCompactionRunAsync(runId, cancellationToken);
    }

    public Task<IReadOnlyList<ShortTermPromotionCandidate>> GenerateServiceShortTermPromotionCandidatesAsync(
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GenerateShortTermPromotionCandidatesAsync(new ShortTermPromotionCandidateGenerationRequest
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            SessionId = sessionId
        }, cancellationToken);
    }

    public Task<IReadOnlyList<ShortTermPromotionCandidate>> QueryServiceShortTermPromotionCandidatesAsync(
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
        return GetServiceClient().QueryShortTermPromotionCandidatesAsync(
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
            cancellationToken);
    }

    public Task<ShortTermPromotionCandidate> GetServiceShortTermPromotionCandidateAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetShortTermPromotionCandidateAsync(candidateId, cancellationToken);
    }

    public Task<ShortTermPromotionCandidateExplanation> ExplainServiceShortTermPromotionCandidateAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().ExplainShortTermPromotionCandidateAsync(candidateId, cancellationToken);
    }

    public Task<ReviewPromotionCandidateResponse> AcceptServiceShortTermPromotionCandidateAsync(
        string candidateId,
        ReviewPromotionCandidateRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().AcceptShortTermPromotionCandidateAsync(candidateId, request, cancellationToken);
    }

    public Task<ReviewPromotionCandidateResponse> RejectServiceShortTermPromotionCandidateAsync(
        string candidateId,
        ReviewPromotionCandidateRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().RejectShortTermPromotionCandidateAsync(candidateId, request, cancellationToken);
    }

    public Task<ReviewPromotionCandidateResponse> ExpireServiceShortTermPromotionCandidateAsync(
        string candidateId,
        ReviewPromotionCandidateRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().ExpireShortTermPromotionCandidateAsync(candidateId, request, cancellationToken);
    }

    public Task<IReadOnlyList<PromotionCandidateReviewRecord>> GetServiceShortTermPromotionCandidateReviewsAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetShortTermPromotionCandidateReviewsAsync(candidateId, cancellationToken);
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
        var candidates = await QueryServiceShortTermPromotionCandidatesAsync(
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

    public Task<IReadOnlyList<StableReviewCandidate>> GenerateServiceStableReviewCandidatesAsync(
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GenerateStableReviewCandidatesAsync(new StableReviewCandidateGenerationRequest
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            SessionId = sessionId,
            Limit = 100
        }, cancellationToken);
    }

    public Task<IReadOnlyList<StableReviewCandidate>> QueryServiceStableReviewCandidatesAsync(
        string? sessionId = null,
        string? status = null,
        string? validationStatus = null,
        string? kind = null,
        string? suggestedStableTarget = null,
        int take = 20,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetStableReviewCandidatesAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            sessionId,
            status,
            validationStatus,
            kind,
            suggestedStableTarget,
            take,
            offset,
            cancellationToken);
    }

    public Task<StableReviewCandidate> GetServiceStableReviewCandidateAsync(
        string stableReviewCandidateId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetStableReviewCandidateAsync(stableReviewCandidateId, cancellationToken);
    }

    public Task<StableReviewCandidateExplanation> ExplainServiceStableReviewCandidateAsync(
        string stableReviewCandidateId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().ExplainStableReviewCandidateAsync(stableReviewCandidateId, cancellationToken);
    }

    public Task<StableReviewDecisionResult> AcceptServiceStableReviewCandidateAsync(
        string stableReviewCandidateId,
        StableReviewDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().AcceptStableReviewCandidateAsync(stableReviewCandidateId, request, cancellationToken);
    }

    public Task<StableReviewDecisionResult> RejectServiceStableReviewCandidateAsync(
        string stableReviewCandidateId,
        StableReviewDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().RejectStableReviewCandidateAsync(stableReviewCandidateId, request, cancellationToken);
    }

    public Task<IReadOnlyList<StableReviewRecord>> GetServiceStableReviewCandidateReviewsAsync(
        string stableReviewCandidateId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetStableReviewCandidateReviewsAsync(stableReviewCandidateId, cancellationToken);
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
        var candidates = await QueryServiceStableReviewCandidatesAsync(
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

    public Task<IReadOnlyList<ContextLearningRecord>> QueryServiceLearningRecordsAsync(
        ContextFeedbackSignal? signal = null,
        ContextFailureType? failureType = null,
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().QueryLearningRecordsAsync(new ContextLearningRecordQuery
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            Signal = signal,
            FailureType = failureType,
            Limit = limit,
            Offset = offset
        }, cancellationToken);
    }

    public Task<IReadOnlyList<PromotionFeedbackSignal>> QueryServiceLearningFeedbackAsync(
        string? action = null,
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetLearningFeedbackAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            action: action,
            limit: limit,
            offset: offset,
            cancellationToken: cancellationToken);
    }

    public Task<ContextLearningRecord> GetServiceLearningRecordAsync(
        string recordId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetLearningRecordAsync(recordId, cancellationToken);
    }

    public Task<IReadOnlyList<ContextLearningCase>> QueryServiceLearningCasesAsync(
        ContextFeedbackSignal? signal = null,
        ContextFailureType? failureType = null,
        ContextLearningCaseStatus? status = null,
        string? caseKind = null,
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().QueryLearningCasesAsync(new ContextLearningCaseQuery
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            Signal = signal,
            FailureType = failureType,
            Status = status,
            CaseKind = caseKind,
            Limit = limit,
            Offset = offset
        }, cancellationToken);
    }

    public Task<ContextLearningCase> GetServiceLearningCaseAsync(
        string caseId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetLearningCaseAsync(caseId, cancellationToken);
    }

    public Task<ContextLearningCaseGenerationResult> GenerateServiceLearningCasesAsync(
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GenerateLearningCasesAsync(new ContextLearningCaseGenerationRequest
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            Limit = 100
        }, cancellationToken);
    }

    public Task<ContextLearningCaseStatusUpdateResponse> ActivateServiceLearningCaseAsync(
        string caseId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().ActivateLearningCaseAsync(caseId, CreateLearningCaseStatusRequest(reason), cancellationToken);
    }

    public Task<ContextLearningCaseStatusUpdateResponse> ArchiveServiceLearningCaseAsync(
        string caseId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().ArchiveLearningCaseAsync(caseId, CreateLearningCaseStatusRequest(reason), cancellationToken);
    }

    public Task<ContextLearningCaseStatusUpdateResponse> RejectServiceLearningCaseAsync(
        string caseId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().RejectLearningCaseAsync(caseId, CreateLearningCaseStatusRequest(reason), cancellationToken);
    }

    public Task<ContextLearningSummary> GetServiceLearningSummaryAsync(
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetLearningSummaryAsync(_state.WorkspaceId, _state.CollectionId, cancellationToken: cancellationToken);
    }

    public Task<IReadOnlyList<ContextLearningCase>> GetServiceRegressionLearningCasesAsync(
        int limit = 20,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetRegressionLearningCasesAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            limit: limit,
            offset: offset,
            cancellationToken: cancellationToken);
    }

    public async Task<ServiceLearningSnapshot> GetServiceLearningSnapshotAsync(
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var records = await QueryServiceLearningRecordsAsync(
            limit: limit,
            offset: offset,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var cases = await QueryServiceLearningCasesAsync(
            limit: limit,
            offset: offset,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var feedback = await QueryServiceLearningFeedbackAsync(
            limit: limit,
            offset: offset,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var summary = await GetServiceLearningSummaryAsync(cancellationToken).ConfigureAwait(false);
        var regressionCases = await GetServiceRegressionLearningCasesAsync(
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

    public async Task<ServicePolicyFeedbackDatasetSnapshot> GetServicePolicyFeedbackDatasetSnapshotAsync(
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var dataset = await GetServiceClient()
            .GetPolicyFeedbackAsync(
                _state.WorkspaceId,
                _state.CollectionId,
                limit: limit,
                offset: offset,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return new ServicePolicyFeedbackDatasetSnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            Dataset = dataset,
            Limit = limit,
            Offset = offset
        };
    }

    public async Task<ServiceLearningFeaturesSnapshot> GetServiceLearningFeaturesSnapshotAsync(
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var dataset = await GetServiceClient()
            .GetLearningFeaturesAsync(
                _state.WorkspaceId,
                _state.CollectionId,
                limit: limit,
                offset: offset,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var qualityReport = await GetServiceClient()
            .GetLearningDatasetQualityAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var feedbackSummary = await GetServiceClient()
            .GetLearningFeedbackSummaryAsync(new LearningFeedbackEventQuery
            {
                WorkspaceId = _state.WorkspaceId,
                CollectionId = _state.CollectionId,
                Limit = 20
            }, cancellationToken)
            .ConfigureAwait(false);
        var feedbackReviewSummary = await GetServiceClient()
            .GetLearningFeedbackReviewSummaryAsync(new LearningFeedbackReviewQuery
            {
                Limit = 20
            }, cancellationToken)
            .ConfigureAwait(false);

        return new ServiceLearningFeaturesSnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            Dataset = dataset,
            QualityReport = qualityReport,
            LearningFeedbackSummary = feedbackSummary,
            LearningFeedbackReviewSummary = feedbackReviewSummary,
            LearningFeedbackFeatureCandidateReport = await ReadLearningFeedbackFeatureCandidateReportAsync(cancellationToken)
                .ConfigureAwait(false),
            LearningFeedbackQualityReport = await ReadLearningFeedbackQualityReportAsync(cancellationToken)
                .ConfigureAwait(false),
            LearningApprovedFeedbackDatasetGateReport = await ReadLearningApprovedFeedbackDatasetGateReportAsync(cancellationToken)
                .ConfigureAwait(false),
            RouterIntentBaselineReport = await ReadRouterIntentBaselineReportAsync(cancellationToken)
                .ConfigureAwait(false),
            RouterShadowTraceQualityReport = await ReadRouterShadowTraceQualityReportAsync(cancellationToken)
                .ConfigureAwait(false),
            RouterDisagreementTriageA3Report = await ReadRouterDisagreementTriageReportAsync(
                    EvalReportPaths.RouterDisagreementTriageA3ReportFileName,
                    cancellationToken)
                .ConfigureAwait(false),
            RouterDisagreementTriageExtendedReport = await ReadRouterDisagreementTriageReportAsync(
                    EvalReportPaths.RouterDisagreementTriageExtendedReportFileName,
                    cancellationToken)
                .ConfigureAwait(false),
            RouterHardNegativeCount = await ReadRouterHardNegativeCountAsync(cancellationToken)
                .ConfigureAwait(false),
            RouterGuardedOptInReadinessGateReport = await ReadRouterGuardedOptInReadinessGateReportAsync(cancellationToken)
                .ConfigureAwait(false),
            CandidateRerankerFeatureCompletenessA3Report = await ReadCandidateRerankerFeatureCompletenessReportAsync(
                    EvalReportPaths.RankerFeatureCompletenessA3ReportFileName,
                    cancellationToken)
                .ConfigureAwait(false),
            CandidateRerankerFeatureCompletenessExtendedReport = await ReadCandidateRerankerFeatureCompletenessReportAsync(
                    EvalReportPaths.RankerFeatureCompletenessExtendedReportFileName,
                    cancellationToken)
                .ConfigureAwait(false),
            CandidateRerankerShadowEvalA3Report = await ReadCandidateRerankerShadowEvalReportAsync(
                    EvalReportPaths.RankerShadowEvalA3ReportFileName,
                    cancellationToken)
                .ConfigureAwait(false),
            CandidateRerankerShadowEvalExtendedReport = await ReadCandidateRerankerShadowEvalReportAsync(
                    EvalReportPaths.RankerShadowEvalExtendedReportFileName,
                    cancellationToken)
                .ConfigureAwait(false),
            CandidateRerankerShadowFailureAuditA3Report = await ReadCandidateRerankerShadowFailureAuditReportAsync(
                    EvalReportPaths.RankerShadowFailureAuditA3ReportFileName,
                    cancellationToken)
                .ConfigureAwait(false),
            CandidateRerankerShadowFailureAuditExtendedReport = await ReadCandidateRerankerShadowFailureAuditReportAsync(
                    EvalReportPaths.RankerShadowFailureAuditExtendedReportFileName,
                    cancellationToken)
                .ConfigureAwait(false),
            CandidateRerankerScoreDistributionA3Report = await ReadCandidateRerankerScoreDistributionReportAsync(
                    EvalReportPaths.RankerScoreDistributionA3ReportFileName,
                    cancellationToken)
                .ConfigureAwait(false),
            CandidateRerankerScoreDistributionExtendedReport = await ReadCandidateRerankerScoreDistributionReportAsync(
                    EvalReportPaths.RankerScoreDistributionExtendedReportFileName,
                    cancellationToken)
                .ConfigureAwait(false),
            CandidateRerankerListwiseCalibrationA3Report = await ReadCandidateRerankerListwiseCalibrationReportAsync(
                    EvalReportPaths.RankerListwiseCalibrationA3ReportFileName,
                    cancellationToken)
                .ConfigureAwait(false),
            CandidateRerankerListwiseCalibrationExtendedReport = await ReadCandidateRerankerListwiseCalibrationReportAsync(
                    EvalReportPaths.RankerListwiseCalibrationExtendedReportFileName,
                    cancellationToken)
                .ConfigureAwait(false),
            CandidateRerankerFormalPriorityAlignmentA3Report = await ReadCandidateRerankerFormalPriorityAlignmentReportAsync(
                    EvalReportPaths.RankerFormalPriorityAlignmentA3ReportFileName,
                    cancellationToken)
                .ConfigureAwait(false),
            CandidateRerankerFormalPriorityAlignmentExtendedReport = await ReadCandidateRerankerFormalPriorityAlignmentReportAsync(
                    EvalReportPaths.RankerFormalPriorityAlignmentExtendedReportFileName,
                    cancellationToken)
                .ConfigureAwait(false),
            CandidateRerankerShadowTraceQualityReport = await ReadCandidateRerankerShadowTraceQualityReportAsync(cancellationToken)
                .ConfigureAwait(false),
            LearningReadinessRegistry = await ReadLearningReadinessRegistryAsync(cancellationToken)
                .ConfigureAwait(false),
            LearningRuntimeChangeReadinessGateReport = await ReadLearningRuntimeChangeReadinessGateReportAsync(cancellationToken)
                .ConfigureAwait(false),
            FoundationFreezeReport = await ReadFoundationFreezeReportAsync(cancellationToken)
                .ConfigureAwait(false),
            FoundationServiceStatus = await ReadFoundationServiceStatusAsync(cancellationToken)
                .ConfigureAwait(false),
            FoundationReportNavigation = await ReadFoundationReportNavigationAsync(cancellationToken)
                .ConfigureAwait(false),
            FoundationApiSecurityDiagnostics = await ReadFoundationApiSecurityDiagnosticsAsync(cancellationToken)
                .ConfigureAwait(false),
            FoundationApiContractReport = await ReadFoundationApiContractReportAsync(cancellationToken)
                .ConfigureAwait(false),
            FoundationServiceAuthDiagnostics = await ReadFoundationServiceAuthDiagnosticsAsync(cancellationToken)
                .ConfigureAwait(false),
            FoundationServiceDeploymentProfileGate = await ReadFoundationServiceDeploymentProfileGateAsync(cancellationToken)
                .ConfigureAwait(false),
            FoundationOpenApiContractReport = await ReadFoundationOpenApiContractReportAsync(cancellationToken)
                .ConfigureAwait(false),
            HostedServiceSmokeReport = await ReadHostedServiceSmokeReportAsync(cancellationToken)
                .ConfigureAwait(false),
            ServiceFoundationFreezeReport = await ReadServiceFoundationFreezeReportAsync(cancellationToken)
                .ConfigureAwait(false),
            ArchitectureCleanupFreezeReport = TryLoadArchitectureCleanupFreezeSummary()?.Report,
            ArchitectureCleanupFreezeGateReport = TryLoadArchitectureCleanupFreezeGateSummary()?.Report,
            Limit = limit,
            Offset = offset
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
            Coverage = coverage,
            ShadowQuality = LoadVectorShadowQualitySummary()
        };
    }

    private async Task<FoundationServiceStatusResponse?> ReadFoundationServiceStatusAsync(
        CancellationToken cancellationToken)
    {
        if (_state.IsServiceMode)
        {
            try
            {
                return await GetServiceClient()
                    .GetFoundationStatusAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ContextCoreApiException)
            {
                return null;
            }
            catch (HttpRequestException)
            {
                return null;
            }
        }

        try
        {
            return await new FoundationStatusService(Directory.GetCurrentDirectory())
                .GetStatusAsync("foundation/status", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<FoundationServiceAuthDiagnosticsReport?> ReadFoundationServiceAuthDiagnosticsAsync(
        CancellationToken cancellationToken)
    {
        var path = Path.Combine("service", "service-auth-diagnostics.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<FoundationServiceAuthDiagnosticsReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<FoundationServiceDeploymentProfileGateReport?> ReadFoundationServiceDeploymentProfileGateAsync(
        CancellationToken cancellationToken)
    {
        var path = Path.Combine("service", "service-deployment-profile-gate.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<FoundationServiceDeploymentProfileGateReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<HostedServiceSmokeReport?> ReadHostedServiceSmokeReportAsync(
        CancellationToken cancellationToken)
    {
        var path = Path.Combine("service", "hosted", "service-hosted-deployment-smoke.json");
        if (!File.Exists(path))
        {
            path = Path.Combine("service", "hosted", "service-readonly-runtime-smoke.json");
        }

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<HostedServiceSmokeReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<ServiceFoundationFreezeReport?> ReadServiceFoundationFreezeReportAsync(
        CancellationToken cancellationToken)
    {
        var path = Path.Combine("service", "service-foundation-freeze-gate.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<ServiceFoundationFreezeReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
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

    public Task<VectorReindexReportQueryResponse> GetServiceVectorReindexReportsAsync(
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetVectorReindexReportsAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            take,
            cancellationToken);
    }

    public Task<VectorReindexResult> GetServiceVectorReindexReportAsync(
        string reportId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetVectorReindexReportAsync(reportId, cancellationToken);
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

    public async Task<ServicePlanningSnapshot> GetServicePlanningSnapshotAsync(
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetServiceClient()
            .GetPlanningSnapshotAsync(
                _state.WorkspaceId,
                _state.CollectionId,
                sessionId,
                cancellationToken)
            .ConfigureAwait(false);

        return new ServicePlanningSnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            Snapshot = snapshot
        };
    }

    public async Task<ServicePlanningProposalSnapshot> ProposeServiceRetrievalPlanAsync(
        string currentInput,
        string? sessionId = null,
        string? mode = null,
        CancellationToken cancellationToken = default)
    {
        var proposal = await GetServiceClient()
            .ProposeRetrievalPlanAsync(
                _state.WorkspaceId,
                _state.CollectionId,
                sessionId,
                currentInput,
                mode,
                cancellationToken)
            .ConfigureAwait(false);

        return new ServicePlanningProposalSnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            CurrentInput = currentInput,
            Proposal = proposal
        };
    }

    public async Task<ServiceRankerShadowDebugSnapshot> DebugServiceLifecycleAwareRankerAsync(
        string query,
        string? mode = null,
        IReadOnlyList<string>? candidateIds = null,
        bool includeLifecycleDetails = true,
        CancellationToken cancellationToken = default)
    {
        var client = GetServiceClient();
        var response = await client
            .DebugLifecycleAwareRankerAsync(
                _state.WorkspaceId,
                _state.CollectionId,
                query,
                mode,
                candidateIds,
                includeLifecycleDetails,
                cancellationToken)
            .ConfigureAwait(false);
        var recentTraces = await client
            .GetRankerShadowTracesAsync(
                _state.WorkspaceId,
                _state.CollectionId,
                take: 50,
                cancellationToken)
            .ConfigureAwait(false);
        var qualitySummary = new RankerShadowTraceQualityReportBuilder()
            .Build(recentTraces, _state.WorkspaceId, _state.CollectionId);

        return new ServiceRankerShadowDebugSnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            Response = response,
            TraceQualitySummary = qualitySummary,
            RecentShadowTraces = recentTraces.Take(5).ToArray()
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
