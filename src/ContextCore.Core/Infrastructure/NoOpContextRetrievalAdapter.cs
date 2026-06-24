using ContextCore.Abstractions;

namespace ContextCore.Core.Infrastructure;

/// <summary>空操作检索适配器。返回空候选差异，不读 vector/graph provider，不改变 baseline。</summary>
public sealed class NoOpContextRetrievalAdapter : IContextRetrievalAdapter
{
    public string Name => "NoOp";

    public Task<RetrievalAdapterResult> ExecuteAsync(RetrievalAdapterRequest request, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new RetrievalAdapterResult
        {
            Applied = false,
            AddedCandidateIds = Array.Empty<string>(),
            RemovedCandidateIds = Array.Empty<string>(),
            TracePath = string.Empty,
        });
    }
}

/// <summary>空操作影子检索适配器。继承 NoOp 行为，追加追踪路径逻辑。</summary>
public sealed class NoOpShadowRetrievalAdapter : IShadowRetrievalAdapter
{
    private readonly string _traceRoot;

    public NoOpShadowRetrievalAdapter(string traceRoot = "vector/trace")
    {
        _traceRoot = traceRoot;
    }

    public string Name => "NoOpShadow";

    public async Task<RetrievalAdapterResult> ExecuteAsync(RetrievalAdapterRequest request, CancellationToken cancellationToken = default)
    {
        var traceDir = Path.Combine(_traceRoot, "shadow-adapter");
        Directory.CreateDirectory(traceDir);
        var tracePath = Path.Combine(traceDir, $"trace-{request.OperationId ?? Guid.NewGuid().ToString("N")}.jsonl");

        var entry = new { request.OperationId, request.WorkspaceId, request.CollectionId, request.QueryText, BaselineCount = request.BaselineCandidateIds.Count, Applied = false, Timestamp = DateTimeOffset.UtcNow };
        await File.AppendAllTextAsync(tracePath,
            System.Text.Json.JsonSerializer.Serialize(entry) + Environment.NewLine,
            cancellationToken).ConfigureAwait(false);

        return new RetrievalAdapterResult
        {
            Applied = false,
            AddedCandidateIds = Array.Empty<string>(),
            RemovedCandidateIds = Array.Empty<string>(),
            TracePath = tracePath,
        };
    }
}