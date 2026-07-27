using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions;

namespace ContextCore.Service.Security;

// ===========================================================================
// 默认实现集合 — RBAC / API Key Store / Tool Authorizer / Quota / Audit Retention
//
// 设计目标：
//   1. 全部为进程内实现，无持久化依赖；适合开发/测试与单节点部署。
//   2. 接口与实现分离：生产环境可替换为 Postgres 持久化实现而不影响调用方。
//   3. 所有方法线程安全（ConcurrentDictionary / lock 保护）。
//   4. 不引入任何外部认证服务器；保持框架轻量。
// ===========================================================================

// ── RBAC 默认实现 ─────────────────────────────────────────────────────────

/// <summary>
/// 默认 RBAC 服务。基于 WorkspaceRolePermissions 默认映射表解析权限。
/// 角色来源：API Key 元数据 / 静态 API Key / 默认角色（按 SecurityOptions.Rbac 配置）。
/// </summary>
public sealed class DefaultWorkspaceRbacService : IWorkspaceRbacService
{
    private readonly SecurityOptions _options;

    public DefaultWorkspaceRbacService(SecurityOptions options)
    {
        _options = options;
    }

    /// <inheritdoc />
    public bool HasPermission(WorkspaceContext context, WorkspacePermission permission)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (permission == WorkspacePermission.None)
        {
            return true;
        }
        return (context.Permissions & permission) == permission;
    }

    /// <inheritdoc />
    public bool IsInRole(WorkspaceContext context, params WorkspaceRole[] roles)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (roles is null || roles.Length == 0)
        {
            return true;
        }
        foreach (var role in roles)
        {
            if (context.Roles.Contains(role))
            {
                return true;
            }
        }
        return false;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<WorkspaceRole>> ResolveRolesAsync(
        string? apiKeyId,
        string workspaceId,
        CancellationToken cancellationToken = default)
    {
        // 1. 通过 API Key 元数据解析角色（由 ApiKeyStore 注入的 Key 元数据携带）
        // 当前简化实现：若 ApiKeyId 非空，使用 FallbackRolesForApiKey；
        // 生产实现应通过 IApiKeyStore.GetByIdAsync(apiKeyId) 查询 Key 元数据中的 Roles 字段。
        IReadOnlyList<string> roleNames = apiKeyId is null
            ? _options.Rbac.DefaultRoles
            : _options.Rbac.FallbackRolesForApiKey;

        // 静态 API Key（Security:ApiKey）的特殊处理由 ApiKeyMiddleware 标记（Items["StaticApiKey"]）
        // 此处不区分；如需区分应在 ApiKeyMiddleware 中显式注入角色到 Items 后由本服务读取。
        var roles = roleNames
            .Select(ParseRole)
            .Where(r => r.HasValue)
            .Select(r => r!.Value)
            .ToList();

        return ValueTask.FromResult<IReadOnlyList<WorkspaceRole>>(roles);
    }

    /// <summary>解析角色字符串为枚举（忽略未知角色）。</summary>
    private static WorkspaceRole? ParseRole(string? roleName)
    {
        if (string.IsNullOrWhiteSpace(roleName))
        {
            return null;
        }
        return Enum.TryParse<WorkspaceRole>(roleName, ignoreCase: true, out var role) ? role : null;
    }
}

// ── API Key Store 默认实现（含轮换） ──────────────────────────────────────

/// <summary>
/// 进程内 API Key 存储（开发/测试）。
/// 支持 API Key 轮换：旧 key 降级为 Secondary（过渡期内仍可校验通过），新 key 成为 Primary。
/// 生产环境应替换为持久化实现（如 Postgres api_keys 表）。
/// </summary>
public sealed class InMemoryApiKeyStore : IApiKeyStore
{
    private readonly ConcurrentDictionary<string, ApiKeyEntry> _byId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ApiKeyEntry> _byKeyHash = new(StringComparer.Ordinal);
    private readonly SecurityOptions _options;
    private readonly ILogger<InMemoryApiKeyStore> _logger;
    private readonly object _createLock = new();

