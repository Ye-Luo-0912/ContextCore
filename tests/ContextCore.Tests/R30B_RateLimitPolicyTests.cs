using ContextCore.Service;
using ContextCore.Service.Security;
using Microsoft.AspNetCore.Http;

namespace ContextCore.Tests;

// ===========================================================================
// WP-B RateLimit 策略消费验收测试
//
// 目标：RateLimitOptions.WorkspacePolicies / EndpointPolicies 真正生效
// （此前 CreatePartitionedLimiter 仅消费 DefaultPolicy）。
//
// 覆盖：
//   1. ResolveEffectivePolicy 优先级：endpoint（最长前缀）> workspace > default；
//   2. 未解析 workspace（"global"）跳过 workspace 策略；
//   3. 分区行为：PerWorkspace=true 各 workspace 独立配额；false 共享全局配额；
//   4. Endpoint 策略（TokenLimit=1）实际生效（限流拒绝第二个请求）。
// ===========================================================================

[TestClass]
[TestCategory("R30")]
public sealed class R30B_RateLimitPolicyTests
{
    // ── 1. ResolveEffectivePolicy：优先级 ────────────────────────────────

    [TestMethod]
    public void ResolvePolicy_NoMatch_UsesDefault()
    {
        var options = new SecurityOptions
        {
            RateLimit = new RateLimitOptions
            {
                DefaultPolicy = new RateLimitPolicyOptions { TokenLimit = 100 },
                WorkspacePolicies = new Dictionary<string, RateLimitPolicyOptions>
                {
                    ["ws-a"] = new() { TokenLimit = 50 }
                },
                EndpointPolicies = new Dictionary<string, RateLimitPolicyOptions>
                {
                    ["/api/admin/"] = new() { TokenLimit = 10 }
                }
            }
        };

        var policy = SecurityServiceCollectionExtensions.ResolveEffectivePolicy(
            HttpContext("ws-other", "/api/agents/runs"), "ws-other", options);

        Assert.AreEqual(100, policy.TokenLimit, "无匹配时使用 DefaultPolicy。");
    }

    [TestMethod]
    public void ResolvePolicy_WorkspaceOverridesDefault()
    {
        var options = new SecurityOptions
        {
            RateLimit = new RateLimitOptions
            {
                DefaultPolicy = new RateLimitPolicyOptions { TokenLimit = 100 },
                WorkspacePolicies = new Dictionary<string, RateLimitPolicyOptions>
                {
                    ["ws-a"] = new() { TokenLimit = 50 }
                }
            }
        };

        var policy = SecurityServiceCollectionExtensions.ResolveEffectivePolicy(
            HttpContext("ws-a", "/api/agents/runs"), "ws-a", options);

        Assert.AreEqual(50, policy.TokenLimit, "workspace 策略应覆盖默认策略。");
    }

    [TestMethod]
    public void ResolvePolicy_EndpointOverridesWorkspace()
    {
        var options = new SecurityOptions
        {
            RateLimit = new RateLimitOptions
            {
                DefaultPolicy = new RateLimitPolicyOptions { TokenLimit = 100 },
                WorkspacePolicies = new Dictionary<string, RateLimitPolicyOptions>
                {
                    ["ws-a"] = new() { TokenLimit = 50 }
                },
                EndpointPolicies = new Dictionary<string, RateLimitPolicyOptions>
                {
                    ["/api/admin/"] = new() { TokenLimit = 10 }
                }
            }
        };

        var policy = SecurityServiceCollectionExtensions.ResolveEffectivePolicy(
            HttpContext("ws-a", "/api/admin/users"), "ws-a", options);

        Assert.AreEqual(10, policy.TokenLimit, "endpoint 策略应优先于 workspace 策略。");
    }

    [TestMethod]
    public void ResolvePolicy_LongestEndpointPrefixWins()
    {
        var options = new SecurityOptions
        {
            RateLimit = new RateLimitOptions
            {
                DefaultPolicy = new RateLimitPolicyOptions { TokenLimit = 100 },
                EndpointPolicies = new Dictionary<string, RateLimitPolicyOptions>
                {
                    ["/api/"] = new() { TokenLimit = 50 },
                    ["/api/admin/"] = new() { TokenLimit = 10 }
                }
            }
        };

        var policy = SecurityServiceCollectionExtensions.ResolveEffectivePolicy(
            HttpContext("ws-a", "/api/admin/users"), "ws-a", options);

        Assert.AreEqual(10, policy.TokenLimit, "最长前缀匹配应优先（/api/admin/ 而非 /api/）。");
    }

    [TestMethod]
    public void ResolvePolicy_GlobalWorkspaceSkipsWorkspacePolicies()
    {
        var options = new SecurityOptions
        {
            RateLimit = new RateLimitOptions
            {
                DefaultPolicy = new RateLimitPolicyOptions { TokenLimit = 100 },
                WorkspacePolicies = new Dictionary<string, RateLimitPolicyOptions>
                {
                    ["ws-a"] = new() { TokenLimit = 50 }
                }
            }
        };

        // "global" = 未解析到 workspace（WorkspaceContextMiddleware 未填充）→ 跳过 workspace 策略
        var policy = SecurityServiceCollectionExtensions.ResolveEffectivePolicy(
            HttpContext("global", "/api/agents/runs"), "global", options);

        Assert.AreEqual(100, policy.TokenLimit, "未解析 workspace 时不应应用 workspace 策略。");
    }

