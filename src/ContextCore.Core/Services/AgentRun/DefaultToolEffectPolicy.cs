using ContextCore.Abstractions;

namespace ContextCore.Core.Services.AgentRunRuntime;

// ===========================================================================
// DefaultToolEffectPolicy — Tool 执行策略默认实现
// 
// 严格提交矩阵（Descriptor.DeclaredSideEffect → 执行后处置）：
// None / ReadOnly → 结果确定后 Commit（只读，重放安全）
// Write → 执行成功时 Commit；失败时 Hold（副作用是否发生未知）
// IdempotentWrite → 稳定幂等键明确返回且执行成功时 Commit；否则 Hold
// FencedWrite → 有效 Fence 确认（执行成功且 Fence 窗口内）时 Commit；否则 Hold
// NonIdempotentWrite → 永不自动提交：Approval + 外部操作身份确认后经对账提交
// RequiresReconciliation → 永不自动提交：必须经 Reconciliation Handler 确认后提交
// Unknown → 永不自动提交（保守策略）
// 
// 该矩阵替代执行器旧的"effectiveSideEffect != Unknown → MarkCommittedWithResultAsync"
// 判定，杜绝以下危险状态被错误自动提交：
// - NonIdempotentWrite（外部副作用不可重放，必须对账）
// - RequiresReconciliation（声明要求对账，未经 Handler 确认不得提交）
// - 外部调用失败但副作用是否发生未知（Succeeded=false）
// - Handler 返回失败但 DeclaredSideEffect 为写（部分副作用可能已发生）
// - Fence 未得到外部系统确认的写操作
// 
// 审批门扩展（Actor 门 → 策略层）：
// RequiresApproval=true + 写副作用 + 未确认审批（approvalGranted=false）→
// 禁止自动提交（Hold + RequiresApprovalBeforeCommit=true），
// 防止绕过 Actor 审批门的直连调用自动执行外部写副作用。
// Actor 经 IAgentApprovalGate 放行后传 approvalGranted=true，保持既有提交流程。
// 
// 重试决策（Dispatch 失败时，安全契约）：
// 外部写调用失败/超时 ≠ 副作用没有发生（请求发送成功 → 外部系统完成写入 → 返回包丢失）。
// 因此普通 Write 没有稳定 IdempotencyKey 或 Provider Fence 时，不能依据本地 retry 配置自动重试。
// 允许自动重试的唯一条件（Descriptor.RetrySafety 显式声明 + 运行时验证）：
// - None/ReadOnly → 无外部副作用面（BeforeDispatchOnly 语义），重放安全；
// - ProviderIdempotent → Provider 明确支持稳定幂等键（且本次携带稳定键）；
// - ProviderConfirmedNoEffect → Provider 本次明确返回 NoEffectConfirmed=true；
// 其余（Never / 未满足条件 / NonIdempotentWrite / RequiresReconciliation / FencedWrite / Unknown）
// → 永不自动重试。退避：Linear = 固定 RetryDelay；Exponential = RetryDelay * 2^(attempt-1)。
// ===========================================================================

/// <summary>
/// Tool 执行策略默认实现：基于 <see cref="ToolDescriptor.DeclaredSideEffect"/> 的严格提交矩阵，
/// 附加审批门扩展与失败重试决策。
/// </summary>
public sealed class DefaultToolEffectPolicy : IToolEffectPolicy
{
    /// <inheritdoc />
    public ToolExecutionPolicy Resolve(
        ToolDescriptor descriptor,
        ToolDispatchPrepareResult? journal,
        ToolExecutionResult? result,
        int attempt = 0,
        bool approvalGranted = false)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        // 1. 基础处置：严格提交矩阵（声明副作用 → Commit / Hold / FailClosed）。
        var policy = ResolveDisposition(descriptor, journal, result);

