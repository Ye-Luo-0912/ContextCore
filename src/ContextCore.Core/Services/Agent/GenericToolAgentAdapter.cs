using System.Collections.Concurrent;
using System.Threading.Channels;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.Agent;

// ===========================================================================
// R23-3/R23-4：Agent Runtime Adapter 实现
//
// 设计原则（对齐 R23 规格）：
//   1. ContextCore 不直接依赖某一个 Agent SDK 的对象模型；所有 SDK 特定类型
//      保留在 Adapter 实现内部，不进入 Abstractions。
//   2. Adapter 持有 session 状态（events / injections / tool results / snapshots），
//      保存在 ConcurrentDictionary；同一 process 内多线程访问安全。
//   3. R23-3 提供 GenericToolAgentAdapter（通用 in-memory 实现）；
//      R23-4 提供 CodexAgentRuntimeAdapter + ClaudeCodeAgentRuntimeAdapter
//      （不依赖 SDK，仅命名空间占位 + RuntimeId/RuntimeKind 标识）。
//   4. 三个 adapter 共享 AgentRuntimeBase 基类（session 状态管理 + 事件流），
//      避免代码重复；各 adapter sealed 防止进一步继承污染。
//
// 设计边界：
//   - Base + Adapter 只负责 session 生命周期与事件流；不直接调用 ContextCore 内部接口
//     （如 IContextPackageBuilder）；context snapshot 组装由
//     DefaultAgentWorkspaceContextProvider 完成。
//   - Base 暴露 GetSessionState / TryAppendEvent 等 public 方法供 provider 使用。
//   - Session 关闭后所有写操作抛 InvalidOperationException；读取仍允许。
// ===========================================================================

/// <summary>
/// R23-4：Agent Runtime Adapter 抽象基类。
/// 提供 session 状态管理 + 事件流的通用实现，供具体 adapter（GenericTool/Codex/Claude）继承。
/// </summary>
/// <remarks>
/// <b>线程安全</b>：所有公共方法线程安全；session 状态使用
/// <see cref="ConcurrentDictionary{TKey, TValue}"/> + 内部锁保护订阅者列表。
///
/// <b>扩展点</b>：子类通过 <see cref="RuntimeId"/> / <see cref="RuntimeKind"/>
/// 标识自身；可选 override <see cref="CreateSessionAsync"/> 以添加 SDK 特定初始化逻辑。
/// </remarks>
public abstract class AgentRuntimeBase : IAgentRuntime
{
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, AgentSessionRecord> _sessions
        = new(StringComparer.Ordinal);

    /// <summary>构造 base。</summary>
    /// <param name="timeProvider">时间提供者（可选，默认 <see cref="TimeProvider.System"/>）。</param>
    protected AgentRuntimeBase(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>时间提供者（供子类与 session 视图使用）。</summary>
    protected TimeProvider TimeProvider => _timeProvider;

    /// <inheritdoc />
    public abstract string RuntimeId { get; }

    /// <inheritdoc />
    public abstract AgentRuntimeKind RuntimeKind { get; }

    /// <inheritdoc />
    public virtual Task<AgentSessionId> CreateSessionAsync(
        AgentSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        cancellationToken.ThrowIfCancellationRequested();

        var sessionId = new AgentSessionId
        {
            Value = $"session-{Guid.NewGuid():N}",
            RuntimeKind = RuntimeKind, // 子类决定 runtime kind
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            CreatedAt = _timeProvider.GetUtcNow()
        };

        var record = new AgentSessionRecord
        {
            SessionId = sessionId,
            CreatedAt = sessionId.CreatedAt,
            InitialTurnId = request.InitialTurnId,
            Metadata = request.Metadata
        };
        if (!_sessions.TryAdd(sessionId.Value, record))
        {
            throw new InvalidOperationException($"Session ID 冲突：{sessionId.Value}");
        }

        TryAppendEvent(record, new AgentEvent
        {
            EventId = $"evt-{Guid.NewGuid():N}",
            Session = sessionId,
            Kind = AgentEventKind.SessionCreated,
            Level = AgentEventLevel.Information,
            OccurredAt = _timeProvider.GetUtcNow(),
            TurnId = request.InitialTurnId,
            Metadata = request.Metadata
        });

        return Task.FromResult(sessionId);
    }

    /// <inheritdoc />
    public Task<bool> CloseSessionAsync(
        AgentSessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_sessions.TryGetValue(sessionId.Value, out var record))
        {
            return Task.FromResult(false);
        }

        lock (record.Lock)
        {
            if (record.IsClosed)
            {
                return Task.FromResult(false);
            }
            record.IsClosed = true;
            record.ClosedAt = _timeProvider.GetUtcNow();
        }

        TryAppendEvent(record, new AgentEvent
        {
            EventId = $"evt-{Guid.NewGuid():N}",
            Session = sessionId,
            Kind = AgentEventKind.SessionClosed,
            Level = AgentEventLevel.Information,
            OccurredAt = _timeProvider.GetUtcNow(),
            TurnId = record.CurrentTurnId
        });

        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task<bool> IsSessionActiveAsync(
        AgentSessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_sessions.TryGetValue(sessionId.Value, out var record))
        {
            return Task.FromResult(false);
        }
        return Task.FromResult(!record.IsClosed);
    }

