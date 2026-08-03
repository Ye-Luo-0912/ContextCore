using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Tests;

// ===========================================================================
// PostgreSQL 迁移 Runner 拆分生产验收测试
//
// 验证 Migration Runner 拆分设计：
// 1. MigrationStepRegistry_ContainsVersionOrderedSteps — 步骤注册表非空、
// 按版本升序排列，每个步骤声明 MigrationId / From / To / Stages。
// 2. ToolDispatchResultsResultKeyStep_DeclaresV48ToV49WithThreeStages —
// v48→v49 步骤的阶段顺序为 Online → Backfill → ConstraintValidate。
// 3. MigrationMetrics_RecordWithoutThrowing — 迁移指标（DDL / 锁等待 /
// 失败版本 / 已应用步骤）在无监听器时记录不抛异常。
//
// 说明：步骤的 PreCheck / ExecuteStage 需要真实 NpgsqlConnection（pg catalog
// 查询），不在无 Postgres 的单元测试中执行；此处验证注册表接线与指标基础设施。
// ===========================================================================

[TestClass]
[TestCategory("Storage")]
[TestCategory("Postgres")]
[TestCategory("Migration")]
public sealed class R29N_PostgresMigrationStepTests
{
    [TestMethod]
    public void MigrationStepRegistry_ContainsVersionOrderedSteps()
    {
        var steps = PostgresMigrationStepRegistry.Steps;

        Assert.IsTrue(steps.Count > 0, "版本化迁移步骤注册表不应为空。");
        for (var i = 1; i < steps.Count; i++)
        {
            var previous = steps[i - 1];
            var current = steps[i];
            Assert.IsTrue(
                string.CompareOrdinal(previous.FromSchemaVersion, current.FromSchemaVersion) < 0,
                "步骤应按 FromSchemaVersion 升序排列。");
        }

        foreach (var step in steps)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(step.MigrationId), "MigrationId 不应为空。");
            Assert.IsFalse(string.IsNullOrWhiteSpace(step.FromSchemaVersion), "FromSchemaVersion 不应为空。");
            Assert.IsFalse(string.IsNullOrWhiteSpace(step.ToSchemaVersion), "ToSchemaVersion 不应为空。");
            Assert.IsFalse(string.IsNullOrWhiteSpace(step.Description), "Description 不应为空。");
            Assert.IsTrue(step.Stages.Count > 0, "Stages 不应为空。");
        }
    }

    [TestMethod]
    public void ToolDispatchResultsResultKeyStep_DeclaresV48ToV49WithThreeStages()
    {
        var step = PostgresMigrationStepRegistry.Steps
            .OfType<PostgresMigrationToolDispatchResultsResultKey>()
            .Single();

        Assert.AreEqual("0002_tool_dispatch_results_result_key", step.MigrationId);
        Assert.AreEqual("cc-schema-v48", step.FromSchemaVersion);
        Assert.AreEqual("cc-schema-v49", step.ToSchemaVersion);
        CollectionAssert.AreEqual(
            new[]
            {
                PostgresMigrationStage.Online,
                PostgresMigrationStage.Backfill,
                PostgresMigrationStage.ConstraintValidate
            },
            step.Stages.ToArray());
    }

    [TestMethod]
    public void MigrationMetrics_RecordWithoutThrowing()
    {
        // 无 MeterListener 时记录为 no-op；验证仪表盘静态初始化与 Record/Add 不抛异常。
        PostgresMigrationMetrics.DdlDuration.Record(1.5);
        PostgresMigrationMetrics.LockWaitDuration.Record(0.25);
        PostgresMigrationMetrics.FailedVersions.Add(
            1,
            new KeyValuePair<string, object?>("version", "cc-schema-v51"));
        PostgresMigrationMetrics.StepsApplied.Add(
            1,
            new KeyValuePair<string, object?>("step", "0002_tool_dispatch_results_result_key"));
    }
}
