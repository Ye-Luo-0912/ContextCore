namespace ContextCore.Abstractions.Models;

/// <summary>
/// 输入层统一命令，作为 Service Alpha 之前的标准化入口。
/// 旧的 ContextItem 摄取会先适配到该命令，再进入输入 pipeline。
/// </summary>
public sealed class ContextInputCommand
{
    public string OperationId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public string InputKind { get; init; } = string.Empty;

    public ContextContentFormat ContentFormat { get; init; } = ContextContentFormat.PlainText;

    public string Content { get; init; } = string.Empty;

    public string? SessionId { get; init; }

    public string? Mode { get; init; }

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public Dictionary<string, string> Metadata { get; init; } = new();
}
