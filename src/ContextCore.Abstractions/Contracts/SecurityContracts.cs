namespace ContextCore.Abstractions;

// ===========================================================================
// 安全基础契约层 — 多租户隔离 / RBAC / Tool Permission / Approval Policy /
// Per-tenant Quota / Audit Retention / API Key Rotation
//
// 目标（为 Agent 与模型激活阶段提供安全基础框架）：
//   1. 定义 WorkspaceContext：从请求中解析出的 workspace 隔离上下文。
//   2. 定义 RBAC 抽象：4 角色 + 5 权限位。
//   3. 定义 IApiKeyStore：支持 API Key 轮换（新 key + 旧 key 共存过渡期）。
//   4. 定义 IToolAuthorizer：在 Tool 执行前校验当前 workspace 是否有权限。
//   5. 定义 IWorkspaceQuotaService：按 workspace 配额（token / cost）。
//   6. 定义 IAuditRetentionService：审计日志超期清理。
//
// 设计原则：
//   1. 契约层不引入任何 I/O 实现。
//   2. WorkspaceContext 仅在请求生命周期内有效（Scoped）。
//   3. 与现有 IAgentApprovalGate / IAgentToolCallValidator 协同：
//      - IAgentToolCallValidator 校验参数合法性与危险 Tool 标记。
//      - IToolAuthorizer 校验当前 workspace 是否有调用该 Tool 的 RBAC 权限。
//      - IAgentApprovalGate 决定高风险操作是否需要人工审批。
// ===========================================================================

// ── 角色 / 权限枚举 ───────────────────────────────────────────────────────

/// <summary>
/// Workspace 角色。组合使用 <see cref="WorkspacePermission"/> 实现 RBAC。
/// 角色按权限递增：Viewer &lt; Developer &lt; Operator &lt; Admin。
/// </summary>
public enum WorkspaceRole : byte
{
    /// <summary>只读访问：查询状态、查看 learning 数据。</summary>
    Viewer = 0,

    /// <summary>开发与执行 Agent：触发 AgentRun、写入记忆。</summary>
    Developer = 1,

    /// <summary>运维：激活/回滚模型、配置 approval policy、管理 API Key。</summary>
    Operator = 2,

    /// <summary>管理员：全部权限（含 Config.Edit 与 API Key 轮换）。</summary>
    Admin = 3
}

/// <summary>
/// Workspace 权限位（标志位组合）。
/// </summary>
[Flags]
public enum WorkspacePermission : ushort
{
    /// <summary>无权限。</summary>
    None = 0,

    /// <summary>触发 Agent Run。</summary>
    AgentRun = 1 << 0,

    /// <summary>激活/回滚模型（Model Control Plane 写操作）。</summary>
    ModelActivate = 1 << 1,

    /// <summary>注册新模型工件。</summary>
    ModelRegister = 1 << 2,

    /// <summary>查看 learning 数据。</summary>
    LearningView = 1 << 3,

    /// <summary>编辑服务配置（admin endpoint 写操作）。</summary>
    ConfigEdit = 1 << 4,

    /// <summary>管理 API Key（创建/轮换/吊销）。</summary>
    ApiKeyManage = 1 << 5,

    /// <summary>管理 Workspace 配额与限流配置。</summary>
    QuotaManage = 1 << 6,

    /// <summary>读取模型工件信息（Model Control Plane 只读端点：active/list/get/ready/consistency）。</summary>
    ModelRead = 1 << 7,

    /// <summary>Admin 隐含的所有权限位（除 QuotaManage 外的并集）。</summary>
    AdminAll = AgentRun | ModelActivate | ModelRegister | ModelRead | LearningView | ConfigEdit | ApiKeyManage | QuotaManage
}

/// <summary>
/// 角色 → 权限默认映射表。
/// </summary>
public static class WorkspaceRolePermissions
{
    /// <summary>Viewer 默认权限：LearningView + ModelRead（仅查询）。</summary>
    public const WorkspacePermission Viewer = WorkspacePermission.LearningView | WorkspacePermission.ModelRead;

