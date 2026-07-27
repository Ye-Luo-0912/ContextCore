namespace ContextCore.Service;

/// <summary>最小 API Key 安全配置。从 appsettings.json 的 Security 节读取，私钥建议放在 ~/.contextcore/secrets.json。</summary>
public sealed class SecurityOptions
{
    /// <summary>是否要求调用方在每个请求中携带 API Key。默认 true；开发环境可设为 false。</summary>
    public bool RequireApiKey { get; init; } = true;

    /// <summary>请求头名称，默认 X-ContextCore-Key。</summary>
    public string ApiKeyHeaderName { get; init; } = "X-ContextCore-Key";

    /// <summary>
    /// 服务端期望的 API Key 值。
    /// 建议通过 ~/.contextcore/secrets.json 的 Security:ApiKey 字段注入，不要写入仓库。
    /// </summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>不需要校验 API Key 的路径前缀列表（精确或前缀匹配）。</summary>
    public IReadOnlyList<string> PublicPaths { get; init; } =
    [
        "/health",
        "/api/health",
        "/openapi",
        "/scalar",
        "/",
    ];

    /// <summary>
    /// 允许的跨源列表（CORS）。
    /// 空列表：仅允许同源请求（支持 no-cors fetch 和 curl，拒绝跨源 XHR/fetch）。
    /// 含有 "*"：允许所有来源（仅限内网测试或开放 API 场景，不建议生产）。
    /// 具体来源如 ["http://localhost:3000", "https://myapp.example.com"]：只允许指定地址跨源请求。
    /// </summary>
    public IReadOnlyList<string> AllowedOrigins { get; init; } = Array.Empty<string>();

    // ── 多租户 Workspace 上下文 ───────────────────────────────────────

    /// <summary>
    /// Workspace 上下文配置。控制如何从请求中解析 workspace_id 与角色。
    /// </summary>
    public WorkspaceContextOptions Workspace { get; init; } = new();

    /// <summary>
    /// RBAC 配置。控制未认证请求与 API Key 缺失角色时的默认权限。
    /// </summary>
    public RbacOptions Rbac { get; init; } = new();

    // ── API Key 轮换 ─────────────────────────────────────────────────

    /// <summary>
    /// API Key 轮换配置。控制轮换过渡期长度与过期清理策略。
    /// </summary>
    public ApiKeyRotationOptions ApiKeyRotation { get; init; } = new();

    // ── Approval Policy ──────────────────────────────────────────────

    /// <summary>
    /// Approval Policy 配置。控制哪些 Tool 调用 / 高 cost Run 需要人工审批。
    /// 与 IAgentApprovalGate 协同：策略匹配时返回 Approved=false 等待人工裁决。
    /// </summary>
    public ApprovalPolicyOptions ApprovalPolicy { get; init; } = new();

    // ── Rate Limit ───────────────────────────────────────────────────

    /// <summary>
    /// 限流配置。按 workspace + endpoint 维度限流。
    /// 使用 .NET 内置 System.Threading.RateLimiting（TokenBucket / FixedWindow / ConcurrencyLimiter）。
    /// </summary>
    public RateLimitOptions RateLimit { get; init; } = new();

    // ── Per-tenant Quota ─────────────────────────────────────────────

    /// <summary>
    /// Per-tenant 配额配置。每个 workspace 的 token / cost 周期预算。
    /// AgentRun 执行前检查配额；耗尽时拒绝或降级。
    /// </summary>
    public WorkspaceQuotaOptions Quota { get; init; } = new();

    // ── 审计保留策略 ──────────────────────────────────────────────────

    /// <summary>
    /// 审计日志保留策略。控制超期清理。
    /// </summary>
    public AuditRetentionOptions AuditRetention { get; init; } = new();
}

/// <summary>Workspace 上下文配置。控制如何从请求中解析 workspace_id 与角色。</summary>
public sealed class WorkspaceContextOptions
{
    /// <summary>
    /// 请求头名称（用于提取 workspace_id）。默认 X-ContextCore-Workspace。
    /// 与 API Key 元数据中的 workspace 字段协同：请求头优先，API Key 元数据次之。
    /// </summary>
    public string WorkspaceIdHeaderName { get; init; } = "X-ContextCore-Workspace";