    // ============= Public helpers（供 DefaultAgentWorkspaceContextProvider 使用） =============

    /// <summary>获取 session 状态（null = 不存在）。</summary>
    public AgentSessionRecord? GetSessionState(AgentSessionId sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        return _sessions.TryGetValue(sessionId.Value, out var record) ? record : null;
    }

    /// <summary>为指定 session 创建 <see cref="IAgentSession"/> 视图。</summary>
    /// <remarks>
    /// 返回的 session 视图本身不持有状态副本；所有操作通过 adapter 共享状态完成。
    /// </remarks>
    public IAgentSession? TryCreateSessionView(AgentSessionId sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        if (!_sessions.TryGetValue(sessionId.Value, out var record))
        {
            return null;
        }
        return new GenericToolAgentSession(this, record, _timeProvider);
    }

    /// <summary>写入事件到 session（线程安全；通知订阅者）。</summary>
    /// <remarks>
    /// 公开方法供 provider 写入 ContextInjected / ToolCallCompleted 等事件。
    /// 若 session 已关闭，事件仍写入（保留历史），但调用方应避免在 closed session 上写入。
    /// </remarks>
    public bool TryAppendEvent(AgentSessionRecord record, AgentEvent evt)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(evt);
        lock (record.Lock)
        {
            record.Events.Add(evt);
            foreach (var sub in record.Subscribers)
            {
                sub.Writer.TryWrite(evt);
            }
        }
        return true;
    }

    /// <summary>获取 adapter 当前持有的 session 数量（用于测试与诊断）。</summary>
    public int SessionCount => _sessions.Count;
}

/// <summary>
/// R23-3：通用 Agent 适配器。提供 <see cref="IAgentRuntime"/> 的 in-memory 实现。
/// </summary>
/// <remarks>
/// 适用于不需要 Agent SDK 适配的场景（如本地工具型 Agent / 测试 / 演示）。
/// 生产场景如需对接 Codex / Claude Code，请使用对应的 RuntimeAdapter（R23-4）。
/// </remarks>
public sealed class GenericToolAgentAdapter : AgentRuntimeBase
{
    /// <summary>Runtime 标识。</summary>
    public override string RuntimeId => "generic-v1";

    /// <summary>Runtime 类型。</summary>
    public override AgentRuntimeKind RuntimeKind => AgentRuntimeKind.GenericTool;

    /// <summary>构造 adapter。</summary>
    /// <param name="timeProvider">时间提供者（可选，默认 <see cref="TimeProvider.System"/>）。</param>
    public GenericToolAgentAdapter(TimeProvider? timeProvider = null)
        : base(timeProvider)
    {
    }
}