    public InMemoryApiKeyStore(SecurityOptions options, ILogger<InMemoryApiKeyStore> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public ValueTask<ApiKeyValidationResult> ValidateAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return ValueTask.FromResult(new ApiKeyValidationResult
            {
                IsValid = false,
                FailureReason = "API Key 为空。"
            });
        }

        var hash = HashApiKey(apiKey);
        if (!_byKeyHash.TryGetValue(hash, out var entry))
        {
            return ValueTask.FromResult(new ApiKeyValidationResult
            {
                IsValid = false,
                FailureReason = "API Key 不存在或已吊销。"
            });
        }

        if (entry.IsRevoked)
        {
            return ValueTask.FromResult(new ApiKeyValidationResult
            {
                IsValid = false,
                FailureReason = $"API Key '{entry.Name}' 已被吊销。"
            });
        }

        if (entry.ExpiresAt is { } expires && expires < DateTimeOffset.UtcNow)
        {
            return ValueTask.FromResult(new ApiKeyValidationResult
            {
                IsValid = false,
                FailureReason = $"API Key '{entry.Name}' 已过期（过渡期结束）。"
            });
        }

        return ValueTask.FromResult(new ApiKeyValidationResult
        {
            IsValid = true,
            ApiKeyId = entry.ApiKeyId,
            ApiKeyName = entry.Name,
            WorkspaceId = entry.WorkspaceId,
            ApiKeyRole = entry.Role
        });
    }

    /// <inheritdoc />
    public ValueTask<ApiKeyEntry?> GetByIdAsync(string apiKeyId, CancellationToken cancellationToken = default)
    {
        return _byId.TryGetValue(apiKeyId, out var entry)
            ? ValueTask.FromResult<ApiKeyEntry?>(entry)
            : ValueTask.FromResult<ApiKeyEntry?>(null);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ApiKeyEntry>> ListByWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        var results = _byId.Values
            .Where(e => string.Equals(e.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return ValueTask.FromResult<IReadOnlyList<ApiKeyEntry>>(results);
    }

    /// <inheritdoc />
    public ValueTask<ApiKeyCreateResult> CreateAsync(ApiKeyCreateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var plainKey = GenerateApiKey();
        var entry = BuildEntry(plainKey, request, role: ApiKeyRole.Primary);
        AddEntry(entry);
        return ValueTask.FromResult(new ApiKeyCreateResult { Entry = entry, PlainTextKey = plainKey });
    }

    /// <inheritdoc />
    public ValueTask<ApiKeyCreateResult> RotateAsync(
        string apiKeyId,
        TimeSpan gracePeriod,
        CancellationToken cancellationToken = default)
    {
        if (!_byId.TryGetValue(apiKeyId, out var existing))
        {
            throw new InvalidOperationException($"API Key '{apiKeyId}' 不存在，无法轮换。");
        }

        lock (_createLock)
        {
            // 1. 旧 key 降级为 Secondary，标记过渡期过期时间
            var oldEntry = existing with
            {
                Role = ApiKeyRole.Secondary,
                ExpiresAt = DateTimeOffset.UtcNow + gracePeriod
            };
            _byId[existing.ApiKeyId] = oldEntry;
            _byKeyHash[existing.KeyHash] = oldEntry;

            // 2. 生成新 key 作为 Primary
            var plainKey = GenerateApiKey();
            var newEntry = new ApiKeyEntry
            {
                ApiKeyId = Guid.NewGuid().ToString("N"),
                Name = existing.Name + "-rotated-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss"),
                WorkspaceId = existing.WorkspaceId,
                Role = ApiKeyRole.Primary,
                KeyHash = HashApiKey(plainKey),
                KeyPrefix = plainKey[..Math.Min(8, plainKey.Length)],
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = existing.ExpiresAt,
                IsRevoked = false,
                Roles = existing.Roles
            };
            AddEntry(newEntry);

            _logger.LogInformation(
                "API Key 轮换：old={OldKeyId}（降级为 Secondary，过期于 {ExpiresAt}），new={NewKeyId}（Primary）。",
                oldEntry.ApiKeyId, oldEntry.ExpiresAt, newEntry.ApiKeyId);

            return ValueTask.FromResult(new ApiKeyCreateResult { Entry = newEntry, PlainTextKey = plainKey });
        }
    }

    /// <inheritdoc />
    public ValueTask RevokeAsync(string apiKeyId, CancellationToken cancellationToken = default)
    {
        if (_byId.TryGetValue(apiKeyId, out var entry))
        {
            var revoked = entry with { IsRevoked = true };
            _byId[apiKeyId] = revoked;
            _byKeyHash[entry.KeyHash] = revoked;
            _logger.LogInformation("API Key 已吊销：{ApiKeyId}（name={Name}）", apiKeyId, entry.Name);
        }
        return default;
    }

    /// <inheritdoc />
    public ValueTask<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var purged = 0;
        foreach (var entry in _byId.Values.ToList())
        {
            var shouldPurge = entry.IsRevoked
                || (entry.ExpiresAt is { } exp && exp < now);

            if (shouldPurge)
            {
                _byId.TryRemove(entry.ApiKeyId, out _);
                _byKeyHash.TryRemove(entry.KeyHash, out _);
                purged++;
            }
        }

        if (purged > 0)
        {
            _logger.LogInformation("清理过期 / 已吊销 API Key：{Count} 个。", purged);
        }

        return ValueTask.FromResult(purged);
    }

    private void AddEntry(ApiKeyEntry entry)
    {
        _byId[entry.ApiKeyId] = entry;
        _byKeyHash[entry.KeyHash] = entry;
    }

    private static ApiKeyEntry BuildEntry(string plainKey, ApiKeyCreateRequest request, ApiKeyRole role)
    {
        return new ApiKeyEntry
        {
            ApiKeyId = Guid.NewGuid().ToString("N"),
            Name = request.Name,
            WorkspaceId = request.WorkspaceId,
            Role = role,
            KeyHash = HashApiKey(plainKey),
            KeyPrefix = plainKey[..Math.Min(8, plainKey.Length)],
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = request.ExpiresAt,
            IsRevoked = false,
            Roles = request.Roles
        };
    }

    /// <summary>生成 32 字节随机 API Key（hex 编码 = 64 字符）。</summary>
    private static string GenerateApiKey()
    {
        Span<byte> buffer = stackalloc byte[32];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToHexString(buffer).ToLowerInvariant();
    }

    /// <summary>SHA-256 哈希（不存明文）。</summary>
    private static string HashApiKey(string apiKey)
    {
        var bytes = Encoding.UTF8.GetBytes(apiKey);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

// ── Tool Authorizer 默认实现 ──────────────────────────────────────────────

/// <summary>
/// 默认 Tool 授权器。基于 ToolName → WorkspacePermission 映射表校验。
/// 未注册的 Tool 视为不限制权限（None）。
/// </summary>
public sealed class DefaultToolAuthorizer : IToolAuthorizer
{
    private readonly ConcurrentDictionary<string, WorkspacePermission> _permissions
        = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<DefaultToolAuthorizer> _logger;

    public DefaultToolAuthorizer(ILogger<DefaultToolAuthorizer> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public ValueTask<ToolAuthorizationResult> AuthorizeAsync(
        WorkspaceContext context,
        string toolName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var required = GetRequiredPermission(toolName);
        if (required == WorkspacePermission.None)
        {
            // 未注册权限要求的 Tool 默认放行
            return ValueTask.FromResult(new ToolAuthorizationResult
            {
                IsAuthorized = true,
                RequiredPermission = WorkspacePermission.None
            });
        }

        var authorized = (context.Permissions & required) == required;
        return ValueTask.FromResult(new ToolAuthorizationResult
        {
            IsAuthorized = authorized,
            FailureReason = authorized ? null : $"当前 workspace 缺少调用 Tool '{toolName}' 所需权限 {required}。",
            RequiredPermission = required
        });
    }

    /// <inheritdoc />
    public void RegisterToolPermission(string toolName, WorkspacePermission requiredPermission)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return;
        }
        _permissions[toolName] = requiredPermission;
        _logger.LogDebug("注册 Tool 权限：{ToolName} → {Permission}", toolName, requiredPermission);
    }

    /// <inheritdoc />
    public WorkspacePermission GetRequiredPermission(string toolName)
    {
        return _permissions.TryGetValue(toolName, out var perm) ? perm : WorkspacePermission.None;
    }
}

