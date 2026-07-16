using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Client;

/// <summary>注册 <see cref="ContextCoreClient"/> 时使用的基础 HTTP 配置。</summary>
public sealed class ContextCoreClientOptions
{
    /// <summary>ContextCore.Service 的根地址，通常指向本机服务或远端网关。</summary>
    public Uri BaseAddress { get; set; } = new("http://localhost:5079");

    /// <summary>
    /// API Key 值，将自动注入到每个请求的 <see cref="ApiKeyHeaderName"/> 头。
    /// 若为空则不注入。通常从私有配置（~/.contextcore/secrets.json）读取。
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>API Key 的请求头名称，需与服务端 Security:ApiKeyHeaderName 一致。默认 X-ContextCore-Key。</summary>
    public string ApiKeyHeaderName { get; set; } = "X-ContextCore-Key";
}

/// <summary>记忆晋升/拒绝/废弃接口的客户端请求模型。</summary>
public sealed class ContextCoreMemoryPromotionRequest
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string SourceMemoryId { get; init; } = string.Empty;

    public string Strategy { get; init; } = "manual";

    public string? Reason { get; init; }

    public double Confidence { get; init; } = 1.0;

    public string? Reviewer { get; init; }
}

/// <summary>工作记忆集合范围请求模型，用于清空当前集合的工作记忆状态。</summary>
public sealed class ContextCoreWorkingMemoryScopeRequest
{
    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;
}

/// <summary>关系查询接口的客户端响应模型。</summary>
public sealed class ContextCoreRelationsResponse
{
    public string ItemId { get; init; } = string.Empty;

    public IReadOnlyList<ContextRelation> Outgoing { get; init; } = Array.Empty<ContextRelation>();

    public IReadOnlyList<ContextRelation> Incoming { get; init; } = Array.Empty<ContextRelation>();
}