    /// <summary>Developer 默认权限：AgentRun + LearningView + ModelRead。</summary>
    public const WorkspacePermission Developer = WorkspacePermission.AgentRun | WorkspacePermission.LearningView | WorkspacePermission.ModelRead;

    /// <summary>Operator 默认权限：Developer + ModelActivate + ModelRegister。</summary>
    public const WorkspacePermission Operator =
        Developer | WorkspacePermission.ModelActivate | WorkspacePermission.ModelRegister;

    /// <summary>Admin 默认权限：全部权限位。</summary>
    public const WorkspacePermission Admin = WorkspacePermission.AdminAll;

    /// <summary>解析角色对应的权限位。</summary>
    public static WorkspacePermission Resolve(WorkspaceRole role) => role switch
    {
        WorkspaceRole.Viewer => Viewer,
        WorkspaceRole.Developer => Developer,
        WorkspaceRole.Operator => Operator,
        WorkspaceRole.Admin => Admin,
        _ => WorkspacePermission.None
    };
}

// ── WorkspaceContext ──────────────────────────────────────────────────────

/// <summary>
/// 请求级 Workspace 上下文。在请求开始时由 WorkspaceContextMiddleware 从请求头 / API Key 中解析填充。
/// 贯穿整个请求生命周期，供 RBAC / Quota / Tool Authorizer 使用。
/// </summary>
public sealed class WorkspaceContext
{
    /// <summary>Workspace 唯一标识（隔离边界）。空字符串表示"默认 workspace"（向后兼容）。</summary>
    public string WorkspaceId { get; init; } = string.Empty;

    /// <summary>来源标记：header / apikey / default。</summary>
    public string Source { get; init; } = "default";

    /// <summary>已认证的 API Key ID（轮换期间用于审计追踪；null 表示未通过 API Key 认证）。</summary>
    public string? ApiKeyId { get; init; }

    /// <summary>API Key 显示名（来自 IApiKeyStore，便于审计日志；不暴露 key 本身）。</summary>
    public string? ApiKeyName { get; init; }

    /// <summary>当前请求的角色列表（来自 API Key 元数据或默认配置）。</summary>
    public IReadOnlyList<WorkspaceRole> Roles { get; init; } = Array.Empty<WorkspaceRole>();

    /// <summary>附加 claims（可选；预留扩展位，如 IP、user agent）。</summary>
    public IReadOnlyDictionary<string, string?> Claims { get; init; } = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>请求是否已通过认证（API Key 校验通过或 RequireApiKey=false）。</summary>
    public bool IsAuthenticated { get; init; }

    /// <summary>解析后的权限位（基于 Roles 聚合）。</summary>
    public WorkspacePermission Permissions
    {
        get
        {
            var perms = WorkspacePermission.None;
            foreach (var role in Roles)
            {
                perms |= WorkspaceRolePermissions.Resolve(role);
            }
            return perms;
        }
    }
}

/// <summary>
/// Workspace 上下文访问器（Scoped）。
/// 中间件填充后，业务层通过此接口读取当前请求的 workspace 信息。
/// </summary>
public interface IWorkspaceContextAccessor
{
    /// <summary>当前请求的 Workspace 上下文；请求未通过中间件时返回 null。</summary>
    WorkspaceContext? Current { get; }

    /// <summary>由 WorkspaceContextMiddleware 在请求开始时调用。</summary>
    void Set(WorkspaceContext context);

    /// <summary>由 WorkspaceContextMiddleware 在请求结束时调用（清理 AsyncLocal）。</summary>
    void Clear();
}

// ── RBAC ──────────────────────────────────────────────────────────────────

/// <summary>
/// Workspace RBAC（基于角色的访问控制）服务。
/// 校验当前 <see cref="WorkspaceContext"/> 是否有指定权限。
/// </summary>
public interface IWorkspaceRbacService
{
    /// <summary>检查上下文是否拥有指定权限位。</summary>
    bool HasPermission(WorkspaceContext context, WorkspacePermission permission);

