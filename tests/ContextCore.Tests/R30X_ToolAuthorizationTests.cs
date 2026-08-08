using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Core.Services.Agent;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.Service;
using ContextCore.Service.Endpoints;
using ContextCore.Service.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContextCore.Tests;

// ===========================================================================
// Tool 授权接入执行链测试
//
// 验证三处重复校验：
// 1. Run 创建时签发不可变授权快照（ToolIds ∩ 主体可执行集；能力位 → 角色派生）；
// 2. Approval 裁决前复核（快照有效 + 工具在授权集内 + 审批者能力覆盖）；
// 3. Actor Tool 派发前复核（快照过期/策略漂移 → 终止本轮；工具未授权 → 跳过）。
// 低权限主体（仅 AgentRun）不能创建/审批/执行高危 Tool（file_* / process_* / network_*）。
// ===========================================================================

[TestClass]
[TestCategory("Agent-Actor")]
public sealed class R30X_ToolAuthorizationTests
{
    private const string Ws = "ws-tool-auth";
    private const string SessionId = "session-tool-auth";

    // ── 策略分类 ─────────────────────────────────────────────────────────

    [TestMethod]
    public void Policy_ClassifiesHighRiskTools_ToCapabilityBits()
    {
        var policy = new DefaultToolAuthorizationPolicy();
        Assert.AreEqual(WorkspacePermission.FileAccess, policy.GetRequirement("file_delete").RequiredCapability);
        Assert.AreEqual(WorkspacePermission.FileAccess, policy.GetRequirement("write_file").RequiredCapability);
        Assert.AreEqual(WorkspacePermission.ProcessExec, policy.GetRequirement("shell_exec").RequiredCapability);
        Assert.AreEqual(WorkspacePermission.ProcessExec, policy.GetRequirement("process_kill").RequiredCapability);
        Assert.AreEqual(WorkspacePermission.NetworkAccess, policy.GetRequirement("http_request").RequiredCapability);
        Assert.AreEqual(WorkspacePermission.NetworkAccess, policy.GetRequirement("web_fetch").RequiredCapability);
    }

    [TestMethod]
    public void Policy_BasicTools_OnlyRequireAgentRun()
    {
        var policy = new DefaultToolAuthorizationPolicy();
        var requirement = policy.GetRequirement("echo");
        Assert.AreEqual(WorkspacePermission.None, requirement.RequiredCapability);
        Assert.AreEqual(WorkspacePermission.AgentRun.ToString(), requirement.ExecutePermissionId);
        Assert.AreEqual(WorkspacePermission.AgentRun.ToString(), requirement.ApprovePermissionId);
    }

    [TestMethod]
    public void Policy_HighRiskTools_DeriveExecuteAndApproveIds()
    {
        var policy = new DefaultToolAuthorizationPolicy();
        var requirement = policy.GetRequirement("file_delete");
        Assert.AreEqual("ToolExecute:file_delete", requirement.ExecutePermissionId);
        Assert.AreEqual("ToolApprove:file_delete", requirement.ApprovePermissionId);
        Assert.AreEqual("v1", policy.PolicyVersion);
    }

    // ── Authorizer 策略回退 ───────────────────────────────────────────────

    [TestMethod]
    public async Task Authorizer_PolicyFallback_BlocksPrincipalWithoutCapability()
    {
        var authorizer = new DefaultToolAuthorizer(
            NullLogger<DefaultToolAuthorizer>.Instance, new DefaultToolAuthorizationPolicy());

        // Developer 仅持 AgentRun，无 FileAccess → 高危文件 Tool 拒绝。
        var result = await authorizer.AuthorizeAsync(Principal(WorkspaceRole.Developer), "file_delete");
        Assert.IsFalse(result.IsAuthorized, "低权限主体不应能通过高危 Tool 授权。");
    }

