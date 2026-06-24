using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.FileSystem.Stores;

/// <summary>文件系统版 Router shadow trace 存储；按 requestId upsert，避免重复采集噪声。</summary>
public sealed class FileRouterIntentShadowTraceStore : IRouterIntentShadowTraceStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FileJsonLineStore _jsonLines;
    private readonly FilePathResolver _paths;

    public FileRouterIntentShadowTraceStore(FileStorageOptions options)
        : this(new FilePathResolver(options), new FileFormatSerializer())
    {
    }

    public FileRouterIntentShadowTraceStore(FilePathResolver paths, FileFormatSerializer serializer)
    {
        _paths = paths;
        _jsonLines = new FileJsonLineStore(serializer);
    }

    public async Task SaveAsync(
        RouterIntentShadowTrace trace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trace);
        if (string.IsNullOrWhiteSpace(trace.WorkspaceId) || string.IsNullOrWhiteSpace(trace.CollectionId))
        {
            return;
        }

        var path = _paths.GetRouterShadowTracesJsonlPath(trace.WorkspaceId, trace.CollectionId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _jsonLines.UpsertAsync(
                    path,
                    trace,
                    item => string.IsNullOrWhiteSpace(item.RequestId) ? Guid.NewGuid().ToString("N") : item.RequestId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<RouterIntentShadowTrace>> QueryAsync(
        RouterIntentShadowTraceQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query.WorkspaceId) || string.IsNullOrWhiteSpace(query.CollectionId))
        {
            return [];
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var take = query.Take > 0 ? query.Take : 50;
            var traces = await ReadTraceFilesAsync(query.WorkspaceId, query.CollectionId, cancellationToken)
                .ConfigureAwait(false);
            return [.. traces
                .Where(item => Matches(query.EntryPoint, item.EntryPoint))
                .OrderByDescending(item => item.CreatedAt)
                .Take(take)];
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<RouterIntentShadowTrace>> ReadTraceFilesAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken)
    {
        var results = new List<RouterIntentShadowTrace>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in EnumerateTraceFiles(workspaceId, collectionId))
        {
            var traces = await _jsonLines.ReadAsync<RouterIntentShadowTrace>(path, cancellationToken)
                .ConfigureAwait(false);
            foreach (var trace in traces)
            {
                var key = trace.RequestId;
                if (string.IsNullOrWhiteSpace(key) || keys.Add(key))
                {
                    results.Add(trace);
                }
            }
        }

        return results;
    }

    private IReadOnlyList<string> EnumerateTraceFiles(string workspaceId, string collectionId)
    {
        var files = new List<string>();
        var directory = _paths.GetRouterShadowTracesDirectory(workspaceId, collectionId);
        if (Directory.Exists(directory))
        {
            files.AddRange(Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc));
        }

        var legacyPath = _paths.GetLegacyRouterShadowTracesJsonlPath(workspaceId, collectionId);
        if (File.Exists(legacyPath))
        {
            files.Add(legacyPath);
        }

        return files;
    }

    private static bool Matches(string? expected, string? actual)
    {
        return string.IsNullOrWhiteSpace(expected)
            || string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
    }
}
