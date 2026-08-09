using System.Reflection;
using ContextCore.Core;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Tests;

/// <summary>
/// OpenTelemetry 观测契约测试（WP-O）：
/// 核心遥测 Meter 名必须匹配 Service 的 AddMeter("ContextCore.*") 通配——
/// 新增组件若使用非 "ContextCore." 前缀的 Meter 名将不被 OTLP 导出捕获。
/// </summary>
[TestClass]
[TestCategory("Contract")]
public sealed class ObservabilityContractTests
{
    [TestMethod]
    public void CoreMeters_MatchOtlpWildcard()
    {
        var meterNames = EnumerateMeterNames(
            typeof(CoreMetrics).Assembly,
            typeof(PostgresMigrationMetrics).Assembly);

        Assert.IsTrue(meterNames.Count > 0, "应发现至少一个核心 Meter。");
        foreach (var name in meterNames)
        {
            Assert.IsTrue(name.StartsWith("ContextCore.", StringComparison.Ordinal),
                $"Meter 名 '{name}' 必须以 'ContextCore.' 前缀（否则 AddMeter(\"ContextCore.*\") OTLP 导出不捕获）。");
        }
    }

    [TestMethod]
    public void CoreMeterNames_IncludeCriticalComponents()
    {
        // 关键组件 Meter 必须存在（迁移 / 队列 / 核心遥测）——防止重构时丢失导出。
        var meterNames = EnumerateMeterNames(
            typeof(CoreMetrics).Assembly,
            typeof(PostgresMigrationMetrics).Assembly);

        CollectionAssert.Contains(meterNames.ToList(), "ContextCore.Storage.Postgres",
            "Postgres 迁移/队列 Meter 应存在。");
        CollectionAssert.Contains(meterNames.ToList(), "ContextCore.Core",
            "Core 遥测 Meter 应存在。");
    }

    [TestMethod]
    public void LearningPipelineMetrics_RecordsWithoutThrowing()
    {
        // WP-W：无 MeterListener 时记录为 no-op（不抛异常）；指标名符合 OTLP 前缀契约。
        ContextCore.Core.Services.MemoryEvolution.LearningPipelineMetrics.RecordExportDuration(12.5);
        ContextCore.Core.Services.MemoryEvolution.LearningPipelineMetrics.RecordQualityGateVerdict(
            ContextCore.Core.Services.MemoryEvolution.LearningDataQualityVerdict.Warning);
        ContextCore.Core.Services.MemoryEvolution.LearningPipelineMetrics.RecordArtifactRebuild(hit: true);
        ContextCore.Core.Services.MemoryEvolution.LearningPipelineMetrics.RecordArtifactRebuild(hit: false);
    }

    private static List<string> EnumerateMeterNames(params Assembly[] assemblies)
    {
        var names = new List<string>();
        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                foreach (var field in fields)
                {
                    if (field.FieldType.FullName == "System.Diagnostics.Metrics.Meter")
                    {
                        var value = field.GetValue(null) as System.Diagnostics.Metrics.Meter;
                        if (value is not null && !string.IsNullOrWhiteSpace(value.Name))
                        {
                            names.Add(value.Name);
                        }
                    }
                }
            }
        }
        return names.Distinct(StringComparer.Ordinal).ToList();
    }
}