    /// <summary>
    /// 默认 workspace ID（未在请求头或 API Key 中指定时使用）。
    /// 空字符串表示"全局默认 workspace"（向后兼容现有不带 workspace 的请求）。
    /// </summary>
    public string DefaultWorkspaceId { get; init; } = string.Empty;

    /// <summary>
    /// 是否要求所有请求必须显式提供 workspace_id（true 时缺失返回 400）。
    /// 默认 false：缺失时回退到 DefaultWorkspaceId（向后兼容）。
    /// </summary>
    public bool RequireExplicitWorkspace { get; init; } = false;

    /// <summary>
    /// 未认证请求（RequireApiKey=false 或 PublicPaths 命中）的默认角色。
    /// 默认 Viewer：仅查询权限，不能触发写操作。
    /// </summary>
    public string DefaultRoleForUnauthenticated { get; init; } = "Viewer";
}

/// <summary>RBAC 配置。控制默认权限与角色解析。</summary>
public sealed class RbacOptions
{
    /// <summary>
    /// 未通过 API Key 认证的请求默认分配的角色列表。
    /// 默认 [Viewer]：仅查询权限。
    /// 当 Security:RequireApiKey=false 时生效；RequireApiKey=true 时未认证请求已被 ApiKeyMiddleware 拒绝。
    /// </summary>
    public IReadOnlyList<string> DefaultRoles { get; init; } = new[] { "Viewer" };

    /// <summary>
    /// 通过 API Key 认证但 API Key 元数据未指定角色时的回退角色列表。
    /// 默认 [Developer]：可触发 AgentRun，但不能激活模型或管理 API Key。
    /// </summary>
    public IReadOnlyList<string> FallbackRolesForApiKey { get; init; } = new[] { "Developer" };

    /// <summary>
    /// 静态 API Key（SecurityOptions.ApiKey 配置）认证通过时分配的角色。
    /// 默认 [Admin]：向后兼容现有"单 API Key 全权"模式。
    /// </summary>
    public IReadOnlyList<string> RolesForStaticApiKey { get; init; } = new[] { "Admin" };

    /// <summary>
    /// 是否启用 RBAC 强制校验（true 时 RequireWorkspaceRole 端点会拒绝无权限请求）。
    /// 默认 true；设为 false 时所有端点放行（仅审计日志记录权限不足事件，便于灰度接入）。
    /// </summary>
    public bool Enforce { get; init; } = true;
}

/// <summary>API Key 轮换配置。</summary>
public sealed class ApiKeyRotationOptions
{
    /// <summary>
    /// 轮换过渡期长度。旧 key 在此期间仍可校验通过，给客户端切换时间。
    /// 默认 7 天；过期后由 PurgeExpiredAsync 自动清理。
    /// </summary>
    public TimeSpan DefaultGracePeriod { get; init; } = TimeSpan.FromDays(7);

    /// <summary>
    /// 是否启用静态 API Key（Security:ApiKey）的轮换支持。
    /// 默认 false：静态 key 保持原有行为（直接字符串比对）。
    /// 设为 true 时静态 key 也会通过 IApiKeyStore 校验（仍可正常工作，但需要注入 IApiKeyStore）。
    /// </summary>
    public bool EnableStaticKeyRotation { get; init; } = false;

    /// <summary>
    /// 后台清理过期 API Key 的间隔。默认 1 小时。
    /// 设为 TimeSpan.Zero 时禁用后台清理（仅手动调用清理）。
    /// </summary>
    public TimeSpan PurgeInterval { get; init; } = TimeSpan.FromHours(1);
}

/// <summary>Approval Policy 配置。</summary>
public sealed class ApprovalPolicyOptions
{
    /// <summary>
    /// 是否启用 Approval Policy。默认 false（向后兼容：所有 Tool 自动通过）。
    /// 设为 true 时 ApprovalPolicy 工具调用需经过 IAgentApprovalGate 审批。
    /// </summary>
    public bool Enabled { get; init; } = false;

