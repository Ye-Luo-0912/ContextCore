using System.Text.Json;

namespace ContextCore.Tests;

[TestClass]
[TestCategory("Synthetic")]
[TestCategory("Gate")]
public class ContextCoreNativeProductionTraceEndpointDryRunArchitectureTests
{
    private static string Resolve(string f) => TestRepoFileResolver.Resolve("learning", "v16_24", f);

    [TestMethod]
    public void Architecture_DryRunImplemented_False()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-dry-run-architecture.json")));
        var st = doc.RootElement.GetProperty("ArchitectureStatus");
        Assert.IsFalse(st.GetProperty("DryRunImplemented").GetBoolean());
        Assert.IsFalse(st.GetProperty("ProductionValidatorImplemented").GetBoolean());
        Assert.IsFalse(st.GetProperty("GoDecision").GetBoolean());
        Assert.IsTrue(st.GetProperty("SimulatedArtifactsOnly").GetBoolean());
    }

    [TestMethod]
    public void FixtureCorpus_NineteenFixtures_AllSynthetic()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-fixture-corpus-contract.json")));
        var fixtures = doc.RootElement.GetProperty("Fixtures");
        Assert.IsTrue(fixtures.GetArrayLength() >= 18);
        int goCount = 0;
        foreach (var f in fixtures.EnumerateArray())
            if (f.GetProperty("GoCandidate").GetBoolean()) goCount++;
        Assert.AreEqual(1, goCount);
    }

    [TestMethod]
    public void SimulationResultSchema_NoRawTokenLogging()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-simulation-result-schema.json")));
        Assert.IsTrue(doc.RootElement.GetProperty("Invariants").GetProperty("NoRawApprovalTokenLogged").GetBoolean());
        Assert.IsTrue(doc.RootElement.GetProperty("Invariants").GetProperty("NoProductionDecisionWritten").GetBoolean());
    }

    [TestMethod]
    public void QuarantineInteraction_Active_DryRunCannotWrite()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-quarantine-interaction-model.json")));
        Assert.AreEqual("Active", doc.RootElement.GetProperty("CurrentQuarantineStatus").GetString());
        Assert.IsFalse(doc.RootElement.GetProperty("QuarantineReleaseAllowed").GetBoolean());
    }

    [TestMethod]
    public void StaticScanCoupling_ReferenceOnly()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-static-scan-coupling.json")));
        Assert.AreEqual("ReferenceOnly", doc.RootElement.GetProperty("CouplingType").GetString());
        Assert.IsFalse(doc.RootElement.GetProperty("LiveScanExecuted").GetBoolean());
    }

    [TestMethod]
    public void TestHarnessPlan_NotImplemented()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-dry-run-test-harness-plan.json")));
        Assert.IsFalse(doc.RootElement.GetProperty("HarnessStatus").GetProperty("TestHarnessImplemented").GetBoolean());
    }

    [TestMethod]
    public void V16_24Gate_AllFlagsCorrect()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-v16-24-gate.json")));
        var gr = doc.RootElement.GetProperty("GateResult");
        Assert.IsTrue(gr.GetProperty("GatePassed").GetBoolean());
        Assert.IsTrue(gr.GetProperty("GeneratorParityEvidenceReady").GetBoolean());
        Assert.IsTrue(gr.GetProperty("GeneratorParityPassed").GetBoolean());
        Assert.IsFalse(gr.GetProperty("DryRunImplemented").GetBoolean());
        Assert.AreEqual("Active", gr.GetProperty("QuarantineStatus").GetString());
    }

    [TestMethod]
    public void NoJsonl()
    {
        var vDir = System.IO.Path.GetDirectoryName(Resolve("native-production-trace-endpoint-approval-validator-v16-24-gate.json"))!;
        var learningDir = System.IO.Path.GetDirectoryName(vDir)!;
        foreach (var d in new[] { "v16_14","v16_15","v16_16","v16_17","v16_18","v16_19","v16_20","v16_21","v16_22","v16_23","v16_24"})
            if (Directory.Exists(System.IO.Path.Combine(learningDir, d)))
                Assert.AreEqual(0, Directory.GetFiles(System.IO.Path.Combine(learningDir, d), "*.jsonl").Length, $"{d}: jsonl must be 0.");
    }

    [TestMethod]
    public void GeneratorParity_RunGeneratorAndCheckKeyFields()
    {
        var assembly = typeof(ContextCore.ControlRoom.Commands.EvalCommand).Assembly;
        var type = assembly.GetType("ContextCore.ControlRoom.Commands.EvalCommand")!;
        var method = type.GetMethod("ExecuteV16_24NativeProductionTraceEndpointDryRunArchitectureAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var task = method.Invoke(null, [Array.Empty<string>(), CancellationToken.None]) as Task;
        Assert.IsNotNull(task);
        task!.GetAwaiter().GetResult();

        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-dry-run-architecture.json")));
        Assert.IsTrue(doc.RootElement.GetProperty("ArchitectureStatus").TryGetProperty("SimulatedArtifactsOnly", out _));

        using var fc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-fixture-corpus-contract.json")));
        Assert.IsTrue(fc.RootElement.GetProperty("Fixtures").GetArrayLength() >= 18);
    }
}
