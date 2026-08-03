using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentRunRuntime;

namespace ContextCore.Tests;

// ===========================================================================
// WP-B Approval 阈值强制验收测试
//
// 目标：DefaultAgentApprovalGate 消费 ApprovalPolicyOptions.CostThresholdUsd /
// TokenThreshold / WorkspaceOverrides（此前仅消费 ApprovalRequiredTools）；
// DefaultAgentToolCallValidator 在全局阈值超限时标记 RequiresApproval。
//
// 覆盖：
//   1. Gate：预估费用超过 CostThresholdUsd → 需人工审批（PendingApproval）；
//   2. Gate：低于阈值 → 自动批准；
//   3. Gate：预估 token 超过 TokenThreshold → 需人工审批；
//   4. Gate：WorkspaceOverride 提高费用阈值 → 同一调用在该 workspace 自动批准；
//   5. Gate：WorkspaceOverride 替换需审批 Tool 列表；
//   6. Validator：全局费用阈值超限 → RequiresApproval=true；低于阈值 → false。
// ===========================================================================

[TestClass]
[TestCategory("R30")]
public sealed class R30B_ApprovalThresholdTests
{
    private const string Ws = "ws-approval";

    private static AgentToolCallRequest ToolCall(double? costUsd = null, long? tokens = null, string tool = "web_search")
        => new()
        {
            ToolName = tool,
            Arguments = "{}",
            EstimatedCostUsd = costUsd,
            EstimatedTokens = tokens
        };

    private static DefaultAgentApprovalGate BuildGate(ApprovalPolicyOptions policy)
        => new(
            approvalRequiredTools: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            autoApproveAll: false,
            approvalStore: new InMemoryAgentApprovalStore(),
            logger: null,
            approvalPolicy: policy);

    // ── 1. 费用阈值 → 需人工审批 ────────────────────────────────────────

    [TestMethod]
    public async Task Gate_CostAboveThreshold_RequiresHumanApproval()
    {
        var gate = BuildGate(new ApprovalPolicyOptions
        {
            Enabled = true,
            CostThresholdUsd = 1.0,
            TokenThreshold = 0 // 禁用 token 触发
        });

        var result = await gate.RequestApprovalAsync(Ws, "run-1", ToolCall(costUsd: 5.0), CancellationToken.None);

        Assert.IsFalse(result.Approved, "超过费用阈值不应自动批准。");
        Assert.IsTrue(result.PendingApproval, "超过费用阈值应挂起等待人工审批。");
        Assert.IsNotNull(result.ApprovalId);
    }

    // ── 2. 低于阈值 → 自动批准 ──────────────────────────────────────────

    [TestMethod]
    public async Task Gate_CostBelowThreshold_AutoApproved()
    {
        var gate = BuildGate(new ApprovalPolicyOptions
        {
            Enabled = true,
            CostThresholdUsd = 1.0,
            TokenThreshold = 0
        });

        var result = await gate.RequestApprovalAsync(Ws, "run-1", ToolCall(costUsd: 0.5), CancellationToken.None);

        Assert.IsTrue(result.Approved, "低于费用阈值应自动批准。");
        Assert.IsFalse(result.PendingApproval);
    }

    // ── 3. token 阈值 → 需人工审批 ──────────────────────────────────────

    [TestMethod]
    public async Task Gate_TokenAboveThreshold_RequiresHumanApproval()
    {
        var gate = BuildGate(new ApprovalPolicyOptions
        {
            Enabled = true,
            CostThresholdUsd = 0, // 禁用费用触发
            TokenThreshold = 100_000
        });

        var result = await gate.RequestApprovalAsync(Ws, "run-1", ToolCall(tokens: 150_000), CancellationToken.None);

        Assert.IsFalse(result.Approved, "超过 token 阈值不应自动批准。");
        Assert.IsTrue(result.PendingApproval, "超过 token 阈值应挂起等待人工审批。");
    }

    // ── 4. WorkspaceOverride 提高阈值 → 该 workspace 自动批准 ──────────

