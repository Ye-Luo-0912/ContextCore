namespace ContextCore.Embedding;

/// <summary>按需加载并在空闲后卸载 ONNX embedding 会话。</summary>
/// <remarks>
/// P5-0.1：使用 SemaphoreSlim 实现 single-flight 加载，确保同一时刻只有一个 Session 被创建。
/// 并发请求中，loser 创建的 Session 会被释放，避免原生内存和模型资源泄漏。
/// </remarks>
public sealed class OnnxEmbeddingSessionManager
{
    private readonly IOnnxEmbeddingSessionFactory _factory;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly object _gate = new();
    private readonly EmbeddingOptions _options;
    private IOnnxEmbeddingSession? _session;
    private DateTimeOffset? _lastUsedAt;

    public OnnxEmbeddingSessionManager(
        EmbeddingOptions options,
        IOnnxEmbeddingSessionFactory? factory = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _factory = factory ?? new OnnxRuntimeEmbeddingSessionFactory();
    }

    public bool IsLoaded
    {
        get
        {
            lock (_gate)
            {
                return _session is not null;
            }
        }
    }

    public int LoadCount { get; private set; }

    public DateTimeOffset? LastUsedAt
    {
        get
        {
            lock (_gate)
            {
                return _lastUsedAt;
            }
        }
    }

    public async Task<IOnnxEmbeddingSession> GetSessionAsync(
        CancellationToken cancellationToken = default)
    {
        // Fast path: session already loaded
        lock (_gate)
        {
            if (_session is not null)
            {
                _lastUsedAt = DateTimeOffset.UtcNow;
                return _session;
            }
        }

        // Single-flight: only one request creates the session at a time
        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check after acquiring the gate
            lock (_gate)
            {
                if (_session is not null)
                {
                    _lastUsedAt = DateTimeOffset.UtcNow;
                    return _session;
                }
            }

            var created = await _factory.CreateAsync(_options, cancellationToken).ConfigureAwait(false);

            lock (_gate)
            {
                // Another request may have loaded a session while we were creating
                if (_session is not null)
                {
                    _lastUsedAt = DateTimeOffset.UtcNow;
                    // We are the loser: dispose our created session to prevent resource leak
                    _ = DisposeSessionAsync(created);
                    return _session;
                }

                _session = created;
                _lastUsedAt = DateTimeOffset.UtcNow;
                LoadCount++;
                return _session;
            }
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private static async ValueTask DisposeSessionAsync(IOnnxEmbeddingSession session)
    {
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best-effort disposal; swallow exceptions to avoid masking the caller's path
        }
    }

    public async Task<bool> UnloadIfIdleAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IOnnxEmbeddingSession? toDispose = null;
        lock (_gate)
        {
            if (_session is null || _lastUsedAt is null)
            {
                return false;
            }

            if (now - _lastUsedAt.Value < _options.IdleUnloadAfter)
            {
                return false;
            }

            toDispose = _session;
            _session = null;
            _lastUsedAt = null;
        }

        await toDispose.DisposeAsync().ConfigureAwait(false);
        return true;
    }

    public async Task ForceUnloadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IOnnxEmbeddingSession? toDispose = null;
        lock (_gate)
        {
            toDispose = _session;
            _session = null;
            _lastUsedAt = null;
        }

        if (toDispose is not null)
        {
            await toDispose.DisposeAsync().ConfigureAwait(false);
        }
    }

}
