using System.Text.Json;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.AgentRunRuntime;

// ===========================================================================
// DefaultAgentToolCallValidator — 默认 Tool 调用校验器
//
// 实现 IAgentToolCallValidator 的默认安全校验逻辑（消费 Tool 声明，而非仅
// 依赖硬编码黑名单）：
// 1. ToolName 非空（基础合法性）。
// 2. Dispatcher 成员校验：Tool 必须位于 IToolDispatcher.SupportedTools 范围，
// 未注册 Tool 直接拒绝（fail-closed，不进入审批）。
// 3. Arguments 为合法 JSON（参数基础合法性）。
// 4. Schema 校验：从 IToolCatalog 的 Tool 定义读取 ParametersJsonSchema，
// 校验必需参数存在 + 参数类型匹配（best-effort：schema 缺失/空/损坏时跳过）。
// 5. 危险 Tool 黑名单（如 file_delete、shell_exec）→ RequiresApproval。
// 6. Descriptor 消费：ToolDescriptor 声明 RequiresApproval / 危险副作用
// （NonIdempotentWrite、RequiresReconciliation）/ RequiresIdempotencyKey
// 但调用未携带幂等键 / RequiresLeaseFence → RequiresApproval（聚合原因）。
//
// 设计决策：
// - Dispatcher / Catalog 通过构造参数注入（null 时跳过成员 / schema / descriptor
// 检查，保持仅黑名单的旧行为——兼容直接 new DefaultAgentToolCallValidator() 的路径）；
// - Catalog 为空时回退到 Dispatcher 的 IToolCatalog 实现（与 Actor 的解析策略一致）；
// - 校验不通过的 Tool 返回 IsValid=false（不分派）；
// - 黑名单匹配使用 OrdinalIgnoreCase（大小写不敏感）；
// - 完全无副作用，可单例注册。
// ===========================================================================