// ── Workspace Quota 默认实现 ──────────────────────────────────────────────

/// <summary>
/// 进程内 Workspace 配额服务。
/// 按 workspace 维度跟踪 token / cost 消耗，周期结束后自动重置。
/// 生产环境应替换为持久化实现（如 Postgres workspace_quota 表）。
/// </summary>
public sealed class InMemoryWorkspaceQuotaService : IWorkspaceQuotaService
{
    private readonly ConcurrentDictionary<string, WorkspaceQuotaState> _state
        = new(StringComparer.Ordinal);
    private readonly SecurityOptions _options;
    private readonly ILogger<InMemoryWorkspaceQuotaService> _logger;
    private readonly object _consumeLock = new();

    public InMemoryWorkspaceQuotaService(SecurityOptions options, ILogger<InMemoryWorkspaceQuotaService> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public ValueTask<WorkspaceQuota> GetQuotaAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        var state = GetOrCreateState(workspaceId);
        return ValueTask.FromResult(state.ToQuota());
    }

    /// <inheritdoc />
    public ValueTask<QuotaConsumptionResult> TryConsumeAsync(
        string workspaceId,
        long tokens,
        double costUsd,
        CancellationToken cancellationToken = default)
    {
        lock (_consumeLock)
        {
            var state = GetOrCreateState(workspaceId);
            state.MaybeResetPeriod();

            var newTokensUsed = state.TokensUsed + tokens;
            var newCostUsed = state.CostUsedUsd + costUsd;

            // 配额检查（MaxTokens=0 / MaxCostUsd=0 视为无限制）
            if (state.MaxTokens > 0 && newTokensUsed > state.MaxTokens)
            {
                return ValueTask.FromResult(new QuotaConsumptionResult
                {
                    Allowed = false,
                    FailureReason = $"Token 配额耗尽：已用 {state.TokensUsed}/{state.MaxTokens}，本次请求 {tokens}。",
                    UpdatedQuota = state.ToQuota()
                });
            }

            if (state.MaxCostUsd > 0 && newCostUsed > state.MaxCostUsd)
            {
                return ValueTask.FromResult(new QuotaConsumptionResult
                {
                    Allowed = false,
                    FailureReason = $"费用配额耗尽：已用 {state.CostUsedUsd:F2}/{state.MaxCostUsd:F2} USD，本次请求 {costUsd:F2}。",
                    UpdatedQuota = state.ToQuota()
                });
            }

            state.TokensUsed = newTokensUsed;
            state.CostUsedUsd = newCostUsed;

            return ValueTask.FromResult(new QuotaConsumptionResult
            {
                Allowed = true,
                UpdatedQuota = state.ToQuota()
            });
        }
    }

