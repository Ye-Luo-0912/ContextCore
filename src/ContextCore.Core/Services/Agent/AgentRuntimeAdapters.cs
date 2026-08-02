using ContextCore.Abstractions;

namespace ContextCore.Core.Services.Agent;

// ===========================================================================
// Codex / Claude Code Agent Runtime Adapter
//
// 目标（对齐 R23 规格）：
//   1. 提供 AgentRuntimeKind=Codex / ClaudeCode 的具体 adapter 实现；
//      共享 AgentRuntimeBase 的 session 状态管理 + 事件流逻辑。
//   2. ContextCore 不直接依赖 OpenAI / Anthropic SDK；本实现仅提供命名空间占位 +
//      RuntimeId/RuntimeKind 标识，外部项目可通过 override CreateSessionAsync
//      扩展为真实 SDK 适配（如调用 SDK 初始化接口）。
//   3. sealed 防止进一步继承污染。
//
// 设计边界：
//   - Adapter 不调用 SDK；外部项目继承此类或装饰此类以添加 SDK 集成。
//   - 不同 RuntimeKind 的 session 状态在 base 中通过 ConcurrentDictionary 隔离；
//     跨 adapter 互不影响。
// ===========================================================================

/// <summary>
/// OpenAI Codex Agent Runtime Adapter。
/// </summary>
/// <remarks>
/// 提供 <see cref="AgentRuntimeKind.Codex"/> 标识 + 共享 base 实现。
///
/// <b>SDK 集成扩展点</b>：实际项目对接 OpenAI Codex SDK 时，可继承此类并 override
/// <see cref="AgentRuntimeBase.CreateSessionAsync"/> 以调用 SDK 的 session 初始化接口。
/// 本实现不直接依赖任何 SDK；保持 ContextCore 与 SDK 解耦。
/// </remarks>
public sealed class CodexAgentRuntimeAdapter : AgentRuntimeBase
{
    /// <summary>Runtime 标识。</summary>
    public override string RuntimeId => "codex-v1";

    /// <summary>Runtime 类型。</summary>
    public override AgentRuntimeKind RuntimeKind => AgentRuntimeKind.Codex;

    /// <summary>构造 adapter。</summary>
    /// <param name="timeProvider">时间提供者（可选，默认 <see cref="TimeProvider.System"/>）。</param>
    public CodexAgentRuntimeAdapter(TimeProvider? timeProvider = null)
        : base(timeProvider)
    {
    }
}

/// <summary>
/// Anthropic Claude Code Agent Runtime Adapter。
/// </summary>
/// <remarks>
/// 提供 <see cref="AgentRuntimeKind.ClaudeCode"/> 标识 + 共享 base 实现。
///
/// <b>SDK 集成扩展点</b>：实际项目对接 Anthropic Claude Code SDK 时，可继承此类并
/// override <see cref="AgentRuntimeBase.CreateSessionAsync"/> 以调用 SDK 的 session
/// 初始化接口。本实现不直接依赖任何 SDK；保持 ContextCore 与 SDK 解耦。
/// </remarks>
public sealed class ClaudeCodeAgentRuntimeAdapter : AgentRuntimeBase
{
    /// <summary>Runtime 标识。</summary>
    public override string RuntimeId => "claude-code-v1";

    /// <summary>Runtime 类型。</summary>
    public override AgentRuntimeKind RuntimeKind => AgentRuntimeKind.ClaudeCode;

    /// <summary>构造 adapter。</summary>
    /// <param name="timeProvider">时间提供者（可选，默认 <see cref="TimeProvider.System"/>）。</param>
    public ClaudeCodeAgentRuntimeAdapter(TimeProvider? timeProvider = null)
        : base(timeProvider)
    {
    }
}
