using System.Text.Json;
using ContextCore.Abstractions;

namespace ContextCore.Core.Infrastructure;

/// <summary>
/// 基于范围的白名单影子检索适配器。仅对显式 allowlisted workspace:collection
/// 对做影子计算（即假设性候选差异）；其他 scope 使用 NoOp。
/// 结果始终 Applied=false，不改变 baseline selected set。
/// </summary>
public sealed class ScopedShadowRetrievalAdapter : IContextRetrievalAdapter, IShadowRetrievalAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HashSet<string> _allowlistedScopes;

    public ScopedShadowRetrievalAdapter(IEnumerable<string>? allowlistedScopes = null)
    {
        _allowlistedScopes = allowlistedScopes is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(allowlistedScopes, StringComparer.OrdinalIgnoreCase);
    }

    public string Name => "ScopedShadow";

    /// <summary>检查指定 scope 是否在白名单中。</summary>
    public bool IsAllowlisted(string workspaceId, string collectionId)
        => _allowlistedScopes.Contains($"{workspaceId}:{collectionId}");

    public Task<RetrievalAdapterResult> ExecuteAsync(RetrievalAdapterRequest request, CancellationToken cancellationToken = default)
    {
        var scopeKey = $"{request.WorkspaceId}:{request.CollectionId}";
        if (!_allowlistedScopes.Contains(scopeKey))
        {
            return Task.FromResult(new RetrievalAdapterResult { Applied = false, TracePath = string.Empty });
        }

        // 假设性候选差异：基于 combined-safe 风格对 baseline 重新评分。
        // 仅给出"哪些候选可能被移除"的提示；不添加新候选。
        var removed = ComputeHypotheticalRemovals(request);

        return Task.FromResult(new RetrievalAdapterResult
        {
            Applied = false,
            AddedCandidateIds = Array.Empty<string>(),
            RemovedCandidateIds = removed,
            TracePath = string.Empty,  // 使用 NoOpShadow 写 trace
        });
    }

    /// <summary>返回写入影子追踪文件的适配器实例。</summary>
    public IContextRetrievalAdapter WithTraceWriter(string traceRoot = "vector/trace")
        => new TracedScopedAdapter(this, traceRoot);

    private static IReadOnlyList<string> ComputeHypotheticalRemovals(RetrievalAdapterRequest request)
    {
        // 简化假设：当 baseline 候选数超过 query 长度的预期范围时，
        // 报告可能被 combined-safe 策略移除的候选（仅留 trace，不影响任何输出）。
        if (request.BaselineCandidateIds.Count <= 1)
            return Array.Empty<string>();

        // 假设性启发式：如果 query 很短（少于 5 个 token），
        // 选取得分排序靠后的候选作为"可能移除"信号。
        var tokenCount = string.IsNullOrWhiteSpace(request.QueryText)
            ? 0
            : request.QueryText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (tokenCount < 5 && request.BaselineCandidateIds.Count > 3)
        {
            return request.BaselineCandidateIds
                .Skip(2).Take(request.BaselineCandidateIds.Count - 3)
                .ToArray();
        }

        return Array.Empty<string>();
    }

    /// <summary>带追踪写入功能的包装适配器。</summary>
    private sealed class TracedScopedAdapter : IContextRetrievalAdapter
    {
        private readonly ScopedShadowRetrievalAdapter _inner;
        private readonly string _traceRoot;

        public TracedScopedAdapter(ScopedShadowRetrievalAdapter inner, string traceRoot)
        {
            _inner = inner; _traceRoot = traceRoot;
        }

        public string Name => _inner.Name;

        public async Task<RetrievalAdapterResult> ExecuteAsync(RetrievalAdapterRequest request, CancellationToken cancellationToken = default)
        {
            var result = await _inner.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            var traceDir = Path.Combine(_traceRoot, "shadow-adapter");
            Directory.CreateDirectory(traceDir);
            var tracePath = Path.Combine(traceDir, $"trace-{request.OperationId ?? Guid.NewGuid().ToString("N")}.jsonl");
            var entry = new
            {
                request.OperationId, request.WorkspaceId, request.CollectionId, request.QueryText,
                BaselineCount = request.BaselineCandidateIds.Count,
                result.AddedCandidateIds, result.RemovedCandidateIds,
                result.Applied, Allowlisted = _inner.IsAllowlisted(request.WorkspaceId, request.CollectionId),
                Timestamp = DateTimeOffset.UtcNow
            };
            await File.AppendAllTextAsync(tracePath, JsonSerializer.Serialize(entry) + Environment.NewLine, cancellationToken).ConfigureAwait(false);
            return new RetrievalAdapterResult
            {
                Applied = result.Applied,
                AddedCandidateIds = result.AddedCandidateIds,
                RemovedCandidateIds = result.RemovedCandidateIds,
                TracePath = tracePath,
            };
        }
    }
}