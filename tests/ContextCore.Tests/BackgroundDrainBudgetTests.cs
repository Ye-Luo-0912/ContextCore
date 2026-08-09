using ContextCore.Core.Services;

namespace ContextCore.Tests;

/// <summary>
/// BackgroundDrainBudget 动态降速测试（WP-D）：
/// 1. 负载缩放因子：DB 池利用率高 → 因子收紧（0% → 1.0；80% → 0.36；100% → 0.2 保底）；
/// 2. ShouldContinueBurst 按因子缩放有效批次数上限（高负载下更早让出）；
/// 3. 无信号（null/越界因子）→ 回退静态预算（行为不变）。
/// </summary>
[TestClass]
[TestCategory("Agent-Run-Full-Loop")]
public sealed class BackgroundDrainBudgetTests
{
    [TestMethod]
    public void ComputeScaleFactor_MapsUtilizationToFactor()
    {
        Assert.AreEqual(1.0, BackgroundDrainBudget.ComputeScaleFactor(0.0), 0.0001, "池空闲 → 因子 1.0（静态预算）。");
        Assert.AreEqual(0.6, BackgroundDrainBudget.ComputeScaleFactor(0.5), 0.0001, "池 50% → 因子 0.6。");
        Assert.AreEqual(0.36, BackgroundDrainBudget.ComputeScaleFactor(0.8), 0.0001, "池 80% → 因子 0.36。");
        Assert.AreEqual(0.2, BackgroundDrainBudget.ComputeScaleFactor(1.0), 0.0001, "池 100% → 因子 0.2 保底。");
        Assert.AreEqual(1.0, BackgroundDrainBudget.ComputeScaleFactor(-0.5), 0.0001, "负值钳制为 0 → 1.0。");
        Assert.AreEqual(0.2, BackgroundDrainBudget.ComputeScaleFactor(5.0), 0.0001, "越界钳制为 1 → 0.2 保底。");
    }

    [TestMethod]
    public void ShouldContinueBurst_StaticBudget_WithoutLoadSignal()
    {
        // 无负载信号（null）→ 静态预算：MaxBatchesPerBurst=8。
        var budget = BackgroundDrainBudget.Compaction;
        var elapsed = TimeSpan.Zero;
        Assert.IsTrue(budget.ShouldContinueBurst(0, elapsed), "burst 起始应允许。");
        Assert.IsTrue(budget.ShouldContinueBurst(7, elapsed), "第 8 批前应允许。");
        Assert.IsFalse(budget.ShouldContinueBurst(8, elapsed), "达到 MaxBatchesPerBurst 应让出。");
        Assert.IsFalse(budget.ShouldContinueBurst(3, budget.MaxBurstDuration), "时长超限应让出。");
    }

    [TestMethod]
    public void ShouldContinueBurst_HighLoad_TightensBatchLimit()
    {
        // 高负载（因子 0.36）→ 有效批次数 = ceil(8 × 0.36) = 3：第 3 批后即让出。
        var budget = BackgroundDrainBudget.Compaction;
        var elapsed = TimeSpan.Zero;
        const double highLoadFactor = 0.36;

        Assert.IsTrue(budget.ShouldContinueBurst(0, elapsed, highLoadFactor), "低批次数应允许。");
        Assert.IsTrue(budget.ShouldContinueBurst(2, elapsed, highLoadFactor), "有效上限前应允许。");
        Assert.IsFalse(budget.ShouldContinueBurst(3, elapsed, highLoadFactor),
            "高负载下应更早让出（批次数按因子收紧）。");
    }

    [TestMethod]
    public void ShouldContinueBurst_InvalidFactor_FallsBackToStatic()
    {
        var budget = BackgroundDrainBudget.QuotaSettlement;
        Assert.IsTrue(budget.ShouldContinueBurst(0, TimeSpan.Zero, loadFactor: 0.0), "0 视为无信号 → 静态。");
        Assert.IsTrue(budget.ShouldContinueBurst(0, TimeSpan.Zero, loadFactor: 1.5), ">1 视为无信号 → 静态。");
        Assert.IsTrue(budget.ShouldContinueBurst(0, TimeSpan.Zero, loadFactor: -0.2), "负值视为无信号 → 静态。");
        Assert.IsFalse(budget.ShouldContinueBurst(budget.MaxBatchesPerBurst, TimeSpan.Zero, loadFactor: 1.0),
            "因子 1.0 = 静态上限。");
    }
}