    // ── 2. 分区行为 ──────────────────────────────────────────────────────

    [TestMethod]
    public void PartitionedLimiter_PerWorkspace_IsolatesQuotasPerWorkspace()
    {
        var options = new SecurityOptions
        {
            RateLimit = new RateLimitOptions
            {
                DefaultPolicy = new RateLimitPolicyOptions
                {
                    Type = RateLimitPolicyType.FixedWindow,
                    TokenLimit = 1,
                    TokenRatePerSecond = 1,
                    QueueLimit = 0,
                    PerWorkspace = true
                }
            }
        };
        var limiter = SecurityServiceCollectionExtensions.CreatePartitionedLimiter(options);

        var ctxA1 = HttpContextWithWorkspace("ws-a", "/api/agents/runs");
        var ctxA2 = HttpContextWithWorkspace("ws-a", "/api/agents/runs");
        var ctxB = HttpContextWithWorkspace("ws-b", "/api/agents/runs");

        using var first = limiter.AttemptAcquire(ctxA1);
        Assert.IsTrue(first.IsAcquired, "首个请求应放行。");
        using var secondSame = limiter.AttemptAcquire(ctxA2);
        Assert.IsFalse(secondSame.IsAcquired, "同一 workspace 第二个请求应被限流（PerWorkspace=true 独立配额）。");
        using var otherWorkspace = limiter.AttemptAcquire(ctxB);
        Assert.IsTrue(otherWorkspace.IsAcquired, "不同 workspace 应拥有独立配额，不受 ws-a 消耗影响。");
    }

    [TestMethod]
    public void PartitionedLimiter_SharedQuota_SpansWorkspaces()
    {
        var options = new SecurityOptions
        {
            RateLimit = new RateLimitOptions
            {
                DefaultPolicy = new RateLimitPolicyOptions
                {
                    Type = RateLimitPolicyType.FixedWindow,
                    TokenLimit = 1,
                    TokenRatePerSecond = 1,
                    QueueLimit = 0,
                    PerWorkspace = false
                }
            }
        };
        var limiter = SecurityServiceCollectionExtensions.CreatePartitionedLimiter(options);

        using var first = limiter.AttemptAcquire(HttpContextWithWorkspace("ws-a", "/api/agents/runs"));
        Assert.IsTrue(first.IsAcquired, "首个请求应放行。");
        using var second = limiter.AttemptAcquire(HttpContextWithWorkspace("ws-b", "/api/agents/runs"));
        Assert.IsFalse(second.IsAcquired, "PerWorkspace=false 时各 workspace 共享全局配额，第二个请求应被限流。");
    }

    // ── 3. Endpoint 策略实际生效 ────────────────────────────────────────

    [TestMethod]
    public void PartitionedLimiter_EndpointPolicy_IsEnforced()
    {
        var options = new SecurityOptions
        {
            RateLimit = new RateLimitOptions
            {
                DefaultPolicy = new RateLimitPolicyOptions
                {
                    Type = RateLimitPolicyType.FixedWindow,
                    TokenLimit = 100,
                    TokenRatePerSecond = 10,
                    QueueLimit = 0,
                    PerWorkspace = true
                },
                EndpointPolicies = new Dictionary<string, RateLimitPolicyOptions>
                {
                    ["/api/admin/"] = new()
                    {
                        Type = RateLimitPolicyType.FixedWindow,
                        TokenLimit = 1,
                        TokenRatePerSecond = 1,
                        QueueLimit = 0,
                        PerWorkspace = true
                    }
                }
            }
        };
        var limiter = SecurityServiceCollectionExtensions.CreatePartitionedLimiter(options);

        using var first = limiter.AttemptAcquire(HttpContextWithWorkspace("ws-a", "/api/admin/users"));
        Assert.IsTrue(first.IsAcquired, "endpoint 策略 TokenLimit=1：首个请求放行。");
        using var second = limiter.AttemptAcquire(HttpContextWithWorkspace("ws-a", "/api/admin/users"));
        Assert.IsFalse(second.IsAcquired, "endpoint 策略应实际生效（第二个请求被限流，而非使用默认 100 配额）。");
    }

    // ── 辅助 ─────────────────────────────────────────────────────────────

    private static DefaultHttpContext HttpContext(string workspaceId, string path)
    {
        var httpContext = new DefaultHttpContext();
        if (!string.Equals(workspaceId, "global", StringComparison.Ordinal))
        {
            httpContext.Items[SecurityServiceCollectionExtensions.WorkspaceContextItemsKey] = workspaceId;
        }
        httpContext.Request.Method = "POST";
        httpContext.Request.Path = path;
        return httpContext;
    }

    private static DefaultHttpContext HttpContextWithWorkspace(string workspaceId, string path)
        => HttpContext(workspaceId, path);
}
