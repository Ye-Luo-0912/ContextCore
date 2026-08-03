using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Client;

public sealed partial class ContextCoreClient
{
    public async Task<ContextInputIngestionResult> IngestAsync(
        ContextInputCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return await PostRequiredAsync<ContextInputCommand, ContextInputIngestionResult>(
            "api/context/ingest", command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextItem> IngestAsync(ContextItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var result = await PostRequiredAsync<ContextItem, ContextInputIngestionResult>(
            "api/context/ingest", item, cancellationToken).ConfigureAwait(false);
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
        var qs = new QueryBuilder()
            .Add("collectionId", collectionId)
            .Add("workspaceId", workspaceId);
        return await GetRequiredAsync<ContextItem>($"api/context/{Escape(id)}{qs}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextQueryPage> QueryContextAsync(
        ContextQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        // 端点返回 ContextQueryPage（Items + 不透明 NextCursor + HasMore + QueryRevision）。
        // 调用方分页时透传上一页的 NextCursor，无需自行构造 keyset 游标。
        return await PostRequiredAsync<ContextQuery, ContextQueryPage>(
            "api/context/query", query, cancellationToken).ConfigureAwait(false);
    }
}