/// <summary>
/// 默认 Tool 调用校验器。
/// 校验 ToolName 非空、Dispatcher 成员、Arguments 合法 JSON、Schema 约束、
/// 危险 Tool 黑名单与 ToolDescriptor 声明的审批 / 副作用 / 幂等 / fence 要求。
/// </summary>
public sealed class DefaultAgentToolCallValidator : IAgentToolCallValidator
{
    /// <summary>默认危险 Tool 黑名单（file_delete / shell_exec / registry_set / process_kill）。</summary>
    public static readonly IReadOnlySet<string> DefaultDangerousTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "file_delete",
        "shell_exec",
        "registry_set",
        "process_kill"
    };

    private readonly IToolDispatcher? _dispatcher;
    private readonly IReadOnlyList<AgentToolDefinition>? _toolDefinitions;
    private readonly IReadOnlySet<string> _dangerousTools;
    private readonly ApprovalPolicyOptions? _approvalPolicy;
    private readonly IToolCostEstimator? _costEstimator;

    /// <summary>
    /// 构造默认校验器。
    /// </summary>
    /// <param name="dispatcher">Tool 分派器（提供 SupportedTools 成员校验与 Descriptor 声明；
    /// null 时跳过成员 / descriptor 检查）。</param>
    /// <param name="catalog">Tool 目录（提供 ParametersJsonSchema 供 schema 校验；
    /// null 时回退到 dispatcher 的 IToolCatalog 实现；均无定义时跳过 schema 检查）。</param>
    /// <param name="dangerousTools">危险 Tool 黑名单（null 时使用 <see cref="DefaultDangerousTools"/>）。</param>
    /// <param name="approvalPolicy">
    /// Approval Policy 配置（SecurityOptions.ApprovalPolicy）。非 null 且 Enabled=true 时，
    /// 费用/token 阈值触发审批；workspace 覆盖由 IAgentApprovalGate 在裁决时合并——校验器无 workspace 上下文）。
    /// </param>
    /// <param name="costEstimator">
    /// 服务端成本估算器（null 时回退到请求携带的模型填写估算值，兼容旧路径；
    /// 生产应注入估算器，成本阈值判定不依赖模型填写）。
    /// </param>
    public DefaultAgentToolCallValidator(
        IToolDispatcher? dispatcher = null,
        IToolCatalog? catalog = null,
        IReadOnlySet<string>? dangerousTools = null,
        ApprovalPolicyOptions? approvalPolicy = null,
        IToolCostEstimator? costEstimator = null)
    {
        _dispatcher = dispatcher;
        // 与 AgentRunActor 的 Tool 定义解析策略保持一致：显式 Catalog 优先，
        // 否则回退到 dispatcher 的 IToolCatalog 实现（如 RealToolDispatcher）。
        _toolDefinitions = catalog?.GetToolDefinitions()
            ?? (dispatcher as IToolCatalog)?.GetToolDefinitions();
        _dangerousTools = dangerousTools ?? DefaultDangerousTools;
        _approvalPolicy = approvalPolicy;
        _costEstimator = costEstimator;
    }

    /// <inheritdoc />
    public ValueTask<AgentToolCallValidationResult> ValidateAsync(
        string runId,
        AgentToolCallRequest toolCall,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        // 1. ToolName 非空校验
        if (string.IsNullOrWhiteSpace(toolCall.ToolName))
        {
            return Invalid("ToolName 不能为空。");
        }

        // 2. Dispatcher 成员校验（fail-closed：未注册 Tool 直接拒绝，不进入审批）
        if (_dispatcher is not null && !_dispatcher.SupportedTools.Contains(toolCall.ToolName))
        {
            return Invalid($"Tool '{toolCall.ToolName}' 不在 Tool Dispatcher 支持列表中，已拒绝（fail-closed）。");
        }

        // 3. Arguments 非空校验
        if (string.IsNullOrWhiteSpace(toolCall.Arguments))
        {
            return Invalid("Arguments 不能为空（必须为合法 JSON）。");
        }

        // 4. Arguments 合法 JSON 校验（doc 复用于 schema 校验，避免二次解析）
        JsonDocument argsDoc;
        try
        {
            argsDoc = JsonDocument.Parse(toolCall.Arguments);
        }
        catch (JsonException ex)
        {
            return Invalid($"Arguments 不是合法 JSON：{ex.Message}");
        }

        using (argsDoc)
        {
            // 5. Schema 校验（best-effort：schema 缺失 / 空 / 损坏时跳过，不误伤合法调用）
            var definition = _toolDefinitions?.FirstOrDefault(
                d => string.Equals(d.Name, toolCall.ToolName, StringComparison.Ordinal));
            if (definition is not null && !string.IsNullOrWhiteSpace(definition.ParametersJsonSchema))
            {
                if (!TryValidateSchema(argsDoc.RootElement, definition.ParametersJsonSchema, out var schemaError))
                {
                    return Invalid(schemaError ?? $"Tool '{toolCall.ToolName}' 的参数不符合声明的 JSON Schema。");
                }
            }

            // 6. 危险 Tool 黑名单检查（匹配则需审批）
            if (_dangerousTools.Contains(toolCall.ToolName))
            {
                return Approval($"Tool '{toolCall.ToolName}' 在危险操作黑名单中，需人工审批后执行。");
            }

            // 7. Descriptor 消费：声明级审批 / 危险副作用 / 幂等键缺失 / fence 要求 → 聚合审批原因
            var descriptor = _dispatcher?.GetDescriptor(toolCall.ToolName);
            if (descriptor is not null)
            {
                var reasons = new List<string>(2);
                if (descriptor.RequiresApproval)
                {
                    reasons.Add($"Tool '{toolCall.ToolName}' 声明 RequiresApproval=true，需人工审批后执行。");
                }
                if (descriptor.DeclaredSideEffect is ToolSideEffect.NonIdempotentWrite or ToolSideEffect.RequiresReconciliation)
                {
                    reasons.Add($"Tool '{toolCall.ToolName}' 声明副作用为 {descriptor.DeclaredSideEffect}，需人工审批后执行。");
                }
                if (descriptor.RequiresIdempotencyKey && string.IsNullOrWhiteSpace(toolCall.IdempotencyKey))
                {
                    reasons.Add($"Tool '{toolCall.ToolName}' 声明 RequiresIdempotencyKey，但调用未携带幂等键，需人工审批。");
                }
                if (descriptor.RequiresLeaseFence)
                {
                    reasons.Add($"Tool '{toolCall.ToolName}' 声明 RequiresLeaseFence，需人工审批确认执行上下文。");
                }
                if (reasons.Count > 0)
                {
                    return Approval(string.Join("；", reasons));
                }
            }

            // 8. 费用 / token 审批阈值（ApprovalPolicyOptions 全局阈值，仅校验器侧触发；
            // workspace 覆盖由 IAgentApprovalGate 在裁决时合并——校验器无 workspace 上下文）。
            // 成本判定优先使用服务端估算（不依赖模型填写）；估算器缺失时回退请求携带值（兼容旧路径）。
            if (_approvalPolicy is { Enabled: true })
            {
                var estimate = _costEstimator is not null
                    ? _costEstimator.Estimate(toolCall.ToolName, toolCall)
                    : new ToolCostEstimate
                    {
                        Tokens = toolCall.EstimatedTokens ?? 0,
                        CostUsd = toolCall.EstimatedCostUsd ?? 0
                    };

                if (_approvalPolicy.CostThresholdUsd > 0 && estimate.CostUsd >= _approvalPolicy.CostThresholdUsd)
                {
                    return Approval(
                        $"Tool '{toolCall.ToolName}' 预估费用 {estimate.CostUsd:F2} USD " +
                        $"超过审批阈值 {_approvalPolicy.CostThresholdUsd:F2} USD，需人工审批。");
                }
                if (_approvalPolicy.TokenThreshold > 0 && estimate.Tokens >= _approvalPolicy.TokenThreshold)
                {
                    return Approval(
                        $"Tool '{toolCall.ToolName}' 预估 token 消耗 {estimate.Tokens} " +
                        $"超过审批阈值 {_approvalPolicy.TokenThreshold}，需人工审批。");
                }
            }

            // 9. 普通校验通过
            return Pass();
        }
    }

    /// <summary>
    /// Best-effort 校验参数是否符合声明的 JSON Schema（子集：required + 属性类型）。
    /// schema 缺失 / 空 / 损坏时视为无约束（返回 true），避免误伤合法调用。
    /// </summary>
    private static bool TryValidateSchema(JsonElement argsRoot, string schemaJson, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(schemaJson) || schemaJson == "{}")
        {
            return true;
        }

        JsonDocument schemaDoc;
        try
        {
            schemaDoc = JsonDocument.Parse(schemaJson);
        }
        catch (JsonException)
        {
            // schema 声明损坏 → 无约束（best-effort，由分派期校验兜底）
            return true;
        }

        using (schemaDoc)
        {
            var schemaRoot = schemaDoc.RootElement;
            if (schemaRoot.ValueKind != JsonValueKind.Object || argsRoot.ValueKind != JsonValueKind.Object)
            {
                // schema 或参数不是 JSON 对象 → 无约束可校验
                return true;
            }

            // required 属性存在性校验
            if (schemaRoot.TryGetProperty("required", out var requiredElement) && requiredElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var requiredName in requiredElement.EnumerateArray())
                {
                    if (requiredName.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }
                    var name = requiredName.GetString()!;
                    if (!argsRoot.TryGetProperty(name, out _))
                    {
                        error = $"缺少必需参数 '{name}'（Tool 声明的 JSON Schema 要求）。";
                        return false;
                    }
                }
            }

            // 属性类型校验（仅校验 schema 显式声明 type 且参数实际携带的属性）
            if (schemaRoot.TryGetProperty("properties", out var propertiesElement) && propertiesElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in propertiesElement.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.Object
                        || !property.Value.TryGetProperty("type", out var typeElement)
                        || typeElement.ValueKind != JsonValueKind.String
                        || !argsRoot.TryGetProperty(property.Name, out var argValue))
                    {
                        continue;
                    }
                    if (!MatchesType(argValue, typeElement.GetString()))
                    {
                        error = $"参数 '{property.Name}' 类型不匹配：期望 {typeElement.GetString()}，实际 {JsonValueKindName(argValue.ValueKind)}。";
                        return false;
                    }
                }
            }

            return true;
        }
    }

    /// <summary>按 JSON Schema 子集判断参数值类型是否匹配声明类型。</summary>
    private static bool MatchesType(JsonElement value, string? typeName)
    {
        return typeName switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "number" => value.ValueKind == JsonValueKind.Number,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "array" => value.ValueKind == JsonValueKind.Array,
            "object" => value.ValueKind == JsonValueKind.Object,
            "null" => value.ValueKind == JsonValueKind.Null,
            // 未知类型声明 → 不校验（best-effort）
            _ => true
        };
    }

    private static string JsonValueKindName(JsonValueKind kind) => kind switch
    {
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Array => "array",
        JsonValueKind.Object => "object",
        JsonValueKind.Null => "null",
        _ => kind.ToString()
    };

    private static ValueTask<AgentToolCallValidationResult> Invalid(string error)
        => ValueTask.FromResult(new AgentToolCallValidationResult
        {
            IsValid = false,
            Error = error,
            RequiresApproval = false
        });

    private static ValueTask<AgentToolCallValidationResult> Approval(string reason)
        => ValueTask.FromResult(new AgentToolCallValidationResult
        {
            IsValid = true,
            Error = null,
            RequiresApproval = true,
            ApprovalReason = reason
        });

    private static ValueTask<AgentToolCallValidationResult> Pass()
        => ValueTask.FromResult(new AgentToolCallValidationResult
        {
            IsValid = true,
            Error = null,
            RequiresApproval = false
        });
}
