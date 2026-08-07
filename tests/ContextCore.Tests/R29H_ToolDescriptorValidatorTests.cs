using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.Service.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ContextCore.Tests;

// ===========================================================================
// Tool Validator 消费 Descriptor Truth 测试
//
// 验证 ：DefaultAgentToolCallValidator 从"硬编码黑名单"升级为消费
// Tool 声明（IToolDispatcher.SupportedTools 成员校验 + IToolCatalog 的
// ParametersJsonSchema + IToolDispatcher.GetDescriptor 的审批/副作用/幂等/fence）：
// 1. 成员校验：未注册 Tool fail-closed 拒绝（不进入审批）；
// 2. 黑名单：危险 Tool 仍需审批（与 Dispatcher 成员校验组合）；
// 3. Schema：required 属性存在性 + 属性类型匹配（best-effort，坏 schema 跳过）；
// 4. Descriptor：RequiresApproval / 危险副作用 / 幂等键缺失 / fence → 审批聚合；
// 5. 兼容性：无 Dispatcher/Catalog 注入时保持仅黑名单旧行为；
// 6. DI：RealDispatch 模式下校验器自动注入 Dispatcher+Catalog，schema 生效。
// ===========================================================================

[TestClass]
[TestCategory("Kill-Point")]
[TestCategory("External-Effect-Truth")]
public sealed class R29H_ToolDescriptorValidatorTests
{
    private const string Ws = "ws-validator";

    // ── 1. 成员校验（fail-closed）─────────────────────────────────────────

    /// <summary>
    /// 验证：Tool 不在 Dispatcher.SupportedTools 范围 → IsValid=false（fail-closed 拒绝，
    /// 不进入审批）。与黑名单组合时，未注册的危险 Tool 同样被拒绝（黑名单不绕过成员校验）。
    /// </summary>
    [TestMethod]
    public void Validator_UnknownTool_NotInDispatcherSupported_Rejected()
    {
        var validator = new DefaultAgentToolCallValidator(
            new ValidatorDispatcher().Add("search"));

        var result = Validate(validator, "unknown_tool", """{"query":"x"}""");

        Assert.IsFalse(result.IsValid, "未注册 Tool 应被拒绝。");
        Assert.IsFalse(result.RequiresApproval, "未注册 Tool 不应进入审批。");
        StringAssert.Contains(result.Error, "不在 Tool Dispatcher 支持列表");
    }

    /// <summary>
    /// 验证：黑名单 Tool 若未注册到 Dispatcher，成员校验先于黑名单拒绝
    /// （fail-closed 优先，未注册 Tool 永不分派，无需审批流程）。
    /// </summary>
    [TestMethod]
    public void Validator_BlacklistedButUnregisteredTool_RejectedNotApproved()
    {
        var validator = new DefaultAgentToolCallValidator(
            new ValidatorDispatcher().Add("echo"));

        var result = Validate(validator, "file_delete", """{"path":"/tmp/x"}""");

        Assert.IsFalse(result.IsValid, "未注册的 file_delete 应被成员校验拒绝。");
        Assert.IsFalse(result.RequiresApproval, "未注册 Tool 不应触发审批。");
    }

    // ── 2. 黑名单（危险 Tool 需审批）─────────────────────────────────────

    /// <summary>
    /// 验证：已注册的黑名单 Tool（file_delete）→ RequiresApproval=true。
    /// </summary>
    [TestMethod]
    public void Validator_DangerousTool_Blacklist_RequiresApproval()
    {
        var validator = new DefaultAgentToolCallValidator(
            new ValidatorDispatcher().Add("file_delete"));

        var result = Validate(validator, "file_delete", """{"path":"/tmp/x"}""");

        Assert.IsTrue(result.IsValid, "黑名单 Tool 本身是合法调用（需审批而非拒绝）。");
        Assert.IsTrue(result.RequiresApproval, "黑名单 Tool 应要求审批。");
        StringAssert.Contains(result.ApprovalReason, "危险操作黑名单");
    }