/// <summary>
/// R23-3：Agent session 内部状态记录。由 <see cref="AgentRuntimeBase"/> 管理。
/// </summary>
/// <remarks>
/// 暴露为 public 仅供 provider（<see cref="DefaultAgentWorkspaceContextProvider"/>）访问；
/// 不属于稳定公共 API，调用方不应直接修改字段。
/// </remarks>
public sealed class AgentSessionRecord
{
    /// <summary>Session 标识。</summary>
    public required AgentSessionId SessionId { get; init; }

    /// <summary>Session 创建时间（UTC）。</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>初始 turn ID（来自 <see cref="AgentSessionRequest.InitialTurnId"/>）。</summary>
    public string? InitialTurnId { get; init; }

    /// <summary>Session 元数据。</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Session 是否已关闭。</summary>
    public bool IsClosed { get; set; }

    /// <summary>关闭时间（UTC；null = 未关闭）。</summary>
    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>当前 turn ID（最近一次 StartTurn 设置；null = 尚未开始任何 turn）。</summary>
    public string? CurrentTurnId { get; set; }

    /// <summary>Session 内的事件列表（按写入顺序；不排序）。</summary>
    public List<AgentEvent> Events { get; } = new();

    /// <summary>Session 收到的 context injection 列表（按注入顺序）。</summary>
    public List<AgentContextInjection> Injections { get; } = new();

    /// <summary>Session 收到的 tool 结果列表（按注入顺序）。</summary>
    public List<AgentToolResultRecord> ToolResults { get; } = new();

    /// <summary>Session 已生成的 snapshot 列表（按创建顺序）。</summary>
    public List<AgentContextSnapshot> Snapshots { get; } = new();

    /// <summary>当前活跃的 event 订阅者列表（Channel<AgentEvent>）。</summary>
    public List<Channel<AgentEvent>> Subscribers { get; } = new();

    /// <summary>同步锁（保护 Events / Subscribers / Injections / ToolResults 等列表写入）。</summary>
    public object Lock { get; } = new();
}

/// <summary>
/// R23-3：Tool 调用结果记录。由 <see cref="IAgentSession.RecordToolCallResultAsync"/> 写入。
/// </summary>
public sealed class AgentToolResultRecord
{
    /// <summary>Tool 调用 ID。</summary>
    public required string ToolCallId { get; init; }

    /// <summary>Tool 名称。</summary>
    public required string ToolName { get; init; }

    /// <summary>Tool 输出（JSON 字符串）。</summary>
    public required string ResultJson { get; init; }

    /// <summary>Tool 结果注入时间（UTC）。</summary>
    public required DateTimeOffset IngestedAt { get; init; }
}

/// <summary>
/// R23-3：<see cref="IAgentSession"/> + <see cref="IAgentEventStream"/> 的 in-memory 实现。
/// </summary>
/// <remarks>
/// Session 视图本身不持有状态副本；所有操作通过 adapter 共享状态完成。
/// Session 关闭后写操作抛 <see cref="InvalidOperationException"/>；读取仍允许。
/// </remarks>
internal sealed class GenericToolAgentSession : IAgentSession, IAgentEventStream
{
    private readonly AgentRuntimeBase _adapter;
    private readonly AgentSessionRecord _record;
    private readonly TimeProvider _timeProvider;

