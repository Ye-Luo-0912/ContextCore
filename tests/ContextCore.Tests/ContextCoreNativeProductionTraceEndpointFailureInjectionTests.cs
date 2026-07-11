using System.Text.Json;

namespace ContextCore.Tests;

[TestClass]
[TestCategory("Synthetic")]
[TestCategory("Gate")]
public class ContextCoreNativeProductionTraceEndpointFailureInjectionTests
{
    private static string Resolve(string f) => TestRepoFileResolver.Resolve("learning", "v16_28", f);

    [TestMethod]
    public void FailurePlan_TwelveCases()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-failure-injection-plan.json")));
        Assert.AreEqual(12, doc.RootElement.GetProperty("FailureCases").GetArrayLength());
    }

    [TestMethod]
    public void FailureResults_AllBlocked_AllOutcomeMatch()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-failure-injection-results.json")));
        var results = doc.RootElement.GetProperty("Results");
        Assert.IsTrue(results.GetArrayLength() >= 12);
        foreach (var r in results.EnumerateArray())
        {
            Assert.IsTrue(r.GetProperty("ActualBlocked").GetBoolean());
            Assert.IsTrue(r.GetProperty("OutcomeMatchesExpected").GetBoolean());
            Assert.IsFalse(r.GetProperty("GlobalGoDecision").GetBoolean());
            Assert.IsFalse(r.GetProperty("ProductionDecisionWritten").GetBoolean());
        }
    }

    [TestMethod]
    public void GuardFailureReport_ZeroViolations_AllBlocked()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-guard-failure-injection-report.json")));
        var gr = doc.RootElement.GetProperty("GuardResult");
        Assert.AreEqual(0, gr.GetProperty("GuardViolationCount").GetInt32());
        Assert.IsTrue(gr.GetProperty("AllForbiddenOperationsBlocked").GetBoolean());
        var ops = doc.RootElement.GetProperty("SimulatedOperations");
        Assert.IsTrue(ops.GetArrayLength() >= 10);
        foreach (var op in ops.EnumerateArray())
        {
            Assert.IsTrue(op.GetProperty("Blocked").GetBoolean());
            Assert.IsTrue(op.GetProperty("RecoveryCompleted").GetBoolean());
        }
    }

    [TestMethod]
    public void DeterminismBreak_DetectedAndContained()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-determinism-break-detection-report.json")));
        var dr = doc.RootElement.GetProperty("DetectionResult");
        Assert.IsTrue(dr.GetProperty("DeterminismBreakDetected").GetBoolean());
        Assert.IsTrue(dr.GetProperty("DeterminismBreakContained").GetBoolean());
        Assert.IsTrue(dr.GetProperty("HashMismatchDetected").GetBoolean());
        Assert.IsTrue(dr.GetProperty("ScenarioMismatchDetected").GetBoolean());
        Assert.IsFalse(dr.GetProperty("GlobalGoDecision").GetBoolean());
    }

    [TestMethod]
    public void NoSideEffects_HasConclusion_AndFullFields()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-no-production-side-effects-report.json")));
        var r = doc.RootElement.GetProperty("Report");
        Assert.IsTrue(doc.RootElement.TryGetProperty("Conclusion", out _));
        Assert.IsFalse(r.GetProperty("BuildDetailedAsyncCalled").GetBoolean());
        Assert.IsFalse(r.GetProperty("EndpointImplementation").GetBoolean());
        Assert.IsFalse(r.GetProperty("RuntimeCandidateTraceSinkAccessorMutated").GetBoolean());
    }

    [TestMethod]
    public void Recovery_HasQuarantineStatus()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-recovery-and-clean-state-report.json")));
        var rr = doc.RootElement.GetProperty("RecoveryResult");
        Assert.AreEqual("Active", rr.GetProperty("QuarantineStatus").GetString());
    }

    [TestMethod]
    public void Recovery_CleanStateRestored()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-recovery-and-clean-state-report.json")));
        var rr = doc.RootElement.GetProperty("RecoveryResult");
        Assert.IsTrue(rr.GetProperty("RecoveryCompleted").GetBoolean());
        Assert.IsTrue(rr.GetProperty("CleanStateRestored").GetBoolean());
        Assert.IsFalse(rr.GetProperty("GlobalGoDecision").GetBoolean());
    }

    [TestMethod]
    public void V16_28Gate_AllFlagsCorrect()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-v16-28-gate.json")));
        var gr = doc.RootElement.GetProperty("GateResult");
        Assert.IsTrue(gr.GetProperty("GatePassed").GetBoolean());
        Assert.IsTrue(gr.GetProperty("AllFailureCasesBlocked").GetBoolean());
        Assert.IsFalse(gr.GetProperty("GoDecision").GetBoolean());
    }

    [TestMethod]
    public void NoJsonl()
    {
        var vDir = System.IO.Path.GetDirectoryName(Resolve("native-production-trace-endpoint-approval-validator-v16-28-gate.json"))!;
        var learningDir = System.IO.Path.GetDirectoryName(vDir)!;
        foreach (var d in new[] { "v16_14","v16_15","v16_16","v16_17","v16_18","v16_19","v16_20","v16_21","v16_22","v16_23","v16_24","v16_25","v16_26","v16_27","v16_28"})
            if (Directory.Exists(System.IO.Path.Combine(learningDir, d)))
                Assert.AreEqual(0, Directory.GetFiles(System.IO.Path.Combine(learningDir, d), "*.jsonl").Length, $"{d}: jsonl must be 0.");
    }

    [TestMethod]
    public void GeneratorParity_RunGeneratorAndCheckKeyFields()
    {
        var assembly = typeof(ContextCore.Evaluation.Commands.EvalCommand).Assembly;
        var type = assembly.GetType("ContextCore.Evaluation.Commands.EvalCommand")!;
        var method = type.GetMethod("ExecuteV16_28NativeProductionTraceEndpointFailureInjectionAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var task = method.Invoke(null, [Array.Empty<string>(), CancellationToken.None]) as Task;
        Assert.IsNotNull(task);
        task!.GetAwaiter().GetResult();

        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-failure-injection-plan.json")));
        Assert.AreEqual(12, doc.RootElement.GetProperty("FailureCases").GetArrayLength());
    }
}
