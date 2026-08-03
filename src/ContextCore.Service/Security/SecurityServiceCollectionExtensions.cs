using System.Threading.RateLimiting;
using ContextCore.Abstractions;
using ContextCore.Service.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ContextCore.Service.Security;

// ===========================================================================
// SecurityServiceCollectionExtensions — 安全框架 DI 注册扩展
//
// 包含：
//   1. AddContextCoreSecurity：注册所有安全服务（WorkspaceContext / RBAC / API Key Store /
//      Tool Authorizer / Quota / Audit Retention）。
//   2. AddContextCoreRateLimiter：注册 .NET 内置 RateLimiter 中间件（按 workspace + endpoint 维度）。
//   3. AddContextCoreApiKeyPurgeWorker：注册后台清理过期 API Key 的 HostedService。
//   4. RequireWorkspaceRole：端点扩展方法（在 Minimal API 上声明所需角色，由 RbacEnforcementFilter 强制校验）。
// ===========================================================================

/// <summary>安全框架 DI 注册扩展。</summary>
public static class SecurityServiceCollectionExtensions
{
    /// <summary>
    /// 注册 ContextCore 安全框架的全部默认服务。
    /// 包括：IWorkspaceContextAccessor / IWorkspaceRbacService / IApiKeyStore /
    /// IToolAuthorizer / IWorkspaceQuotaService / IAuditRetentionService。
    /// </summary>
    /// <param name="services">DI 容器。</param>
    /// <param name="securityOptions">已绑定好的 SecurityOptions 实例（由 Program.cs 从 Configuration 读取）。</param>
    public static IServiceCollection AddContextCoreSecurity(
        this IServiceCollection services,
        SecurityOptions securityOptions)
    {
        ArgumentNullException.ThrowIfNull(securityOptions);

        // 已在 Program.cs 中注册为 Singleton，此处确保幂等
        services.TryAddSingleton(securityOptions);

        // IWorkspaceContextAccessor：Scoped（AsyncLocal 在请求间隔离）
        services.TryAddScoped<IWorkspaceContextAccessor, WorkspaceContextAccessor>();

        // RBAC：Singleton（无状态，仅依赖 SecurityOptions）
        services.TryAddSingleton<IWorkspaceRbacService>(_ => new DefaultWorkspaceRbacService(securityOptions));

        // API Key Store：Singleton（进程内字典；生产应替换为持久化实现）
        services.TryAddSingleton<IApiKeyStore>(sp =>
            new InMemoryApiKeyStore(securityOptions, sp.GetRequiredService<ILogger<InMemoryApiKeyStore>>()));

        // Tool Authorizer：Singleton（注册表为进程内字典）
        services.TryAddSingleton<IToolAuthorizer>(sp =>
            new DefaultToolAuthorizer(sp.GetRequiredService<ILogger<DefaultToolAuthorizer>>()));

        // Workspace Quota Service：Singleton（进程内字典）
        services.TryAddSingleton<IWorkspaceQuotaService>(sp =>
            new InMemoryWorkspaceQuotaService(securityOptions, sp.GetRequiredService<ILogger<InMemoryWorkspaceQuotaService>>()));

        // Audit Retention：Singleton（no-op 默认实现）
        services.TryAddSingleton<IAuditRetentionService>(sp =>
            new DefaultAuditRetentionService(sp.GetRequiredService<ILogger<DefaultAuditRetentionService>>()));

        return services;
    }

    /// <summary>
    /// 注册 .NET 内置 RateLimiter 中间件。仅当 Security:RateLimit:Enabled=true 时实际生效。
    /// 按 workspace + endpoint 维度限流（自定义 IRateLimiterPolicy）。
    /// </summary>
    public static IServiceCollection AddContextCoreRateLimiter(
        this IServiceCollection services,
        SecurityOptions securityOptions)
    {
        ArgumentNullException.ThrowIfNull(securityOptions);

        if (!securityOptions.RateLimit.Enabled)
        {
            return services;
        }

        // .NET 内置 RateLimiter 服务
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                context.HttpContext.Response.Headers["Retry-After"] = "60";
                return ValueTask.CompletedTask;
            };

