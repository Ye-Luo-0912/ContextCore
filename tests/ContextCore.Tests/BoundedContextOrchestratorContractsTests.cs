using System.Reflection;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Tests;

/// <summary>
/// Bounded Context Orchestrator 契约测试。
///
/// 验证目标：
/// 1. ContextRepairReason 枚举 8 值（byte 底层 + Unknown=0 + 7 类确定性异常）
/// 2. ContextRepairBudget 4 字段 + 默认值 + with 表达式
/// 3. ContextRepairDiagnosis 必填字段 + 默认值 + with 表达式
/// 4. ContextRepairRequest 必填字段 + 默认值
/// 5. ContextRepairResponse 必填字段 + 默认值 + 计算属性
/// 6. IContextRepairDetector 接口最小化（1 方法 + 返回 Task）
/// 7. IContextRepairExecutor 接口最小化（1 方法 + 返回 Task）
/// 8. IBoundedContextOrchestrator 接口最小化（1 方法 + 返回 Task）
/// 9. BoundedContextOrchestrationResult 必填字段 + WasRepaired / IsSuccess / Duration 计算属性
/// 10. sealed record / interface / no async void 反射验证
/// 11. 7 类确定性异常与 PackageQualityReport 指标的语义映射文档化
/// </summary>
[TestClass]
[TestCategory("R22")]
public sealed class BoundedContextOrchestratorContractsTests
{
    // =========================================================================
    // 1. ContextRepairReason 枚举 8 值
    // =========================================================================

    [TestMethod]
    public void ContextRepairReason_Has8Values()
    {
        var values = Enum.GetValues<ContextRepairReason>();
        Assert.AreEqual(8, values.Length);
        Assert.IsTrue(values.Contains(ContextRepairReason.Unknown));
        Assert.IsTrue(values.Contains(ContextRepairReason.PrimaryAnchorUncovered));
        Assert.IsTrue(values.Contains(ContextRepairReason.HardConstraintMissing));
        Assert.IsTrue(values.Contains(ContextRepairReason.MustHitMissing));
        Assert.IsTrue(values.Contains(ContextRepairReason.SevereRedundancy));
        Assert.IsTrue(values.Contains(ContextRepairReason.SectionSqueezeAnomaly));
        Assert.IsTrue(values.Contains(ContextRepairReason.TokenUtilizationTooLow));
        Assert.IsTrue(values.Contains(ContextRepairReason.LifecycleConflictUnresolved));
    }

    [TestMethod]
    public void ContextRepairReason_ValuesAreUnique()
    {
        var values = Enum.GetValues<ContextRepairReason>().Select(v => (byte)v).ToList();
        Assert.AreEqual(values.Count, values.Distinct().Count());
    }

    [TestMethod]
    public void ContextRepairReason_BackedByByte()
    {
        var underlyingType = Enum.GetUnderlyingType(typeof(ContextRepairReason));
        Assert.AreEqual(typeof(byte), underlyingType);
    }

    [TestMethod]
    public void ContextRepairReason_UnknownIsZero()
    {
        Assert.AreEqual((byte)0, (byte)ContextRepairReason.Unknown);
    }

    [TestMethod]
    public void ContextRepairReason_SevenAnomalyValues_NonZeroAndSequential()
    {
        // 7 类确定性异常值 = 1..7（Unknown=0 不算）
        Assert.AreEqual(1, (byte)ContextRepairReason.PrimaryAnchorUncovered);
        Assert.AreEqual(2, (byte)ContextRepairReason.HardConstraintMissing);
        Assert.AreEqual(3, (byte)ContextRepairReason.MustHitMissing);
        Assert.AreEqual(4, (byte)ContextRepairReason.SevereRedundancy);
        Assert.AreEqual(5, (byte)ContextRepairReason.SectionSqueezeAnomaly);
        Assert.AreEqual(6, (byte)ContextRepairReason.TokenUtilizationTooLow);
        Assert.AreEqual(7, (byte)ContextRepairReason.LifecycleConflictUnresolved);
    }