        // 2. 审批门扩展：RequiresApproval 声明 + 写副作用 + 未确认审批 → 禁止自动提交。
        // Actor 门放行后 approvalGranted=true，本门退化为记录标记（RequiresApprovalBeforeCommit=false）。
        // ReadOnly/None 无外部副作用，审批仅由 Actor 门负责，策略层不拦截。
        if (descriptor.RequiresApproval && !approvalGranted && IsExternalSideEffect(descriptor.DeclaredSideEffect))
        {
            policy = policy with
            {
                Disposition = ToolExecutionDisposition.HoldForReconciliation,
                Reason = string.IsNullOrEmpty(policy.Reason)
                    ? "RequiresApproval 声明未确认审批：写副作用禁止自动提交"
                    : $"{policy.Reason}；RequiresApproval 未确认审批，禁止自动提交",
                RequiresApprovalBeforeCommit = true
            };
        }

        // 3. 重试决策（仅 Dispatch 失败时；成功时恒 Abort）。
        policy = policy with { Retry = DecideRetry(descriptor, result, attempt) };

        // 4. 投递模式决策：回传 Descriptor 声明（策略层可未来按副作用/运行时条件覆盖）。
        policy = policy with { DeliveryMode = descriptor.DeliveryMode };

        return policy;
    }

    /// <summary>
    /// 严格提交矩阵：根据 Descriptor.DeclaredSideEffect 与执行结果解析基础处置。
    /// </summary>
    private static ToolExecutionPolicy ResolveDisposition(
        ToolDescriptor descriptor,
        ToolDispatchPrepareResult? journal,
        ToolExecutionResult? result)
    {
        switch (descriptor.DeclaredSideEffect)
        {
            case ToolSideEffect.None:
            case ToolSideEffect.ReadOnly:
                // 只读/无副作用：结果确定后即可提交（重放安全，无需额外前置条件）。
                return new ToolExecutionPolicy
                {
                    Disposition = ToolExecutionDisposition.Commit,
                    Reason = "只读/无副作用，结果确定后提交"
                };

            case ToolSideEffect.Write:
                // 普通写：执行成功 → 结果确定，可提交；失败 → 部分副作用可能已发生，不可自动提交。
                if (result is null)
                {
                    return Hold("缺少执行结果");
                }
                if (!result.Succeeded)
                {
                    return Hold($"写副作用执行失败，副作用是否发生未知：{result.Error}");
                }
                return new ToolExecutionPolicy
                {
                    Disposition = ToolExecutionDisposition.Commit,
                    Reason = "写副作用执行成功，结果确定"
                };

            case ToolSideEffect.IdempotentWrite:
                // 幂等写：仅当稳定幂等键存在且执行成功时提交（外部系统可据此去重）；
                // 无幂等键或执行失败 → 对账（不自动提交）。
                if (result is null)
                {
                    return Hold("缺少执行结果");
                }
                if (!result.Succeeded)
                {
                    return Hold($"幂等写执行失败，副作用是否发生未知：{result.Error}");
                }
                if (string.IsNullOrWhiteSpace(result.IdempotencyKey))
                {
                    return Hold("幂等键缺失：无法安全提交（外部系统无法去重）");
                }
                return new ToolExecutionPolicy
                {
                    Disposition = ToolExecutionDisposition.Commit,
                    Reason = "幂等写：稳定幂等键存在且执行成功"
                };

            case ToolSideEffect.FencedWrite:
                // Fenced 写：仅当执行成功（Fence 有效期内完成，Fence 有效性由执行器
                // 在 Dispatch 前校验 ExpiresAt 并在提交前复核）时提交；失败 → 对账。
                if (result is null)
                {
                    return Hold("缺少执行结果");
                }
                if (!result.Succeeded)
                {
                    return Hold($"Fenced 写执行失败，Fence 未得到外部系统确认：{result.Error}");
                }
                return new ToolExecutionPolicy
                {
                    Disposition = ToolExecutionDisposition.Commit,
                    Reason = "Fenced 写：执行成功且 Fence 有效期内完成"
                };

            case ToolSideEffect.NonIdempotentWrite:
                // 非幂等写：外部副作用不可重放，即使执行成功也不自动提交——
                // 必须 Approval + 外部操作身份确认，经对账（Reconciliation）由裁决方提交。
                return new ToolExecutionPolicy
                {
                    Disposition = ToolExecutionDisposition.HoldForReconciliation,
                    Reason = "非幂等写：需 Approval + 外部操作身份确认，禁止自动提交",
                    RequiresReconciliationBeforeCommit = true
                };

            case ToolSideEffect.RequiresReconciliation:
                // 声明要求对账：必须经 Reconciliation Handler 确认外部副作用真相后提交。
                return new ToolExecutionPolicy
                {
                    Disposition = ToolExecutionDisposition.HoldForReconciliation,
                    Reason = "声明 RequiresReconciliation：需对账确认后提交",
                    RequiresReconciliationBeforeCommit = true
                };

            case ToolSideEffect.Unknown:
            default:
                // 副作用未知：不自动提交（保守策略，等待调用方裁决）。
                return Hold("副作用未知：不自动提交");
        }
    }

    /// <summary>
    /// 重试决策：Dispatch 失败且满足 <see cref="ToolRetrySafety"/> 安全契约、未达上限时 Retry（含退避延迟）；
    /// 否则 Abort（成功 / 未配置重试 / 达上限 / 重试不安全）。
    /// </summary>
    private static ToolRetryDecision DecideRetry(
        ToolDescriptor descriptor,
        ToolExecutionResult? result,
        int attempt)
    {
        // 仅在失败时考虑重试（成功 / 无结果 → 终止）。
        if (result is null || result.Succeeded)
        {
            return ToolRetryDecision.Abort("执行成功或缺少结果，无需重试");
        }

        // 未配置重试策略 → 不自动重试。
        if (descriptor.RetryBackoffPolicy == ToolRetryBackoffPolicy.None || descriptor.MaxRetries <= 0)
        {
            return ToolRetryDecision.Abort("未配置重试策略（RetryBackoffPolicy=None 或 MaxRetries=0）");
        }

        // 已达最大重试次数。
        if (attempt >= descriptor.MaxRetries)
        {
            return ToolRetryDecision.Abort($"已达最大重试次数（MaxRetries={descriptor.MaxRetries}）");
        }

        // 重试安全门：普通 Write 没有稳定 IdempotencyKey 或 Provider Fence 时，
        // 不能依据本地 retry 配置自动重试——外部写失败/超时 ≠ 副作用未发生。
        // 允许自动重试的唯一条件：None/ReadOnly；或 Descriptor 显式声明 ProviderIdempotent
        // （Provider 支持稳定幂等键）且本次携带稳定键；或 ProviderConfirmedNoEffect
        // 且 Provider 本次明确返回 NoEffectConfirmed=true。
        switch (descriptor.DeclaredSideEffect)
        {
            case ToolSideEffect.None:
            case ToolSideEffect.ReadOnly:
                // 只读/无副作用：无外部副作用面（BeforeDispatchOnly 语义），重试安全。
                break;

            case ToolSideEffect.IdempotentWrite:
                // 幂等写：仅当 Descriptor 显式声明 ProviderIdempotent（Provider 支持稳定幂等键）
                // 且本次携带稳定幂等键时才允许重试——声明副作用类型本身不代表 Provider 支持去重。
                if (descriptor.RetrySafety != ToolRetrySafety.ProviderIdempotent)
                {
                    return ToolRetryDecision.Abort(
                        $"幂等写未声明 RetrySafety=ProviderIdempotent（Provider 未确认支持稳定幂等键），禁止自动重试");
                }
                if (string.IsNullOrWhiteSpace(result.IdempotencyKey))
                {
                    return ToolRetryDecision.Abort("幂等写重试需要稳定幂等键（IdempotencyKey），缺失时不安全");
                }
                break;

            case ToolSideEffect.FencedWrite:
                // Fenced 写：仅当 Descriptor 声明 ProviderConfirmedNoEffect 且 Provider 本次
                // 明确返回 NoEffectConfirmed=true（外部 Fence 已确认阻止旧请求、无副作用）时允许。
                if (descriptor.RetrySafety != ToolRetrySafety.ProviderConfirmedNoEffect)
                {
                    return ToolRetryDecision.Abort(
                        "Fenced 写未声明 RetrySafety=ProviderConfirmedNoEffect（Fence 未确认阻止旧请求），禁止自动重试");
                }
                if (!result.NoEffectConfirmed)
                {
                    return ToolRetryDecision.Abort("Fenced 写未获得 Provider NoEffectConfirmed=true，禁止自动重试");
                }
                break;

            case ToolSideEffect.Write:
                // 普通写：默认 RetrySafety=Never → 即使 MaxRetries>0 也不自动重试。
                // 仅当 Descriptor 显式声明 ProviderIdempotent（+ 稳定幂等键）或
                // ProviderConfirmedNoEffect（+ 本次 NoEffectConfirmed=true）时允许。
                if (descriptor.RetrySafety == ToolRetrySafety.ProviderIdempotent)
                {
                    if (string.IsNullOrWhiteSpace(result.IdempotencyKey))
                    {
                        return ToolRetryDecision.Abort(
                            "普通写声明 RetrySafety=ProviderIdempotent 但缺少稳定幂等键，重试不安全");
                    }
                    break;
                }
                if (descriptor.RetrySafety == ToolRetrySafety.ProviderConfirmedNoEffect)
                {
                    if (!result.NoEffectConfirmed)
                    {
                        return ToolRetryDecision.Abort(
                            "普通写声明 RetrySafety=ProviderConfirmedNoEffect 但 Provider 未返回 NoEffectConfirmed=true，禁止自动重试");
                    }
                    break;
                }
                return ToolRetryDecision.Abort(
                    $"普通写 RetrySafety={descriptor.RetrySafety}：外部写失败/超时 ≠ 副作用未发生，禁止依据本地重试配置自动重试");

            default:
                // NonIdempotentWrite / RequiresReconciliation / Unknown：
                // 外部副作用不可重放或真相未知 → 永不自动重试。
                return ToolRetryDecision.Abort(
                    $"副作用 {descriptor.DeclaredSideEffect} 不允许自动重试（外部副作用不可重放）");
        }

        var delay = ComputeRetryDelay(descriptor, attempt);
        var attemptsRemaining = descriptor.MaxRetries - attempt - 1;
        return new ToolRetryDecision
        {
            ShouldRetry = true,
            Delay = delay,
            AttemptsRemaining = attemptsRemaining,
            Reason = $"第 {attempt + 1} 次失败，自动重试（延迟 {delay.TotalSeconds:F1}s，剩余 {attemptsRemaining} 次）"
        };
    }

    /// <summary>
    /// 计算重试退避延迟：Linear = 固定 RetryDelay；Exponential = RetryDelay * 2^(attempt-1)。
    /// </summary>
    private static TimeSpan ComputeRetryDelay(ToolDescriptor descriptor, int attempt)
        => descriptor.RetryBackoffPolicy == ToolRetryBackoffPolicy.Exponential
            ? TimeSpan.FromTicks(descriptor.RetryDelay.Ticks << Math.Min(attempt, 10))
            : descriptor.RetryDelay;

    /// <summary>写副作用判定：除 None/ReadOnly 外的副作用均涉及外部副作用，受审批门约束。</summary>
    private static bool IsExternalSideEffect(ToolSideEffect sideEffect)
        => sideEffect is not ToolSideEffect.None and not ToolSideEffect.ReadOnly;

    private static ToolExecutionPolicy Hold(string reason) => new()
    {
        Disposition = ToolExecutionDisposition.HoldForReconciliation,
        Reason = reason
    };
}
