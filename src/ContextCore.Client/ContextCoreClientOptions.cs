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

/// <summary>
/// 旧版 /api/status 客户端响应模型。
/// 新代码请改用 <see cref="RuntimeStatusResponse"/> / <see cref="RuntimeReadinessResponse"/>。
/// </summary>
public sealed class ContextCoreStatusResponse
{
    public string Status { get; init; } = string.Empty;

    public DateTimeOffset Utc { get; init; }

    public ContextCoreStorageStatus Storage { get; init; } = new();

    /// <summary>
    /// /api/status 的 readiness 摘要。
    /// 该属性补齐后，客户端可以直接消费服务端的显式成功 DTO，而不再忽略 readiness 信息。
    /// </summary>
    public ContextCoreReadinessStatus Readiness { get; init; } = new();

    public ContextCoreJobStatus Jobs { get; init; } = new();

    public string RetrievalBaseline { get; init; } = string.Empty;

    public IReadOnlyList<ContextCoreRuntimeCapabilityStatus> Capabilities { get; init; } = Array.Empty<ContextCoreRuntimeCapabilityStatus>();
}

public sealed class ContextCoreStorageStatus
{
    public string Provider { get; init; } = string.Empty;

    public string? RootPath { get; init; }
}

public sealed class ContextCoreJobStatus
{
    public int Queued { get; init; }

    public int Running { get; init; }
}

public sealed class ContextCoreReadinessStatus
{
    public string State { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public bool ProductionReady { get; init; }

    public string ProviderState { get; init; } = string.Empty;

    public IReadOnlyList<ContextCoreReadinessCheckStatus> Checks { get; init; } = Array.Empty<ContextCoreReadinessCheckStatus>();
}

public sealed class ContextCoreReadinessCheckStatus
{
    public string Name { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;
}

public sealed class ContextCoreRuntimeCapabilityStatus
{
    public string Name { get; init; } = string.Empty;

    public string State { get; init; } = string.Empty;

    public bool Active { get; init; }

    public string Message { get; init; } = string.Empty;
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
