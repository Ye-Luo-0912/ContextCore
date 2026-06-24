using System.Net;
using System.Net.Http.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Client;

/// <summary>
/// ContextCore HTTP API 的轻量客户端封装，供外部系统调用服务入口而不是直接引用 Core/Storage。
/// </summary>
public sealed class ContextCoreClient
{
    private readonly HttpClient _http;

    public ContextCoreClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<RuntimeStatusResponse> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        return await GetRequiredAsync<RuntimeStatusResponse>("api/status", cancellationToken).ConfigureAwait(false);
    }

    public async Task<RuntimeReadinessResponse> GetReadinessAsync(CancellationToken cancellationToken = default)
    {
        return await GetRequiredAsync<RuntimeReadinessResponse>("api/health/ready", cancellationToken).ConfigureAwait(false);
    }

    public async Task<RuntimeReadinessResponse> GetDeepStatusAsync(
        bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        var path = refresh ? "api/status/deep?refresh=true" : "api/status/deep";
        return await GetRequiredAsync<RuntimeReadinessResponse>(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RuntimeSnapshotResponse> GetRuntimeSnapshotAsync(
        bool includeDeep = false,
        bool refreshDeep = false,
        CancellationToken cancellationToken = default)
    {
        var statusTask = GetStatusAsync(cancellationToken);
        var readinessTask = GetReadinessAsync(cancellationToken);
        Task<RuntimeReadinessResponse?> deepTask = includeDeep
            ? GetOptionalDeepStatusAsync(refreshDeep, cancellationToken)
            : Task.FromResult<RuntimeReadinessResponse?>(null);

        await Task.WhenAll(statusTask, readinessTask, deepTask).ConfigureAwait(false);

        return new RuntimeSnapshotResponse
        {
            Status = await statusTask.ConfigureAwait(false),
            Readiness = await readinessTask.ConfigureAwait(false),
            DeepStatus = await deepTask.ConfigureAwait(false)
        };
    }

    private async Task<RuntimeReadinessResponse?> GetOptionalDeepStatusAsync(
        bool refresh,
        CancellationToken cancellationToken)
    {
        return await GetDeepStatusAsync(refresh, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextInputIngestionResult> IngestAsync(
        ContextInputCommand command,
        CancellationToken cancellationToken = default)
    {
        return await PostRequiredAsync<ContextInputCommand, ContextInputIngestionResult>(
            "api/context/ingest",
            command,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextItem> IngestAsync(ContextItem item, CancellationToken cancellationToken = default)
    {
        var result = await PostRequiredAsync<ContextItem, ContextInputIngestionResult>(
            "api/context/ingest",
            item,
            cancellationToken).ConfigureAwait(false);
        return result.Item;
    }

    public async Task<ContextItem> GetContextAsync(
        string id,
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);

        var path = $"api/context/{Escape(id)}?workspaceId={Escape(workspaceId)}&collectionId={Escape(collectionId)}";
        return await GetRequiredAsync<ContextItem>(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContextItem>> QueryContextAsync(ContextQuery query, CancellationToken cancellationToken = default)
    {
        return await PostRequiredAsync<ContextQuery, IReadOnlyList<ContextItem>>("api/context/query", query, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextQueryResponse> QueryContextAsync(
        ContextQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        var items = await PostRequiredAsync<ContextQueryRequest, IReadOnlyList<ContextItem>>(
            "api/context/query",
            request,
            cancellationToken).ConfigureAwait(false);
        return new ContextQueryResponse
        {
            Items = items,
            Count = items.Count
        };
    }

    public async Task<ContextPlanningSnapshot> GetPlanningSnapshotAsync(
        string workspaceId,
        string? collectionId = null,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var parts = new List<string> { $"workspaceId={Escape(workspaceId)}" };
        if (!string.IsNullOrWhiteSpace(collectionId)) parts.Add($"collectionId={Escape(collectionId)}");
        if (!string.IsNullOrWhiteSpace(sessionId)) parts.Add($"sessionId={Escape(sessionId)}");

        return await GetRequiredAsync<ContextPlanningSnapshot>(
            $"api/context/planning/snapshot?{string.Join('&', parts)}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<VectorIndexStatusResponse> GetVectorStatusAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);

        var path = $"api/vector/status?workspaceId={Escape(workspaceId)}&collectionId={Escape(collectionId)}";
        return await GetRequiredAsync<VectorIndexStatusResponse>(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VectorIndexDiagnosticsReport> GetVectorDiagnosticsAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);

        var path = $"api/vector/diagnostics?workspaceId={Escape(workspaceId)}&collectionId={Escape(collectionId)}";
        return await GetRequiredAsync<VectorIndexDiagnosticsReport>(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VectorReindexPreviewResponse> PreviewVectorReindexAsync(
        VectorReindexPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CollectionId);

        return await PostRequiredAsync<VectorReindexPreviewRequest, VectorReindexPreviewResponse>(
            "api/vector/reindex-preview",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<VectorQueryPreviewResult> PreviewVectorQueryAsync(
        VectorQueryPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CollectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.QueryText);

        return await PostRequiredAsync<VectorQueryPreviewRequest, VectorQueryPreviewResult>(
            "api/vector/query-preview",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<VectorReindexPlan> CreateVectorReindexPlanAsync(
        VectorReindexRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CollectionId);

        return await PostRequiredAsync<VectorReindexRequest, VectorReindexPlan>(
            "api/vector/reindex-plan",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<VectorReindexSubmitResponse> SubmitVectorReindexAsync(
        VectorReindexRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CollectionId);

        return await PostRequiredAsync<VectorReindexRequest, VectorReindexSubmitResponse>(
            "api/vector/reindex-submit",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<VectorReindexReportQueryResponse> GetVectorReindexReportsAsync(
        string workspaceId,
        string collectionId,
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);

        var path = $"api/vector/reindex-reports?workspaceId={Escape(workspaceId)}&collectionId={Escape(collectionId)}&take={take}";
        return await GetRequiredAsync<VectorReindexReportQueryResponse>(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VectorReindexResult> GetVectorReindexReportAsync(
        string reportId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportId);

        return await GetRequiredAsync<VectorReindexResult>(
            $"api/vector/reindex-reports/{Escape(reportId)}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<VectorLifecycleMetadataReviewCandidateGenerationResult> GenerateVectorLifecycleMetadataReviewCandidatesAsync(
        VectorLifecycleMetadataReviewCandidateGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CollectionId);

        return await PostRequiredAsync<VectorLifecycleMetadataReviewCandidateGenerationRequest, VectorLifecycleMetadataReviewCandidateGenerationResult>(
            "api/vector/lifecycle-metadata/review-candidates/generate",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<VectorLifecycleMetadataReviewCandidate>> GetVectorLifecycleMetadataReviewCandidatesAsync(
        string workspaceId,
        string? collectionId = null,
        string? status = null,
        string? layer = null,
        string? itemKind = null,
        string? mustHitItemId = null,
        string? sourceEvalSet = null,
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var parts = new List<string>
        {
            $"workspaceId={Escape(workspaceId)}",
            $"limit={limit}",
            $"offset={offset}"
        };
        if (!string.IsNullOrWhiteSpace(collectionId)) parts.Add($"collectionId={Escape(collectionId)}");
        if (!string.IsNullOrWhiteSpace(status)) parts.Add($"status={Escape(status)}");
        if (!string.IsNullOrWhiteSpace(layer)) parts.Add($"layer={Escape(layer)}");
        if (!string.IsNullOrWhiteSpace(itemKind)) parts.Add($"itemKind={Escape(itemKind)}");
        if (!string.IsNullOrWhiteSpace(mustHitItemId)) parts.Add($"mustHitItemId={Escape(mustHitItemId)}");
        if (!string.IsNullOrWhiteSpace(sourceEvalSet)) parts.Add($"sourceEvalSet={Escape(sourceEvalSet)}");

        return await GetRequiredAsync<IReadOnlyList<VectorLifecycleMetadataReviewCandidate>>(
            $"api/vector/lifecycle-metadata/review-candidates?{string.Join('&', parts)}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<VectorLifecycleMetadataReviewCandidate> GetVectorLifecycleMetadataReviewCandidateAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);

        return await GetRequiredAsync<VectorLifecycleMetadataReviewCandidate>(
            $"api/vector/lifecycle-metadata/review-candidates/{Escape(candidateId)}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<VectorLifecycleMetadataReviewCandidateExplanation> ExplainVectorLifecycleMetadataReviewCandidateAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);

        return await GetRequiredAsync<VectorLifecycleMetadataReviewCandidateExplanation>(
            $"api/vector/lifecycle-metadata/review-candidates/{Escape(candidateId)}/explain",
            cancellationToken).ConfigureAwait(false);
    }

    public Task<VectorLifecycleMetadataReviewResult> ApproveVectorLifecycleMetadataReviewCandidateAsync(
        string candidateId,
        VectorLifecycleMetadataReviewRequest request,
        CancellationToken cancellationToken = default)
        => ReviewVectorLifecycleMetadataReviewCandidateAsync(candidateId, "approve", request, cancellationToken);

    public Task<VectorLifecycleMetadataReviewResult> RejectVectorLifecycleMetadataReviewCandidateAsync(
        string candidateId,
        VectorLifecycleMetadataReviewRequest request,
        CancellationToken cancellationToken = default)
        => ReviewVectorLifecycleMetadataReviewCandidateAsync(candidateId, "reject", request, cancellationToken);

    public Task<VectorLifecycleMetadataReviewResult> NeedsEvidenceVectorLifecycleMetadataReviewCandidateAsync(
        string candidateId,
        VectorLifecycleMetadataReviewRequest request,
        CancellationToken cancellationToken = default)
        => ReviewVectorLifecycleMetadataReviewCandidateAsync(candidateId, "needs-evidence", request, cancellationToken);

    public Task<VectorLifecycleMetadataReviewResult> SupersedeVectorLifecycleMetadataReviewCandidateAsync(
        string candidateId,
        VectorLifecycleMetadataReviewRequest request,
        CancellationToken cancellationToken = default)
        => ReviewVectorLifecycleMetadataReviewCandidateAsync(candidateId, "supersede", request, cancellationToken);

    public async Task<IReadOnlyList<VectorLifecycleMetadataReviewRecord>> GetVectorLifecycleMetadataReviewHistoryAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);

        return await GetRequiredAsync<IReadOnlyList<VectorLifecycleMetadataReviewRecord>>(
            $"api/vector/lifecycle-metadata/review-candidates/{Escape(candidateId)}/reviews",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<VectorLifecycleSidecarMetadataEntry>> GetVectorLifecycleMetadataSidecarAsync(
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        var path = $"api/vector/lifecycle-metadata/sidecar?workspaceId={Escape(workspaceId)}"
            + (string.IsNullOrWhiteSpace(collectionId) ? string.Empty : $"&collectionId={Escape(collectionId)}");
        return await GetRequiredAsync<IReadOnlyList<VectorLifecycleSidecarMetadataEntry>>(path, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<VectorLifecycleMetadataReviewResult> ReviewVectorLifecycleMetadataReviewCandidateAsync(
        string candidateId,
        string route,
        VectorLifecycleMetadataReviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        ArgumentNullException.ThrowIfNull(request);

        return await PostRequiredAsync<VectorLifecycleMetadataReviewRequest, VectorLifecycleMetadataReviewResult>(
            $"api/vector/lifecycle-metadata/review-candidates/{Escape(candidateId)}/{route}",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<RetrievalPlanProposal> ProposeRetrievalPlanAsync(
        ContextPlanningProposalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);

        return await PostRequiredAsync<ContextPlanningProposalRequest, RetrievalPlanProposal>(
            "api/context/planning/propose",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    public Task<RetrievalPlanProposal> ProposeRetrievalPlanAsync(
        string workspaceId,
        string? collectionId,
        string? sessionId,
        string currentInput,
        string? mode = null,
        CancellationToken cancellationToken = default)
    {
        return ProposeRetrievalPlanAsync(
            new ContextPlanningProposalRequest
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                SessionId = sessionId,
                CurrentInput = currentInput,
                Mode = mode
            },
            cancellationToken);
    }

    public async Task<LifecycleAwareRankerShadowDebugResponse> DebugLifecycleAwareRankerAsync(
        LifecycleAwareRankerShadowDebugRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CollectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Query);

        return await PostRequiredAsync<LifecycleAwareRankerShadowDebugRequest, LifecycleAwareRankerShadowDebugResponse>(
            "api/retrieval/ranker-shadow/debug",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    public Task<LifecycleAwareRankerShadowDebugResponse> DebugLifecycleAwareRankerAsync(
        string workspaceId,
        string collectionId,
        string query,
        string? mode = null,
        IReadOnlyList<string>? candidateIds = null,
        bool includeLifecycleDetails = true,
        CancellationToken cancellationToken = default)
    {
        return DebugLifecycleAwareRankerAsync(
            new LifecycleAwareRankerShadowDebugRequest
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Query = query,
                Mode = string.IsNullOrWhiteSpace(mode) ? "ChatMode" : mode,
                CandidateIds = candidateIds ?? Array.Empty<string>(),
                IncludeLifecycleDetails = includeLifecycleDetails
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<LifecycleAwareRankerShadowTraceRecord>> GetRankerShadowTracesAsync(
        string workspaceId,
        string collectionId,
        int take = 10,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);

        var path = $"api/learning/ranker-shadow/traces?workspaceId={Escape(workspaceId)}&collectionId={Escape(collectionId)}&take={take}";
        return await GetRequiredAsync<IReadOnlyList<LifecycleAwareRankerShadowTraceRecord>>(path, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<string> ExportRankerShadowTracesAsync(
        string workspaceId,
        string collectionId,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);

        var path = $"api/learning/ranker-shadow/traces?workspaceId={Escape(workspaceId)}&collectionId={Escape(collectionId)}&take={take}&format=jsonl";
        return await GetRequiredStringAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GraphExpansionShadowTraceRecord>> GetGraphExpansionShadowTracesAsync(
        string workspaceId,
        string collectionId,
        int take = 10,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);

        var path = $"api/learning/graph-expansion-shadow/traces?workspaceId={Escape(workspaceId)}&collectionId={Escape(collectionId)}&take={take}";
        return await GetRequiredAsync<IReadOnlyList<GraphExpansionShadowTraceRecord>>(path, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<string> ExportGraphExpansionShadowTracesAsync(
        string workspaceId,
        string collectionId,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);

        var path = $"api/learning/graph-expansion-shadow/traces?workspaceId={Escape(workspaceId)}&collectionId={Escape(collectionId)}&take={take}&format=jsonl";
        return await GetRequiredStringAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RouterIntentShadowTrace>> GetRouterShadowTracesAsync(
        string workspaceId,
        string collectionId,
        int take = 10,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);

        var path = $"api/learning/router-shadow/traces?workspaceId={Escape(workspaceId)}&collectionId={Escape(collectionId)}&take={take}";
        return await GetRequiredAsync<IReadOnlyList<RouterIntentShadowTrace>>(path, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<string> ExportRouterShadowTracesAsync(
        string workspaceId,
        string collectionId,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);

        var path = $"api/learning/router-shadow/traces?workspaceId={Escape(workspaceId)}&collectionId={Escape(collectionId)}&take={take}&format=jsonl";
        return await GetRequiredStringAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextMemoryItem> AddMemoryAsync(ContextMemoryItem item, CancellationToken cancellationToken = default)
    {
        return await PostRequiredAsync<ContextMemoryItem, ContextMemoryItem>("api/memory/add", item, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkingMemoryItem> AddWorkingMemoryItemAsync(
        WorkingMemoryItem item,
        CancellationToken cancellationToken = default)
    {
        return await PostRequiredAsync<WorkingMemoryItem, WorkingMemoryItem>("api/memory/working/add", item, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WorkingMemoryItem>> GetRecentWorkingMemoryAsync(
        string workspaceId,
        string collectionId,
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);

        var path = $"api/memory/working/recent?workspaceId={Escape(workspaceId)}&collectionId={Escape(collectionId)}&take={take}";
        return await GetRequiredAsync<IReadOnlyList<WorkingMemoryItem>>(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearWorkingMemoryAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);

        await PostNoContentAsync(
            "api/memory/working/clear",
            new ContextCoreWorkingMemoryScopeRequest
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkingMemoryActiveContext?> GetWorkingMemoryActiveContextAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);

        var path = $"api/memory/working/active-context?workspaceId={Escape(workspaceId)}&collectionId={Escape(collectionId)}";
        return await GetOptionalAsync<WorkingMemoryActiveContext>(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkingMemoryActiveContext> SetWorkingMemoryActiveContextAsync(
        WorkingMemoryActiveContext activeContext,
        CancellationToken cancellationToken = default)
    {
        return await PostRequiredAsync<WorkingMemoryActiveContext, WorkingMemoryActiveContext>(
            "api/memory/working/active-context",
            activeContext,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkingMemoryCurrentTask?> GetWorkingMemoryCurrentTaskAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);

        var path = $"api/memory/working/current-task?workspaceId={Escape(workspaceId)}&collectionId={Escape(collectionId)}";
        return await GetOptionalAsync<WorkingMemoryCurrentTask>(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkingMemoryCurrentTask> SetWorkingMemoryCurrentTaskAsync(
        WorkingMemoryCurrentTask currentTask,
        CancellationToken cancellationToken = default)
    {
        return await PostRequiredAsync<WorkingMemoryCurrentTask, WorkingMemoryCurrentTask>(
            "api/memory/working/current-task",
            currentTask,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContextMemoryItem>> QueryMemoryAsync(
        ContextMemoryQuery query,
        CancellationToken cancellationToken = default)
    {
        return await PostRequiredAsync<ContextMemoryQuery, IReadOnlyList<ContextMemoryItem>>("api/memory/query", query, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CandidateMemorySnapshot> GetCandidateMemorySnapshotAsync(
        string workspaceId,
        string? collectionId = null,
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var parts = new List<string>
        {
            $"workspaceId={Escape(workspaceId)}",
            $"take={take}"
        };
        if (!string.IsNullOrWhiteSpace(collectionId))
        {
            parts.Add($"collectionId={Escape(collectionId)}");
        }

        return await GetRequiredAsync<CandidateMemorySnapshot>(
            $"api/memory/candidates/snapshot?{string.Join('&', parts)}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<CandidateMemoryRecord> GetCandidateMemoryAsync(
        string candidateId,
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var parts = new List<string> { $"workspaceId={Escape(workspaceId)}" };
        if (!string.IsNullOrWhiteSpace(collectionId))
        {
            parts.Add($"collectionId={Escape(collectionId)}");
        }

        return await GetRequiredAsync<CandidateMemoryRecord>(
            $"api/memory/candidates/{Escape(candidateId)}?{string.Join('&', parts)}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<CandidateMemoryExplanation> ExplainCandidateMemoryAsync(
        string candidateId,
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var parts = new List<string> { $"workspaceId={Escape(workspaceId)}" };
        if (!string.IsNullOrWhiteSpace(collectionId))
        {
            parts.Add($"collectionId={Escape(collectionId)}");
        }

        return await GetRequiredAsync<CandidateMemoryExplanation>(
            $"api/memory/candidates/{Escape(candidateId)}/explain?{string.Join('&', parts)}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<CandidateMemoryDiagnosticsReport> GetCandidateMemoryDiagnosticsAsync(
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var parts = new List<string> { $"workspaceId={Escape(workspaceId)}" };
        if (!string.IsNullOrWhiteSpace(collectionId))
        {
            parts.Add($"collectionId={Escape(collectionId)}");
        }

        return await GetRequiredAsync<CandidateMemoryDiagnosticsReport>(
            $"api/memory/candidates/diagnostics?{string.Join('&', parts)}",
            cancellationToken).ConfigureAwait(false);
    }

    public Task<CandidateMemoryReviewResult> MarkCandidateMemoryReadyForStableReviewAsync(
        string candidateId,
        CandidateMemoryReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return PostCandidateMemoryReviewAsync(candidateId, "ready-for-stable-review", request, cancellationToken);
    }

    public Task<CandidateMemoryReviewResult> MarkCandidateMemoryNeedsMoreEvidenceAsync(
        string candidateId,
        CandidateMemoryReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return PostCandidateMemoryReviewAsync(candidateId, "needs-more-evidence", request, cancellationToken);
    }

    public Task<CandidateMemoryReviewResult> RejectCandidateMemoryAsync(
        string candidateId,
        CandidateMemoryReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return PostCandidateMemoryReviewAsync(candidateId, "reject", request, cancellationToken);
    }

    public Task<CandidateMemoryReviewResult> ExpireCandidateMemoryAsync(
        string candidateId,
        CandidateMemoryReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return PostCandidateMemoryReviewAsync(candidateId, "expire", request, cancellationToken);
    }

    public Task<CandidateMemoryReviewResult> SupersedeCandidateMemoryAsync(
        string candidateId,
        CandidateMemoryReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return PostCandidateMemoryReviewAsync(candidateId, "supersede", request, cancellationToken);
    }

    public async Task<IReadOnlyList<CandidateMemoryReviewRecord>> GetCandidateMemoryReviewsAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);

        return await GetRequiredAsync<IReadOnlyList<CandidateMemoryReviewRecord>>(
            $"api/memory/candidates/{Escape(candidateId)}/reviews",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<CandidateMemoryReviewResult> PostCandidateMemoryReviewAsync(
        string candidateId,
        string actionPath,
        CandidateMemoryReviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionPath);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);

        var parts = new List<string> { $"workspaceId={Escape(request.WorkspaceId)}" };
        if (!string.IsNullOrWhiteSpace(request.CollectionId))
        {
            parts.Add($"collectionId={Escape(request.CollectionId)}");
        }

        return await PostRequiredAsync<CandidateMemoryReviewRequest, CandidateMemoryReviewResult>(
            $"api/memory/candidates/{Escape(candidateId)}/{actionPath}?{string.Join('&', parts)}",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContextGlobalItem>> QueryGlobalContextAsync(
        string workspaceId,
        string? collectionId = null,
        ContextScope? scope = null,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var parts = new List<string> { $"workspaceId={Escape(workspaceId)}" };
        if (!string.IsNullOrWhiteSpace(collectionId))
        {
            parts.Add($"collectionId={Escape(collectionId)}");
        }

        if (scope is not null)
        {
            parts.Add($"scope={scope}");
        }

        parts.Add($"take={take}");
        return await GetRequiredAsync<IReadOnlyList<ContextGlobalItem>>($"api/memory/global?{string.Join('&', parts)}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<StableMemorySnapshot> GetStableMemorySnapshotAsync(
        string workspaceId,
        string? collectionId = null,
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var parts = new List<string>
        {
            $"workspaceId={Escape(workspaceId)}",
            $"take={take}"
        };
        if (!string.IsNullOrWhiteSpace(collectionId))
        {
            parts.Add($"collectionId={Escape(collectionId)}");
        }

        return await GetRequiredAsync<StableMemorySnapshot>(
            $"api/memory/stable/snapshot?{string.Join('&', parts)}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<StableMemoryDiagnosticsReport> GetStableMemoryDiagnosticsAsync(
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var parts = new List<string> { $"workspaceId={Escape(workspaceId)}" };
        if (!string.IsNullOrWhiteSpace(collectionId))
        {
            parts.Add($"collectionId={Escape(collectionId)}");
        }

        return await GetRequiredAsync<StableMemoryDiagnosticsReport>(
            $"api/memory/stable/diagnostics?{string.Join('&', parts)}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<StableMemoryExplanation> ExplainStableMemoryAsync(
        string itemId,
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var parts = new List<string> { $"workspaceId={Escape(workspaceId)}" };
        if (!string.IsNullOrWhiteSpace(collectionId))
        {
            parts.Add($"collectionId={Escape(collectionId)}");
        }

        return await GetRequiredAsync<StableMemoryExplanation>(
            $"api/memory/stable/{Escape(itemId)}/explain?{string.Join('&', parts)}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<StableReplacementChainResponse> GetStableReplacementChainAsync(
        string itemId,
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var parts = new List<string> { $"workspaceId={Escape(workspaceId)}" };
        if (!string.IsNullOrWhiteSpace(collectionId))
        {
            parts.Add($"collectionId={Escape(collectionId)}");
        }

        return await GetRequiredAsync<StableReplacementChainResponse>(
            $"api/memory/stable/{Escape(itemId)}/replacement-chain?{string.Join('&', parts)}",
            cancellationToken).ConfigureAwait(false);
    }

    public Task<StableLifecycleReviewResult> DeprecateStableMemoryAsync(
        string itemId,
        StableLifecycleReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return PostStableLifecycleReviewAsync(itemId, "deprecate", request, cancellationToken);
    }

    public Task<StableLifecycleReviewResult> SupersedeStableMemoryAsync(
        string itemId,
        StableLifecycleReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return PostStableLifecycleReviewAsync(itemId, "supersede", request, cancellationToken);
    }

    public Task<StableLifecycleReviewResult> RejectStableMemoryAsync(
        string itemId,
        StableLifecycleReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return PostStableLifecycleReviewAsync(itemId, "reject", request, cancellationToken);
    }

    public async Task<IReadOnlyList<StableLifecycleReviewRecord>> GetStableMemoryReviewsAsync(
        string itemId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);

        return await GetRequiredAsync<IReadOnlyList<StableLifecycleReviewRecord>>(
            $"api/memory/stable/{Escape(itemId)}/reviews",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<StableLifecycleReviewResult> PostStableLifecycleReviewAsync(
        string itemId,
        string actionPath,
        StableLifecycleReviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionPath);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);

        var parts = new List<string> { $"workspaceId={Escape(request.WorkspaceId)}" };
        if (!string.IsNullOrWhiteSpace(request.CollectionId))
        {
            parts.Add($"collectionId={Escape(request.CollectionId)}");
        }

        return await PostRequiredAsync<StableLifecycleReviewRequest, StableLifecycleReviewResult>(
            $"api/memory/stable/{Escape(itemId)}/{actionPath}?{string.Join('&', parts)}",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ShortTermRawEvent>> GetShortTermRawEventsAsync(
        string workspaceId,
        string? collectionId = null,
        string? sessionId = null,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        var parts = new List<string> { $"workspaceId={Escape(workspaceId)}", $"take={take}" };
        if (!string.IsNullOrWhiteSpace(collectionId)) parts.Add($"collectionId={Escape(collectionId)}");
        if (!string.IsNullOrWhiteSpace(sessionId)) parts.Add($"sessionId={Escape(sessionId)}");
        return await GetRequiredAsync<IReadOnlyList<ShortTermRawEvent>>($"api/memory/short-term/raw?{string.Join('&', parts)}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ShortTermWorkingItem>> GetShortTermWorkingItemsAsync(
        string workspaceId,
        string? collectionId = null,
        string? sessionId = null,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        var parts = new List<string> { $"workspaceId={Escape(workspaceId)}", $"take={take}" };
        if (!string.IsNullOrWhiteSpace(collectionId)) parts.Add($"collectionId={Escape(collectionId)}");
        if (!string.IsNullOrWhiteSpace(sessionId)) parts.Add($"sessionId={Escape(sessionId)}");
        return await GetRequiredAsync<IReadOnlyList<ShortTermWorkingItem>>($"api/memory/short-term/working?{string.Join('&', parts)}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<ShortTermMemorySummary> GetShortTermSummaryAsync(
        string workspaceId,
        string? collectionId = null,
        string? sessionId = null,
        int latestRawTake = 10,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        var parts = new List<string> { $"workspaceId={Escape(workspaceId)}", $"latestRawTake={latestRawTake}" };
        if (!string.IsNullOrWhiteSpace(collectionId)) parts.Add($"collectionId={Escape(collectionId)}");
        if (!string.IsNullOrWhiteSpace(sessionId)) parts.Add($"sessionId={Escape(sessionId)}");
        return await GetRequiredAsync<ShortTermMemorySummary>($"api/memory/short-term/summary?{string.Join('&', parts)}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<ShortTermMemoryCompactionResult> CompactShortTermMemoryAsync(
        ShortTermMemoryCompactionRequest request,
        CancellationToken cancellationToken = default)
    {
        return await PostRequiredAsync<ShortTermMemoryCompactionRequest, ShortTermMemoryCompactionResult>(
            "api/memory/short-term/compact",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ShortTermArchiveSummary> GetShortTermArchiveSummaryAsync(
        string workspaceId,
        string? collectionId = null,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        var parts = new List<string> { $"workspaceId={Escape(workspaceId)}" };
        if (!string.IsNullOrWhiteSpace(collectionId)) parts.Add($"collectionId={Escape(collectionId)}");
        if (!string.IsNullOrWhiteSpace(sessionId)) parts.Add($"sessionId={Escape(sessionId)}");
        return await GetRequiredAsync<ShortTermArchiveSummary>($"api/memory/short-term/archive/summary?{string.Join('&', parts)}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<ShortTermArchiveItemsResponse> GetShortTermArchiveItemsAsync(
        string workspaceId,
        string? collectionId = null,
        string? sessionId = null,
        string? kind = null,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        var parts = new List<string>
        {
            $"workspaceId={Escape(workspaceId)}",
            $"limit={limit}"
        };
        if (!string.IsNullOrWhiteSpace(collectionId)) parts.Add($"collectionId={Escape(collectionId)}");
        if (!string.IsNullOrWhiteSpace(sessionId)) parts.Add($"sessionId={Escape(sessionId)}");
        if (!string.IsNullOrWhiteSpace(kind)) parts.Add($"kind={Escape(kind)}");
        return await GetRequiredAsync<ShortTermArchiveItemsResponse>($"api/memory/short-term/archive/items?{string.Join('&', parts)}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ShortTermCompactionRun>> GetShortTermCompactionRunsAsync(
        string? workspaceId = null,
        string? collectionId = null,
        string? sessionId = null,
        string? trigger = null,
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        var parts = new List<string> { $"take={take}" };
        if (!string.IsNullOrWhiteSpace(workspaceId)) parts.Add($"workspaceId={Escape(workspaceId)}");
        if (!string.IsNullOrWhiteSpace(collectionId)) parts.Add($"collectionId={Escape(collectionId)}");
        if (!string.IsNullOrWhiteSpace(sessionId)) parts.Add($"sessionId={Escape(sessionId)}");
        if (!string.IsNullOrWhiteSpace(trigger)) parts.Add($"trigger={Escape(trigger)}");
        return await GetRequiredAsync<IReadOnlyList<ShortTermCompactionRun>>($"api/memory/short-term/compact/runs?{string.Join('&', parts)}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<ShortTermCompactionRun> GetShortTermCompactionRunAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        return await GetRequiredAsync<ShortTermCompactionRun>($"api/memory/short-term/compact/runs/{Escape(runId)}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ShortTermPromotionCandidate>> GenerateShortTermPromotionCandidatesAsync(
        ShortTermPromotionCandidateGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        return await PostRequiredAsync<ShortTermPromotionCandidateGenerationRequest, IReadOnlyList<ShortTermPromotionCandidate>>(
            "api/memory/short-term/promotion/candidates/generate",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ShortTermPromotionCandidate>> GetShortTermPromotionCandidatesAsync(
        string workspaceId,
        string? collectionId = null,
        string? sessionId = null,
        PromotionCandidateStatus? status = null,
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        return await QueryShortTermPromotionCandidatesAsync(
            workspaceId,
            collectionId,
            sessionId,
            status,
            kind: null,
            suggestedTargetLayer: null,
            minConfidence: null,
            minImportance: null,
            limit: take,
            offset: 0,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ShortTermPromotionCandidate> GetShortTermPromotionCandidateAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        return await GetRequiredAsync<ShortTermPromotionCandidate>($"api/memory/short-term/promotion/candidates/{Escape(candidateId)}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ShortTermPromotionCandidate>> QueryShortTermPromotionCandidatesAsync(
        string workspaceId,
        string? collectionId = null,
        string? sessionId = null,
        PromotionCandidateStatus? status = null,
        string? kind = null,
        string? suggestedTargetLayer = null,
        double? minConfidence = null,
        double? minImportance = null,
        int limit = 20,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        var parts = new List<string>
        {
            $"workspaceId={Escape(workspaceId)}",
            $"limit={limit}",
            $"offset={offset}"
        };
        if (!string.IsNullOrWhiteSpace(collectionId)) parts.Add($"collectionId={Escape(collectionId)}");
        if (!string.IsNullOrWhiteSpace(sessionId)) parts.Add($"sessionId={Escape(sessionId)}");
        if (status is not null) parts.Add($"status={status}");
        if (!string.IsNullOrWhiteSpace(kind)) parts.Add($"kind={Escape(kind)}");
        if (!string.IsNullOrWhiteSpace(suggestedTargetLayer)) parts.Add($"suggestedTargetLayer={Escape(suggestedTargetLayer)}");
        if (minConfidence is not null) parts.Add($"minConfidence={minConfidence.Value}");
        if (minImportance is not null) parts.Add($"minImportance={minImportance.Value}");
        return await GetRequiredAsync<IReadOnlyList<ShortTermPromotionCandidate>>($"api/memory/short-term/promotion/candidates?{string.Join('&', parts)}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<ShortTermPromotionCandidateExplanation> ExplainShortTermPromotionCandidateAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        return await GetRequiredAsync<ShortTermPromotionCandidateExplanation>(
            $"api/memory/short-term/promotion/candidates/{Escape(candidateId)}/explain",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ReviewPromotionCandidateResponse> AcceptShortTermPromotionCandidateAsync(
        string candidateId,
        ReviewPromotionCandidateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        return await PostRequiredAsync<ReviewPromotionCandidateRequest, ReviewPromotionCandidateResponse>(
            $"api/memory/short-term/promotion/candidates/{Escape(candidateId)}/accept",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<PromotionCandidateReviewResult> AcceptShortTermPromotionCandidateAsync(
        string candidateId,
        PromotionCandidateReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        return await PostRequiredAsync<PromotionCandidateReviewRequest, PromotionCandidateReviewResult>(
            $"api/memory/short-term/promotion/candidates/{Escape(candidateId)}/accept",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ReviewPromotionCandidateResponse> RejectShortTermPromotionCandidateAsync(
        string candidateId,
        ReviewPromotionCandidateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        return await PostRequiredAsync<ReviewPromotionCandidateRequest, ReviewPromotionCandidateResponse>(
            $"api/memory/short-term/promotion/candidates/{Escape(candidateId)}/reject",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<PromotionCandidateReviewResult> RejectShortTermPromotionCandidateAsync(
        string candidateId,
        PromotionCandidateReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        return await PostRequiredAsync<PromotionCandidateReviewRequest, PromotionCandidateReviewResult>(
            $"api/memory/short-term/promotion/candidates/{Escape(candidateId)}/reject",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ReviewPromotionCandidateResponse> ExpireShortTermPromotionCandidateAsync(
        string candidateId,
        ReviewPromotionCandidateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        return await PostRequiredAsync<ReviewPromotionCandidateRequest, ReviewPromotionCandidateResponse>(
            $"api/memory/short-term/promotion/candidates/{Escape(candidateId)}/expire",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PromotionCandidateReviewRecord>> GetShortTermPromotionCandidateReviewsAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        return await GetRequiredAsync<IReadOnlyList<PromotionCandidateReviewRecord>>(
            $"api/memory/short-term/promotion/candidates/{Escape(candidateId)}/reviews",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StableReviewCandidate>> GenerateStableReviewCandidatesAsync(
        StableReviewCandidateGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        return await PostRequiredAsync<StableReviewCandidateGenerationRequest, IReadOnlyList<StableReviewCandidate>>(
            "api/memory/stable-review/candidates/generate",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StableReviewCandidate>> GetStableReviewCandidatesAsync(
        string workspaceId,
        string? collectionId = null,
        string? sessionId = null,
        string? status = null,
        string? validationStatus = null,
        string? kind = null,
        string? suggestedStableTarget = null,
        int limit = 20,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        return await QueryStableReviewCandidatesAsync(new StableReviewCandidateQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            SessionId = sessionId,
            Status = status,
            ValidationStatus = validationStatus,
            Kind = kind,
            SuggestedStableTarget = suggestedStableTarget,
            Limit = limit,
            Offset = offset
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StableReviewCandidate>> QueryStableReviewCandidatesAsync(
        StableReviewCandidateQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.WorkspaceId);
        return await GetRequiredAsync<IReadOnlyList<StableReviewCandidate>>(
            $"api/memory/stable-review/candidates?{BuildStableReviewCandidateQueryString(query)}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<StableReviewCandidate> GetStableReviewCandidateAsync(
        string stableReviewCandidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableReviewCandidateId);
        return await GetRequiredAsync<StableReviewCandidate>(
            $"api/memory/stable-review/candidates/{Escape(stableReviewCandidateId)}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<StableReviewCandidateExplanation> ExplainStableReviewCandidateAsync(
        string stableReviewCandidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableReviewCandidateId);
        return await GetRequiredAsync<StableReviewCandidateExplanation>(
            $"api/memory/stable-review/candidates/{Escape(stableReviewCandidateId)}/explain",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<StableReviewDecisionResult> AcceptStableReviewCandidateAsync(
        string stableReviewCandidateId,
        StableReviewDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableReviewCandidateId);
        return await PostRequiredAsync<StableReviewDecisionRequest, StableReviewDecisionResult>(
            $"api/memory/stable-review/candidates/{Escape(stableReviewCandidateId)}/accept",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<StableReviewDecisionResult> RejectStableReviewCandidateAsync(
        string stableReviewCandidateId,
        StableReviewDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableReviewCandidateId);
        return await PostRequiredAsync<StableReviewDecisionRequest, StableReviewDecisionResult>(
            $"api/memory/stable-review/candidates/{Escape(stableReviewCandidateId)}/reject",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StableReviewRecord>> GetStableReviewCandidateReviewsAsync(
        string stableReviewCandidateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableReviewCandidateId);
        return await GetRequiredAsync<IReadOnlyList<StableReviewRecord>>(
            $"api/memory/stable-review/candidates/{Escape(stableReviewCandidateId)}/reviews",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextProvenanceResponse> GetProvenanceAsync(
        string itemId,
        string? workspaceId = null,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(workspaceId)) parts.Add($"workspaceId={Escape(workspaceId)}");
        if (!string.IsNullOrWhiteSpace(collectionId)) parts.Add($"collectionId={Escape(collectionId)}");
        var path = parts.Count == 0
            ? $"api/provenance/{Escape(itemId)}"
            : $"api/provenance/{Escape(itemId)}?{string.Join('&', parts)}";
        return await GetRequiredAsync<ContextProvenanceResponse>(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContextLearningRecord>> QueryLearningRecordsAsync(
        ContextLearningRecordQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await GetRequiredAsync<IReadOnlyList<ContextLearningRecord>>(
            $"api/learning/records?{BuildLearningRecordQueryString(query)}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PromotionFeedbackSignal>> GetLearningFeedbackAsync(
        string? workspaceId = null,
        string? collectionId = null,
        string? sessionId = null,
        string? candidateId = null,
        string? action = null,
        int limit = 20,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var query = new PromotionFeedbackSignalQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            SessionId = sessionId,
            CandidateId = candidateId,
            Action = action,
            Limit = limit,
            Offset = offset
        };
        return await GetRequiredAsync<IReadOnlyList<PromotionFeedbackSignal>>(
            $"api/learning/feedback?{BuildPromotionFeedbackQueryString(query)}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<LearningFeedbackSubmitResult> SubmitLearningFeedbackAsync(
        LearningFeedbackEvent feedbackEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(feedbackEvent);
        return await SubmitLearningFeedbackAsync(ToLearningFeedbackSubmitRequest(feedbackEvent), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<LearningFeedbackSubmitResult> SubmitLearningFeedbackAsync(
        LearningFeedbackSubmitRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await PostRequiredAsync<LearningFeedbackSubmitRequest, LearningFeedbackSubmitResult>(
            "api/learning/feedback",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LearningFeedbackEvent>> GetLearningFeedbackAsync(
        LearningFeedbackEventQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await GetRequiredAsync<IReadOnlyList<LearningFeedbackEvent>>(
            $"api/learning/feedback?{BuildRuntimeLearningFeedbackQueryString(query)}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<LearningFeedbackSummaryReport> GetLearningFeedbackSummaryAsync(
        LearningFeedbackEventQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await GetRequiredAsync<LearningFeedbackSummaryReport>(
            $"api/learning/feedback/summary?{BuildRuntimeLearningFeedbackQueryString(query, includeRuntimeFlag: false)}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ExportLearningFeedbackAsync(
        LearningFeedbackEventQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await GetRequiredStringAsync(
            $"api/learning/feedback/export?{BuildRuntimeLearningFeedbackQueryString(query, includeRuntimeFlag: false)}",
            cancellationToken).ConfigureAwait(false);
    }

    public Task<LearningFeedbackReviewResult> ApproveLearningFeedbackAsync(
        string feedbackId,
        LearningFeedbackReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return ReviewLearningFeedbackAsync(feedbackId, "approve", request, cancellationToken);
    }

    public Task<LearningFeedbackReviewResult> RejectLearningFeedbackAsync(
        string feedbackId,
        LearningFeedbackReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return ReviewLearningFeedbackAsync(feedbackId, "reject", request, cancellationToken);
    }

    public Task<LearningFeedbackReviewResult> MarkLearningFeedbackNeedsRedactionAsync(
        string feedbackId,
        LearningFeedbackReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return ReviewLearningFeedbackAsync(feedbackId, "needs-redaction", request, cancellationToken);
    }

    public Task<LearningFeedbackReviewResult> MarkLearningFeedbackNeedsEvidenceAsync(
        string feedbackId,
        LearningFeedbackReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return ReviewLearningFeedbackAsync(feedbackId, "needs-evidence", request, cancellationToken);
    }

    public async Task<IReadOnlyList<LearningFeedbackReviewRecord>> GetLearningFeedbackReviewsAsync(
        LearningFeedbackReviewQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await GetRequiredAsync<IReadOnlyList<LearningFeedbackReviewRecord>>(
            $"api/learning/feedback/reviews?{BuildLearningFeedbackReviewQueryString(query)}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<LearningFeedbackReviewSummaryReport> GetLearningFeedbackReviewSummaryAsync(
        LearningFeedbackReviewQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await GetRequiredAsync<LearningFeedbackReviewSummaryReport>(
            $"api/learning/feedback/reviews/summary?{BuildLearningFeedbackReviewQueryString(query)}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<PolicyFeedbackDataset> GetPolicyFeedbackAsync(
        string workspaceId,
        string? collectionId = null,
        string? sessionId = null,
        int limit = 200,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        var path = $"api/learning/policy-feedback?{BuildPolicyFeedbackQueryString(workspaceId, collectionId, sessionId, limit, offset)}";
        return await GetRequiredAsync<PolicyFeedbackDataset>(path, cancellationToken).ConfigureAwait(false);
    }

    private async Task<LearningFeedbackReviewResult> ReviewLearningFeedbackAsync(
        string feedbackId,
        string action,
        LearningFeedbackReviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feedbackId);
        ArgumentNullException.ThrowIfNull(request);
        return await PostRequiredAsync<LearningFeedbackReviewRequest, LearningFeedbackReviewResult>(
            $"api/learning/feedback/{Escape(feedbackId)}/review/{action}",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ExportPolicyFeedbackAsync(
        string workspaceId,
        string? collectionId = null,
        string? sessionId = null,
        int limit = 1000,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        var path = $"api/learning/policy-feedback/export?{BuildPolicyFeedbackQueryString(workspaceId, collectionId, sessionId, limit, offset)}";
        return await GetRequiredStringAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LearningFeatureDataset> GetLearningFeaturesAsync(
        string workspaceId,
        string? collectionId = null,
        string? sessionId = null,
        int limit = 500,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        var path = $"api/learning/features?{BuildPolicyFeedbackQueryString(workspaceId, collectionId, sessionId, limit, offset)}";
        return await GetRequiredAsync<LearningFeatureDataset>(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LearningFeatureExportResult> ExportLearningFeaturesAsync(
        string workspaceId,
        string? collectionId = null,
        string? sessionId = null,
        string? outputDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        var path = $"api/learning/features/export?{BuildLearningFeatureExportQueryString(workspaceId, collectionId, sessionId, outputDirectory)}";
        return await GetRequiredAsync<LearningFeatureExportResult>(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LearningDatasetQualityReport> GetLearningDatasetQualityAsync(
        string? featureDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var path = string.IsNullOrWhiteSpace(featureDirectory)
            ? "api/learning/features/quality"
            : $"api/learning/features/quality?featureDirectory={Escape(featureDirectory)}";
        return await GetRequiredAsync<LearningDatasetQualityReport>(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextLearningRecord> GetLearningRecordAsync(
        string recordId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        return await GetRequiredAsync<ContextLearningRecord>(
            $"api/learning/records/{Escape(recordId)}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContextLearningCase>> QueryLearningCasesAsync(
        ContextLearningCaseQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await GetRequiredAsync<IReadOnlyList<ContextLearningCase>>(
            $"api/learning/cases?{BuildLearningCaseQueryString(query)}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContextLearningCase>> GetLearningCasesAsync(
        string? workspaceId = null,
        string? collectionId = null,
        string? sessionId = null,
        ContextFeedbackSignal? signal = null,
        ContextFailureType? failureType = null,
        ContextLearningCaseStatus? status = null,
        string? caseKind = null,
        string? sourceRecordId = null,
        int limit = 20,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        return await QueryLearningCasesAsync(new ContextLearningCaseQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            SessionId = sessionId,
            Signal = signal,
            FailureType = failureType,
            Status = status,
            CaseKind = caseKind,
            SourceRecordId = sourceRecordId,
            Limit = limit,
            Offset = offset
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextLearningCase> GetLearningCaseAsync(
        string caseId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caseId);
        return await GetRequiredAsync<ContextLearningCase>(
            $"api/learning/cases/{Escape(caseId)}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextLearningCase> CreateLearningCaseAsync(
        ContextLearningCase learningCase,
        CancellationToken cancellationToken = default)
    {
        return await PostRequiredAsync<ContextLearningCase, ContextLearningCase>(
            "api/learning/cases",
            learningCase,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextLearningCaseGenerationResult> GenerateLearningCasesAsync(
        ContextLearningCaseGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        return await PostRequiredAsync<ContextLearningCaseGenerationRequest, ContextLearningCaseGenerationResult>(
            "api/learning/cases/generate",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextLearningCaseStatusUpdateResponse> ActivateLearningCaseAsync(
        string caseId,
        ContextLearningCaseStatusUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caseId);
        return await PostRequiredAsync<ContextLearningCaseStatusUpdateRequest, ContextLearningCaseStatusUpdateResponse>(
            $"api/learning/cases/{Escape(caseId)}/activate",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextLearningCaseStatusUpdateResponse> ArchiveLearningCaseAsync(
        string caseId,
        ContextLearningCaseStatusUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caseId);
        return await PostRequiredAsync<ContextLearningCaseStatusUpdateRequest, ContextLearningCaseStatusUpdateResponse>(
            $"api/learning/cases/{Escape(caseId)}/archive",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextLearningCaseStatusUpdateResponse> RejectLearningCaseAsync(
        string caseId,
        ContextLearningCaseStatusUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caseId);
        return await PostRequiredAsync<ContextLearningCaseStatusUpdateRequest, ContextLearningCaseStatusUpdateResponse>(
            $"api/learning/cases/{Escape(caseId)}/reject",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextLearningSummary> GetLearningSummaryAsync(
        string? workspaceId = null,
        string? collectionId = null,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(workspaceId)) parts.Add($"workspaceId={Escape(workspaceId)}");
        if (!string.IsNullOrWhiteSpace(collectionId)) parts.Add($"collectionId={Escape(collectionId)}");
        if (!string.IsNullOrWhiteSpace(sessionId)) parts.Add($"sessionId={Escape(sessionId)}");
        var path = parts.Count == 0 ? "api/learning/summary" : $"api/learning/summary?{string.Join('&', parts)}";
        return await GetRequiredAsync<ContextLearningSummary>(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContextLearningCase>> GetRegressionLearningCasesAsync(
        string? workspaceId = null,
        string? collectionId = null,
        string? sessionId = null,
        int limit = 20,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var parts = new List<string>
        {
            $"limit={limit}",
            $"offset={offset}"
        };
        if (!string.IsNullOrWhiteSpace(workspaceId)) parts.Add($"workspaceId={Escape(workspaceId)}");
        if (!string.IsNullOrWhiteSpace(collectionId)) parts.Add($"collectionId={Escape(collectionId)}");
        if (!string.IsNullOrWhiteSpace(sessionId)) parts.Add($"sessionId={Escape(sessionId)}");
        return await GetRequiredAsync<IReadOnlyList<ContextLearningCase>>(
            $"api/learning/regression/cases?{string.Join('&', parts)}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextPromotionRecord> PromoteMemoryAsync(
        ContextCoreMemoryPromotionRequest request,
        CancellationToken cancellationToken = default)
    {
        return await PostRequiredAsync<ContextCoreMemoryPromotionRequest, ContextPromotionRecord>("api/memory/promote", request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextPackage> BuildPackageAsync(ContextPackageRequest request, CancellationToken cancellationToken = default)
    {
        return await PostRequiredAsync<ContextPackageRequest, ContextPackage>("api/package/build", request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextPackageBuildResult> BuildPackageDetailedAsync(
        ContextPackageRequest request,
        CancellationToken cancellationToken = default)
    {
        return await PostRequiredAsync<ContextPackageRequest, ContextPackageBuildResult>(
            "api/package/build-detailed",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextPackage> PreviewPackageAsync(ContextPackageRequest request, CancellationToken cancellationToken = default)
    {
        return await PostRequiredAsync<ContextPackageRequest, ContextPackage>("api/package/preview", request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContextPackagePolicy>> QueryPackagePoliciesAsync(
        string workspaceId,
        string collectionId,
        string? queryText = null,
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);

        var parts = new List<string>
        {
            $"workspaceId={Escape(workspaceId)}",
            $"collectionId={Escape(collectionId)}",
            $"take={take}"
        };
        if (!string.IsNullOrWhiteSpace(queryText))
        {
            parts.Add($"queryText={Escape(queryText)}");
        }

        return await GetRequiredAsync<IReadOnlyList<ContextPackagePolicy>>(
            $"api/package/policies?{string.Join('&', parts)}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextPackagePolicy> GetPackagePolicyAsync(
        string workspaceId,
        string collectionId,
        string policyId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyId);

        var path = $"api/package/policies/{Escape(policyId)}?workspaceId={Escape(workspaceId)}&collectionId={Escape(collectionId)}";
        return await GetRequiredAsync<ContextPackagePolicy>(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CompressionResponse> RunCompressionAsync(CompressionRequest request, CancellationToken cancellationToken = default)
    {
        return await PostRequiredAsync<CompressionRequest, CompressionResponse>("api/compression/sync", request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextCoreRelationsResponse> QueryRelationsAsync(
        string itemId,
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);

        var path = $"api/relations/{Escape(itemId)}?workspaceId={Escape(workspaceId)}&collectionId={Escape(collectionId)}";
        return await GetRequiredAsync<ContextCoreRelationsResponse>(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RelationTypeDefinition>> GetRelationTypesAsync(
        CancellationToken cancellationToken = default)
    {
        return await GetRequiredAsync<IReadOnlyList<RelationTypeDefinition>>(
            "api/relations/types",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RelationExpansionProfile>> GetRelationExpansionProfilesAsync(
        CancellationToken cancellationToken = default)
    {
        return await GetRequiredAsync<IReadOnlyList<RelationExpansionProfile>>(
            "api/relations/expansion/profiles",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<RelationExpansionPreviewResponse> PreviewRelationExpansionAsync(
        RelationExpansionPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await PostRequiredAsync<RelationExpansionPreviewRequest, RelationExpansionPreviewResponse>(
            "api/relations/expansion/preview",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<RelationGraphDiagnosticsReport> GetRelationDiagnosticsAsync(
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var parts = new List<string> { $"workspaceId={Escape(workspaceId)}" };
        if (!string.IsNullOrWhiteSpace(collectionId))
        {
            parts.Add($"collectionId={Escape(collectionId)}");
        }

        return await GetRequiredAsync<RelationGraphDiagnosticsReport>(
            $"api/relations/diagnostics?{string.Join('&', parts)}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<RelationGraphDiagnosticsReport> GetItemRelationDiagnosticsAsync(
        string itemId,
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var parts = new List<string> { $"workspaceId={Escape(workspaceId)}" };
        if (!string.IsNullOrWhiteSpace(collectionId))
        {
            parts.Add($"collectionId={Escape(collectionId)}");
        }

        return await GetRequiredAsync<RelationGraphDiagnosticsReport>(
            $"api/relations/diagnostics/{Escape(itemId)}?{string.Join('&', parts)}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<RelationExplainResponse> ExplainRelationAsync(
        string relationId,
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var parts = new List<string> { $"workspaceId={Escape(workspaceId)}" };
        if (!string.IsNullOrWhiteSpace(collectionId))
        {
            parts.Add($"collectionId={Escape(collectionId)}");
        }

        return await GetRequiredAsync<RelationExplainResponse>(
            $"api/relations/{Escape(relationId)}/explain?{string.Join('&', parts)}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<RelationReviewResult> ReviewRelationAsync(
        string relationId,
        RelationReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relationId);
        return await PostRequiredAsync<RelationReviewRequest, RelationReviewResult>(
            $"api/relations/{Escape(relationId)}/review",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<RelationReviewResult> RejectRelationAsync(
        string relationId,
        RelationReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relationId);
        return await PostRequiredAsync<RelationReviewRequest, RelationReviewResult>(
            $"api/relations/{Escape(relationId)}/reject",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<RelationReviewResult> DeprecateRelationAsync(
        string relationId,
        RelationReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relationId);
        return await PostRequiredAsync<RelationReviewRequest, RelationReviewResult>(
            $"api/relations/{Escape(relationId)}/deprecate",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<RelationReviewResult> MarkRelationNeedsEvidenceAsync(
        string relationId,
        RelationReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relationId);
        return await PostRequiredAsync<RelationReviewRequest, RelationReviewResult>(
            $"api/relations/{Escape(relationId)}/needs-evidence",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RelationReviewRecord>> GetRelationReviewsAsync(
        string relationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relationId);
        return await GetRequiredAsync<IReadOnlyList<RelationReviewRecord>>(
            $"api/relations/{Escape(relationId)}/reviews",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContextConstraint>> QueryConstraintsAsync(
        string workspaceId,
        string? collectionId = null,
        ConstraintLevel? level = null,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        var parts = new List<string> { $"workspaceId={Escape(workspaceId)}" };
        if (!string.IsNullOrWhiteSpace(collectionId))
        {
            parts.Add($"collectionId={Escape(collectionId)}");
        }

        if (level is not null)
        {
            parts.Add($"level={level}");
        }

        parts.Add($"take={take}");
        return await GetRequiredAsync<IReadOnlyList<ContextConstraint>>($"api/constraints?{string.Join('&', parts)}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContextConstraint>> GetCandidateConstraintsAsync(
        string workspaceId,
        string? collectionId = null,
        ContextMemoryStatus? status = ContextMemoryStatus.Candidate,
        int limit = 20,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        return await GetRequiredAsync<IReadOnlyList<ContextConstraint>>(
            $"api/constraints/candidates?{BuildCandidateConstraintQueryString(workspaceId, collectionId, status, limit, offset)}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextConstraint> GetCandidateConstraintAsync(
        string constraintId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(constraintId);
        return await GetRequiredAsync<ContextConstraint>(
            $"api/constraints/candidates/{Escape(constraintId)}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<CandidateConstraintReviewResult> ActivateCandidateConstraintAsync(
        string constraintId,
        CandidateConstraintReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(constraintId);
        ArgumentNullException.ThrowIfNull(request);
        return await PostRequiredAsync<CandidateConstraintReviewRequest, CandidateConstraintReviewResult>(
            $"api/constraints/candidates/{Escape(constraintId)}/activate",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<CandidateConstraintReviewResult> RejectCandidateConstraintAsync(
        string constraintId,
        CandidateConstraintReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(constraintId);
        ArgumentNullException.ThrowIfNull(request);
        return await PostRequiredAsync<CandidateConstraintReviewRequest, CandidateConstraintReviewResult>(
            $"api/constraints/candidates/{Escape(constraintId)}/reject",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CandidateConstraintReviewRecord>> GetCandidateConstraintReviewsAsync(
        string constraintId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(constraintId);
        return await GetRequiredAsync<IReadOnlyList<CandidateConstraintReviewRecord>>(
            $"api/constraints/candidates/{Escape(constraintId)}/reviews",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConstraintGapGenerationResult> GenerateConstraintGapsAsync(
        ConstraintGapGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CollectionId);

        return await PostRequiredAsync<ConstraintGapGenerationRequest, ConstraintGapGenerationResult>(
            "api/constraints/gaps/generate",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ConstraintGapCandidate>> GetConstraintGapsAsync(
        string workspaceId,
        string? collectionId = null,
        string? sessionId = null,
        string? source = null,
        string? sourceSampleId = null,
        string? status = null,
        string? severity = null,
        int limit = 20,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        return await QueryConstraintGapsAsync(new ConstraintGapCandidateQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            SessionId = sessionId,
            Source = source,
            SourceSampleId = sourceSampleId,
            Status = status,
            Severity = severity,
            Limit = limit,
            Offset = offset
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ConstraintGapCandidate>> QueryConstraintGapsAsync(
        ConstraintGapCandidateQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.WorkspaceId);

        return await GetRequiredAsync<IReadOnlyList<ConstraintGapCandidate>>(
            $"api/constraints/gaps?{BuildConstraintGapQueryString(query)}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConstraintGapCandidate> GetConstraintGapAsync(
        string gapId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gapId);
        return await GetRequiredAsync<ConstraintGapCandidate>(
            $"api/constraints/gaps/{Escape(gapId)}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConstraintGapReviewResult> AcceptConstraintGapAsync(
        string gapId,
        ConstraintGapReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gapId);
        ArgumentNullException.ThrowIfNull(request);
        return await PostRequiredAsync<ConstraintGapReviewRequest, ConstraintGapReviewResult>(
            $"api/constraints/gaps/{Escape(gapId)}/accept",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConstraintGapReviewResult> RejectConstraintGapAsync(
        string gapId,
        ConstraintGapReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gapId);
        ArgumentNullException.ThrowIfNull(request);
        return await PostRequiredAsync<ConstraintGapReviewRequest, ConstraintGapReviewResult>(
            $"api/constraints/gaps/{Escape(gapId)}/reject",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ConstraintGapReviewRecord>> GetConstraintGapReviewsAsync(
        string gapId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gapId);
        return await GetRequiredAsync<IReadOnlyList<ConstraintGapReviewRecord>>(
            $"api/constraints/gaps/{Escape(gapId)}/reviews",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextJob> EnqueueCompressionJobAsync(CompressionRequest request, CancellationToken cancellationToken = default)
    {
        return await PostRequiredAsync<CompressionRequest, ContextJob>("api/jobs/compression", request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContextJob>> QueryJobsAsync(ContextJobQuery query, CancellationToken cancellationToken = default)
    {
        // 查询作业是 GET 接口，这里手动拼接查询字符串以保持客户端不依赖额外 URL builder。
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(query.WorkspaceId))
        {
            parts.Add($"workspaceId={Escape(query.WorkspaceId)}");
        }

        if (!string.IsNullOrWhiteSpace(query.CollectionId))
        {
            parts.Add($"collectionId={Escape(query.CollectionId)}");
        }

        if (query.State is not null)
        {
            parts.Add($"state={query.State}");
        }

        parts.Add($"take={query.Take}");
        var path = $"api/jobs?{string.Join('&', parts)}";
        return await GetRequiredAsync<IReadOnlyList<ContextJob>>(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextJob> GetJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        return await GetRequiredAsync<ContextJob>($"api/jobs/{Escape(jobId)}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextCoreRequeueJobResponse> RequeueJobAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        return await PostRequiredAsync<object, ContextCoreRequeueJobResponse>(
            $"api/jobs/{Escape(jobId)}/requeue",
            new { },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextCoreModelStatusResponse> GetModelStatusAsync(CancellationToken cancellationToken = default)
    {
        return await GetRequiredAsync<ContextCoreModelStatusResponse>("api/model/status", cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextCoreModelRouteResolveResponse> ResolveModelRouteAsync(
        ContextCoreModelRouteResolveRequest request,
        CancellationToken cancellationToken = default)
    {
        return await PostRequiredAsync<ContextCoreModelRouteResolveRequest, ContextCoreModelRouteResolveResponse>(
            "api/model/route/resolve",
            request,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextCoreAdminStatusResponse> GetAdminStatusAsync(
        string? workspaceId = null,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(workspaceId))
        {
            parts.Add($"workspaceId={Escape(workspaceId)}");
        }

        if (!string.IsNullOrWhiteSpace(collectionId))
        {
            parts.Add($"collectionId={Escape(collectionId)}");
        }

        var path = parts.Count == 0 ? "api/admin/status" : $"api/admin/status?{string.Join('&', parts)}";
        return await GetRequiredAsync<ContextCoreAdminStatusResponse>(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextCoreBackupStatusResponse> GetBackupStatusAsync(CancellationToken cancellationToken = default)
    {
        return await GetRequiredAsync<ContextCoreBackupStatusResponse>("api/admin/backup/status", cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextCoreBackupValidateResponse> ValidateBackupAsync(CancellationToken cancellationToken = default)
    {
        return await GetRequiredAsync<ContextCoreBackupValidateResponse>("api/admin/backup/validate", cancellationToken).ConfigureAwait(false);
    }

    public async Task<PostgresStorageStatusResponse> GetPostgresStorageStatusAsync(CancellationToken cancellationToken = default)
    {
        return await GetRequiredAsync<PostgresStorageStatusResponse>(
            "api/admin/storage/postgres/status",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<PostgresOperationalStoreDiagnostics> GetPostgresStorageDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        return await GetRequiredAsync<PostgresOperationalStoreDiagnostics>(
            "api/admin/storage/postgres/diagnostics",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<PostgresMigrationPlanResponse> PreviewPostgresMigrationsAsync(CancellationToken cancellationToken = default)
    {
        return await PostRequiredAsync<object, PostgresMigrationPlanResponse>(
            "api/admin/storage/postgres/migrations/dry-run",
            new { },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<PostgresMigrationApplyResponse> ApplyPostgresMigrationsAsync(
        bool confirm,
        CancellationToken cancellationToken = default)
    {
        return await PostRequiredAsync<PostgresMigrationRequest, PostgresMigrationApplyResponse>(
            "api/admin/storage/postgres/migrations/apply",
            new PostgresMigrationRequest { Confirm = confirm },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<PostgresRelationScopedServiceModeStatusResponse> GetRelationProviderStatusAsync(CancellationToken cancellationToken = default)
    {
        return await GetRequiredAsync<PostgresRelationScopedServiceModeStatusResponse>(
            "api/admin/storage/relation-provider/status",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<PostgresRelationScopedServiceModeStatusResponse> GetRelationProviderScopedDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        return await GetRequiredAsync<PostgresRelationScopedServiceModeStatusResponse>(
            "api/admin/storage/relation-provider/scoped-diagnostics",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<FoundationServiceStatusResponse> GetFoundationStatusAsync(CancellationToken cancellationToken = default)
    {
        return await GetFoundationEnvelopeDataAsync<FoundationServiceStatusResponse>(
            "api/admin/foundation/status",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<FoundationServiceStatusResponse> GetFoundationReleaseCandidateStatusAsync(CancellationToken cancellationToken = default)
    {
        return await GetFoundationEnvelopeDataAsync<FoundationServiceStatusResponse>(
            "api/admin/foundation/release-candidate",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<FoundationServiceStatusResponse> GetFoundationReleaseCandidateAsync(CancellationToken cancellationToken = default)
        => await GetFoundationReleaseCandidateStatusAsync(cancellationToken).ConfigureAwait(false);

    public async Task<FoundationServiceStatusResponse> GetFoundationReproducibilityStatusAsync(CancellationToken cancellationToken = default)
    {
        return await GetFoundationEnvelopeDataAsync<FoundationServiceStatusResponse>(
            "api/admin/foundation/reproducibility",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<FoundationServiceStatusResponse> GetFoundationReproducibilityAsync(CancellationToken cancellationToken = default)
        => await GetFoundationReproducibilityStatusAsync(cancellationToken).ConfigureAwait(false);

    public async Task<FoundationServiceStatusResponse> GetFoundationRuntimeChangeGateStatusAsync(CancellationToken cancellationToken = default)
    {
        return await GetFoundationEnvelopeDataAsync<FoundationServiceStatusResponse>(
            "api/admin/foundation/runtime-change-gate",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<FoundationServiceStatusResponse> GetRuntimeChangeGateAsync(CancellationToken cancellationToken = default)
        => await GetFoundationRuntimeChangeGateStatusAsync(cancellationToken).ConfigureAwait(false);

    public async Task<FoundationServiceStatusResponse> GetFoundationVectorFormalPreviewStatusAsync(CancellationToken cancellationToken = default)
    {
        return await GetFoundationEnvelopeDataAsync<FoundationServiceStatusResponse>(
            "api/admin/foundation/vector-formal-preview",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<FoundationServiceStatusResponse> GetVectorFormalPreviewStatusAsync(CancellationToken cancellationToken = default)
        => await GetFoundationVectorFormalPreviewStatusAsync(cancellationToken).ConfigureAwait(false);

    public async Task<FoundationServiceStatusResponse> GetFoundationPostgresFreezeStatusAsync(CancellationToken cancellationToken = default)
    {
        return await GetFoundationEnvelopeDataAsync<FoundationServiceStatusResponse>(
            "api/admin/foundation/postgres-freeze-status",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<FoundationServiceStatusResponse> GetPostgresFreezeStatusAsync(CancellationToken cancellationToken = default)
        => await GetFoundationPostgresFreezeStatusAsync(cancellationToken).ConfigureAwait(false);

    public async Task<FoundationReportNavigationResponse> GetFoundationReportsAsync(CancellationToken cancellationToken = default)
    {
        return await GetFoundationEnvelopeDataAsync<FoundationReportNavigationResponse>(
            "api/admin/foundation/reports",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<FoundationReportNavigationEntry> GetFoundationReportAsync(
        string reportId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportId);

        return await GetFoundationEnvelopeDataAsync<FoundationReportNavigationEntry>(
            $"api/admin/foundation/reports/{Escape(reportId)}",
            cancellationToken).ConfigureAwait(false);
    }

    private static string Escape(string value)
    {
        return Uri.EscapeDataString(value);
    }

    private async Task<TResponse> GetFoundationEnvelopeDataAsync<TResponse>(
        string path,
        CancellationToken cancellationToken)
    {
        var envelope = await GetRequiredAsync<FoundationApiResponseEnvelope<TResponse>>(path, cancellationToken)
            .ConfigureAwait(false);
        if (envelope.Data is null)
        {
            throw new ContextCoreApiException(
                new ContextCoreErrorResponse
                {
                    ErrorCode = ContextCoreErrorCodes.StorageUnavailable,
                    Message = $"Foundation API returned no data for {path} (status={envelope.Status}, recommendation={envelope.Recommendation}).",
                    Target = path
                },
                HttpStatusCode.ServiceUnavailable);
        }

        return envelope.Data;
    }

    private static string BuildLearningRecordQueryString(ContextLearningRecordQuery query)
    {
        var parts = new List<string>
        {
            $"limit={query.Limit}",
            $"offset={query.Offset}"
        };
        if (!string.IsNullOrWhiteSpace(query.WorkspaceId)) parts.Add($"workspaceId={Escape(query.WorkspaceId)}");
        if (!string.IsNullOrWhiteSpace(query.CollectionId)) parts.Add($"collectionId={Escape(query.CollectionId)}");
        if (!string.IsNullOrWhiteSpace(query.SessionId)) parts.Add($"sessionId={Escape(query.SessionId)}");
        if (query.Signal is not null) parts.Add($"signal={query.Signal.Value}");
        if (query.FailureType is not null) parts.Add($"failureType={query.FailureType.Value}");
        if (!string.IsNullOrWhiteSpace(query.SourceKind)) parts.Add($"sourceKind={Escape(query.SourceKind)}");
        if (!string.IsNullOrWhiteSpace(query.SourceId)) parts.Add($"sourceId={Escape(query.SourceId)}");
        return string.Join('&', parts);
    }

    private static string BuildPromotionFeedbackQueryString(PromotionFeedbackSignalQuery query)
    {
        var parts = new List<string>
        {
            $"limit={query.Limit}",
            $"offset={query.Offset}"
        };
        if (!string.IsNullOrWhiteSpace(query.WorkspaceId)) parts.Add($"workspaceId={Escape(query.WorkspaceId)}");
        if (!string.IsNullOrWhiteSpace(query.CollectionId)) parts.Add($"collectionId={Escape(query.CollectionId)}");
        if (!string.IsNullOrWhiteSpace(query.SessionId)) parts.Add($"sessionId={Escape(query.SessionId)}");
        if (!string.IsNullOrWhiteSpace(query.CandidateId)) parts.Add($"candidateId={Escape(query.CandidateId)}");
        if (!string.IsNullOrWhiteSpace(query.Action)) parts.Add($"action={Escape(query.Action)}");
        return string.Join('&', parts);
    }

    private static string BuildRuntimeLearningFeedbackQueryString(
        LearningFeedbackEventQuery query,
        bool includeRuntimeFlag = true)
    {
        var parts = new List<string>
        {
            $"limit={query.Limit}",
            $"offset={query.Offset}"
        };
        if (includeRuntimeFlag)
        {
            parts.Add("runtimeFeedback=true");
        }

        if (!string.IsNullOrWhiteSpace(query.WorkspaceId)) parts.Add($"workspaceId={Escape(query.WorkspaceId)}");
        if (!string.IsNullOrWhiteSpace(query.CollectionId)) parts.Add($"collectionId={Escape(query.CollectionId)}");
        if (!string.IsNullOrWhiteSpace(query.Source)) parts.Add($"source={Escape(query.Source)}");
        if (!string.IsNullOrWhiteSpace(query.SourceOperationId)) parts.Add($"sourceOperationId={Escape(query.SourceOperationId)}");
        if (!string.IsNullOrWhiteSpace(query.CapabilityId)) parts.Add($"capabilityId={Escape(query.CapabilityId)}");
        if (!string.IsNullOrWhiteSpace(query.TargetId)) parts.Add($"targetId={Escape(query.TargetId)}");
        if (!string.IsNullOrWhiteSpace(query.TargetType)) parts.Add($"targetType={Escape(query.TargetType)}");
        if (!string.IsNullOrWhiteSpace(query.FeedbackKind)) parts.Add($"feedbackKind={Escape(query.FeedbackKind)}");
        return string.Join('&', parts);
    }

    private static string BuildLearningFeedbackReviewQueryString(LearningFeedbackReviewQuery query)
    {
        var parts = new List<string>
        {
            $"limit={query.Limit}",
            $"offset={query.Offset}"
        };
        if (!string.IsNullOrWhiteSpace(query.FeedbackId)) parts.Add($"feedbackId={Escape(query.FeedbackId)}");
        if (query.ReviewStatus is not null) parts.Add($"reviewStatus={query.ReviewStatus.Value}");
        if (!string.IsNullOrWhiteSpace(query.Reviewer)) parts.Add($"reviewer={Escape(query.Reviewer)}");
        return string.Join('&', parts);
    }

    private static LearningFeedbackSubmitRequest ToLearningFeedbackSubmitRequest(LearningFeedbackEvent feedbackEvent)
    {
        if (!Enum.TryParse<LearningFeedbackTargetType>(
            feedbackEvent.TargetType,
            ignoreCase: true,
            out var parsedTargetType))
        {
            throw new ArgumentException($"Invalid targetType '{feedbackEvent.TargetType}'.", nameof(feedbackEvent));
        }

        return new LearningFeedbackSubmitRequest
        {
            FeedbackId = feedbackEvent.FeedbackId,
            WorkspaceId = feedbackEvent.WorkspaceId,
            CollectionId = feedbackEvent.CollectionId,
            Source = feedbackEvent.Source,
            SourceOperationId = feedbackEvent.SourceOperationId,
            CapabilityId = feedbackEvent.CapabilityId,
            TargetId = feedbackEvent.TargetId,
            TargetType = parsedTargetType,
            FeedbackKind = feedbackEvent.FeedbackKind,
            FeedbackValue = feedbackEvent.FeedbackValue,
            Reason = feedbackEvent.Reason,
            UserCorrection = feedbackEvent.UserCorrection,
            RedactionMode = feedbackEvent.RedactionMode,
            MetadataOnly = feedbackEvent.MetadataOnly,
            TrainingUse = feedbackEvent.TrainingUse,
            Confidence = feedbackEvent.Confidence,
            CreatedAt = feedbackEvent.CreatedAt,
            Metadata = feedbackEvent.Metadata
        };
    }

    private static string BuildPolicyFeedbackQueryString(
        string workspaceId,
        string? collectionId,
        string? sessionId,
        int limit,
        int offset)
    {
        var parts = new List<string>
        {
            $"workspaceId={Escape(workspaceId)}",
            $"limit={limit}",
            $"offset={offset}"
        };
        if (!string.IsNullOrWhiteSpace(collectionId)) parts.Add($"collectionId={Escape(collectionId)}");
        if (!string.IsNullOrWhiteSpace(sessionId)) parts.Add($"sessionId={Escape(sessionId)}");
        return string.Join('&', parts);
    }

    private static string BuildLearningFeatureExportQueryString(
        string workspaceId,
        string? collectionId,
        string? sessionId,
        string? outputDirectory)
    {
        var parts = new List<string>
        {
            $"workspaceId={Escape(workspaceId)}"
        };
        if (!string.IsNullOrWhiteSpace(collectionId)) parts.Add($"collectionId={Escape(collectionId)}");
        if (!string.IsNullOrWhiteSpace(sessionId)) parts.Add($"sessionId={Escape(sessionId)}");
        if (!string.IsNullOrWhiteSpace(outputDirectory)) parts.Add($"outputDirectory={Escape(outputDirectory)}");
        return string.Join('&', parts);
    }

    private static string BuildStableReviewCandidateQueryString(StableReviewCandidateQuery query)
    {
        var parts = new List<string>
        {
            $"workspaceId={Escape(query.WorkspaceId)}",
            $"limit={query.Limit}",
            $"offset={query.Offset}"
        };
        if (!string.IsNullOrWhiteSpace(query.CollectionId)) parts.Add($"collectionId={Escape(query.CollectionId)}");
        if (!string.IsNullOrWhiteSpace(query.SessionId)) parts.Add($"sessionId={Escape(query.SessionId)}");
        if (!string.IsNullOrWhiteSpace(query.Status)) parts.Add($"status={Escape(query.Status)}");
        if (!string.IsNullOrWhiteSpace(query.ValidationStatus)) parts.Add($"validationStatus={Escape(query.ValidationStatus)}");
        if (!string.IsNullOrWhiteSpace(query.Kind)) parts.Add($"kind={Escape(query.Kind)}");
        if (!string.IsNullOrWhiteSpace(query.SuggestedStableTarget)) parts.Add($"suggestedStableTarget={Escape(query.SuggestedStableTarget)}");
        return string.Join('&', parts);
    }

    private static string BuildConstraintGapQueryString(ConstraintGapCandidateQuery query)
    {
        var parts = new List<string>
        {
            $"workspaceId={Escape(query.WorkspaceId)}",
            $"limit={query.Limit}",
            $"offset={query.Offset}"
        };
        if (!string.IsNullOrWhiteSpace(query.CollectionId)) parts.Add($"collectionId={Escape(query.CollectionId)}");
        if (!string.IsNullOrWhiteSpace(query.SessionId)) parts.Add($"sessionId={Escape(query.SessionId)}");
        if (!string.IsNullOrWhiteSpace(query.Source)) parts.Add($"source={Escape(query.Source)}");
        if (!string.IsNullOrWhiteSpace(query.SourceSampleId)) parts.Add($"sourceSampleId={Escape(query.SourceSampleId)}");
        if (!string.IsNullOrWhiteSpace(query.Status)) parts.Add($"status={Escape(query.Status)}");
        if (!string.IsNullOrWhiteSpace(query.Severity)) parts.Add($"severity={Escape(query.Severity)}");
        return string.Join('&', parts);
    }

    private static string BuildCandidateConstraintQueryString(
        string workspaceId,
        string? collectionId,
        ContextMemoryStatus? status,
        int limit,
        int offset)
    {
        var parts = new List<string>
        {
            $"workspaceId={Escape(workspaceId)}",
            $"limit={limit}",
            $"offset={offset}"
        };
        if (!string.IsNullOrWhiteSpace(collectionId)) parts.Add($"collectionId={Escape(collectionId)}");
        if (status is not null) parts.Add($"status={status.Value}");
        return string.Join('&', parts);
    }

    private static string BuildLearningCaseQueryString(ContextLearningCaseQuery query)
    {
        var parts = new List<string>
        {
            $"limit={query.Limit}",
            $"offset={query.Offset}"
        };
        if (!string.IsNullOrWhiteSpace(query.WorkspaceId)) parts.Add($"workspaceId={Escape(query.WorkspaceId)}");
        if (!string.IsNullOrWhiteSpace(query.CollectionId)) parts.Add($"collectionId={Escape(query.CollectionId)}");
        if (!string.IsNullOrWhiteSpace(query.SessionId)) parts.Add($"sessionId={Escape(query.SessionId)}");
        if (query.Signal is not null) parts.Add($"signal={query.Signal.Value}");
        if (query.FailureType is not null) parts.Add($"failureType={query.FailureType.Value}");
        if (query.Status is not null) parts.Add($"status={query.Status.Value}");
        if (!string.IsNullOrWhiteSpace(query.CaseKind)) parts.Add($"caseKind={Escape(query.CaseKind)}");
        if (!string.IsNullOrWhiteSpace(query.SourceRecordId)) parts.Add($"sourceRecordId={Escape(query.SourceRecordId)}");
        return string.Join('&', parts);
    }

    private async Task<TResponse> GetRequiredAsync<TResponse>(string path, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessOrThrowAsync(response, cancellationToken).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken).ConfigureAwait(false);
        return result ?? throw new InvalidOperationException($"ContextCore returned an empty response for GET {path}.");
    }

    private async Task<string> GetRequiredStringAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessOrThrowAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<TResponse?> GetOptionalAsync<TResponse>(string path, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessOrThrowAsync(response, cancellationToken).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken).ConfigureAwait(false);
        return result ?? throw new InvalidOperationException($"ContextCore returned an empty response for GET {path}.");
    }

    private async Task<TResponse> PostRequiredAsync<TRequest, TResponse>(string path, TRequest request, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(path, request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessOrThrowAsync(response, cancellationToken).ConfigureAwait(false);
        // 服务端的成功响应应总是有 JSON 主体；空主体通常表示端点契约被破坏。
        var result = await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken).ConfigureAwait(false);
        return result ?? throw new InvalidOperationException($"ContextCore returned an empty response for POST {path}.");
    }

    private async Task PostNoContentAsync<TRequest>(string path, TRequest request, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(path, request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessOrThrowAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureSuccessOrThrowAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        ContextCoreErrorResponse? errorResponse = null;
        try
        {
            errorResponse = await response.Content
                .ReadFromJsonAsync<ContextCoreErrorResponse>(cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // 回退到默认 HTTP 异常。
        }

        if (errorResponse is not null && !string.IsNullOrWhiteSpace(errorResponse.ErrorCode))
        {
            throw new ContextCoreApiException(errorResponse, response.StatusCode);
        }

        response.EnsureSuccessStatusCode();
    }
}