    [TestMethod]
    public async Task Authorizer_PolicyFallback_AllowsPrincipalWithCapability()
    {
        var authorizer = new DefaultToolAuthorizer(
            NullLogger<DefaultToolAuthorizer>.Instance, new DefaultToolAuthorizationPolicy());

        var result = await authorizer.AuthorizeAsync(Principal(WorkspaceRole.Admin), "file_delete");
        Assert.IsTrue(result.IsAuthorized, "Admin 持有 FileAccess 能力位，应通过授权。");
    }

    // ── Run 创建时签发快照 ────────────────────────────────────────────────

    [TestMethod]
    public async Task RunCreate_LowPrivilegePrincipal_ExcludesHighRiskTools()
    {
        // Developer（仅 AgentRun）显式请求 file_delete → 快照只授权基础工具。
        var (run, _) = await CreateRunViaEndpointAsync(
            Principal(WorkspaceRole.Developer), RbacOptions(),
            new[] { "echo", "file_delete" }, new DefaultToolAuthorizationPolicy());

        var snapshot = run.AuthorizationSnapshot;
        Assert.IsNotNull(snapshot, "策略已注册时应签发授权快照。");
        CollectionAssert.AreEquivalent(new[] { "echo" }, snapshot!.GrantedToolIds.ToList(),
            "低权限主体请求的高危 Tool 不应进入授权集。");
        CollectionAssert.DoesNotContain(snapshot.GrantedPermissions.ToList(), "ToolExecute:file_delete",
            "未授权工具不应派生执行权限标识。");
    }

    [TestMethod]
    public async Task RunCreate_AdminPrincipal_GrantsHighRiskTools()
    {
        var (run, _) = await CreateRunViaEndpointAsync(
            Principal(WorkspaceRole.Admin), RbacOptions(),
            new[] { "echo", "file_delete" }, new DefaultToolAuthorizationPolicy());

        var snapshot = run.AuthorizationSnapshot;
        Assert.IsNotNull(snapshot);
        CollectionAssert.AreEquivalent(new[] { "echo", "file_delete" }, snapshot!.GrantedToolIds.ToList());
        CollectionAssert.Contains(snapshot.GrantedPermissions.ToList(), "ToolExecute:file_delete");
        CollectionAssert.Contains(snapshot.GrantedPermissions.ToList(), "ToolApprove:file_delete");
        CollectionAssert.Contains(snapshot.GrantedPermissions.ToList(), WorkspacePermission.FileAccess.ToString());
        Assert.AreEqual("v1", snapshot.PolicyVersion, "快照应固化策略版本。");
        Assert.IsTrue(snapshot.ExpiresAt > DateTimeOffset.UtcNow, "快照应未过期（过期时间 = Run 截止时间）。");
    }

    [TestMethod]
    public async Task RunCreate_UnspecifiedTools_GrantsCapableSubsetOfCatalog()
    {
        // 未指定 ToolIds → 取 Catalog/Dispatcher 全部已注册工具中主体可执行的子集。
        var (run, _) = await CreateRunViaEndpointAsync(
            Principal(WorkspaceRole.Developer), RbacOptions(),
            toolIds: null, new DefaultToolAuthorizationPolicy());

        var snapshot = run.AuthorizationSnapshot;
        Assert.IsNotNull(snapshot);
        CollectionAssert.AreEquivalent(new[] { "echo" }, snapshot!.GrantedToolIds.ToList(),
            "Catalog 中仅 echo（基础工具）对 Developer 可执行。");
    }

    [TestMethod]
    public async Task RunCreate_NoPolicyRegistered_NoSnapshot()
    {
        // 策略未注册（旧路径）→ 不签发快照，派发时仅受 AllowedToolIds 约束。
        var (run, _) = await CreateRunViaEndpointAsync(
            Principal(WorkspaceRole.Admin), RbacOptions(),
            new[] { "echo" }, policy: null);

        Assert.IsNull(run.AuthorizationSnapshot, "策略未注册时不应签发快照。");
    }

