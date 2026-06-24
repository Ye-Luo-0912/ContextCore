using System.Text.Json;
using System.Text.Json.Serialization;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.FileSystem;

/// <summary>文件系统 artifact store，所有覆盖写通过原子写入完成。</summary>
public sealed class FileArtifactStore : IArtifactStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ContextCoreDataLayout _layout;
    private readonly FileSystemReader _reader;
    private readonly FileSystemWriter _writer;
    private readonly FileJsonLineStore _jsonLineStore;

    public FileArtifactStore(FileStorageOptions options)
        : this(
            new ContextCoreDataLayout(options),
            new FileSystemReader(),
            new FileSystemWriter(),
            new FileJsonLineStore(new FileFormatSerializer()))
    {
    }

    public FileArtifactStore(
        ContextCoreDataLayout layout,
        FileSystemReader reader,
        FileSystemWriter writer,
        FileJsonLineStore jsonLineStore)
    {
        _layout = layout;
        _reader = reader;
        _writer = writer;
        _jsonLineStore = jsonLineStore;
    }

    public async Task<string> WriteJsonAsync<T>(
        ArtifactDescriptor descriptor,
        T value,
        CancellationToken cancellationToken = default)
    {
        var jsonDescriptor = descriptor with { Extension = ".json" };
        var path = _layout.ResolveArtifactPath(jsonDescriptor);
        await _writer.WriteAllTextAtomicAsync(
            path,
            JsonSerializer.Serialize(value, JsonOptions),
            cancellationToken).ConfigureAwait(false);
        await UpsertManifestAsync(jsonDescriptor, path, cancellationToken).ConfigureAwait(false);
        return path;
    }

    public async Task<string> WriteMarkdownAsync(
        ArtifactDescriptor descriptor,
        string markdown,
        CancellationToken cancellationToken = default)
    {
        var markdownDescriptor = descriptor with { Extension = ".md" };
        var path = _layout.ResolveArtifactPath(markdownDescriptor);
        await _writer.WriteAllTextAtomicAsync(path, markdown, cancellationToken).ConfigureAwait(false);
        await UpsertManifestAsync(markdownDescriptor, path, cancellationToken).ConfigureAwait(false);
        return path;
    }

    public async Task<string> AppendJsonLineAsync<T>(
        ArtifactDescriptor descriptor,
        T value,
        CancellationToken cancellationToken = default)
    {
        var jsonlDescriptor = descriptor with { Extension = ".jsonl" };
        var path = _layout.ResolveArtifactPath(jsonlDescriptor);
        await _jsonLineStore.AppendAsync(path, value, cancellationToken).ConfigureAwait(false);
        await UpsertManifestAsync(jsonlDescriptor, path, cancellationToken).ConfigureAwait(false);
        return path;
    }

    public async Task<T?> ReadJsonAsync<T>(
        ArtifactDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        var path = _layout.ResolveArtifactPath(descriptor with { Extension = ".json" });
        var json = await _reader.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(json)
            ? default
            : JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    public async Task<IReadOnlyList<ArtifactManifestEntry>> ListAsync(
        ArtifactKind? kind = null,
        CancellationToken cancellationToken = default)
    {
        var entries = await _jsonLineStore
            .ReadAsync<ArtifactManifestEntry>(_layout.GetManifestPath(), cancellationToken)
            .ConfigureAwait(false);
        return
        [
            .. entries
                .Where(entry => kind is null || entry.Descriptor.Kind == kind)
                .OrderByDescending(entry => entry.UpdatedAt)
        ];
    }

    public FileLayoutStatus BuildStatus()
    {
        var manifest = _jsonLineStore
            .ReadAsync<ArtifactManifestEntry>(_layout.GetManifestPath())
            .GetAwaiter()
            .GetResult();
        var diagnostics = new List<string>();
        if (!Directory.Exists(_layout.RootPath))
        {
            diagnostics.Add("DataRootMissing");
        }

        var samples = new[]
        {
            new ArtifactDescriptor
            {
                Kind = ArtifactKind.LearningFeedback,
                WorkspaceId = "default",
                CollectionId = "test",
                CapabilityId = "feedback",
                ReportId = "learning-feedback-quality-report",
                Extension = ".json"
            },
            new ArtifactDescriptor
            {
                Kind = ArtifactKind.Vector,
                WorkspaceId = "default",
                CollectionId = "test",
                CapabilityId = "reindex",
                ReportId = "vector-index-coverage-report",
                Extension = ".json"
            },
            new ArtifactDescriptor
            {
                Kind = ArtifactKind.Graph,
                WorkspaceId = "default",
                CollectionId = "test",
                CapabilityId = "graph",
                ReportId = "graph-expansion-shadow-trace-quality-report",
                Extension = ".json"
            },
            new ArtifactDescriptor
            {
                Kind = ArtifactKind.Eval,
                CapabilityId = "p15",
                ReportId = "eval-report-p15-a3",
                Extension = ".json"
            }
        };

        return new FileLayoutStatus
        {
            DataRoot = _layout.RootPath,
            ArtifactCategories = Enum.GetNames<ArtifactKind>(),
            ResolvedPathSamples =
            [
                .. samples
                    .Select(descriptor =>
                        _layout.CreateManifestEntry(descriptor, _layout.ResolveArtifactPath(descriptor)))
            ],
            ManifestCount = manifest.Count,
            ReportCount = manifest.Count(entry => entry.Descriptor.Kind is ArtifactKind.Report or ArtifactKind.Eval
                || entry.RelativePath.Contains("/reports/", StringComparison.OrdinalIgnoreCase)),
            Diagnostics = diagnostics
        };
    }

    private async Task UpsertManifestAsync(
        ArtifactDescriptor descriptor,
        string path,
        CancellationToken cancellationToken)
    {
        var entry = _layout.CreateManifestEntry(descriptor, path);
        await _jsonLineStore.UpsertAsync(
            _layout.GetManifestPath(),
            entry,
            item => item.ArtifactId,
            cancellationToken).ConfigureAwait(false);
    }
}