    /// <summary>
    /// 验证：无 Dispatcher/Catalog 注入时保持旧行为——黑名单 Tool 直接要求审批
    /// （兼容直接 new DefaultAgentToolCallValidator() 的构造路径）。
    /// </summary>
    [TestMethod]
    public void Validator_NoDispatcher_BlacklistOnly_PreservesLegacyBehavior()
    {
        var validator = new DefaultAgentToolCallValidator();

        var dangerous = Validate(validator, "file_delete", """{"path":"/tmp/x"}""");
        Assert.IsTrue(dangerous.IsValid, "旧路径下黑名单 Tool 应合法（需审批）。");
        Assert.IsTrue(dangerous.RequiresApproval, "旧路径下黑名单 Tool 应要求审批。");

        // 无 Dispatcher → 跳过成员校验，任意 Tool 名称均可通过（仅黑名单生效）
        var unknown = Validate(validator, "some_tool", "{}");
        Assert.IsTrue(unknown.IsValid, "无 Dispatcher 时不校验成员。");
        Assert.IsFalse(unknown.RequiresApproval, "非黑名单 Tool 无需审批。");
    }

    // ── 3. Schema 校验（required + 类型，best-effort）────────────────────

    private const string SearchSchema = """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}""";

    /// <summary>
    /// 验证：缺少 required 属性 → IsValid=false。
    /// </summary>
    [TestMethod]
    public void Validator_Schema_MissingRequired_Rejected()
    {
        var validator = new DefaultAgentToolCallValidator(
            new ValidatorDispatcher().Add("search", schema: SearchSchema));

        var result = Validate(validator, "search", """{"limit":10}""");

        Assert.IsFalse(result.IsValid, "缺少 required 属性应被 schema 校验拒绝。");
        StringAssert.Contains(result.Error, "缺少必需参数 'query'");
    }

    /// <summary>
    /// 验证：属性类型不匹配 → IsValid=false。
    /// </summary>
    [TestMethod]
    public void Validator_Schema_TypeMismatch_Rejected()
    {
        var validator = new DefaultAgentToolCallValidator(
            new ValidatorDispatcher().Add("search", schema: SearchSchema));

        var result = Validate(validator, "search", """{"query":42}""");

        Assert.IsFalse(result.IsValid, "类型不匹配应被 schema 校验拒绝。");
        StringAssert.Contains(result.Error, "类型不匹配");
        StringAssert.Contains(result.Error, "期望 string");
    }

    /// <summary>
    /// 验证：参数符合 schema → 通过且无需审批。
    /// </summary>
    [TestMethod]
    public void Validator_Schema_MatchingArgs_Passes()
    {
        var validator = new DefaultAgentToolCallValidator(
            new ValidatorDispatcher().Add("search", schema: SearchSchema));

        var result = Validate(validator, "search", """{"query":"hello"}""");

        Assert.IsTrue(result.IsValid, "符合 schema 的调用应通过。");
        Assert.IsFalse(result.RequiresApproval, "普通 Tool 无需审批。");
    }

    /// <summary>
    /// 验证：空 schema（"{}"）→ 无约束，任何合法 JSON 参数通过。
    /// </summary>
    [TestMethod]
    public void Validator_Schema_EmptySchema_SkipsConstraints()
    {
        var validator = new DefaultAgentToolCallValidator(
            new ValidatorDispatcher().Add("search", schema: "{}"));

        var result = Validate(validator, "search", """{"whatever":[1,2,3]}""");

        Assert.IsTrue(result.IsValid, "空 schema 不应施加约束。");
    }

    /// <summary>
    /// 验证：schema 声明损坏（非 JSON / 非对象）→ best-effort 跳过，不误伤合法调用。
    /// </summary>
    [TestMethod]
    public void Validator_Schema_MalformedSchema_SkipsConstraints()
    {
        var validator = new DefaultAgentToolCallValidator(
            new ValidatorDispatcher().Add("search", schema: "not-a-json-schema{{"));

        var result = Validate(validator, "search", "{}");

        Assert.IsTrue(result.IsValid, "损坏的 schema 应被跳过（best-effort）。");
    }