    [TestMethod]
    public async Task RunCreate_RbacNotEnforced_FullTrustSnapshot()
    {
        // RBAC 未强制（Enforce=false）→ 与端点放行语义一致，视为全量授权。
        var (run, _) = await CreateRunViaEndpointAsync(
            Principal(WorkspaceRole.Developer),
            new SecurityOptions { Rbac = new RbacOptions { Enforce = false } },
            new[] { "echo", "file_delete" }, new DefaultToolAuthorizationPolicy());

        var snapshot = run.AuthorizationSnapshot;
        Assert.IsNotNull(snapshot);
        CollectionAssert.AreEquivalent(new[] { "echo", "file_delete" }, snapshot!.GrantedToolIds.ToList(),
            "RBAC 未强制时所有显式请求工具都应授权。");
    }

    // ── Approval 裁决前复核 ───────────────────────────────────────────────

    [TestMethod]
    public async Task Approval_LowPrivilegeApprover_Forbidden()
    {
        var snapshot = Snapshot(new[] { "file_delete" });
        var run = BuildRun("审批复核", snapshot);
        var approval = Approval("file_delete");

        var error = await AgentExecutionEndpoints.CheckApprovalAuthorizationAsync(
            run, approval, Principal(WorkspaceRole.Developer),
            RbacOptions(), new DefaultToolAuthorizationPolicy(), toolAuthorizer: null, CancellationToken.None);

        Assert.IsNotNull(error, "仅持 AgentRun 的低权限审批者不能批准高危 Tool。");
    }

    [TestMethod]
    public async Task Approval_CapableApprover_Passes()
    {
        var snapshot = Snapshot(new[] { "file_delete" });
        var run = BuildRun("审批复核", snapshot);
        var approval = Approval("file_delete");

        var error = await AgentExecutionEndpoints.CheckApprovalAuthorizationAsync(
            run, approval, Principal(WorkspaceRole.Admin),
            RbacOptions(), new DefaultToolAuthorizationPolicy(), toolAuthorizer: null, CancellationToken.None);

        Assert.IsNull(error, "Admin 持有 FileAccess 能力位，应可批准高危 Tool。");
    }

    [TestMethod]
    public async Task Approval_ExpiredSnapshot_Forbidden()
    {
        var snapshot = Snapshot(new[] { "file_delete" }, expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        var run = BuildRun("审批复核", snapshot);

        var error = await AgentExecutionEndpoints.CheckApprovalAuthorizationAsync(
            run, Approval("file_delete"), Principal(WorkspaceRole.Admin),
            RbacOptions(), new DefaultToolAuthorizationPolicy(), toolAuthorizer: null, CancellationToken.None);

        Assert.IsNotNull(error, "快照过期后不得批准执行。");
    }

    [TestMethod]
    public async Task Approval_ToolNotInSnapshot_Forbidden()
    {
        // 快照只授权 echo，模型/审批却针对 file_delete。
        var snapshot = Snapshot(new[] { "echo" });
        var run = BuildRun("审批复核", snapshot);

        var error = await AgentExecutionEndpoints.CheckApprovalAuthorizationAsync(
            run, Approval("file_delete"), Principal(WorkspaceRole.Admin),
            RbacOptions(), new DefaultToolAuthorizationPolicy(), toolAuthorizer: null, CancellationToken.None);

        Assert.IsNotNull(error, "不在授权快照内的工具不得被批准。");
    }

    // ── Actor Tool 派发前复核 ─────────────────────────────────────────────

    [TestMethod]
    public async Task Dispatch_ToolNotGranted_SkipsTool_RunContinues()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("未授权跳过", Snapshot(new[] { "echo" }));
        await runStore.CreateAsync(run);

        var dispatcher = new StubToolDispatcher();
        var transport = new ScriptedToolCallTransport("file_delete");
        var actor = new AgentRunActor(
            runStore, eventStore, transport,
            new DefaultAgentLoopPolicy(),
            dispatcher,
            toolAuthorizationPolicy: new DefaultToolAuthorizationPolicy());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        var stored = await runStore.GetAsync(Ws, run.RunId);
        Assert.AreEqual(AgentRunState.Completed, stored!.State, "未授权 Tool 被跳过，Run 应继续完成。");
        CollectionAssert.DoesNotContain(dispatcher.DispatchedTools, "file_delete", "未授权 Tool 不应被分派。");

        var events = await eventStore.ReadAsync(Ws, run.RunId);
        // payload 为 JSON 序列化（非 ASCII 以 \uXXXX 转义），反序列化后校验 error 字段。
        var hasAuthzRejection = events.Any(e =>
        {
            if (e.EventType != AgentRunEventType.ToolCallCompleted || string.IsNullOrEmpty(e.Payload))
            {
                return false;
            }
            using var doc = JsonDocument.Parse(e.Payload);
            return doc.RootElement.TryGetProperty("error", out var error)
                   && error.ValueKind == JsonValueKind.String
                   && error.GetString()?.Contains("不在 Run 授权快照") == true;
        });
        Assert.IsTrue(hasAuthzRejection, "应有记录授权拒绝的 ToolCallCompleted 事件。");
    }