    /// <inheritdoc />
    public ValueTask ResetAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        if (_state.TryGetValue(workspaceId, out var state))
        {
            state.TokensUsed = 0;
            state.CostUsedUsd = 0;
            state.PeriodStartedAt = DateTimeOffset.UtcNow;
        }
        return default;
    }

    /// <inheritdoc />
    public ValueTask SetLimitAsync(
        string workspaceId,
        long maxTokens,
        double maxCostUsd,
        TimeSpan period,
        CancellationToken cancellationToken = default)
    {
        var state = GetOrCreateState(workspaceId);
        state.MaxTokens = maxTokens;
        state.MaxCostUsd = maxCostUsd;
        state.Period = period;
        state.PeriodStartedAt = DateTimeOffset.UtcNow;
        state.TokensUsed = 0;
        state.CostUsedUsd = 0;
        _logger.LogInformation(
            "配置 workspace 配额：{WorkspaceId} MaxTokens={MaxTokens} MaxCostUsd={MaxCostUsd} Period={Period}",
            workspaceId, maxTokens, maxCostUsd, period);
        return default;
    }

    private WorkspaceQuotaState GetOrCreateState(string workspaceId)
    {
        return _state.GetOrAdd(workspaceId, id =>
        {
            // 从配置读取 workspace 的配额上限
            var limits = _options.Quota;
            var limit = limits.WorkspaceLimits.TryGetValue(id, out var ws)
                ? ws
                : limits.DefaultLimit;

            return new WorkspaceQuotaState
            {
                WorkspaceId = id,
                MaxTokens = limit.MaxTokens,
                MaxCostUsd = limit.MaxCostUsd,
                Period = limit.PeriodSpan,
                PeriodStartedAt = DateTimeOffset.UtcNow
            };
        });
    }

    private sealed class WorkspaceQuotaState
    {
        public string WorkspaceId { get; init; } = string.Empty;
        public long MaxTokens { get; set; }
        public long TokensUsed { get; set; }
        public double MaxCostUsd { get; set; }
        public double CostUsedUsd { get; set; }
        public TimeSpan Period { get; set; } = TimeSpan.FromHours(1);
        public DateTimeOffset PeriodStartedAt { get; set; }

        public void MaybeResetPeriod()
        {
            if (Period <= TimeSpan.Zero)
            {
                return;
            }
            var now = DateTimeOffset.UtcNow;
            if (now >= PeriodStartedAt + Period)
            {
                TokensUsed = 0;
                CostUsedUsd = 0;
                PeriodStartedAt = now;
            }
        }

        public WorkspaceQuota ToQuota() => new()
        {
            WorkspaceId = WorkspaceId,
            MaxTokens = MaxTokens,
            TokensUsed = TokensUsed,
            MaxCostUsd = MaxCostUsd,
            CostUsedUsd = CostUsedUsd,
            Period = Period,
            PeriodStartedAt = PeriodStartedAt
        };
    }
}

