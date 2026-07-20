using System.Collections.Concurrent;
using System.Text.Json;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.Agent;

// ===========================================================================
// R23-3：DefaultAgentWorkspaceContextProvider — 默认 Agent 工作空间上下文 provider。
//
// 目标（对齐 R23 规格）：
//   1. 实现 IAgentWorkspaceContextProvider 的 3 个方法：
//      - GetContextSnapshotAsync：从 session 注入 + tool 结果组装 token-budget-bounded snapshot
//      - InjectAsync：保存决策/约束/free text 到 session 状态
//      - IngestToolResultAsync：摄入 tool 调用结果到 session 状态
//   2. 基础实现：不调用 ContextCore 内部接口（IContextPackageBuilder 等）；
//      真正的 ContextCore 集成（按相关性检索 / 决策注入）由 R23-4 完成。
//   3. Snapshot token 估算：粗略按 Content.Length / 4（≈ 1 token per 4 chars）；
//      生产实现应替换为真实 tokenizer（参考 ContextTokenizers）。
//
// 设计边界：
//   - Provider 持有 GenericToolAgentAdapter 引用（共享 session 状态），不重复存储；
//   - Provider 线程安全：所有写操作通过 adapter 的 TryAppendEvent（内部锁）完成；
//   - Snapshot 序列化使用 System.Text.Json（默认 camelCase + 不转义非 ASCII）。
// ===========================================================================

/// <summary>
/// R23-3：默认 <see cref="IAgentWorkspaceContextProvider"/> 实现。
/// </summary>
/// <remarks>
/// 将 agent 注入内容与 tool 结果打包为 token-budget-bounded snapshot。
/// 不直接调用 ContextCore 内部接口；本实现仅负责 session 级上下文聚合。
///
/// <b>Token 估算</b>：1 token ≈ 4 chars（粗略；生产实现应替换为真实 tokenizer）。
/// <b>Snapshot 序列化</b>：使用 <see cref="JsonSerializer"/> 默认配置（camelCase）。
/// </remarks>
public sealed class DefaultAgentWorkspaceContextProvider : IAgentWorkspaceContextProvider
{
    /// <summary>粗略 token 估算：1 token ≈ 4 chars。</summary>
    public const int CharsPerToken = 4;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly AgentRuntimeBase _adapter;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, AgentContextSnapshot?> _lastSnapshotBySession
        = new(StringComparer.Ordinal);

    /// <summary>构造 provider。</summary>
    /// <param name="adapter">Agent adapter（共享 session 状态；支持 GenericTool/Codex/Claude）。</param>
    /// <param name="timeProvider">时间提供者（可选，默认 <see cref="TimeProvider.System"/>）。</param>
    public DefaultAgentWorkspaceContextProvider(
        AgentRuntimeBase adapter,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        _adapter = adapter;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public Task<AgentContextSnapshotRef> GetContextSnapshotAsync(
        AgentSessionId sessionId,
        int tokenBudget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        if (tokenBudget <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tokenBudget), tokenBudget,
                "Token budget must be > 0");
        }
        cancellationToken.ThrowIfCancellationRequested();

        var record = _adapter.GetSessionState(sessionId)
            ?? throw new InvalidOperationException($"Session not found: {sessionId.Value}");

        // 组装 sections：从 injections + tool results 提取内容
        var sections = new List<AgentContextSection>();
        var sortCounter = 0;

        // Section 1: 注入的 free text + 决策/约束引用
        var injectionRecords = new List<AgentContextInjection>();
        lock (record.Lock)
        {
            injectionRecords.AddRange(record.Injections);
        }
        if (injectionRecords.Count > 0)
        {
            var content = BuildInjectionContent(injectionRecords);
            sections.Add(BuildSection("injections", sortCounter++, content, "Agent.Injected"));
        }

        // Section 2: tool 结果
        var toolResults = new List<AgentToolResultRecord>();
        lock (record.Lock)
        {
            toolResults.AddRange(record.ToolResults);
        }
        if (toolResults.Count > 0)
        {
            var content = BuildToolResultContent(toolResults);
            sections.Add(BuildSection("tool-results", sortCounter++, content, "Agent.ToolResult"));
        }