    /// <summary>
    /// 验证：Catalog 未显式注入时，回退到 Dispatcher 的 IToolCatalog 实现读取 schema。
    /// </summary>
    [TestMethod]
    public void Validator_Schema_FallsBackToDispatcherCatalog()
    {
        // ValidatorDispatcher 同时实现 IToolCatalog，不显式传 catalog 也应生效
        var validator = new DefaultAgentToolCallValidator(
            new ValidatorDispatcher().Add("search", schema: SearchSchema));

        var bad = Validate(validator, "search", "{}");
        Assert.IsFalse(bad.IsValid, "回退到 Dispatcher Catalog 时 schema 校验应生效。");
    }

    // ── 4. Descriptor 消费（审批聚合）────────────────────────────────────

    /// <summary>
    /// 验证：Descriptor 声明 RequiresApproval=true → RequiresApproval。
    /// </summary>
    [TestMethod]
    public void Validator_Descriptor_RequiresApproval_FlagsApproval()
    {
        var validator = new DefaultAgentToolCallValidator(
            new ValidatorDispatcher().Add("danger", descriptor: new ToolDescriptor
            {
                Name = "danger",
                RequiresApproval = true
            }));

        var result = Validate(validator, "danger", "{}");

        Assert.IsTrue(result.IsValid, "声明需审批的 Tool 是合法调用。");
        Assert.IsTrue(result.RequiresApproval, "Descriptor 声明 RequiresApproval 应要求审批。");
        StringAssert.Contains(result.ApprovalReason, "RequiresApproval=true");
    }

    /// <summary>
    /// 验证：Descriptor 声明危险副作用（NonIdempotentWrite / RequiresReconciliation）
    /// → RequiresApproval。
    /// </summary>
    [TestMethod]
    public void Validator_Descriptor_DangerousSideEffect_FlagsApproval()
    {
        var nonIdempotent = new DefaultAgentToolCallValidator(
            new ValidatorDispatcher().Add("email", descriptor: new ToolDescriptor
            {
                Name = "email",
                DeclaredSideEffect = ToolSideEffect.NonIdempotentWrite
            }));
        var r1 = Validate(nonIdempotent, "email", "{}");
        Assert.IsTrue(r1.RequiresApproval, "NonIdempotentWrite 副作用应要求审批。");
        StringAssert.Contains(r1.ApprovalReason, "NonIdempotentWrite");

        var reconciliation = new DefaultAgentToolCallValidator(
            new ValidatorDispatcher().Add("pay", descriptor: new ToolDescriptor
            {
                Name = "pay",
                DeclaredSideEffect = ToolSideEffect.RequiresReconciliation
            }));
        var r2 = Validate(reconciliation, "pay", "{}");
        Assert.IsTrue(r2.RequiresApproval, "RequiresReconciliation 副作用应要求审批。");
        StringAssert.Contains(r2.ApprovalReason, "RequiresReconciliation");
    }

    /// <summary>
    /// 验证：Descriptor 声明 RequiresIdempotencyKey 但调用未携带幂等键 → 审批；
    /// 携带幂等键 → 通过。
    /// </summary>
    [TestMethod]
    public void Validator_Descriptor_RequiresIdempotencyKey_MissingKey_FlagsApproval()
    {
        var validator = new DefaultAgentToolCallValidator(
            new ValidatorDispatcher().Add("upsert", descriptor: new ToolDescriptor
            {
                Name = "upsert",
                RequiresIdempotencyKey = true
            }));

        var missing = Validate(validator, "upsert", "{}", idempotencyKey: null);
        Assert.IsTrue(missing.RequiresApproval, "声明幂等键但调用未携带 → 应要求审批。");
        StringAssert.Contains(missing.ApprovalReason, "RequiresIdempotencyKey");

        var withKey = Validate(validator, "upsert", "{}", idempotencyKey: "key-1");
        Assert.IsTrue(withKey.IsValid, "携带幂等键的调用应通过。");
        Assert.IsFalse(withKey.RequiresApproval, "携带幂等键不应要求审批。");
    }