    /// <summary>检查上下文是否在指定角色中（任一匹配即返回 true）。</summary>
    bool IsInRole(WorkspaceContext context, params WorkspaceRole[] roles);

    /// <summary>解析 API Key 对应的 workspace 角色列表。</summary>
    ValueTask<IReadOnlyList<WorkspaceRole>> ResolveRolesAsync(string? apiKeyId, string workspaceId, CancellationToken cancellationToken = default);
}

// ── API Key Store（含轮换） ───────────────────────────────────────────────

/// <summary>
/// API Key 校验结果。
/// </summary>
public sealed record ApiKeyValidationResult
{
    /// <summary>是否校验通过。</summary>
    public required bool IsValid { get; init; }

    /// <summary>API Key ID（IsValid=true 时非空）。</summary>
    public string? ApiKeyId { get; init; }

    /// <summary>API Key 显示名。</summary>
    public string? ApiKeyName { get; init; }

    /// <summary>绑定的 workspace ID。</summary>
    public string? WorkspaceId { get; init; }

    /// <summary>该 Key 在轮换中的角色：Primary / Secondary（过渡期内的旧 key）。</summary>
    public ApiKeyRole ApiKeyRole { get; init; }

    /// <summary>校验失败原因（IsValid=false 时填充）。</summary>
    public string? FailureReason { get; init; }
}

/// <summary>API Key 在轮换中的角色。</summary>
public enum ApiKeyRole : byte
{
    /// <summary>主 key（当前推荐使用）。</summary>
    Primary = 0,

    /// <summary>次 key（轮换过渡期的旧 key，仅过渡期内有效）。</summary>
    Secondary = 1,

    /// <summary>未指定（旧路径回退，视为 Primary）。</summary>
    Unspecified = 2
}

/// <summary>API Key 元数据（不含 key 本身；用于审计与显示）。</summary>
public sealed record ApiKeyEntry
{
    /// <summary>API Key ID。</summary>
    public required string ApiKeyId { get; init; }

    /// <summary>API Key 显示名（便于审计，不暴露 key 本身）。</summary>
    public required string Name { get; init; }

    /// <summary>绑定的 workspace ID。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>当前角色（Primary / Secondary）。</summary>
    public ApiKeyRole Role { get; init; } = ApiKeyRole.Primary;

    /// <summary>Key 哈希（SHA-256；仅存储哈希，不存明文）。</summary>
    public required string KeyHash { get; init; }

    /// <summary>Key 前缀（前 8 字符；用于 UI 展示识别）。</summary>
    public string? KeyPrefix { get; init; }

    /// <summary>创建时间（UTC）。</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>过期时间（UTC；null = 永不过期）。</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>是否已吊销。</summary>
    public bool IsRevoked { get; init; }

    /// <summary>分配的角色列表（用于 RBAC）。</summary>
    public IReadOnlyList<WorkspaceRole> Roles { get; init; } = Array.Empty<WorkspaceRole>();
}

/// <summary>创建 API Key 的请求。</summary>
public sealed record ApiKeyCreateRequest
{
    /// <summary>显示名（便于审计）。</summary>
    public required string Name { get; init; }

    /// <summary>绑定的 workspace ID。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>分配的角色列表。</summary>
    public IReadOnlyList<WorkspaceRole> Roles { get; init; } = new[] { WorkspaceRole.Viewer };

    /// <summary>过期时间（null = 永不过期）。</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>创建 API Key 的结果（含明文 key，仅在创建时返回一次）。</summary>
public sealed record ApiKeyCreateResult
{
    /// <summary>API Key 元数据（持久化存储的版本，不含明文）。</summary>
    public required ApiKeyEntry Entry { get; init; }