    /// <summary>
    /// 需要人工审批的 Tool 名称列表（按 workspace 配置覆盖）。
    /// 默认包含常见危险 Tool：file_delete / shell_exec / registry_set / process_kill。
    /// </summary>
    public IReadOnlyList<string> ApprovalRequiredTools { get; init; } = new[]
    {
        "file_delete",
        "shell_exec",
        "registry_set",
        "process_kill"
    };

    /// <summary>
    /// 触发审批的费用阈值（USD）。Run 预估费用超过此值时需要审批。
    /// 默认 1.0；设为 0 时禁用费用触发审批（仅按 Tool 名称触发）。
    /// </summary>
    public double CostThresholdUsd { get; init; } = 1.0;

    /// <summary>
    /// 触发审批的 token 阈值。单次 Run 预估 token 超过此值时需要审批。
    /// 默认 100000；设为 0 时禁用 token 触发审批。
    /// </summary>
    public long TokenThreshold { get; init; } = 100_000;

    /// <summary>
    /// 按 workspace 配置的 Approval Policy 覆盖。
    /// key=workspaceId，value=该 workspace 的策略（合并到全局策略之上）。
    /// </summary>
    public IReadOnlyDictionary<string, WorkspaceApprovalOverride> WorkspaceOverrides { get; init; }
        = new Dictionary<string, WorkspaceApprovalOverride>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>按 workspace 配置的 Approval Policy 覆盖。</summary>
public sealed class WorkspaceApprovalOverride
{
    /// <summary>覆盖全局 ApprovalRequiredTools（null = 继承全局配置）。</summary>
    public IReadOnlyList<string>? ApprovalRequiredTools { get; init; }

    /// <summary>覆盖全局 CostThresholdUsd（null = 继承全局配置）。</summary>
    public double? CostThresholdUsd { get; init; }

    /// <summary>覆盖全局 TokenThreshold（null = 继承全局配置）。</summary>
    public long? TokenThreshold { get; init; }
}

/// <summary>限流配置。</summary>
public sealed class RateLimitOptions
{
    /// <summary>
    /// 是否启用限流。默认 false（向后兼容：不限流）。
    /// 设为 true 时按 workspace + endpoint 维度限流。
    /// </summary>
    public bool Enabled { get; init; } = false;

    /// <summary>
    /// 全局默认限流策略（所有 workspace 共享）。默认 TokenBucket：100 token / 10 token-per-second。
    /// </summary>
    public RateLimitPolicyOptions DefaultPolicy { get; init; } = new()
    {
        Type = RateLimitPolicyType.TokenBucket,
        TokenLimit = 100,
        TokenRatePerSecond = 10,
        QueueLimit = 0
    };

    /// <summary>
    /// 按 workspace 配置的限流覆盖。
    /// key=workspaceId，value=该 workspace 的限流策略（覆盖全局 DefaultPolicy）。
    /// </summary>
    public IReadOnlyDictionary<string, RateLimitPolicyOptions> WorkspacePolicies { get; init; }
        = new Dictionary<string, RateLimitPolicyOptions>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 按 endpoint 路径前缀配置的限流覆盖（如 /api/admin/、/api/model/）。
    /// key=路径前缀，value=该路径前缀的限流策略。
    /// 优先级：workspace > endpoint > default。
    /// </summary>
    public IReadOnlyDictionary<string, RateLimitPolicyOptions> EndpointPolicies { get; init; }
        = new Dictionary<string, RateLimitPolicyOptions>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>限流策略配置。</summary>
public sealed class RateLimitPolicyOptions
{
    /// <summary>策略类型。</summary>
    public RateLimitPolicyType Type { get; init; } = RateLimitPolicyType.TokenBucket;

    /// <summary>TokenBucket: token 上限；FixedWindow: 窗口内请求数上限；Concurrency: 并发数上限。</summary>
    public int TokenLimit { get; init; } = 100;

    /// <summary>TokenBucket: token 补充速率（每秒）；FixedWindow: 窗口长度（秒）。</summary>
    public double TokenRatePerSecond { get; init; } = 10;

    /// <summary>排队等待上限（超过后立即拒绝 429）。</summary>
    public int QueueLimit { get; init; } = 0;