    // =========================================================================
    // 2. ContextRepairBudget 4 字段 + 默认值 + with 表达式
    // =========================================================================

    [TestMethod]
    public void ContextRepairBudget_DefaultValues()
    {
        var budget = new ContextRepairBudget();

        Assert.AreEqual(0, budget.MaxAdditionalStoreCalls);
        Assert.AreEqual(0, budget.MaxAdditionalCandidates);
        Assert.AreEqual(0, budget.MaxAdditionalTokens);
        Assert.AreEqual(TimeSpan.Zero, budget.MaxAdditionalLatency);
    }

    [TestMethod]
    public void ContextRepairBudget_AllFieldsCanBeSet()
    {
        var budget = new ContextRepairBudget
        {
            MaxAdditionalStoreCalls = 3,
            MaxAdditionalCandidates = 10,
            MaxAdditionalTokens = 2000,
            MaxAdditionalLatency = TimeSpan.FromMilliseconds(500)
        };

        Assert.AreEqual(3, budget.MaxAdditionalStoreCalls);
        Assert.AreEqual(10, budget.MaxAdditionalCandidates);
        Assert.AreEqual(2000, budget.MaxAdditionalTokens);
        Assert.AreEqual(TimeSpan.FromMilliseconds(500), budget.MaxAdditionalLatency);
    }

    [TestMethod]
    public void ContextRepairBudget_WithExpression_ProducesNewInstance()
    {
        var original = new ContextRepairBudget
        {
            MaxAdditionalStoreCalls = 1,
            MaxAdditionalCandidates = 5
        };
        var updated = original with { MaxAdditionalTokens = 1000 };

        Assert.AreEqual(1, original.MaxAdditionalStoreCalls);
        Assert.AreEqual(5, original.MaxAdditionalCandidates);
        Assert.AreEqual(0, original.MaxAdditionalTokens);
        Assert.AreEqual(1, updated.MaxAdditionalStoreCalls);
        Assert.AreEqual(5, updated.MaxAdditionalCandidates);
        Assert.AreEqual(1000, updated.MaxAdditionalTokens);
        Assert.AreNotSame(original, updated);
    }

    [TestMethod]
    public void ContextRepairBudget_IsSealedRecord()
    {
        var type = typeof(ContextRepairBudget);
        Assert.IsTrue(type.IsSealed);
        Assert.IsTrue(type.IsClass);
        Assert.IsFalse(type.IsValueType);
    }

    // =========================================================================
    // 3. ContextRepairDiagnosis 必填字段 + 默认值 + with 表达式
    // =========================================================================

    [TestMethod]
    public void ContextRepairDiagnosis_RequiredFields_AreEnforced()
    {
        var diagnosis = MakeDiagnosis();

        Assert.AreEqual("diag-1", diagnosis.DiagnosisId);
        Assert.AreEqual("req-1", diagnosis.DecisionRequestId);
        Assert.AreEqual("ws-test", diagnosis.WorkspaceId);
        Assert.AreEqual("col-test", diagnosis.CollectionId);
        Assert.AreEqual(ContextRepairReason.PrimaryAnchorUncovered, diagnosis.Reason);
        Assert.AreEqual("AnchorCoverage=0.4 < 0.8", diagnosis.ReasonDetail);
        Assert.IsTrue(diagnosis.DiagnosedAt > DateTimeOffset.MinValue);
    }

    [TestMethod]
    public void ContextRepairDiagnosis_TriggerMetricFields_DefaultZero()
    {
        var diagnosis = MakeDiagnosis();

        Assert.AreEqual(0.0, diagnosis.TriggerMetricValue);
        Assert.AreEqual(0.0, diagnosis.TriggerMetricThreshold);
    }