        // 汇总决策 / 约束 ID
        var decisionIds = injectionRecords
            .SelectMany(i => i.DecisionRequestIds)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var constraintIds = injectionRecords
            .SelectMany(i => i.ConstraintIds)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var toolCallRefs = toolResults
            .ToDictionary(t => t.ToolCallId, t => t.ToolName, StringComparer.Ordinal);

        // Token 估算 + 截断
        var actualTokens = sections.Sum(s => s.ActualTokens);
        var sectionBudget = tokenBudget;
        var truncatedSections = new List<AgentContextSection>();
        var remainingBudget = tokenBudget;
        foreach (var section in sections.OrderBy(s => s.SortOrder))
        {
            if (remainingBudget <= 0)
            {
                break;
            }
            var sectionTokens = Math.Min(section.ActualTokens, remainingBudget);
            var truncatedContent = TruncateContent(section.Content, sectionTokens);
            truncatedSections.Add(section with
            {
                TokenBudget = sectionTokens,
                ActualTokens = sectionTokens,
                Content = truncatedContent
            });
            remainingBudget -= sectionTokens;
        }

        var now = _timeProvider.GetUtcNow();
        var snapshot = new AgentContextSnapshot
        {
            SnapshotId = $"snap-{Guid.NewGuid():N}",
            Session = sessionId,
            CreatedAt = now,
            TokenBudget = tokenBudget,
            ActualTokens = tokenBudget - remainingBudget,
            Sections = truncatedSections,
            DecisionRequestIds = decisionIds,
            ConstraintIds = constraintIds,
            ToolCallRefs = toolCallRefs,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["source"] = "DefaultAgentWorkspaceContextProvider",
                ["injectionCount"] = injectionRecords.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["toolResultCount"] = toolResults.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["charsPerToken"] = CharsPerToken.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }
        };

        // 保存 snapshot 到 session state（供 delta 计算 + 审计）
        lock (record.Lock)
        {
            record.Snapshots.Add(snapshot);
        }

        // 缓存 last snapshot（供 delta 计算）
        _lastSnapshotBySession[sessionId.Value] = snapshot;

        // 序列化 snapshot → AgentContextSnapshotRef
        var contentJson = JsonSerializer.Serialize(snapshot, JsonOptions);

        var snapshotRef = new AgentContextSnapshotRef
        {
            SnapshotId = snapshot.SnapshotId,
            Session = sessionId,
            CreatedAt = now,
            ActualTokens = snapshot.ActualTokens,
            TokenBudget = tokenBudget,
            ContentJson = contentJson,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["schemaVersion"] = snapshot.SchemaVersion,
                ["sectionCount"] = snapshot.Sections.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["decisionCount"] = snapshot.DecisionRequestIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["constraintCount"] = snapshot.ConstraintIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["toolCallRefCount"] = snapshot.ToolCallRefs.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }
        };

        return Task.FromResult(snapshotRef);
    }

    /// <inheritdoc />
    public Task InjectAsync(
        AgentSessionId sessionId,
        AgentContextInjection injection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(injection);
        cancellationToken.ThrowIfCancellationRequested();

        var record = _adapter.GetSessionState(sessionId)
            ?? throw new InvalidOperationException($"Session not found: {sessionId.Value}");

        if (record.IsClosed)
        {
            throw new InvalidOperationException(
                $"Session 已关闭：{sessionId.Value}；injection 不再允许。");
        }

        lock (record.Lock)
        {
            record.Injections.Add(injection);
        }

        _adapter.TryAppendEvent(record, new AgentEvent
        {
            EventId = $"evt-{Guid.NewGuid():N}",
            Session = sessionId,
            Kind = AgentEventKind.ContextInjected,
            Level = AgentEventLevel.Information,
            OccurredAt = _timeProvider.GetUtcNow(),
            TurnId = record.CurrentTurnId,
            PayloadJson = JsonSerializer.Serialize(injection, JsonOptions),
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["injectionId"] = injection.InjectionId,
                ["decisionCount"] = injection.DecisionRequestIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["constraintCount"] = injection.ConstraintIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }
        });

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task IngestToolResultAsync(
        AgentSessionId sessionId,
        string toolCallId,
        string toolName,
        string resultJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolCallId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(resultJson);
        cancellationToken.ThrowIfCancellationRequested();

        var record = _adapter.GetSessionState(sessionId)
            ?? throw new InvalidOperationException($"Session not found: {sessionId.Value}");

        if (record.IsClosed)
        {
            throw new InvalidOperationException(
                $"Session 已关闭：{sessionId.Value}；tool result ingestion 不再允许。");
        }

        var now = _timeProvider.GetUtcNow();
        lock (record.Lock)
        {
            record.ToolResults.Add(new AgentToolResultRecord
            {
                ToolCallId = toolCallId,
                ToolName = toolName,
                ResultJson = resultJson,
                IngestedAt = now
            });
        }

        _adapter.TryAppendEvent(record, new AgentEvent
        {
            EventId = $"evt-{Guid.NewGuid():N}",
            Session = sessionId,
            Kind = AgentEventKind.ToolCallCompleted,
            Level = AgentEventLevel.Information,
            OccurredAt = now,
            TurnId = record.CurrentTurnId,
            PayloadJson = resultJson,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["toolCallId"] = toolCallId,
                ["toolName"] = toolName
            }
        });

        return Task.CompletedTask;
    }

    // ============= 辅助方法 =============

    /// <summary>获取指定 session 最近一次 snapshot（null = 尚未生成）。</summary>
    /// <remarks>供测试 / delta 计算使用。</remarks>
    public AgentContextSnapshot? GetLastSnapshot(AgentSessionId sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        return _lastSnapshotBySession.TryGetValue(sessionId.Value, out var snap) ? snap : null;
    }

    private static AgentContextSection BuildSection(
        string sectionName,
        int sortOrder,
        string content,
        string source)
    {
        var actualTokens = EstimateTokens(content);
        return new AgentContextSection
        {
            SectionName = sectionName,
            SortOrder = sortOrder,
            // TokenBudget 由后续截断阶段填充；此处先填 ActualTokens 用于计算总需求。
            TokenBudget = actualTokens,
            ActualTokens = actualTokens,
            Content = content,
            Source = source
        };
    }

    private static string BuildInjectionContent(IReadOnlyList<AgentContextInjection> injections)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var inj in injections)
        {
            sb.AppendLine($"# Injection {inj.InjectionId} (at {inj.InjectedAt:o})");
            if (inj.DecisionRequestIds.Count > 0)
            {
                sb.AppendLine($"  Decisions: {string.Join(", ", inj.DecisionRequestIds)}");
            }
            if (inj.ConstraintIds.Count > 0)
            {
                sb.AppendLine($"  Constraints: {string.Join(", ", inj.ConstraintIds)}");
            }
            if (!string.IsNullOrEmpty(inj.FreeText))
            {
                sb.AppendLine("  Free text:");
                sb.AppendLine(inj.FreeText);
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string BuildToolResultContent(IReadOnlyList<AgentToolResultRecord> toolResults)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var tr in toolResults)
        {
            sb.AppendLine($"## Tool call {tr.ToolCallId} ({tr.ToolName}) at {tr.IngestedAt:o}");
            sb.AppendLine(tr.ResultJson);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static int EstimateTokens(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return 0;
        }
        return (content.Length + CharsPerToken - 1) / CharsPerToken;
    }

    private static string TruncateContent(string content, int maxTokens)
    {
        if (string.IsNullOrEmpty(content) || maxTokens <= 0)
        {
            return string.Empty;
        }
        var maxChars = maxTokens * CharsPerToken;
        if (content.Length <= maxChars)
        {
            return content;
        }
        return content.Substring(0, maxChars);
    }
}