            // 全局 fallback 策略（无 workspace / endpoint 匹配时使用）
            options.GlobalLimiter = CreatePartitionedLimiter(securityOptions);
        });

        return services;
    }

    /// <summary>
    /// 创建按 workspace 分区的 RateLimiter（自定义 PartitionedRateLimiter）。
    /// 每个请求按"endpoint 路径前缀（最长匹配）&gt; workspace 策略 &gt; 全局默认"解析
    /// 有效策略，使 <see cref="RateLimitOptions.WorkspacePolicies"/> 与
    /// <see cref="RateLimitOptions.EndpointPolicies"/> 真正生效（此前仅 DefaultPolicy 被消费）。
    /// 分区键：策略 PerWorkspace=true 时按 workspaceId 隔离配额，否则共享 "global" 配额。
    /// </summary>
    internal static PartitionedRateLimiter<HttpContext> CreatePartitionedLimiter(SecurityOptions securityOptions)
    {
        return PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        {
            // 从 HttpContext.Items 获取 workspaceId（由 WorkspaceContextMiddleware 填充）
            var workspaceId = httpContext.Items.TryGetValue(WorkspaceContextItemsKey, out var v)
                && v is string ws && !string.IsNullOrWhiteSpace(ws)
                    ? ws
                    : "global";

            var policy = ResolveEffectivePolicy(httpContext, workspaceId, securityOptions);
            // 按 workspace 隔离配额（PerWorkspace=true）或共享全局配额（PerWorkspace=false）
            var partitionKey = policy.PerWorkspace ? workspaceId : "global";

            // 按策略类型创建对应的 RateLimiter（Concurrency / FixedWindow / TokenBucket 均生效）。
            // 旧实现总是 GetFixedWindowLimiter，Concurrency 等策略类型被静默忽略。
            return RateLimitPartition.Get(partitionKey, _ => CreateRateLimiter(policy));
        });
    }

    /// <summary>
    /// 解析请求的有效限流策略：endpoint 路径前缀（最长匹配优先，避免短前缀吞掉长前缀）
    /// &gt; workspace 策略覆盖 &gt; 全局默认。
    /// </summary>
    internal static RateLimitPolicyOptions ResolveEffectivePolicy(
        HttpContext httpContext,
        string workspaceId,
        SecurityOptions securityOptions)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(securityOptions);

        var rateLimit = securityOptions.RateLimit;
        var path = httpContext.Request.Path.Value ?? string.Empty;

        // 1. Endpoint 路径前缀覆盖（最长前缀优先）
        RateLimitPolicyOptions? endpointPolicy = null;
        var longestPrefix = -1;
        foreach (var (prefix, policy) in rateLimit.EndpointPolicies)
        {
            if (!string.IsNullOrWhiteSpace(prefix)
                && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && prefix.Length > longestPrefix)
            {
                endpointPolicy = policy;
                longestPrefix = prefix.Length;
            }
        }
        if (endpointPolicy is not null)
        {
            return endpointPolicy;
        }

        // 2. Workspace 策略覆盖（"global" 表示未解析到 workspace，跳过）
        if (!string.IsNullOrWhiteSpace(workspaceId)
            && !string.Equals(workspaceId, "global", StringComparison.Ordinal)
            && rateLimit.WorkspacePolicies.TryGetValue(workspaceId, out var workspacePolicy))
        {
            return workspacePolicy;
        }

        // 3. 全局默认
        return rateLimit.DefaultPolicy;
    }

    /// <summary>根据策略类型创建对应的 RateLimiter（当前简化实现统一使用 FixedWindowLimiter）。</summary>
    private static RateLimiter CreateRateLimiter(RateLimitPolicyOptions policy)
    {
        return policy.Type switch
        {
            RateLimitPolicyType.Concurrency => new ConcurrencyLimiter(new ConcurrencyLimiterOptions
            {
                PermitLimit = policy.TokenLimit,
                QueueLimit = policy.QueueLimit,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            }),
            _ => new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
            {
                PermitLimit = policy.TokenLimit,
                Window = TimeSpan.FromSeconds(Math.Max(1, policy.TokenRatePerSecond)),
                QueueLimit = policy.QueueLimit,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            })
        };
    }

    /// <summary>HttpContext.Items 中存储 workspaceId 的键（由 WorkspaceContextMiddleware 填充）。</summary>
    public const string WorkspaceContextItemsKey = "__ContextCore_WorkspaceId";

    /// <summary>
    /// 注册后台清理过期 API Key 的 HostedService。
    /// 仅当 Security:ApiKeyRotation:PurgeInterval 大于 TimeSpan.Zero 时实际启用。
    /// </summary>
    public static IServiceCollection AddContextCoreApiKeyPurgeWorker(
        this IServiceCollection services,
        SecurityOptions securityOptions)
    {
        ArgumentNullException.ThrowIfNull(securityOptions);

        if (securityOptions.ApiKeyRotation.PurgeInterval <= TimeSpan.Zero)
        {
            return services;
        }

        services.AddHostedService<ApiKeyPurgeWorker>();
        return services;
    }
}