    [TestMethod]
    public void ContextRepairDiagnosis_TriggerMetricFields_CanBeSet()
    {
        var diagnosis = MakeDiagnosis() with
        {
            TriggerMetricValue = 0.4,
            TriggerMetricThreshold = 0.8
        };

        Assert.AreEqual(0.4, diagnosis.TriggerMetricValue);
        Assert.AreEqual(0.8, diagnosis.TriggerMetricThreshold);
    }

    [TestMethod]
    public void ContextRepairDiagnosis_OptionalFields_DefaultNullOrEmpty()
    {
        var diagnosis = MakeDiagnosis();

        Assert.IsNull(diagnosis.QualityReport);
        Assert.IsNull(diagnosis.SuggestedRepairStrategy);
        Assert.AreEqual(0, diagnosis.Metadata.Count);
    }

    [TestMethod]
    public void ContextRepairDiagnosis_OptionalFields_CanBeSet()
    {
        var report = new PackageQualityReport();
        var diagnosis = MakeDiagnosis() with
        {
            QualityReport = report,
            SuggestedRepairStrategy = "re-retrieve-must-hit",
            Metadata = new Dictionary<string, string> { ["anchor"] = "primary" }
        };

        Assert.IsNotNull(diagnosis.QualityReport);
        Assert.AreEqual("re-retrieve-must-hit", diagnosis.SuggestedRepairStrategy);
        Assert.AreEqual(1, diagnosis.Metadata.Count);
        Assert.AreEqual("primary", diagnosis.Metadata["anchor"]);
    }

    [TestMethod]
    public void ContextRepairDiagnosis_WithExpression_ProducesNewInstance()
    {
        var original = MakeDiagnosis();
        var updated = original with { Reason = ContextRepairReason.HardConstraintMissing };

        Assert.AreEqual(ContextRepairReason.PrimaryAnchorUncovered, original.Reason);
        Assert.AreEqual(ContextRepairReason.HardConstraintMissing, updated.Reason);
        Assert.AreNotSame(original, updated);
    }

    [TestMethod]
    public void ContextRepairDiagnosis_IsSealedRecord()
    {
        var type = typeof(ContextRepairDiagnosis);
        Assert.IsTrue(type.IsSealed);
        Assert.IsTrue(type.IsClass);
        Assert.IsFalse(type.IsValueType);
    }

    // =========================================================================
    // 4. ContextRepairRequest 必填字段 + 默认值
    // =========================================================================

    [TestMethod]
    public void ContextRepairRequest_RequiredFields_AreEnforced()
    {
        var request = MakeRepairRequest();

        Assert.AreEqual("repair-1", request.RepairRequestId);
        Assert.IsNotNull(request.Diagnosis);
        Assert.IsNotNull(request.Budget);
        Assert.IsNotNull(request.OriginalDecision);
        Assert.IsTrue(request.RequestedAt > DateTimeOffset.MinValue);
    }

    [TestMethod]
    public void ContextRepairRequest_OptionalFields_DefaultNull()
    {
        var request = MakeRepairRequest();

        Assert.IsNull(request.OriginalQualityReport);
        Assert.IsNull(request.TriggeredBy);
    }

    [TestMethod]
    public void ContextRepairRequest_OptionalFields_CanBeSet()
    {
        var report = new PackageQualityReport();
        var request = MakeRepairRequest() with
        {
            OriginalQualityReport = report,
            TriggeredBy = "user-1"
        };

        Assert.IsNotNull(request.OriginalQualityReport);
        Assert.AreEqual("user-1", request.TriggeredBy);
    }

    [TestMethod]
    public void ContextRepairRequest_BudgetExplicitlySpecified()
    {
        // 设计原则 ：预算必须显式（不提供默认值 → 调用方必须传入）
        var request = MakeRepairRequest(budget: new ContextRepairBudget
        {
            MaxAdditionalStoreCalls = 2,
            MaxAdditionalCandidates = 5,
            MaxAdditionalTokens = 500,
            MaxAdditionalLatency = TimeSpan.FromSeconds(1)
        });

        Assert.AreEqual(2, request.Budget.MaxAdditionalStoreCalls);
        Assert.AreEqual(5, request.Budget.MaxAdditionalCandidates);
        Assert.AreEqual(500, request.Budget.MaxAdditionalTokens);
        Assert.AreEqual(TimeSpan.FromSeconds(1), request.Budget.MaxAdditionalLatency);
    }

