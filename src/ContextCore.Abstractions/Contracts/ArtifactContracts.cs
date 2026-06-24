using ContextCore.Abstractions.Models;

namespace ContextCore.Abstractions;

/// <summary>统一解析 ContextCore 数据与报告 artifact 的文件系统路径。</summary>
public interface IContextPathResolver
{
    string RootPath { get; }

    string SanitizeSegment(string value);

    string ResolveArtifactPath(ArtifactDescriptor descriptor);

    string GetRelativePath(string fullPath);
}

/// <summary>统一 artifact 写入入口；负责原子写、JSONL 追加和 manifest 更新。</summary>
public interface IArtifactStore
{
    Task<string> WriteJsonAsync<T>(
        ArtifactDescriptor descriptor,
        T value,
        CancellationToken cancellationToken = default);

    Task<string> WriteMarkdownAsync(
        ArtifactDescriptor descriptor,
        string markdown,
        CancellationToken cancellationToken = default);

    Task<string> AppendJsonLineAsync<T>(
        ArtifactDescriptor descriptor,
        T value,
        CancellationToken cancellationToken = default);

    Task<T?> ReadJsonAsync<T>(
        ArtifactDescriptor descriptor,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ArtifactManifestEntry>> ListAsync(
        ArtifactKind? kind = null,
        CancellationToken cancellationToken = default);
}
