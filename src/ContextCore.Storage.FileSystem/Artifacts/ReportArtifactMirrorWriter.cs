using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.FileSystem;

/// <summary>把 legacy report 输出镜像到标准 artifact layout，并写入 manifest。</summary>
public sealed class ReportArtifactMirrorWriter
{
    private readonly ContextCoreDataLayout _layout;
    private readonly FileSystemWriter _writer;
    private readonly FileJsonLineStore _jsonLineStore;
    private readonly ReportArtifactDescriptorFactory _factory;

    public ReportArtifactMirrorWriter(FileStorageOptions options)
        : this(
            new ContextCoreDataLayout(options),
            new FileSystemWriter(),
            new FileJsonLineStore(new FileFormatSerializer()),
            new ReportArtifactDescriptorFactory())
    {
    }

    public ReportArtifactMirrorWriter(
        ContextCoreDataLayout layout,
        FileSystemWriter writer,
        FileJsonLineStore jsonLineStore,
        ReportArtifactDescriptorFactory factory)
    {
        _layout = layout;
        _writer = writer;
        _jsonLineStore = jsonLineStore;
        _factory = factory;
    }

    public async Task<IReadOnlyList<string>> MirrorAsync(
        string legacyPath,
        string text,
        string? workspaceId = "default",
        string? collectionId = "test",
        string? sourceCommand = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyPath);
        var relativeLegacyPath = legacyPath.Replace('\\', '/').TrimStart('/');
        if (!ReportArtifactRegistry.ShouldMirror(relativeLegacyPath))
        {
            return Array.Empty<string>();
        }

        var snapshot = _factory.CreateSnapshot(relativeLegacyPath, workspaceId, collectionId);
        var latest = _factory.CreateLatest(snapshot);
        var paths = new List<string>(capacity: 2)
        {
            await WriteAsync(snapshot, relativeLegacyPath, text, isLatest: false, isSnapshot: true, sourceCommand, cancellationToken)
                .ConfigureAwait(false),
            await WriteAsync(latest, relativeLegacyPath, text, isLatest: true, isSnapshot: false, sourceCommand, cancellationToken)
                .ConfigureAwait(false)
        };
        return paths;
    }

    private async Task<string> WriteAsync(
        ArtifactDescriptor descriptor,
        string relativeLegacyPath,
        string text,
        bool isLatest,
        bool isSnapshot,
        string? sourceCommand,
        CancellationToken cancellationToken)
    {
        var routedPath = _layout.ResolveArtifactPath(descriptor);
        await _writer
            .WriteAllTextAtomicAsync(routedPath, text, cancellationToken)
            .ConfigureAwait(false);

        var manifestEntry = _layout.CreateManifestEntry(
            descriptor,
            routedPath,
            legacyPath: relativeLegacyPath,
            isLatest: isLatest,
            isSnapshot: isSnapshot,
            sourceCommand: sourceCommand);
        await _jsonLineStore
            .UpsertAsync(
                _layout.GetManifestPath(),
                manifestEntry,
                entry => string.IsNullOrWhiteSpace(entry.RelativePath) ? entry.ArtifactId : entry.RelativePath,
                cancellationToken)
            .ConfigureAwait(false);
        return routedPath;
    }
}
