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

        try
        {
            var result = await _generated.Api.Context[id].GetAsync(config =>
            {
                config.QueryParameters.WorkspaceId = workspaceId;
                config.QueryParameters.CollectionId = collectionId;
            }, cancellationToken).ConfigureAwait(false);
            return MapToAbstraction<ContextItem>(result)
                ?? throw new InvalidOperationException($"ContextCore returned an empty response for GET api/context/{id}.");
        }
        catch (ContextCore.Client.Generated.Models.ContextCoreErrorResponse ex)
        {
            throw ToApiException(ex);
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex)
        {
            throw ToApiException(ex);
        }
    }

    public async Task<ContextQueryResponse> QueryContextAsync(
        ContextQuery query,
        CancellationToken cancellationToken = default)
    {
        // Query 端点的生成 RequestBuilder 无 errorMapping，400 错误会丢失结构化错误体，
        // 保留直接 HttpClient + STJ 反序列化以正确读取错误响应。
        ArgumentNullException.ThrowIfNull(query);
        var items = await PostRequiredAsync<ContextQuery, IReadOnlyList<ContextItem>>(
            "api/context/query", query, cancellationToken).ConfigureAwait(false);
        return new ContextQueryResponse
        {
            Items = items,
            Count = items.Count
        };
    }
}