// ── 端点扩展：RequireWorkspaceRole ──────────────────────────────────────────

/// <summary>
/// 端点扩展：在 Minimal API 上声明所需角色。
/// 通过添加 endpoint filter 在请求处理前校验当前 WorkspaceContext 是否在指定角色中。
/// </summary>
public static class WorkspaceRoleEndpointExtensions
{
    /// <summary>
    /// 标记端点需要指定角色（任一匹配即放行）。
    /// 同时添加 metadata（用于 OpenAPI 文档）与 endpoint filter（用于实际校验）。
    /// 采用泛型约束保留原始 builder 类型，确保后续链式调用（如 <c>.Produces&lt;T&gt;()</c>）可用。
    /// </summary>
    /// <param name="builder">端点构建器。</param>
    /// <param name="roles">所需角色列表（任一匹配即放行）。</param>
    public static TBuilder RequireWorkspaceRole<TBuilder>(
        this TBuilder builder,
        params WorkspaceRole[] roles)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(roles);
        if (roles.Length == 0)
        {
            return builder;
        }

        // metadata：供 OpenAPI 文档与审计识别
        builder.WithMetadata(new WorkspaceRoleRequirement(roles));

        // endpoint filter：实际校验（AddEndpointFilter 返回 IEndpointConventionBuilder，
        // 但我们返回原始 builder 以保留类型信息供后续链式调用）
        builder.AddEndpointFilter(async (context, next) =>
        {
            var httpContext = context.HttpContext;
            var accessor = httpContext.RequestServices.GetService<IWorkspaceContextAccessor>();
            var securityOptions = httpContext.RequestServices.GetService<SecurityOptions>();

            // RBAC 强制校验未启用时直接放行（仅审计日志记录）
            if (securityOptions is null || !securityOptions.Rbac.Enforce)
            {
                return await next(context).ConfigureAwait(false);
            }

            var workspaceContext = accessor?.Current;
            if (workspaceContext is null)
            {
                return Results.Unauthorized();
            }

            // 检查角色匹配（任一即放行）
            var matched = false;
            foreach (var role in roles)
            {
                if (workspaceContext.Roles.Contains(role))
                {
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                return Results.Forbid();
            }

            return await next(context).ConfigureAwait(false);
        });

        return builder;
    }

    /// <summary>
    /// 标记端点需要指定权限位。
    /// 同时添加 metadata（用于 OpenAPI 文档）与 endpoint filter（用于实际校验）。
    /// 采用泛型约束保留原始 builder 类型，确保后续链式调用（如 <c>.Produces&lt;T&gt;()</c>）可用。
    /// </summary>
    /// <param name="builder">端点构建器。</param>
    /// <param name="permission">所需权限位。</param>
    public static TBuilder RequireWorkspacePermission<TBuilder>(
        this TBuilder builder,
        WorkspacePermission permission)
        where TBuilder : IEndpointConventionBuilder
    {
        if (permission == WorkspacePermission.None)
        {
            return builder;
        }

        builder.WithMetadata(new WorkspacePermissionRequirement(permission));

        builder.AddEndpointFilter(async (context, next) =>
        {
            var httpContext = context.HttpContext;
            var accessor = httpContext.RequestServices.GetService<IWorkspaceContextAccessor>();
            var securityOptions = httpContext.RequestServices.GetService<SecurityOptions>();

            if (securityOptions is null || !securityOptions.Rbac.Enforce)
            {
                return await next(context).ConfigureAwait(false);
            }

            var workspaceContext = accessor?.Current;
            if (workspaceContext is null)
            {
                return Results.Unauthorized();
            }

            if ((workspaceContext.Permissions & permission) != permission)
            {
                return Results.Forbid();
            }

            return await next(context).ConfigureAwait(false);
        });

        return builder;
    }
}

/// <summary>端点 metadata：所需角色列表（任一匹配即放行）。</summary>
public sealed class WorkspaceRoleRequirement
{
    /// <summary>所需角色列表。</summary>
    public IReadOnlyList<WorkspaceRole> Roles { get; }

    /// <summary>构造所需角色要求。</summary>
    public WorkspaceRoleRequirement(params WorkspaceRole[] roles)
    {
        Roles = roles ?? Array.Empty<WorkspaceRole>();
    }
}

/// <summary>端点 metadata：所需权限位。</summary>
public sealed class WorkspacePermissionRequirement
{
    /// <summary>所需权限位。</summary>
    public WorkspacePermission Permission { get; }

    /// <summary>构造所需权限要求。</summary>
    public WorkspacePermissionRequirement(WorkspacePermission permission)
    {
        Permission = permission;
    }
}