    /// <summary>
    /// 验证：Descriptor 声明 RequiresLeaseFence → 审批。
    /// </summary>
    [TestMethod]
    public void Validator_Descriptor_RequiresLeaseFence_FlagsApproval()
    {
        var validator = new DefaultAgentToolCallValidator(
            new ValidatorDispatcher().Add("fenced", descriptor: new ToolDescriptor
            {
                Name = "fenced",
                RequiresLeaseFence = true
            }));

        var result = Validate(validator, "fenced", "{}");

        Assert.IsTrue(result.RequiresApproval, "声明 fence 要求的 Tool 应要求审批。");
        StringAssert.Contains(result.ApprovalReason, "RequiresLeaseFence");
    }

    /// <summary>
    /// 验证：多个 Descriptor 标志 → 单一审批结果，原因聚合。
    /// </summary>
    [TestMethod]
    public void Validator_Descriptor_MultipleFlags_AggregatedReason()
    {
        var validator = new DefaultAgentToolCallValidator(
            new ValidatorDispatcher().Add("multi", descriptor: new ToolDescriptor
            {
                Name = "multi",
                RequiresApproval = true,
                DeclaredSideEffect = ToolSideEffect.NonIdempotentWrite,
                RequiresIdempotencyKey = true,
                RequiresLeaseFence = true
            }));

        var result = Validate(validator, "multi", "{}");

        Assert.IsTrue(result.RequiresApproval, "多标志应聚合为一次审批。");
        StringAssert.Contains(result.ApprovalReason, "RequiresApproval=true");
        StringAssert.Contains(result.ApprovalReason, "NonIdempotentWrite");
        StringAssert.Contains(result.ApprovalReason, "RequiresIdempotencyKey");
        StringAssert.Contains(result.ApprovalReason, "RequiresLeaseFence");
    }

    /// <summary>
    /// 验证：无标志 Descriptor（副作用 None）→ 通过，无需审批。
    /// </summary>
    [TestMethod]
    public void Validator_Descriptor_BenignDescriptor_Passes()
    {
        var validator = new DefaultAgentToolCallValidator(
            new ValidatorDispatcher().Add("echo", descriptor: new ToolDescriptor
            {
                Name = "echo",
                DeclaredSideEffect = ToolSideEffect.None
            }));

        var result = Validate(validator, "echo", """{"text":"hi"}""");

        Assert.IsTrue(result.IsValid, "无危险标志的 Tool 应通过。");
        Assert.IsFalse(result.RequiresApproval, "无危险标志不应要求审批。");
    }

    // ── 5. 基础合法性 ────────────────────────────────────────────────────

    /// <summary>
    /// 验证：ToolName 为空 → IsValid=false。
    /// </summary>
    [TestMethod]
    public void Validator_ToolName_Empty_Rejected()
    {
        var validator = new DefaultAgentToolCallValidator();

        var result = Validate(validator, "  ", "{}");

        Assert.IsFalse(result.IsValid, "空 ToolName 应被拒绝。");
    }

    /// <summary>
    /// 验证：Arguments 非合法 JSON → IsValid=false。
    /// </summary>
    [TestMethod]
    public void Validator_Arguments_InvalidJson_Rejected()
    {
        var validator = new DefaultAgentToolCallValidator(
            new ValidatorDispatcher().Add("echo"));

        var result = Validate(validator, "echo", "not json {{");

        Assert.IsFalse(result.IsValid, "非法 JSON 参数应被拒绝。");
        StringAssert.Contains(result.Error, "不是合法 JSON");
    }

    // ── 6. DI 组合：RealDispatch 模式下校验器自动注入 Dispatcher + Catalog ──

