using System.Collections.Concurrent;
using System.Text.Json;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.AgentRunRuntime;

// ===========================================================================
// DefaultAgentToolCallValidator — 默认 Tool 调用校验器
//
// 实现 IAgentToolCallValidator 的默认安全校验逻辑：
//   1. 检查 ToolName 非空（基础合法性）。
//   2. 检查 Arguments 为合法 JSON（参数 schema 合法性）。
//   3. 基于配置的危险 Tool 黑名单检查（如 file_delete、shell_exec）。
//   4. 黑名单中的 Tool 设置 RequiresApproval=true（交由 IAgentApprovalGate 二次确认）。
//
// 设计决策：
//   - 黑名单通过构造参数注入（默认包含常见危险 Tool）；
//   - 校验不通过的 Tool 返回 IsValid=false（不分派）；
//   - 黑名单匹配使用 OrdinalIgnoreCase（大小写不敏感）；
//   - 完全无副作用，可单例注册。
// ===========================================================================

/// <summary>
/// 默认 Tool 调用校验器。
/// 校验 ToolName 非空、Arguments 合法 JSON、危险 Tool 黑名单匹配。
/// </summary>
public sealed class DefaultAgentToolCallValidator : IAgentToolCallValidator
{
    /// <summary>默认危险 Tool 黑名单（file_delete / shell_exec / registry_set / process_kill）。</summary>
    public static readonly IReadOnlySet<string> DefaultDangerousTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "file_delete",
        "shell_exec",
        "registry_set",
        "process_kill"
    };

    private readonly IReadOnlySet<string> _dangerousTools;

    /// <summary>
    /// 构造默认校验器。
    /// </summary>
    /// <param name="dangerousTools">危险 Tool 黑名单（null 时使用 <see cref="DefaultDangerousTools"/>）。</param>
    public DefaultAgentToolCallValidator(IReadOnlySet<string>? dangerousTools = null)
    {
        _dangerousTools = dangerousTools ?? DefaultDangerousTools;
    }

    /// <inheritdoc />
    public ValueTask<AgentToolCallValidationResult> ValidateAsync(
        string runId,
        AgentToolCallRequest toolCall,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        // 1. ToolName 非空校验
        if (string.IsNullOrWhiteSpace(toolCall.ToolName))
        {
            return ValueTask.FromResult(new AgentToolCallValidationResult
            {
                IsValid = false,
                Error = "ToolName 不能为空。",
                RequiresApproval = false
            });
        }

        // 2. Arguments 合法 JSON 校验
        if (string.IsNullOrWhiteSpace(toolCall.Arguments))
        {
            return ValueTask.FromResult(new AgentToolCallValidationResult
            {
                IsValid = false,
                Error = "Arguments 不能为空（必须为合法 JSON）。",
                RequiresApproval = false
            });
        }

        try
        {
            using var doc = JsonDocument.Parse(toolCall.Arguments);
            _ = doc.RootElement;
        }
        catch (JsonException ex)
        {
            return ValueTask.FromResult(new AgentToolCallValidationResult
            {
                IsValid = false,
                Error = $"Arguments 不是合法 JSON：{ex.Message}",
                RequiresApproval = false
            });
        }

        // 3. 危险 Tool 黑名单检查（匹配则需审批）
        if (_dangerousTools.Contains(toolCall.ToolName))
        {
            return ValueTask.FromResult(new AgentToolCallValidationResult
            {
                IsValid = true,
                Error = null,
                RequiresApproval = true,
                ApprovalReason = $"Tool '{toolCall.ToolName}' 在危险操作黑名单中，需人工审批后执行。"
            });
        }

        // 4. 普通校验通过
        return ValueTask.FromResult(new AgentToolCallValidationResult
        {
            IsValid = true,
            Error = null,
            RequiresApproval = false
        });
    }
}
