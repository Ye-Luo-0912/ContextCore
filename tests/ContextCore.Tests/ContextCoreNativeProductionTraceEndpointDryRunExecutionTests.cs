using System.Text.Json;

namespace ContextCore.Tests;

[TestClass]
[TestCategory("Synthetic")]
[TestCategory("Gate")]
public class ContextCoreNativeProductionTraceEndpointDryRunExecutionTests
{
    private static string Resolve(string f) => TestRepoFileResolver.Resolve("learning", "v16_26", f);

    [TestMethod]
    public void CheckedInGate_HasPurpose_BeforeGeneratorRuns()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-v16-26-gate.json")));
        Assert.IsTrue(doc.RootElement.TryGetProperty("Purpose", out _), "Checked-in gate MUST have Purpose.");
        Assert.IsFalse(doc.RootElement.GetProperty("GateResult").GetProperty("GoDecision").GetBoolean());
    }

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
        var assembly = typeof(ContextCore.Evaluation.Commands.EvalCommand).Assembly;
        var type = assembly.GetType("ContextCore.Evaluation.Commands.EvalCommand")!;
        var method = type.GetMethod("ExecuteV16_26NativeProductionTraceEndpointDryRunHarnessAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var task = method.Invoke(null, [Array.Empty<string>(), CancellationToken.None]) as Task;
        Assert.IsNotNull(task);
        task!.GetAwaiter().GetResult();

        // Execution report has Purpose
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-dry-run-harness-execution-report.json")));
        Assert.IsTrue(doc.RootElement.TryGetProperty("Purpose", out _));
        Assert.IsTrue(doc.RootElement.GetProperty("ExecutionStatus").GetProperty("SyntheticDryRunHarnessImplemented").GetBoolean());

        // Guard report has Purpose, BlockReason, ExternalFilesystemRead, RuntimeInfluenceAllowed, PackageOutputChanged, VectorBindingChanged
        using var guard = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-synthetic-guard-execution-report.json")));
        Assert.IsTrue(guard.RootElement.TryGetProperty("Purpose", out _));
        Assert.IsTrue(guard.RootElement.GetProperty("BlockedOperations")[0].TryGetProperty("BlockReason", out _));
        var gr = guard.RootElement.GetProperty("GuardResult");
        Assert.IsTrue(gr.TryGetProperty("ExternalFilesystemRead", out _));
        Assert.IsTrue(gr.TryGetProperty("RuntimeInfluenceAllowed", out _));
        Assert.IsTrue(gr.TryGetProperty("PackageOutputChanged", out _));
        Assert.IsTrue(gr.TryGetProperty("VectorBindingChanged", out _));

        // Audit evidence has Purpose
        using var audit = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-audit-evidence.json")));
        Assert.IsTrue(audit.RootElement.TryGetProperty("Purpose", out _));

        // No side effects report has Purpose and Conclusion
        using var nse = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-no-production-side-effects-report.json")));
        Assert.IsTrue(nse.RootElement.TryGetProperty("Purpose", out _));
        Assert.IsTrue(nse.RootElement.TryGetProperty("Conclusion", out _));

        // Result writer evidence has Purpose
        using var rw = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-result-writer-evidence.json")));
        Assert.IsTrue(rw.RootElement.TryGetProperty("Purpose", out _));

        // Gate has Purpose
        using var gateDoc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-v16-26-gate.json")));
        Assert.IsTrue(gateDoc.RootElement.TryGetProperty("Purpose", out _), "Gate must have Purpose.");
    }
}
