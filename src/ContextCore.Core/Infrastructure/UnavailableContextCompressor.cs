using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core;

/// <summary>当配置的压缩提供商暂不可用时使用的诊断压缩器。</summary>
/// <remarks>
/// 正常生产路径应使用 <see cref="LlmContextCompressor"/>。该类用于测试，或让未来提供商在实现前明确失败。
/// </remarks>
public sealed class UnavailableContextCompressor : IContextCompressor
{
    private readonly string _provider;

    public UnavailableContextCompressor(string provider)
    {
        _provider = string.IsNullOrWhiteSpace(provider) ? "unknown" : provider;
    }

    public Task<CompressionResponse> CompressAsync(
        CompressionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var operationId = string.IsNullOrWhiteSpace(request.OperationId)
            ? Guid.NewGuid().ToString("N")
            : request.OperationId;
        var now = DateTimeOffset.UtcNow;

        return Task.FromResult(new CompressionResponse
        {
            OperationId = operationId,
            Status = CompressionStatus.Failed,
            Errors =
            [
                new ContextError
                {
                    Code = "CompressionProviderUnavailable",
                    Message = $"已配置压缩提供商 '{_provider}'，但该提供商尚未实现。",
                    Detail = "开发或测试环境请使用 Compression:Provider = \"mock\"；生产环境请使用已实现的 LLM 压缩器。"
                }
            ],
            CreatedAt = now,
            CompletedAt = now
        });
    }
}
