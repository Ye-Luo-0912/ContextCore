using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Client;

public sealed partial class ContextCoreClient
{
    // 注意：Runtime 域的响应模型包含 UntypedNode / composed type 字段（来自 OpenAPI union type），
    // Kiota 的 JsonSerializationWriter 在序列化 null UntypedNode 时会破坏 JSON 结构。
    // 因此这些端点保留直接 HttpClient + STJ 反序列化，不走生成客户端的 JSON round-trip。

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
}
