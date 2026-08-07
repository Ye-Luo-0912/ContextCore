namespace ContextCore.Abstractions;

/// <summary>
/// Agent Run 的租户复合身份键（工作区 + Run）。HA 层（租约 / 心跳 / 结算 / 恢复 /
/// 对账）表示 Run 身份一律使用本键，禁止用裸 runId——数据库已按
/// (workspace_id, run_id) 复合键建模，不同工作区可使用相同 RunId 而互不干扰。
/// </summary>
/// <param name="WorkspaceId">Run 所属工作区 ID。</param>
/// <param name="RunId">Agent Run ID（同一工作区内唯一）。</param>
public readonly record struct TenantRunKey(string WorkspaceId, string RunId)
{
    /// <summary>稳定的文本表示（用于日志与诊断，非存储键）。</summary>
    public override string ToString() => $"{WorkspaceId}/{RunId}";
}