// ── Audit Retention 默认实现 ──────────────────────────────────────────────

/// <summary>
/// 默认审计保留服务（无操作占位）。
/// 当前审计日志通过 AuditLogMiddleware 输出到 ILogger，由日志框架处理保留。
/// 启用文件审计或 Postgres audit_log 表后，应替换为实际清理实现。
/// </summary>
public sealed class DefaultAuditRetentionService : IAuditRetentionService
{
    private readonly ILogger<DefaultAuditRetentionService> _logger;

    public DefaultAuditRetentionService(ILogger<DefaultAuditRetentionService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public ValueTask<int> PurgeOlderAsync(TimeSpan retention, CancellationToken cancellationToken = default)
    {
        // 默认实现：ILogger 输出由日志框架自身的保留策略管理（如 serilog rolling file）。
        // 文件审计日志 / Postgres audit_log 表的清理需在持久化实现中完成。
        _logger.LogDebug("PurgeOlderAsync 调用：retention={Retention}（默认实现为 no-op）", retention);
        return ValueTask.FromResult(0);
    }

    /// <inheritdoc />
    public ValueTask<int> ArchiveOlderAsync(TimeSpan retention, string archivePath, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("ArchiveOlderAsync 调用：retention={Retention} archivePath={ArchivePath}（默认实现为 no-op）", retention, archivePath);
        return ValueTask.FromResult(0);
    }
}
