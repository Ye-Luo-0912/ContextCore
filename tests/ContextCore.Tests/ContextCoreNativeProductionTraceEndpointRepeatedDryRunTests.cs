using System.Text.Json;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreNativeProductionTraceEndpointRepeatedDryRunTests
{
    private static string Resolve(string f) => TestRepoFileResolver.Resolve("learning", "v16_27", f);

    [TestMethod]
    public void RepeatedExecution_RunCount3()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-repeated-dry-run-execution-report.json")));
        var es = doc.RootElement.GetProperty("ExecutionSummary");
        Assert.IsTrue(es.GetProperty("RunCount").GetInt32() >= 3);
        Assert.AreEqual(19, es.GetProperty("ScenarioCountPerRun").GetInt32());
        Assert.IsFalse(es.GetProperty("GlobalGoDecisionAllRuns").GetBoolean());
    }

    [TestMethod]
    public void Determinism_AllFieldsMatch()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-determinism-comparison-report.json")));
        Assert.IsTrue(doc.RootElement.GetProperty("DeterminismPassed").GetBoolean());
        Assert.AreEqual(0, doc.RootElement.GetProperty("MismatchesByField").GetArrayLength());
    }

    [TestMethod]
    public void NormalizedHashes_AllEqual_AllSame()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-normalized-result-hash-report.json")));
        var hr = doc.RootElement.GetProperty("HashReport");
        Assert.IsTrue(hr.GetProperty("AllHashesEqual").GetBoolean());
        Assert.AreEqual(1, hr.GetProperty("UniqueNormalizedHashes").GetInt32());
        Assert.AreEqual(3, hr.GetProperty("Hashes").GetArrayLength());
        // Verify all 3 hash values are identical strings
        var hashes = hr.GetProperty("Hashes").EnumerateArray().Select(h => h.GetString()).ToList();
        Assert.AreEqual(1, hashes.Distinct().Count(), "All 3 hashes must be identical.");
    }

    [TestMethod]
    public void SideEffectStability_HasStableFieldsAndConclusion()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-side-effect-stability-report.json")));
        Assert.IsTrue(doc.RootElement.GetProperty("AllSideEffectReportsStable").GetBoolean());
        Assert.IsTrue(doc.RootElement.TryGetProperty("StableFields", out _));
        Assert.IsTrue(doc.RootElement.TryGetProperty("Conclusion", out _));
    }

    [TestMethod]
    public void GuardStability_HasBlockedOpsCount_OperationsStable_Conclusion()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-guard-stability-report.json")));
        Assert.IsTrue(doc.RootElement.TryGetProperty("BlockedOperationsCount", out _));
        Assert.IsTrue(doc.RootElement.GetProperty("OperationsStable").GetBoolean());
        Assert.IsTrue(doc.RootElement.TryGetProperty("Conclusion", out _));
    }

    [TestMethod]
    public void GateStability_HasStableGateFields_Conclusion()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-gate-stability-report.json")));
        Assert.IsTrue(doc.RootElement.TryGetProperty("StableGateFields", out _));
        Assert.IsTrue(doc.RootElement.TryGetProperty("Conclusion", out _));
    }

    [TestMethod]
    public void V16_27Gate_AllFlagsCorrect()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-v16-27-gate.json")));
        var gr = doc.RootElement.GetProperty("GateResult");
        Assert.IsTrue(gr.GetProperty("GatePassed").GetBoolean());
        Assert.IsTrue(gr.GetProperty("DeterminismPassed").GetBoolean());
        Assert.IsFalse(gr.GetProperty("GoDecision").GetBoolean());
        Assert.AreEqual(1, gr.GetProperty("UniqueNormalizedHashes").GetInt32());
    }

    [TestMethod]
    public void NoJsonl()
    {
        var vDir = System.IO.Path.GetDirectoryName(Resolve("native-production-trace-endpoint-approval-validator-v16-27-gate.json"))!;
        var learningDir = System.IO.Path.GetDirectoryName(vDir)!;
        foreach (var d in new[] { "v16_14","v16_15","v16_16","v16_17","v16_18","v16_19","v16_20","v16_21","v16_22","v16_23","v16_24","v16_25","v16_26","v16_27"})
            if (Directory.Exists(System.IO.Path.Combine(learningDir, d)))
                Assert.AreEqual(0, Directory.GetFiles(System.IO.Path.Combine(learningDir, d), "*.jsonl").Length, $"{d}: jsonl must be 0.");
    }

    [TestMethod]
    public void GeneratorParity_RunGeneratorAndCheckKeyFields()
    {
        var assembly = typeof(ContextCore.ControlRoom.Commands.EvalCommand).Assembly;
        var type = assembly.GetType("ContextCore.ControlRoom.Commands.EvalCommand")!;
        var method = type.GetMethod("ExecuteV16_27NativeProductionTraceEndpointRepeatedDryRunAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var task = method.Invoke(null, [Array.Empty<string>(), CancellationToken.None]) as Task;
        Assert.IsNotNull(task);
        task!.GetAwaiter().GetResult();

        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-repeated-dry-run-execution-report.json")));
        Assert.IsTrue(doc.RootElement.GetProperty("ExecutionSummary").GetProperty("RunCount").GetInt32() >= 3);

        using var det = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-determinism-comparison-report.json")));
        Assert.IsTrue(det.RootElement.GetProperty("DeterminismPassed").GetBoolean());
    }
}
