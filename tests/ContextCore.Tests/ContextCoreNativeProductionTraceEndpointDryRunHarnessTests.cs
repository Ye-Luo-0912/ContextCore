using System.Text.Json;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreNativeProductionTraceEndpointDryRunHarnessTests
{
    private static string Resolve(string f) => TestRepoFileResolver.Resolve("learning", "v16_25", f);

    [TestMethod]
    public void HarnessPlan_HarnessNotImplemented()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-dry-run-harness-implementation-plan.json")));
        var ps = doc.RootElement.GetProperty("PlanStatus");
        Assert.IsFalse(ps.GetProperty("DryRunHarnessImplemented").GetBoolean());
        Assert.IsTrue(ps.GetProperty("SyntheticFixtureExecutionOnly").GetBoolean());
        Assert.IsFalse(ps.GetProperty("GoDecision").GetBoolean());
    }

    [TestMethod]
    public void SyntheticOnlyGuard_BlocksAllTenOperations()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-synthetic-only-guard.json")));
        var ops = doc.RootElement.GetProperty("BlockedOperations");
        Assert.IsTrue(ops.GetArrayLength() >= 10);
        foreach (var op in ops.EnumerateArray())
            Assert.IsTrue(op.GetProperty("Blocked").GetBoolean());
    }

    [TestMethod]
    public void ScenarioMatrix_GlobalGoAlwaysFalse()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-dry-run-scenario-matrix.json")));
        var scenarios = doc.RootElement.GetProperty("Scenarios");
        Assert.IsTrue(scenarios.GetArrayLength() >= 19, "Scenario matrix must have >= 19 scenarios.");
        int simulatedGo = 0;
        foreach (var s in scenarios.EnumerateArray())
        {
            Assert.IsFalse(s.GetProperty("GlobalGoDecision").GetBoolean());
            if (s.GetProperty("SimulatedGo").GetBoolean()) simulatedGo++;
        }
        Assert.AreEqual(1, simulatedGo, "Exactly one simulated happy-path GoCandidate.");
    }

    [TestMethod]
    public void SimulationExecutor_NeverSetsGlobalGoDecisionTrue()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-simulation-executor-contract.json")));
        Assert.IsTrue(doc.RootElement.GetProperty("ExecutorRules").GetProperty("NeverSetGoDecisionGlobally").GetBoolean());
        Assert.IsTrue(doc.RootElement.GetProperty("ExecutorStatus").GetProperty("GlobalGoDecisionAlwaysFalse").GetBoolean());
    }

    [TestMethod]
    public void ResultWriter_NoJsonlProductionTrace()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-result-writer-contract.json")));
        Assert.IsTrue(doc.RootElement.GetProperty("WriterRules").GetProperty("NoJsonlProductionTrace").GetBoolean());
        Assert.IsTrue(doc.RootElement.GetProperty("WriterRules").GetProperty("NoProductionDecisionFile").GetBoolean());
    }

    [TestMethod]
    public void V16_25Gate_AllFlagsCorrect()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-v16-25-gate.json")));
        var gr = doc.RootElement.GetProperty("GateResult");
        Assert.IsTrue(gr.GetProperty("GatePassed").GetBoolean());
        Assert.IsFalse(gr.GetProperty("DryRunHarnessImplemented").GetBoolean());
        Assert.IsFalse(gr.GetProperty("GoDecision").GetBoolean());
    }

    [TestMethod]
    public void NoJsonl()
    {
        var vDir = System.IO.Path.GetDirectoryName(Resolve("native-production-trace-endpoint-approval-validator-v16-25-gate.json"))!;
        var learningDir = System.IO.Path.GetDirectoryName(vDir)!;
        foreach (var d in new[] { "v16_14","v16_15","v16_16","v16_17","v16_18","v16_19","v16_20","v16_21","v16_22","v16_23","v16_24","v16_25"})
            if (Directory.Exists(System.IO.Path.Combine(learningDir, d)))
                Assert.AreEqual(0, Directory.GetFiles(System.IO.Path.Combine(learningDir, d), "*.jsonl").Length, $"{d}: jsonl must be 0.");
    }

    [TestMethod]
    public void CheckedIn_ParityEvidence_HasFullFields_BeforeGeneratorRuns()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-generator-parity-evidence.json")));
        var cr0 = doc.RootElement.GetProperty("ComparisonResults")[0];
        Assert.IsTrue(cr0.TryGetProperty("CheckedInPropertyCount", out _));
        Assert.IsTrue(cr0.TryGetProperty("GeneratedPropertyCount", out _));
        Assert.IsTrue(cr0.TryGetProperty("ExtraPropertyPaths", out _));
        Assert.IsTrue(cr0.TryGetProperty("TypeMismatchPaths", out _));
        Assert.IsTrue(doc.RootElement.GetProperty("ParitySummary").TryGetProperty("TotalPropertiesChecked", out _));
    }

    [TestMethod]
    public void GeneratorParity_RunGeneratorAndCheckKeyFields()
    {
        var assembly = typeof(ContextCore.ControlRoom.Commands.EvalCommand).Assembly;
        var type = assembly.GetType("ContextCore.ControlRoom.Commands.EvalCommand")!;
        var method = type.GetMethod("ExecuteV16_25NativeProductionTraceEndpointDryRunHarnessPlanAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var task = method.Invoke(null, [Array.Empty<string>(), CancellationToken.None]) as Task;
        Assert.IsNotNull(task);
        task!.GetAwaiter().GetResult();

        using var sm = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-dry-run-scenario-matrix.json")));
        Assert.IsTrue(sm.RootElement.GetProperty("Scenarios").GetArrayLength() >= 19, "Generator must produce >= 19 scenarios.");

        using var pe = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-generator-parity-evidence.json")));
        Assert.IsTrue(pe.RootElement.GetProperty("ComparisonResults")[0].TryGetProperty("CheckedInPropertyCount", out _));
        Assert.IsTrue(pe.RootElement.GetProperty("ComparisonResults")[0].TryGetProperty("ExtraPropertyPaths", out _));
    }
}