    [TestMethod]
    public void ContextRepairRequest_IsSealedRecord()
    {
        var type = typeof(ContextRepairRequest);
        Assert.IsTrue(type.IsSealed);
        Assert.IsTrue(type.IsClass);
        Assert.IsFalse(type.IsValueType);
    }

    // =========================================================================
    // 5. ContextRepairResponse 必填字段 + 默认值 + 计算属性
    // =========================================================================

    [TestMethod]
    public void ContextRepairResponse_RequiredFields_AreEnforced()
    {
        var response = MakeRepairResponse();

        Assert.AreEqual("repair-1", response.RepairRequestId);
        Assert.IsFalse(response.IsSuccess);
        Assert.IsFalse(response.WasRepaired);
        Assert.IsNotNull(response.RepairedDecision);
        Assert.IsNotNull(response.ConsumedBudget);
        Assert.IsTrue(response.CompletedAt > DateTimeOffset.MinValue);
    }

    [TestMethod]
    public void ContextRepairResponse_DefaultErrors_Empty()
    {
        var response = MakeRepairResponse();

        Assert.AreEqual(0, response.Errors.Count);
        Assert.AreEqual(string.Empty, response.RepairSummary);
        Assert.IsNull(response.RepairedQualityReport);
    }

    [TestMethod]
    public void ContextRepairResponse_WithExpression_ProducesNewInstance()
    {
        var original = MakeRepairResponse();
        var updated = original with { IsSuccess = true, WasRepaired = true };

        Assert.IsFalse(original.IsSuccess);
        Assert.IsTrue(updated.IsSuccess);
        Assert.AreNotSame(original, updated);
    }

    [TestMethod]
    public void ContextRepairResponse_IsSealedRecord()
    {
        var type = typeof(ContextRepairResponse);
        Assert.IsTrue(type.IsSealed);
        Assert.IsTrue(type.IsClass);
        Assert.IsFalse(type.IsValueType);
    }

    // =========================================================================
    // 6. IContextRepairDetector 接口最小化
    // =========================================================================

    [TestMethod]
    public void IContextRepairDetector_IsInterface()
    {
        Assert.IsTrue(typeof(IContextRepairDetector).IsInterface);
    }

    [TestMethod]
    public void IContextRepairDetector_HasSingleMethod_DetectAsync()
    {
        var type = typeof(IContextRepairDetector);
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance);

