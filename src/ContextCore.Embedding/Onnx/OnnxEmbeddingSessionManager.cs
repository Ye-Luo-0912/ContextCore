namespace ContextCore.Embedding;

/// <summary>按需加载并在空闲后卸载 ONNX embedding 会话。</summary>
public sealed class OnnxEmbeddingSessionManager
{
    private readonly IOnnxEmbeddingSessionFactory _factory;
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
        IOnnxEmbeddingSession? existing;
        lock (_gate)
        {
            existing = _session;
            if (existing is not null)
            {
                _lastUsedAt = DateTimeOffset.UtcNow;
                return existing;
            }
        }

        var created = await _factory.CreateAsync(_options, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (_session is not null)
            {
                _lastUsedAt = DateTimeOffset.UtcNow;
                return _session;
            }

            _session = created;
            _lastUsedAt = DateTimeOffset.UtcNow;
            LoadCount++;
            return _session;
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