    [TestMethod]
    public async Task Gate_WorkspaceOverrideRaisesCostThreshold_AutoApprovedInThatWorkspace()
    {
        var gate = BuildGate(new ApprovalPolicyOptions
        {
            Enabled = true,
            CostThresholdUsd = 1.0,
            TokenThreshold = 0,
            WorkspaceOverrides = new Dictionary<string, WorkspaceApprovalOverride>(StringComparer.OrdinalIgnoreCase)
            {
                [Ws] = new() { CostThresholdUsd = 10.0 }
            }
        });

        // 同一调用（5.0 USD）：全局阈值 1.0 会触发审批；workspace 覆盖提高到 10.0 → 自动批准。
        var result = await gate.RequestApprovalAsync(Ws, "run-1", ToolCall(costUsd: 5.0), CancellationToken.None);

        Assert.IsTrue(result.Approved, "WorkspaceOverride 提高阈值后，该 workspace 的调用应自动批准。");
        Assert.IsFalse(result.PendingApproval);
    }

    // ── 5. WorkspaceOverride 替换需审批 Tool 列表 ───────────────────────

    [TestMethod]
    public async Task Gate_WorkspaceOverrideReplacesToolList_OverrideListWins()
    {
        var gate = BuildGate(new ApprovalPolicyOptions
        {
            Enabled = true,
            CostThresholdUsd = 0,
            TokenThreshold = 0,
            ApprovalRequiredTools = new[] { "file_delete" },
            WorkspaceOverrides = new Dictionary<string, WorkspaceApprovalOverride>(StringComparer.OrdinalIgnoreCase)
            {
                [Ws] = new() { ApprovalRequiredTools = new[] { "db_drop" } }
            }
        });

        // 全局列表含 file_delete、覆盖列表含 db_drop：file_delete 在覆盖 workspace 不再需要审批。
        var notApproved = await gate.RequestApprovalAsync(Ws, "run-1", ToolCall(tool: "db_drop"), CancellationToken.None);
        var approved = await gate.RequestApprovalAsync(Ws, "run-2", ToolCall(tool: "file_delete"), CancellationToken.None);

        Assert.IsTrue(notApproved.PendingApproval, "覆盖列表中的 Tool 需人工审批。");
        Assert.IsTrue(approved.Approved, "覆盖列表替换全局列表后，全局列表 Tool 在该 workspace 自动批准。");
    }

    // ── 6. Validator：全局阈值触发 RequiresApproval ─────────────────────

    [TestMethod]
    public async Task Validator_CostAboveThreshold_RequiresApproval()
    {
        var validator = new DefaultAgentToolCallValidator(
            dispatcher: null,
            catalog: null,
            dangerousTools: null,
            approvalPolicy: new ApprovalPolicyOptions
            {
                Enabled = true,
                CostThresholdUsd = 1.0,
                TokenThreshold = 0
            });

        var result = await validator.ValidateAsync("run-1", ToolCall(costUsd: 5.0), CancellationToken.None);

        Assert.IsTrue(result.IsValid, "阈值触发不影响合法性。");
        Assert.IsTrue(result.RequiresApproval, "超过费用阈值应标记 RequiresApproval。");
        StringAssert.Contains(result.ApprovalReason, "费用");
    }

    [TestMethod]
    public async Task Validator_CostBelowThreshold_NoApproval()
    {
        var validator = new DefaultAgentToolCallValidator(
            dispatcher: null,
            catalog: null,
            dangerousTools: null,
            approvalPolicy: new ApprovalPolicyOptions
            {
                Enabled = true,
                CostThresholdUsd = 1.0,
                TokenThreshold = 0
            });

        var result = await validator.ValidateAsync("run-1", ToolCall(costUsd: 0.2), CancellationToken.None);

        Assert.IsTrue(result.IsValid);
        Assert.IsFalse(result.RequiresApproval, "低于阈值不应要求审批。");
    }

    [TestMethod]
    public async Task Validator_PolicyNull_ThresholdIgnored()
    {
        // 向后兼容：未注入 ApprovalPolicyOptions 时阈值不生效（即使调用携带预估费用）。
        var validator = new DefaultAgentToolCallValidator();

        var result = await validator.ValidateAsync("run-1", ToolCall(costUsd: 999.0), CancellationToken.None);

        Assert.IsTrue(result.IsValid);
        Assert.IsFalse(result.RequiresApproval, "无策略配置时不应因阈值触发审批。");
    }
}