    /// <summary>明文 API Key（仅在创建时返回一次；调用方应妥善保存，服务端不再保留）。</summary>
    public required string PlainTextKey { get; init; }
}

/// <summary>
/// API Key 存储（含轮换支持）。
/// 默认实现：InMemoryApiKeyStore（开发/测试）；生产应替换为持久化实现。
/// </summary>
/// <remarks>
/// 轮换流程：
/// 1. 调用 <see cref="RotateAsync"/> 生成新 key + 旧 key 降级为 Secondary（标记过渡期过期时间）。
/// 2. 客户端切换到新 key。
/// 3. 旧 key 过期后由 <see cref="PurgeExpiredAsync"/> 自动清理。
/// </remarks>
public interface IApiKeyStore
{
    /// <summary>校验 API Key 明文（不记录明文，仅哈希比对）。</summary>
    ValueTask<ApiKeyValidationResult> ValidateAsync(string apiKey, CancellationToken cancellationToken = default);

    /// <summary>按 ID 查询 API Key 元数据。</summary>
    ValueTask<ApiKeyEntry?> GetByIdAsync(string apiKeyId, CancellationToken cancellationToken = default);

    /// <summary>列出指定 workspace 下的所有 API Key（不含明文）。</summary>
    ValueTask<IReadOnlyList<ApiKeyEntry>> ListByWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default);

    /// <summary>创建新的 API Key。返回元数据 + 明文（明文仅在创建时返回一次）。</summary>
    ValueTask<ApiKeyCreateResult> CreateAsync(ApiKeyCreateRequest request, CancellationToken cancellationToken = default);

    /// <summary>轮换 API Key：旧 key 降级为 Secondary，新 key 成为 Primary。</summary>
    ValueTask<ApiKeyCreateResult> RotateAsync(string apiKeyId, TimeSpan gracePeriod, CancellationToken cancellationToken = default);

    /// <summary>显式吊销 API Key（立即失效，不等过渡期）。</summary>
    ValueTask RevokeAsync(string apiKeyId, CancellationToken cancellationToken = default);

    /// <summary>清理已过期或已吊销的 API Key。返回清理的条目数。</summary>
    ValueTask<int> PurgeExpiredAsync(CancellationToken cancellationToken = default);
}

// ── Tool Authorizer ───────────────────────────────────────────────────────

/// <summary>Tool 授权结果。</summary>
public sealed record ToolAuthorizationResult
{
    /// <summary>是否授权通过。</summary>
    public required bool IsAuthorized { get; init; }

    /// <summary>失败原因（IsAuthorized=false 时填充）。</summary>
    public string? FailureReason { get; init; }

    /// <summary>所需权限位（用于审计日志）。</summary>
    public WorkspacePermission RequiredPermission { get; init; }

    /// <summary>所需角色（用于审计日志）。</summary>
    public WorkspaceRole? RequiredRole { get; init; }
}

/// <summary>
/// Tool 授权器。在 Tool 实际执行前校验当前 workspace 是否有 RBAC 权限调用该 Tool。
/// </summary>
/// <remarks>
/// 与 <see cref="IAgentToolCallValidator"/> 的区别：
/// - IAgentToolCallValidator：参数合法性 + 危险 Tool 标记（不感知 workspace/角色）。
/// - IToolAuthorizer：RBAC 校验（基于 WorkspaceContext.Permissions）。
/// 调用顺序：ValidateAsync → AuthorizeAsync → ApprovalGate → Dispatch。
/// </remarks>
public interface IToolAuthorizer
{
    /// <summary>校验当前 workspace 是否有调用指定 Tool 的权限。</summary>
    ValueTask<ToolAuthorizationResult> AuthorizeAsync(
        WorkspaceContext context,
        string toolName,
        CancellationToken cancellationToken = default);

    /// <summary>注册 Tool 所需的权限位（启动时配置）。</summary>
    void RegisterToolPermission(string toolName, WorkspacePermission requiredPermission);

    /// <summary>查询 Tool 所需权限位（未注册返回 None = 不限制）。</summary>
    WorkspacePermission GetRequiredPermission(string toolName);
}

// ── Workspace Quota ───────────────────────────────────────────────────────

/// <summary>Workspace 配额（token / cost 周期预算）。</summary>
public sealed record WorkspaceQuota
{
    /// <summary>Workspace ID。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>周期内最大 token 消耗。</summary>
    public long MaxTokens { get; init; }