    public GenericToolAgentSession(
        AgentRuntimeBase adapter,
        AgentSessionRecord record,
        TimeProvider timeProvider)
    {
        _adapter = adapter;
        _record = record;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public AgentSessionId SessionId => _record.SessionId;

    /// <inheritdoc />
    public IAgentEventStream Events => this;

    /// <inheritdoc />
    public Task<string> StartTurnAsync(string? turnId = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureNotClosed();

        var resolvedTurnId = string.IsNullOrWhiteSpace(turnId)
            ? $"turn-{Guid.NewGuid():N}"
            : turnId;

        lock (_record.Lock)
        {
            _record.CurrentTurnId = resolvedTurnId;
        }

        _adapter.TryAppendEvent(_record, new AgentEvent
        {
            EventId = $"evt-{Guid.NewGuid():N}",
            Session = _record.SessionId,
            Kind = AgentEventKind.TurnStarted,
            Level = AgentEventLevel.Information,
            OccurredAt = _timeProvider.GetUtcNow(),
            TurnId = resolvedTurnId
        });

        return Task.FromResult(resolvedTurnId);
    }

    /// <inheritdoc />
    public Task CompleteTurnAsync(string turnId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(turnId);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureNotClosed();

        _adapter.TryAppendEvent(_record, new AgentEvent
        {
            EventId = $"evt-{Guid.NewGuid():N}",
            Session = _record.SessionId,
            Kind = AgentEventKind.TurnCompleted,
            Level = AgentEventLevel.Information,
            OccurredAt = _timeProvider.GetUtcNow(),
            TurnId = turnId
        });

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RecordToolCallResultAsync(
        string toolCallId,
        string toolName,
        string resultJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolCallId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(resultJson);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureNotClosed();

        var now = _timeProvider.GetUtcNow();
        lock (_record.Lock)
        {
            _record.ToolResults.Add(new AgentToolResultRecord
            {
                ToolCallId = toolCallId,
                ToolName = toolName,
                ResultJson = resultJson,
                IngestedAt = now
            });
        }

        _adapter.TryAppendEvent(_record, new AgentEvent
        {
            EventId = $"evt-{Guid.NewGuid():N}",
            Session = _record.SessionId,
            Kind = AgentEventKind.ToolCallCompleted,
            Level = AgentEventLevel.Information,
            OccurredAt = now,
            TurnId = _record.CurrentTurnId,
            PayloadJson = resultJson,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["toolCallId"] = toolCallId,
                ["toolName"] = toolName
            }
        });

        return Task.CompletedTask;
    }

    // ============= IAgentEventStream =============

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentEvent> SubscribeAsync(
        AgentSessionId sessionId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        if (!string.Equals(sessionId.Value, _record.SessionId.Value, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"SessionId mismatch：expected {_record.SessionId.Value}，got {sessionId.Value}",
                nameof(sessionId));
        }

        var channel = Channel.CreateUnbounded<AgentEvent>();
        // 单次锁：先订阅 + 推送历史事件，保证不漏（订阅后新事件会通过 TryAppendEvent 通知本 channel）。
        lock (_record.Lock)
        {
            _record.Subscribers.Add(channel);
            foreach (var evt in _record.Events)
            {
                channel.Writer.TryWrite(evt);
            }
        }

        try
        {
            await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                yield return evt;
            }
        }
        finally
        {
            // 取消订阅：从订阅者列表移除，完成 channel 让消费者退出。
            lock (_record.Lock)
            {
                _record.Subscribers.Remove(channel);
            }
            channel.Writer.TryComplete();
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentEvent>> QueryAsync(
        AgentEventQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        IEnumerable<AgentEvent> source;
        lock (_record.Lock)
        {
            source = _record.Events.ToList();
        }

        if (query.Kind is { } kind)
        {
            source = source.Where(e => e.Kind == kind);
        }
        if (query.Level is { } level)
        {
            source = source.Where(e => e.Level == level);
        }
        if (!string.IsNullOrEmpty(query.TurnId))
        {
            source = source.Where(e => e.TurnId == query.TurnId);
        }
        if (!string.IsNullOrEmpty(query.CorrelationId))
        {
            source = source.Where(e => e.CorrelationId == query.CorrelationId);
        }
        if (query.Since is { } since)
        {
            source = source.Where(e => e.OccurredAt >= since);
        }
        if (query.Until is { } until)
        {
            source = source.Where(e => e.OccurredAt <= until);
        }

        var ordered = source.OrderBy(e => e.OccurredAt).ThenBy(e => e.EventId);
        var take = query.Take <= 0 ? int.MaxValue : query.Take;
        var result = ordered.Take(take).ToList();
        return Task.FromResult<IReadOnlyList<AgentEvent>>(result);
    }

    private void EnsureNotClosed()
    {
        if (_record.IsClosed)
        {
            throw new InvalidOperationException(
                $"Session 已关闭：{_record.SessionId.Value}；写操作不再允许。");
        }
    }
}