        Assert.AreEqual(1, methods.Length);
        Assert.AreEqual("DetectAsync", methods[0].Name);
    }

    [TestMethod]
    public void IContextRepairDetector_DetectAsync_ReturnsTaskOfReadOnlyList()
    {
        var method = typeof(IContextRepairDetector).GetMethod("DetectAsync");
        Assert.IsNotNull(method);
        Assert.IsTrue(method!.ReturnType.IsGenericType);
        Assert.AreEqual(typeof(Task<>), method.ReturnType.GetGenericTypeDefinition());
        Assert.AreEqual(typeof(IReadOnlyList<ContextRepairDiagnosis>), method.ReturnType.GetGenericArguments()[0]);
    }

    [TestMethod]
    public void IContextRepairDetector_DetectAsync_HasStoreOperationReadAttribute()
    {
        var method = typeof(IContextRepairDetector).GetMethod("DetectAsync");
        Assert.IsNotNull(method);
        var attr = method!.GetCustomAttribute<StoreOperationAttribute>();
        Assert.IsNotNull(attr);
        Assert.AreEqual(StoreOperationKind.Read, attr!.Kind);
    }

    // =========================================================================
    // 7. IContextRepairExecutor 接口最小化
    // =========================================================================

    [TestMethod]
    public void IContextRepairExecutor_IsInterface()
    {
        Assert.IsTrue(typeof(IContextRepairExecutor).IsInterface);
    }

    [TestMethod]
    public void IContextRepairExecutor_HasSingleMethod_ExecuteAsync()
    {
        var type = typeof(IContextRepairExecutor);
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance);

        Assert.AreEqual(1, methods.Length);
        Assert.AreEqual("ExecuteAsync", methods[0].Name);
    }

    [TestMethod]
    public void IContextRepairExecutor_ExecuteAsync_ReturnsTaskOfResponse()
    {
        var method = typeof(IContextRepairExecutor).GetMethod("ExecuteAsync");
        Assert.IsNotNull(method);
        Assert.IsTrue(method!.ReturnType.IsGenericType);
        Assert.AreEqual(typeof(Task<>), method.ReturnType.GetGenericTypeDefinition());
        Assert.AreEqual(typeof(ContextRepairResponse), method.ReturnType.GetGenericArguments()[0]);
    }

    [TestMethod]
    public void IContextRepairExecutor_ExecuteAsync_HasStoreOperationWriteAttribute()
    {
        var method = typeof(IContextRepairExecutor).GetMethod("ExecuteAsync");
        Assert.IsNotNull(method);
        var attr = method!.GetCustomAttribute<StoreOperationAttribute>();
        Assert.IsNotNull(attr);
        Assert.AreEqual(StoreOperationKind.Write, attr!.Kind);
    }

    // =========================================================================
    // 8. IBoundedContextOrchestrator 接口最小化
    // =========================================================================

    [TestMethod]
    public void IBoundedContextOrchestrator_IsInterface()
    {
        Assert.IsTrue(typeof(IBoundedContextOrchestrator).IsInterface);
    }

    [TestMethod]
    public void IBoundedContextOrchestrator_HasSingleMethod_OrchestrateAsync()
    {
        var type = typeof(IBoundedContextOrchestrator);
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance);

        Assert.AreEqual(1, methods.Length);
        Assert.AreEqual("OrchestrateAsync", methods[0].Name);
    }

    [TestMethod]
    public void IBoundedContextOrchestrator_OrchestrateAsync_ReturnsTaskOfResult()
    {
        var method = typeof(IBoundedContextOrchestrator).GetMethod("OrchestrateAsync");
        Assert.IsNotNull(method);
        Assert.IsTrue(method!.ReturnType.IsGenericType);
        Assert.AreEqual(typeof(Task<>), method.ReturnType.GetGenericTypeDefinition());
        Assert.AreEqual(typeof(BoundedContextOrchestrationResult), method.ReturnType.GetGenericArguments()[0]);
    }

    [TestMethod]
    public void IBoundedContextOrchestrator_OrchestrateAsync_HasStoreOperationWriteAttribute()
    {
        var method = typeof(IBoundedContextOrchestrator).GetMethod("OrchestrateAsync");
        Assert.IsNotNull(method);
        var attr = method!.GetCustomAttribute<StoreOperationAttribute>();
        Assert.IsNotNull(attr);
        Assert.AreEqual(StoreOperationKind.Write, attr!.Kind);
    }

    [TestMethod]
    public void IBoundedContextOrchestrator_OrchestrateAsync_Has4Parameters()
    {
        var method = typeof(IBoundedContextOrchestrator).GetMethod("OrchestrateAsync");
        Assert.IsNotNull(method);
        var parameters = method!.GetParameters();
        Assert.AreEqual(4, parameters.Length);
        Assert.AreEqual(typeof(ContextDecisionResult), parameters[0].ParameterType);
        Assert.AreEqual(typeof(PackageQualityReport), parameters[1].ParameterType);
        Assert.AreEqual(typeof(ContextRepairBudget), parameters[2].ParameterType);
        Assert.AreEqual(typeof(CancellationToken), parameters[3].ParameterType);
    }

    // =========================================================================
    // 9. BoundedContextOrchestrationResult 必填字段 + 计算属性
    // =========================================================================

    [TestMethod]
    public void BoundedContextOrchestrationResult_RequiredFields_AreEnforced()
    {
        var result = MakeOrchestrationResult();

        Assert.AreEqual("orch-1", result.OrchestrationId);
        Assert.IsNotNull(result.FinalDecision);
        Assert.IsNotNull(result.FinalQualityReport);
        Assert.AreEqual(0, result.Diagnoses.Count);
        Assert.IsNull(result.RepairResponse);
        Assert.IsTrue(result.StartedAt > DateTimeOffset.MinValue);
        Assert.IsTrue(result.CompletedAt > DateTimeOffset.MinValue);
    }

    [TestMethod]
    public void BoundedContextOrchestrationResult_WasRepaired_FalseWhenResponseNull()
    {
        var result = MakeOrchestrationResult();

        Assert.IsFalse(result.WasRepaired);
    }

    [TestMethod]
    public void BoundedContextOrchestrationResult_IsSuccess_TrueWhenResponseNull()
    {
        var result = MakeOrchestrationResult();

        Assert.IsTrue(result.IsSuccess);
    }

    [TestMethod]
    public void BoundedContextOrchestrationResult_WasRepaired_TrueWhenResponseWasRepaired()
    {
        var response = MakeRepairResponse() with { WasRepaired = true, IsSuccess = true };
        var result = MakeOrchestrationResult() with { RepairResponse = response };

        Assert.IsTrue(result.WasRepaired);
        Assert.IsTrue(result.IsSuccess);
    }

    [TestMethod]
    public void BoundedContextOrchestrationResult_IsSuccess_FalseWhenResponseFailed()
    {
        var response = MakeRepairResponse() with { IsSuccess = false };
        var result = MakeOrchestrationResult() with { RepairResponse = response };

        Assert.IsFalse(result.IsSuccess);
    }

    [TestMethod]
    public void BoundedContextOrchestrationResult_Duration_ComputedFromTimestamps()
    {
        var start = DateTimeOffset.UtcNow;
        var end = start.AddSeconds(2.5);
        var result = MakeOrchestrationResult() with
        {
            StartedAt = start,
            CompletedAt = end
        };

        Assert.AreEqual(TimeSpan.FromSeconds(2.5), result.Duration);
    }

    [TestMethod]
    public void BoundedContextOrchestrationResult_IsSealedRecord()
    {
        var type = typeof(BoundedContextOrchestrationResult);
        Assert.IsTrue(type.IsSealed);
        Assert.IsTrue(type.IsClass);
        Assert.IsFalse(type.IsValueType);
    }

    // =========================================================================
    // 10. 反射验证：no async void
    // =========================================================================

    [TestMethod]
    public void NoAsyncVoid_InOrchestratorInterfaces()
    {
        var types = new[]
        {
            typeof(IContextRepairDetector),
            typeof(IContextRepairExecutor),
            typeof(IBoundedContextOrchestrator)
        };

        foreach (var type in types)
        {
            foreach (var method in type.GetMethods())
            {
                // async void 在接口中不应该出现，且 Task 返回类型已验证
                Assert.AreNotEqual(typeof(void), method.ReturnType,
                    $"{type.Name}.{method.Name} must not return void");
            }
        }
    }

    // =========================================================================
    // 11. 7 类确定性异常与 PackageQualityReport 指标的语义映射文档化
    // =========================================================================

    [TestMethod]
    public void SevenAnomalyReasons_CorrespondToPackageQualityMetrics()
    {
        // 文档化映射：每个异常 Reason 对应 PackageQualityReport 的一个指标
        // 这是 DefaultContextRepairDetector 实现的契约基础
        var mapping = new Dictionary<ContextRepairReason, string>
        {
            [ContextRepairReason.PrimaryAnchorUncovered] = nameof(PackageQualityReport.AnchorCoverage),
            [ContextRepairReason.HardConstraintMissing] = nameof(PackageQualityReport.HardConstraintSatisfaction),
            [ContextRepairReason.MustHitMissing] = nameof(PackageQualityReport.RequiredItemCoverage),
            [ContextRepairReason.SevereRedundancy] = nameof(PackageQualityReport.Redundancy),
            [ContextRepairReason.SectionSqueezeAnomaly] = nameof(PackageQualityReport.SectionBalance),
            [ContextRepairReason.TokenUtilizationTooLow] = nameof(PackageQualityReport.TokenEfficiency),
            [ContextRepairReason.LifecycleConflictUnresolved] = nameof(PackageQualityReport.LifecycleRisk)
        };

        // 7 类异常都有对应指标
        Assert.AreEqual(7, mapping.Count);

        // 每个映射的属性必须真实存在于 PackageQualityReport
        var reportType = typeof(PackageQualityReport);
        foreach (var metricPropertyName in mapping.Values)
        {
            var prop = reportType.GetProperty(metricPropertyName);
            Assert.IsNotNull(prop, $"PackageQualityReport should have property {metricPropertyName}");
            Assert.AreEqual(typeof(PackageQualityMetric), prop!.PropertyType);
        }
    }

    [TestMethod]
    public void SevenAnomalyReasons_DoNotIncludeUnknown()
    {
        // Unknown 不应该出现在正式修复请求中
        var anomalyReasons = new[]
        {
            ContextRepairReason.PrimaryAnchorUncovered,
            ContextRepairReason.HardConstraintMissing,
            ContextRepairReason.MustHitMissing,
            ContextRepairReason.SevereRedundancy,
            ContextRepairReason.SectionSqueezeAnomaly,
            ContextRepairReason.TokenUtilizationTooLow,
            ContextRepairReason.LifecycleConflictUnresolved
        };

        Assert.AreEqual(7, anomalyReasons.Length);
        Assert.IsFalse(anomalyReasons.Contains(ContextRepairReason.Unknown));
    }

    // =========================================================================
    // 辅助方法
    // =========================================================================

    private static ContextRepairDiagnosis MakeDiagnosis()
    {
        return new ContextRepairDiagnosis
        {
            DiagnosisId = "diag-1",
            DecisionRequestId = "req-1",
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            Reason = ContextRepairReason.PrimaryAnchorUncovered,
            ReasonDetail = "AnchorCoverage=0.4 < 0.8",
            DiagnosedAt = DateTimeOffset.UtcNow
        };
    }

    private static ContextDecisionResult MakeDecisionResult()
    {
        return new ContextDecisionResult
        {
            RequestId = "req-1",
            DecisionSource = ContextDecisionSource.Package,
            PolicyVersion = ContextDecisionPolicyVersions.DecisionSchemaV2_0,
            ModelEnabled = false
        };
    }

    private static ContextRepairRequest MakeRepairRequest(ContextRepairBudget? budget = null)
    {
        return new ContextRepairRequest
        {
            RepairRequestId = "repair-1",
            Diagnosis = MakeDiagnosis(),
            Budget = budget ?? new ContextRepairBudget(),
            OriginalDecision = MakeDecisionResult(),
            RequestedAt = DateTimeOffset.UtcNow
        };
    }

    private static ContextRepairResponse MakeRepairResponse()
    {
        return new ContextRepairResponse
        {
            RepairRequestId = "repair-1",
            IsSuccess = false,
            WasRepaired = false,
            RepairedDecision = MakeDecisionResult(),
            ConsumedBudget = new ContextRepairBudget(),
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    private static BoundedContextOrchestrationResult MakeOrchestrationResult()
    {
        var now = DateTimeOffset.UtcNow;
        return new BoundedContextOrchestrationResult
        {
            OrchestrationId = "orch-1",
            FinalDecision = MakeDecisionResult(),
            FinalQualityReport = new PackageQualityReport(),
            Diagnoses = Array.Empty<ContextRepairDiagnosis>(),
            StartedAt = now,
            CompletedAt = now
        };
    }
}