    /// <summary>当前已消耗 token。</summary>
    public long TokensUsed { get; init; }

    /// <summary>周期内最大费用（美元）。</summary>
    public double MaxCostUsd { get; init; }

    /// <summary>当前已产生费用（美元）。</summary>
    public double CostUsedUsd { get; init; }

    /// <summary>配额周期长度（默认 1 小时；周期结束后自动重置）。</summary>
    public TimeSpan Period { get; init; } = TimeSpan.FromHours(1);

    /// <summary>当前周期开始时间（UTC）。</summary>
    public DateTimeOffset PeriodStartedAt { get; init; }

    /// <summary>当前周期结束时间（UTC；PeriodStartedAt + Period）。</summary>
    public DateTimeOffset PeriodEndsAt => PeriodStartedAt + Period;

    /// <summary>是否已耗尽 token 配额。</summary>
    public bool IsTokenExhausted => MaxTokens > 0 && TokensUsed >= MaxTokens;

    /// <summary>是否已耗尽费用配额。</summary>
    public bool IsCostExhausted => MaxCostUsd > 0 && CostUsedUsd >= MaxCostUsd;

    /// <summary>剩余 token（MaxTokens=0 视为无限制，返回 long.MaxValue）。</summary>
    public long RemainingTokens => MaxTokens <= 0 ? long.MaxValue : Math.Max(0, MaxTokens - TokensUsed);

    /// <summary>剩余费用（美元）。</summary>
    public double RemainingCostUsd => MaxCostUsd <= 0 ? double.MaxValue : Math.Max(0, MaxCostUsd - CostUsedUsd);
}

/// <summary>配额消费结果。</summary>
public sealed record QuotaConsumptionResult
{
    /// <summary>是否成功扣减配额（false = 配额已耗尽）。</summary>
    public required bool Allowed { get; init; }

    /// <summary>失败原因（Allowed=false 时填充）。</summary>
    public string? FailureReason { get; init; }

    /// <summary>扣减后的最新配额快照。</summary>
    public required WorkspaceQuota UpdatedQuota { get; init; }
}

/// <summary>
/// Workspace 配额服务。按 workspace 维度跟踪 token / cost 消耗。
/// </summary>
/// <remarks>
/// 与 <see cref="AgentCostBudget"/> 的区别：
/// - AgentCostBudget：单次 Run 级别的预算。
/// - IWorkspaceQuotaService：workspace 级别的周期性配额（跨多次 Run 累计）。
/// </remarks>
public interface IWorkspaceQuotaService
{
    /// <summary>查询当前 workspace 的配额快照。</summary>
    ValueTask<WorkspaceQuota> GetQuotaAsync(string workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 尝试扣减配额（原子操作）。配额耗尽时返回 Allowed=false。
    /// </summary>
    ValueTask<QuotaConsumptionResult> TryConsumeAsync(
        string workspaceId,
        long tokens,
        double costUsd,
        CancellationToken cancellationToken = default);

    /// <summary>重置 workspace 配额。</summary>
    ValueTask ResetAsync(string workspaceId, CancellationToken cancellationToken = default);

    /// <summary>配置 workspace 的配额上限。</summary>
    ValueTask SetLimitAsync(string workspaceId, long maxTokens, double maxCostUsd, TimeSpan period, CancellationToken cancellationToken = default);
}

// ── Audit Retention ───────────────────────────────────────────────────────

/// <summary>
/// 审计日志保留策略服务。定期清理超期审计记录。
/// </summary>
public interface IAuditRetentionService
{
    /// <summary>清理超过保留期限的审计记录。</summary>
    /// <param name="retention">保留期限（默认 90 天）。</param>
    ValueTask<int> PurgeOlderAsync(TimeSpan retention, CancellationToken cancellationToken = default);

    /// <summary>归档超期审计记录到冷存储（可选；未实现时直接 Purge）。</summary>
    ValueTask<int> ArchiveOlderAsync(TimeSpan retention, string archivePath, CancellationToken cancellationToken = default);
}
