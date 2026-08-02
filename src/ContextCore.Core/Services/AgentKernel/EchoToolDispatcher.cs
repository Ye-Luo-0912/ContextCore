using ContextCore.Abstractions;

namespace ContextCore.Core.Services.AgentKernel;

// ===========================================================================
// EchoToolDispatcher — Echo Tool Dispatcher（测试用）
//
// 目标：
//   1. 实现 IToolDispatcher 的最简版本。
//   2. 仅支持 "echo" tool：原样返回 request.Payload。
//   3. 用于测试和单机部署验证 Kernel 循环。
// ===========================================================================

/// <summary>
/// Echo Tool Dispatcher（测试用，原样返回 payload）。
/// </summary>
/// <remarks>
/// 仅支持 <c>"echo"</c> tool。DispatchAsync 原样返回 <see cref="ToolDispatchRequest.Payload"/>。
/// </remarks>
public sealed class EchoToolDispatcher : IToolDispatcher
{
    private static readonly IReadOnlySet<string> s_supportedTools =
        new HashSet<string>(StringComparer.Ordinal) { "echo" };

    /// <inheritdoc />
    public IReadOnlySet<string> SupportedTools => s_supportedTools;

    /// <inheritdoc />
    /// <summary>echo 无前置声明（纯函数），返回 null。</summary>
    public ToolDescriptor? GetDescriptor(string toolName) => null;

    /// <inheritdoc />
    public ValueTask<ToolDispatchResult> DispatchAsync(ToolDispatchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(new ToolDispatchResult
        {
            Succeeded = true,
            Result = request.Payload,
            Duration = TimeSpan.Zero,
            // echo 是纯函数，无副作用，恢复时可安全重放
            SideEffect = ToolSideEffect.None
        });
    }
}