    [TestMethod]
    public async Task Dispatch_ExpiredSnapshot_FailsRun()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("过期快照", Snapshot(new[] { "file_delete" }, expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1)));
        await runStore.CreateAsync(run);

        var dispatcher = new StubToolDispatcher();
        var actor = new AgentRunActor(
            runStore, eventStore, new ScriptedToolCallTransport("file_delete"),
            new DefaultAgentLoopPolicy(),
            dispatcher,
            toolAuthorizationPolicy: new DefaultToolAuthorizationPolicy());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        var stored = await runStore.GetAsync(Ws, run.RunId);
        Assert.AreEqual(AgentRunState.Failed, stored!.State, "快照过期 → 终止本轮（fail-closed）。");
        Assert.IsNotNull(stored.FailureReason);
        Assert.IsTrue(stored.FailureReason!.Contains("过期"), "失败原因应说明快照过期。");
        CollectionAssert.DoesNotContain(dispatcher.DispatchedTools, "file_delete", "过期快照下不得分派任何 Tool。");
    }

    [TestMethod]
    public async Task Dispatch_GrantedTool_ExecutesNormally()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("已授权执行", Snapshot(new[] { "file_delete" }));
        await runStore.CreateAsync(run);

        var dispatcher = new StubToolDispatcher();
        var actor = new AgentRunActor(
            runStore, eventStore, new ScriptedToolCallTransport("file_delete"),
            new DefaultAgentLoopPolicy(),
            dispatcher,
            toolAuthorizationPolicy: new DefaultToolAuthorizationPolicy());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        var stored = await runStore.GetAsync(Ws, run.RunId);
        Assert.AreEqual(AgentRunState.Completed, stored!.State, "已授权 Tool 正常执行并完成。");
        CollectionAssert.Contains(dispatcher.DispatchedTools, "file_delete", "授权集内的 Tool 应被分派。");
    }

    [TestMethod]
    public async Task Dispatch_NoSnapshot_LegacyBasicTool_Allows()
    {
        // 无快照（历史 Run）+ 基础无副作用 Tool → 兼容放行（生产模式治理边界）。
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("旧路径基础工具", snapshot: null);
        await runStore.CreateAsync(run);

        var dispatcher = new StubToolDispatcher();
        var actor = new AgentRunActor(
            runStore, eventStore, new ScriptedToolCallTransport("echo"),
            new DefaultAgentLoopPolicy(),
            dispatcher,
            toolAuthorizationPolicy: new DefaultToolAuthorizationPolicy());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        var stored = await runStore.GetAsync(Ws, run.RunId);
        Assert.AreEqual(AgentRunState.Completed, stored!.State, "基础无副作用 Tool 可兼容放行。");
        CollectionAssert.Contains(dispatcher.DispatchedTools, "echo");
    }

    [TestMethod]
    public async Task Dispatch_NoSnapshot_LegacySideEffectTool_RequiresReauthorization()
    {
        // 无快照（历史 Run）+ File/Process/Network 类副作用 Tool → 生产模式要求重新授权
        // （旧 Run 不得成为绕过新安全模型的永久例外）。
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("旧路径副作用工具", snapshot: null);
        await runStore.CreateAsync(run);

        var dispatcher = new StubToolDispatcher();
        var actor = new AgentRunActor(
            runStore, eventStore, new ScriptedToolCallTransport("file_delete"),
            new DefaultAgentLoopPolicy(),
            dispatcher,
            toolAuthorizationPolicy: new DefaultToolAuthorizationPolicy());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        var stored = await runStore.GetAsync(Ws, run.RunId);
        CollectionAssert.DoesNotContain(dispatcher.DispatchedTools, "file_delete",
            "File/Process/Network 类 Tool 不得在无授权快照下分派（要求重新授权）。");
        Assert.IsTrue(stored!.FailureReason?.Contains("重新授权") == true,
            "失败原因应说明要求重新授权。");
    }

    [TestMethod]
    public async Task Dispatch_StaleAuthorizationEpoch_RejectsImmediately()
    {
        // AuthorizationEpoch：管理员撤权（epoch++）后，固化旧纪元的快照立即失效
        // （无需等待 ExpiresAt）——一次轻量整数比较使全部旧授权快照失效。
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("旧纪元快照", Snapshot(new[] { "echo" }) with { AuthorizationEpoch = 41 });
        await runStore.CreateAsync(run);

        var dispatcher = new StubToolDispatcher();
        var actor = new AgentRunActor(
            runStore, eventStore, new ScriptedToolCallTransport("echo"),
            new DefaultAgentLoopPolicy(),
            dispatcher,
            toolAuthorizationPolicy: new DefaultToolAuthorizationPolicy());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        var stored = await runStore.GetAsync(Ws, run.RunId);
        CollectionAssert.DoesNotContain(dispatcher.DispatchedTools, "echo",
            "旧纪元快照不得分派任何 Tool。");
        Assert.IsTrue(stored!.FailureReason?.Contains("授权纪元") == true,
            "失败原因应说明授权纪元已变更。");
    }

    // ── 测试辅助 ─────────────────────────────────────────────────────────

    private static WorkspaceContext Principal(WorkspaceRole role) => new()
    {
        WorkspaceId = Ws,
        Source = "test",
        ApiKeyId = $"key-{role}",
        Roles = new[] { role },
        IsAuthenticated = true
    };

    private static SecurityOptions RbacOptions() => new()
    {
        Rbac = new RbacOptions { Enforce = true },
        Quota = new WorkspaceQuotaOptions { Enabled = false }
    };

    private static ToolAuthorizationSnapshot Snapshot(
        IReadOnlyList<string> grantedTools,
        DateTimeOffset? expiresAt = null)
    {
        var policy = new DefaultToolAuthorizationPolicy();
        var permissions = new List<string> { WorkspacePermission.AgentRun.ToString() };
        var capabilities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in grantedTools)
        {
            var requirement = policy.GetRequirement(tool);
            if (requirement.RequiredCapability != WorkspacePermission.None)
            {
                permissions.Add(requirement.ExecutePermissionId);
                permissions.Add(requirement.ApprovePermissionId);
                capabilities.Add(requirement.RequiredCapability.ToString());
            }
        }
        permissions.AddRange(capabilities);

        return new ToolAuthorizationSnapshot
        {
            WorkspaceId = Ws,
            PrincipalId = "principal-test",
            GrantedToolIds = grantedTools,
            GrantedPermissions = permissions,
            PolicyVersion = new DefaultToolAuthorizationPolicy().PolicyVersion,
            // 默认固化当前授权纪元（旧纪元失效测试用 with 覆盖）。
            AuthorizationEpoch = new DefaultToolAuthorizationPolicy().AuthorizationEpoch,
            IssuedAt = DateTimeOffset.UtcNow.AddHours(-1),
            ExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddHours(1)
        };
    }

    private static AgentApproval Approval(string toolName) => new()
    {
        ApprovalId = "approval-1",
        RunId = "run-approval",
        WorkspaceId = Ws,
        ToolCallId = "toolcall-1",
        ToolName = toolName,
        Status = AgentApprovalStatus.Pending,
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static AgentRun BuildRun(string task, ToolAuthorizationSnapshot? snapshot) => new()
    {
        RunId = "run-" + Guid.NewGuid().ToString("N"),
        WorkspaceId = Ws,
        SessionId = SessionId,
        Task = task,
        State = AgentRunState.Created,
        Turn = 0,
        ModelCallsUsed = 0,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        TurnBudget = new AgentTurnBudget { MaxTurns = 10, TurnsUsed = 0, MaxModelCalls = 10 },
        AuthorizationSnapshot = snapshot
    };

    private static async Task<(AgentRun Run, int Status)> CreateRunViaEndpointAsync(
        WorkspaceContext principal,
        SecurityOptions? securityOptions,
        IReadOnlyList<string>? toolIds,
        IToolAuthorizationPolicy? policy)
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var host = BuildHost(runStore, eventStore);

        try
        {
            var accessor = new FixedWorkspaceContextAccessor();
            accessor.Set(principal);

            var httpContext = new DefaultHttpContext
            {
                RequestServices = BuildRequestServices(securityOptions, policy),
                Response = { Body = new MemoryStream() }
            };

            var request = new CreateRunRequest
            {
                Task = "授权快照测试任务",
                WorkspaceId = Ws,
                SessionId = SessionId,
                ToolIds = toolIds
            };

            var result = await AgentExecutionEndpoints.CreateAgentRunHandlerAsync(
                request, runStore, host, accessor, httpContext, CancellationToken.None);
            await result.ExecuteAsync(httpContext);

            var runs = await runStore.ListBySessionAsync(Ws, SessionId);
            var run = runs.FirstOrDefault()
                      ?? throw new AssertFailedException("Run 未创建。");
            return (run, httpContext.Response.StatusCode);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    private static AgentKernelHost BuildHost(InMemoryAgentRunStore runStore, InMemoryAgentRunEventStore eventStore)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAgentRunStore>(runStore);
        services.AddSingleton<IAgentRunEventStore>(eventStore);
        services.AddSingleton<IToolDispatcher>(new EchoToolDispatcher());
        services.AddSingleton<IAgentModelTransport>(new FinalAnswerTransport());
        services.AddSingleton<AgentKernelHost>();
        services.AddSingleton(new AgentHostOptions
        {
            ChannelCapacity = 8,
            WorkerCount = 2,
            DrainTimeout = TimeSpan.FromSeconds(5)
        });
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        return services.BuildServiceProvider().GetRequiredService<AgentKernelHost>();
    }

    private static ServiceProvider BuildRequestServices(SecurityOptions? securityOptions, IToolAuthorizationPolicy? policy)
    {
        var services = new ServiceCollection();
        if (securityOptions is not null)
        {
            services.AddSingleton(securityOptions);
        }
        if (policy is not null)
        {
            services.AddSingleton<IToolAuthorizationPolicy>(policy);
        }
        services.AddSingleton<IToolDispatcher>(new EchoToolDispatcher());
        services.AddSingleton<IToolCatalog>(new EchoToolDispatcher());
        services.AddLogging();
        return services.BuildServiceProvider();
    }

    private sealed class FixedWorkspaceContextAccessor : IWorkspaceContextAccessor
    {
        public WorkspaceContext? Current { get; private set; }

        public void Set(WorkspaceContext context) => Current = context;

        public void Clear() => Current = null;
    }

    /// <summary>支持高危 Tool 名称的测试分派器（记录实际分派）。</summary>
    private sealed class StubToolDispatcher : IToolDispatcher, IToolCatalog
    {
        public IReadOnlySet<string> SupportedTools { get; } = new HashSet<string>(StringComparer.Ordinal)
        {
            "echo", "file_delete", "file_read", "http_request"
        };

        public List<string> DispatchedTools { get; } = new();

        public ToolDescriptor? GetDescriptor(string toolName) => null;

        public IReadOnlyList<AgentToolDefinition> GetToolDefinitions() => SupportedTools
            .Select(t => new AgentToolDefinition
            {
                Name = t,
                Description = $"测试工具 {t}",
                ParametersJsonSchema = "{}"
            })
            .ToList();

        public ValueTask<ToolDispatchResult> DispatchAsync(
            ToolDispatchRequest request,
            CancellationToken cancellationToken = default)
        {
            DispatchedTools.Add(request.ToolName);
            return ValueTask.FromResult(new ToolDispatchResult
            {
                Succeeded = true,
                Result = "ok",
                Duration = TimeSpan.Zero,
                SideEffect = ToolSideEffect.None
            });
        }
    }

    /// <summary>首次调用返回指定 Tool 调用、之后返回最终答案的模型传输。</summary>
    private sealed class ScriptedToolCallTransport : IAgentModelTransport
    {
        private readonly string _toolName;
        private bool _first = true;

        public ScriptedToolCallTransport(string toolName)
        {
            _toolName = toolName;
        }

        public ValueTask<AgentModelResponse> CallAsync(string runId, string context, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("应调用结构化 messages 重载。");

        public ValueTask<AgentModelResponse> CallAsync(
            string runId,
            IReadOnlyList<AgentMessage> messages,
            CancellationToken cancellationToken = default)
        {
            if (_first)
            {
                _first = false;
                return ValueTask.FromResult(new AgentModelResponse
                {
                    Content = "调用工具",
                    ToolCalls = new[]
                    {
                        new AgentToolCallRequest { ToolName = _toolName, Arguments = "{}" }
                    },
                    IsFinalAnswer = false,
                    TokensConsumed = 10,
                    Duration = TimeSpan.FromMilliseconds(1)
                });
            }

            return ValueTask.FromResult(new AgentModelResponse
            {
                Content = "完成",
                ToolCalls = Array.Empty<AgentToolCallRequest>(),
                IsFinalAnswer = true,
                TokensConsumed = 10,
                Duration = TimeSpan.FromMilliseconds(1)
            });
        }

        public ValueTask<AgentModelResponse> CallAsync(AgentModelRequest request, CancellationToken cancellationToken = default)
            => CallAsync(request.RunId, request.Messages, cancellationToken);
    }

    /// <summary>立即返回最终答案的模型传输（端点夹具用）。</summary>
    private sealed class FinalAnswerTransport : IAgentModelTransport
    {
        public ValueTask<AgentModelResponse> CallAsync(string runId, string context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Answer());

        public ValueTask<AgentModelResponse> CallAsync(
            string runId,
            IReadOnlyList<AgentMessage> messages,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Answer());

        public ValueTask<AgentModelResponse> CallAsync(AgentModelRequest request, CancellationToken cancellationToken = default)
            => CallAsync(request.RunId, request.Messages, cancellationToken);

        private static AgentModelResponse Answer() => new()
        {
            Content = "完成",
            ToolCalls = Array.Empty<AgentToolCallRequest>(),
            IsFinalAnswer = true,
            TokensConsumed = 10,
            Duration = TimeSpan.FromMilliseconds(1)
        };
    }
}
