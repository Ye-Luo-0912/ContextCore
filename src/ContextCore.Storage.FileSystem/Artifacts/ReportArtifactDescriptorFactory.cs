using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.FileSystem;

/// <summary>根据旧报告路径创建标准 report artifact descriptor。</summary>
public sealed class ReportArtifactDescriptorFactory
{
    public ArtifactDescriptor CreateSnapshot(
        string legacyPath,
        string? workspaceId,
        string? collectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyPath);
        var normalized = legacyPath.Replace('\\', '/').TrimStart('/');
        if (!ReportArtifactRegistry.TryClassify(normalized, out var kind, out var capabilityId, out var providerId))
        {
            throw new InvalidOperationException($"unsupported report artifact path: {legacyPath}");
        }

        var extension = Path.GetExtension(normalized);
        var reportId = Path.GetFileNameWithoutExtension(normalized);
        return new ArtifactDescriptor
        {
            Kind = kind,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            CapabilityId = capabilityId,
            ProviderId = providerId,
            ReportId = reportId,
            DateShard = DateTimeOffset.UtcNow.ToString("yyyyMMdd"),
            Extension = string.IsNullOrWhiteSpace(extension) ? ".json" : extension
        };
    }

    public ArtifactDescriptor CreateLatest(ArtifactDescriptor snapshot)
        => snapshot with
        {
            OperationId = null,
            ReportId = "latest",
            DateShard = null
        };
}