    /// <summary>是否按 workspace 隔离限流配额（true = 每个 workspace 独立配额；false = 全局共享）。</summary>
    public bool PerWorkspace { get; init; } = true;
}

/// <summary>限流策略类型。</summary>
public enum RateLimitPolicyType
{
    /// <summary>令牌桶（默认；允许突发流量）。</summary>
    TokenBucket = 0,

    /// <summary>固定窗口（窗口内请求数上限）。</summary>
    FixedWindow = 1,

    /// <summary>并发限制（同时处理请求数上限）。</summary>
    Concurrency = 2
}

/// <summary>Per-tenant 配额配置。</summary>
public sealed class WorkspaceQuotaOptions
{
    /// <summary>
    /// 是否启用 Per-tenant 配额。默认 false（向后兼容：无配额限制）。
    /// 设为 true 时 AgentRun 执行前会检查 workspace 配额。
    /// </summary>
    public bool Enabled { get; init; } = false;

    /// <summary>
    /// 默认配额（未显式配置的 workspace 使用此值）。
    /// 默认 0 = 不限制（MaxTokens=0, MaxCostUsd=0 表示无限制）。
    /// </summary>
    public WorkspaceQuotaLimit DefaultLimit { get; init; } = new()
    {
        MaxTokens = 0,
        MaxCostUsd = 0,
        Period = "01:00:00"
    };

    /// <summary>
    /// 按 workspace 配置的配额覆盖。
    /// key=workspaceId，value=该 workspace 的配额上限。
    /// </summary>
    public IReadOnlyDictionary<string, WorkspaceQuotaLimit> WorkspaceLimits { get; init; }
        = new Dictionary<string, WorkspaceQuotaLimit>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 配额耗尽时的行为。
    /// </summary>
    public QuotaExhaustionBehavior ExhaustionBehavior { get; init; } = QuotaExhaustionBehavior.Reject;
}

/// <summary>Workspace 配额上限配置。</summary>
public sealed class WorkspaceQuotaLimit
{
    /// <summary>周期内最大 token 消耗（0 = 无限制）。</summary>
    public long MaxTokens { get; init; } = 0;

    /// <summary>周期内最大费用 USD（0 = 无限制）。</summary>
    public double MaxCostUsd { get; init; } = 0;

    /// <summary>配额周期长度（ TimeSpan 字符串格式，如 "01:00:00" 表示 1 小时）。</summary>
    public string Period { get; init; } = "01:00:00";

    /// <summary>解析 Period 字符串为 TimeSpan。</summary>
    public TimeSpan PeriodSpan => TimeSpan.TryParse(Period, out var ts) ? ts : TimeSpan.FromHours(1);
}

/// <summary>配额耗尽时的行为。</summary>
public enum QuotaExhaustionBehavior
{
    /// <summary>拒绝新请求（返回 429 / 503）。</summary>
    Reject = 0,

    /// <summary>降级到更便宜的模型（需配合 IModelActivationManager 配置 fallback）。</summary>
    Degrade = 1,

    /// <summary>记录警告但允许继续（仅审计，不阻止）。</summary>
    WarnOnly = 2
}

/// <summary>审计日志保留策略。</summary>
public sealed class AuditRetentionOptions
{
    /// <summary>
    /// 审计日志保留期限。默认 90 天。
    /// 超期记录由 IAuditRetentionService 清理或归档。
    /// </summary>
    public TimeSpan RetentionPeriod { get; init; } = TimeSpan.FromDays(90);

    /// <summary>
    /// 是否启用归档（true = 超期记录先归档到冷存储；false = 直接清理）。
    /// 默认 false：直接清理（适合开发环境）。
    /// </summary>
    public bool EnableArchive { get; init; } = false;

    /// <summary>
    /// 归档路径（文件系统目录）。空字符串表示使用 Storage:RootPath 下的 _audit-archive 子目录。
    /// </summary>
    public string ArchivePath { get; init; } = string.Empty;

    /// <summary>
    /// 后台清理任务的调度间隔。默认 24 小时。
    /// 设为 TimeSpan.Zero 时禁用后台清理（仅手动调用清理）。
    /// </summary>
    public TimeSpan PurgeInterval { get; init; } = TimeSpan.FromHours(24);
}
