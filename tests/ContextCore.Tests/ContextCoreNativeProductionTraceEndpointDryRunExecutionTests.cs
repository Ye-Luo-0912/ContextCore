using System.Text.Json;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreNativeProductionTraceEndpointDryRunExecutionTests
{
    private static string Resolve(string f) => TestRepoFileResolver.Resolve("learning", "v16_26", f);

    [TestMethod]
    public void ExecutionReport_HarnessImplemented_19Scenarios()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-dry-run-harness-execution-report.json")));
        var st = doc.RootElement.GetProperty("ExecutionStatus");
        Assert.IsTrue(st.GetProperty("SyntheticDryRunHarnessImplemented").GetBoolean());
        Assert.IsFalse(st.GetProperty("ProductionValidatorImplemented").GetBoolean());
        Assert.AreEqual(19, st.GetProperty("TotalScenarios").GetInt32());
        Assert.IsFalse(st.GetProperty("GlobalGoDecision").GetBoolean());
        Assert.IsFalse(st.GetProperty("ProductionDecisionWritten").GetBoolean());
    }

    [TestMethod]
    public void FixtureResults_19Results_1SimulatedGo_AllGlobalGoFalse()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-synthetic-fixture-results.json")));
        var results = doc.RootElement.GetProperty("Results");
        Assert.AreEqual(19, results.GetArrayLength());
        int goCount = 0;
        foreach (var r in results.EnumerateArray())
        {
            Assert.IsFalse(r.GetProperty("GlobalGoDecision").GetBoolean());
            Assert.IsFalse(r.GetProperty("ProductionDecisionWritten").GetBoolean());
            Assert.IsTrue(r.GetProperty("SyntheticOnly").GetBoolean());
            Assert.IsTrue(r.GetProperty("OutcomeMatchesExpected").GetBoolean());
            if (r.GetProperty("SimulatedGoCandidateAllowed").GetBoolean()) goCount++;
        }
        Assert.AreEqual(1, goCount);
    }

    [TestMethod]
    public void GuardReport_10Blocked_ZeroViolations()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-synthetic-guard-execution-report.json")));
        var ops = doc.RootElement.GetProperty("BlockedOperations");
        Assert.AreEqual(10, ops.GetArrayLength());
        foreach (var op in ops.EnumerateArray())
        {
            Assert.IsTrue(op.GetProperty("Blocked").GetBoolean());
            Assert.IsFalse(op.GetProperty("ProductionSideEffect").GetBoolean());
        }
        var gr = doc.RootElement.GetProperty("GuardResult");
        Assert.IsTrue(gr.GetProperty("SyntheticOnlyGuardPassed").GetBoolean());
        Assert.IsFalse(gr.GetProperty("RealApprovalArtifactRead").GetBoolean());
        Assert.IsFalse(gr.GetProperty("FileRuntimeCandidateTraceSinkCreated").GetBoolean());
    }

    [TestMethod]
    public void NoSideEffects_AllZero()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-no-production-side-effects-report.json")));
        var r = doc.RootElement.GetProperty("Report");
        Assert.IsFalse(r.GetProperty("ApprovalArtifactCreated").GetBoolean());
        Assert.AreEqual(0, r.GetProperty("ProductionTraceJsonlFiles").GetInt32());
        Assert.IsFalse(r.GetProperty("FileRuntimeCandidateTraceSinkWired").GetBoolean());
        Assert.IsFalse(r.GetProperty("RuntimeInfluenceAllowed").GetBoolean());
    }

    [TestMethod]
    public void V16_26Gate_AllFlagsCorrect()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-v16-26-gate.json")));
        var gr = doc.RootElement.GetProperty("GateResult");
        Assert.IsTrue(gr.GetProperty("GatePassed").GetBoolean());
        Assert.IsTrue(gr.GetProperty("SyntheticDryRunHarnessImplemented").GetBoolean());
        Assert.IsFalse(gr.GetProperty("ProductionValidatorImplemented").GetBoolean());
        Assert.AreEqual(19, gr.GetProperty("SyntheticScenarioCount").GetInt32());
        Assert.IsFalse(gr.GetProperty("GlobalGoDecision").GetBoolean());
    }

    [TestMethod]
    public void NoJsonl()
    {
        var vDir = System.IO.Path.GetDirectoryName(Resolve("native-production-trace-endpoint-approval-validator-v16-26-gate.json"))!;
        var learningDir = System.IO.Path.GetDirectoryName(vDir)!;
        foreach (var d in new[] { "v16_14","v16_15","v16_16","v16_17","v16_18","v16_19","v16_20","v16_21","v16_22","v16_23","v16_24","v16_25","v16_26"})
            if (Directory.Exists(System.IO.Path.Combine(learningDir, d)))
                Assert.AreEqual(0, Directory.GetFiles(System.IO.Path.Combine(learningDir, d), "*.jsonl").Length, $"{d}: jsonl must be 0.");
    }

    [TestMethod]
    public void GeneratorParity_RunGeneratorAndCheckKeyFields()
    {
        var assembly = typeof(ContextCore.ControlRoom.Commands.EvalCommand).Assembly;
        var type = assembly.GetType("ContextCore.ControlRoom.Commands.EvalCommand")!;
        var method = type.GetMethod("ExecuteV16_26NativeProductionTraceEndpointDryRunHarnessAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var task = method.Invoke(null, [Array.Empty<string>(), CancellationToken.None]) as Task;
        Assert.IsNotNull(task);
        task!.GetAwaiter().GetResult();

        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-dry-run-harness-execution-report.json")));
        Assert.IsTrue(doc.RootElement.GetProperty("ExecutionStatus").GetProperty("SyntheticDryRunHarnessImplemented").GetBoolean());

        using var results = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-synthetic-fixture-results.json")));
        Assert.AreEqual(19, results.RootElement.GetProperty("Results").GetArrayLength());
    }
}
