using ContextCore.Abstractions;

namespace ContextCore.Core.Services.AgentRunRuntime;

// ===========================================================================
// DefaultToolEffectPolicy — Tool 执行策略默认实现
//
// 严格提交矩阵（Descriptor.DeclaredSideEffect → 执行后处置）：
//   None / ReadOnly        → 结果确定后 Commit（只读，重放安全）
//   Write                  → 执行成功时 Commit；失败时 Hold（副作用是否发生未知）
//   IdempotentWrite        → 稳定幂等键明确返回且执行成功时 Commit；否则 Hold
//   FencedWrite            → 有效 Fence 确认（执行成功且 Fence 窗口内）时 Commit；否则 Hold
//   NonIdempotentWrite     → 永不自动提交：Approval + 外部操作身份确认后经对账提交
//   RequiresReconciliation → 永不自动提交：必须经 Reconciliation Handler 确认后提交
//   Unknown                → 永不自动提交（保守策略）
//
// 该矩阵替代执行器旧的"effectiveSideEffect != Unknown → MarkCommittedWithResultAsync"
// 判定，杜绝以下危险状态被错误自动提交：
//   - NonIdempotentWrite（外部副作用不可重放，必须对账）
//   - RequiresReconciliation（声明要求对账，未经 Handler 确认不得提交）
//   - 外部调用失败但副作用是否发生未知（Succeeded=false）
//   - Handler 返回失败但 DeclaredSideEffect 为写（部分副作用可能已发生）
//   - Fence 未得到外部系统确认的写操作
// ===========================================================================

/// <summary>
/// Tool 执行策略默认实现：基于 <see cref="ToolDescriptor.DeclaredSideEffect"/> 的严格提交矩阵。
/// </summary>
public sealed class DefaultToolEffectPolicy : IToolEffectPolicy
{
    /// <inheritdoc />
    public ToolExecutionPolicy Resolve(
        ToolDescriptor descriptor,
        ToolDispatchPrepareResult? journal,
        ToolExecutionResult? result)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

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

    private static ToolExecutionPolicy Hold(string reason) => new()
    {
        Disposition = ToolExecutionDisposition.HoldForReconciliation,
        Reason = reason
    };
}