    /// <summary>
    /// 验证：RealDispatch 模式经 DI 解析的校验器自动注入 RealToolDispatcher
    /// （成员 + schema + descriptor 全部生效）。
    /// </summary>
    [TestMethod]
    public void DI_RealDispatchMode_Validator_EnforcesCatalogSchemaAndMembership()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "filesystem",
            ["ContextCoreRuntime:Profile"] = "Development",
            ["ContextCoreRuntime:ToolMode"] = "RealDispatch"
        });

        var services = new ServiceCollection();
        services.AddSingleton<IToolHandler>(new ValidatorTestHandler(
            "search",
            new ToolDescriptor { Name = "search", DeclaredSideEffect = ToolSideEffect.ReadOnly },
            schema: SearchSchema));
        services.AddContextCore(ContextCore.Abstractions.ModelExecutionOptions.Default);
        services.AddContextCoreRuntime(config);
        var provider = services.BuildServiceProvider();

        var validator = provider.GetRequiredService<IAgentToolCallValidator>();

        // schema：缺少 required → 拒绝
        var missingRequired = Validate(validator, "search", "{}");
        Assert.IsFalse(missingRequired.IsValid, "DI 注入 Catalog 后 schema 校验应生效。");

        // 成员：未注册 Tool → 拒绝
        var unknown = Validate(validator, "not_registered", "{}");
        Assert.IsFalse(unknown.IsValid, "DI 注入 Dispatcher 后成员校验应生效。");

        // 通过路径
        var ok = Validate(validator, "search", """{"query":"hello"}""");
        Assert.IsTrue(ok.IsValid, "符合 schema 的已注册 Tool 应通过。");
    }

    /// <summary>
    /// 验证：默认 Echo 模式经 DI 解析的校验器注入 EchoToolDispatcher——echo 通过、
    /// 其他 Tool 被成员校验拒绝。
    /// </summary>
    [TestMethod]
    public void DI_DefaultEchoMode_Validator_EnforcesMembership()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "filesystem",
            ["ContextCoreRuntime:Profile"] = "Development"
        });

        var services = new ServiceCollection();
        services.AddContextCore(ContextCore.Abstractions.ModelExecutionOptions.Default);
        services.AddContextCoreRuntime(config);
        var provider = services.BuildServiceProvider();

        var validator = provider.GetRequiredService<IAgentToolCallValidator>();

        var echo = Validate(validator, "echo", """{"text":"hi"}""");
        Assert.IsTrue(echo.IsValid, "echo 在 EchoToolDispatcher 支持范围内应通过。");

        var unknown = Validate(validator, "some_tool", "{}");
        Assert.IsFalse(unknown.IsValid, "Echo 模式下未注册 Tool 应被成员校验拒绝。");
    }

    // ── 7. 服务端成本估算（不依赖模型填写）────────────────────────────────

    /// <summary>
    /// 验证：注入服务端估算器后，审批成本判定使用服务端估算——模型在请求中填写的高估算值
    /// （EstimatedCostUsd=999）被服务端估算覆盖，参数小时不触发成本审批。
    /// </summary>
    [TestMethod]
    public void Validator_ServerSideEstimate_OverridesModelFilledHighCost()
    {
        var validator = new DefaultAgentToolCallValidator(
            new ValidatorDispatcher().Add("echo"),
            approvalPolicy: new ApprovalPolicyOptions { Enabled = true, CostThresholdUsd = 0.001 },
            costEstimator: new DefaultToolCostEstimator());

        // 参数极短（服务端估算 < 阈值），但模型谎报高费用——服务端估算覆盖后不应触发审批。
        var result = validator.ValidateAsync(
            "run-validator",
            new AgentToolCallRequest { ToolName = "echo", Arguments = """{"text":"x"}""", EstimatedCostUsd = 999 },
            CancellationToken.None).AsTask().GetAwaiter().GetResult();

        Assert.IsTrue(result.IsValid, "服务端估算覆盖模型填写值：短参数不应触发成本审批。");
        Assert.IsFalse(result.RequiresApproval, "模型高估不应绕过服务端估算。");
    }

    /// <summary>
    /// 验证：模型不填写估算值时，服务端估算仍能按参数大小触发成本审批。
    /// </summary>
    [TestMethod]
    public void Validator_ServerSideEstimate_TriggersOnLargeArguments()
    {
        var validator = new DefaultAgentToolCallValidator(
            new ValidatorDispatcher().Add("echo"),
            approvalPolicy: new ApprovalPolicyOptions { Enabled = true, CostThresholdUsd = 0.001 },
            costEstimator: new DefaultToolCostEstimator());

        // 参数足够大（服务端估算 tokens=2500 → cost=0.005 > 0.001），模型未填估算值。
        var largeArgs = new string('x', 10_000);
        var result = validator.ValidateAsync(
            "run-validator",
            new AgentToolCallRequest { ToolName = "echo", Arguments = "{\"text\":\"" + largeArgs + "\"}" },
            CancellationToken.None).AsTask().GetAwaiter().GetResult();

        Assert.IsTrue(result.RequiresApproval, "服务端估算超过成本阈值应触发审批（不依赖模型填写）。");
    }

    /// <summary>
    /// 验证：未注入估算器时回退到请求携带的模型填写值（兼容旧路径）。
    /// </summary>
    [TestMethod]
    public void Validator_WithoutEstimator_FallsBackToModelFilledValues()
    {
        var validator = new DefaultAgentToolCallValidator(
            new ValidatorDispatcher().Add("echo"),
            approvalPolicy: new ApprovalPolicyOptions { Enabled = true, CostThresholdUsd = 1.0 });

        var result = validator.ValidateAsync(
            "run-validator",
            new AgentToolCallRequest { ToolName = "echo", Arguments = """{"text":"x"}""", EstimatedCostUsd = 999 },
            CancellationToken.None).AsTask().GetAwaiter().GetResult();

        Assert.IsTrue(result.RequiresApproval, "无估算器时回退模型填写值触发成本审批（旧行为）。");
    }

    // ── 测试辅助 ─────────────────────────────────────────────────────────────

    private static AgentToolCallValidationResult Validate(
        IAgentToolCallValidator validator,
        string toolName,
        string arguments,
        string? idempotencyKey = null)
        => validator.ValidateAsync(
            "run-validator",
            new AgentToolCallRequest
            {
                ToolName = toolName,
                Arguments = arguments,
                IdempotencyKey = idempotencyKey
            },
            CancellationToken.None).AsTask().GetAwaiter().GetResult();

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> settings)
        => new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    /// <summary>同时实现 IToolDispatcher + IToolCatalog 的 stub（模拟 RealToolDispatcher）。</summary>
    private sealed class ValidatorDispatcher : IToolDispatcher, IToolCatalog
    {
        private readonly HashSet<string> _supported = new(StringComparer.Ordinal);
        private readonly List<AgentToolDefinition> _definitions = new();
        private readonly Dictionary<string, ToolDescriptor> _descriptors = new(StringComparer.Ordinal);

        public ValidatorDispatcher Add(string toolName, ToolDescriptor? descriptor = null, string? schema = null)
        {
            _supported.Add(toolName);
            _descriptors[toolName] = descriptor ?? new ToolDescriptor { Name = toolName };
            _definitions.Add(new AgentToolDefinition
            {
                Name = toolName,
                ParametersJsonSchema = schema ?? "{}"
            });
            return this;
        }

        public IReadOnlySet<string> SupportedTools => _supported;

        public ToolDescriptor? GetDescriptor(string toolName)
            => _descriptors.TryGetValue(toolName, out var descriptor) ? descriptor : null;

        public IReadOnlyList<AgentToolDefinition> GetToolDefinitions() => _definitions;

        public ValueTask<ToolDispatchResult> DispatchAsync(ToolDispatchRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new ToolDispatchResult
            {
                Succeeded = true,
                Result = request.Payload,
                Duration = TimeSpan.Zero,
                SideEffect = ToolSideEffect.None
            });
    }

    /// <summary>注册到 RealToolDispatcher 的 Tool Handler stub。</summary>
    private sealed class ValidatorTestHandler : IToolHandler
    {
        public ValidatorTestHandler(string toolName, ToolDescriptor descriptor, string? schema)
        {
            ToolName = toolName;
            Descriptor = descriptor;
            ParametersJsonSchema = schema;
        }

        public string ToolName { get; }
        public ToolDescriptor Descriptor { get; }
        public string? Description => $"Test tool: {ToolName}";
        public string? ParametersJsonSchema { get; }

        public ValueTask<ToolHandlerResult> HandleAsync(ToolExecutionContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new ToolHandlerResult
            {
                Succeeded = true,
                Result = "ok",
                SideEffect = ToolSideEffect.None
            });
    }
}
