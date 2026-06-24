using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.FileSystem;

/// <summary>Trace artifact 写入入口；负责把 trace 数据落到标准 layout 并维护 manifest。</summary>
public sealed class TraceArtifactWriter
{
    private readonly IArtifactStore _artifactStore;
    private readonly TraceArtifactDescriptorFactory _descriptorFactory;

    public TraceArtifactWriter(IArtifactStore artifactStore)
        : this(artifactStore, new TraceArtifactDescriptorFactory())
    {
    }

    public TraceArtifactWriter(
        IArtifactStore artifactStore,
        TraceArtifactDescriptorFactory descriptorFactory)
    {
        _artifactStore = artifactStore;
        _descriptorFactory = descriptorFactory;
    }

    public Task<string> WriteTraceAsync<T>(
        string workspaceId,
        string collectionId,
        ArtifactKind traceKind,
        T trace,
        string? operationId = null,
        string? dateShard = null,
        string? capabilityId = null,
        string? reportId = null,
        CancellationToken cancellationToken = default)
    {
        var descriptor = _descriptorFactory.Create(
            workspaceId,
            collectionId,
            traceKind,
            operationId,
            dateShard,
            capabilityId,
            reportId,
            ".json");
        return _artifactStore.WriteJsonAsync(descriptor, trace, cancellationToken);
    }

    public Task<string> AppendTraceJsonLineAsync<T>(
        string workspaceId,
        string collectionId,
        ArtifactKind traceKind,
        T trace,
        string? operationId = null,
        string? dateShard = null,
        string? capabilityId = null,
        string? reportId = null,
        CancellationToken cancellationToken = default)
    {
        var descriptor = _descriptorFactory.Create(
            workspaceId,
            collectionId,
            traceKind,
            operationId,
            dateShard,
            capabilityId,
            reportId,
            ".jsonl");
        return _artifactStore.AppendJsonLineAsync(descriptor, trace, cancellationToken);
    }

    public Task<string> WriteToolCallRequestAsync<T>(
        string workspaceId,
        string collectionId,
        string operationId,
        T request,
        string? dateShard = null,
        CancellationToken cancellationToken = default)
        => WriteToolCallAsync(workspaceId, collectionId, operationId, "request", request, dateShard, cancellationToken);

    public Task<string> WriteToolCallResponseAsync<T>(
        string workspaceId,
        string collectionId,
        string operationId,
        T response,
        string? dateShard = null,
        CancellationToken cancellationToken = default)
        => WriteToolCallAsync(workspaceId, collectionId, operationId, "response", response, dateShard, cancellationToken);

    public Task<string> WriteToolCallErrorAsync<T>(
        string workspaceId,
        string collectionId,
        string operationId,
        T error,
        string? dateShard = null,
        CancellationToken cancellationToken = default)
        => WriteToolCallAsync(workspaceId, collectionId, operationId, "error", error, dateShard, cancellationToken);

    public Task<string> WriteTraceManifestAsync<T>(
        string workspaceId,
        string collectionId,
        T manifest,
        string? dateShard = null,
        CancellationToken cancellationToken = default)
        => WriteTraceAsync(
            workspaceId,
            collectionId,
            ArtifactKind.TraceError,
            manifest,
            operationId: null,
            dateShard: dateShard,
            capabilityId: "manifest",
            reportId: "trace-manifest",
            cancellationToken: cancellationToken);

    private Task<string> WriteToolCallAsync<T>(
        string workspaceId,
        string collectionId,
        string operationId,
        string phase,
        T value,
        string? dateShard,
        CancellationToken cancellationToken)
    {
        var descriptor = _descriptorFactory.CreateToolCall(workspaceId, collectionId, operationId, phase, dateShard);
        return _artifactStore.WriteJsonAsync(descriptor, value, cancellationToken);
    }
}
