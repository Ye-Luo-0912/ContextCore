using System.Text.Json;

namespace ContextCore.Tests;

[TestClass]
[TestCategory("Synthetic")]
[TestCategory("Gate")]
public class ContextCoreNativeProductionTraceExecutionEndpointFinalApprovalTests
{
    private static string ResolveArtifactPath(string fileName) =>
        TestRepoFileResolver.Resolve("learning", "v16_18", fileName);

    [TestMethod]
    public void FinalApproval_Ready_ButNotApproved()
    {
        var path = ResolveArtifactPath("native-production-trace-execution-endpoint-implementation-final-approval.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var result = doc.RootElement.GetProperty("FinalApprovalResult");

        Assert.IsTrue(result.GetProperty("EndpointImplementationFinalApprovalReady").GetBoolean());
        Assert.IsFalse(result.GetProperty("EndpointImplementationFinalApproved").GetBoolean());
        Assert.IsFalse(result.GetProperty("EndpointImplementationAllowed").GetBoolean());
        Assert.IsFalse(result.GetProperty("EndpointImplemented").GetBoolean());
        Assert.IsFalse(result.GetProperty("ProductionTraceExecutionAuthorized").GetBoolean());
    }

    [TestMethod]
    public void FinalApproval_EightCriteria_AllSatisfied()
    {
        var path = ResolveArtifactPath("native-production-trace-execution-endpoint-implementation-final-approval.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var criteria = doc.RootElement.GetProperty("FinalApprovalCriteria");
        Assert.AreEqual(8, criteria.GetArrayLength());
        foreach (var c in criteria.EnumerateArray())
            Assert.AreEqual("Satisfied", c.GetProperty("Status").GetString());
    }

    [TestMethod]
    public void FinalApproval_AllGatesFalse()
    {
        var path = ResolveArtifactPath("native-production-trace-execution-endpoint-implementation-final-approval.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var gates = doc.RootElement.GetProperty("GateSemantics");
        Assert.IsFalse(gates.GetProperty("LiveCaptureExecutionImplemented").GetBoolean());
        Assert.IsFalse(gates.GetProperty("NativeProductionTraceReady").GetBoolean());
        Assert.IsFalse(gates.GetProperty("RuntimeInfluenceAllowed").GetBoolean());
        Assert.IsTrue(gates.GetProperty("RuntimeInfluenceAllowedPermanent").GetBoolean());
        Assert.IsFalse(gates.GetProperty("PackageOutputChanged").GetBoolean());
        Assert.IsFalse(gates.GetProperty("VectorBindingChanged").GetBoolean());
    }

    [TestMethod]
    public void FinalApproval_SafetyAudit_AllClean()
    {
        var path = ResolveArtifactPath("native-production-trace-execution-endpoint-implementation-final-approval.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var safety = doc.RootElement.GetProperty("SafetyAudit");
        Assert.AreEqual(0, safety.GetProperty("JsonlTraceFilesInV16_18").GetInt32());
        Assert.IsFalse(safety.GetProperty("FileRuntimeCandidateTraceSinkWired").GetBoolean());
        Assert.IsTrue(safety.GetProperty("NoImplementationCodeWritten").GetBoolean());
    }

    [TestMethod]
    public void FinalApprovalGate_ReadsFromFile()
    {
        var path = ResolveArtifactPath("native-production-trace-execution-endpoint-implementation-final-approval-gate.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var gateResult = doc.RootElement.GetProperty("GateResult");
        Assert.IsTrue(gateResult.GetProperty("GatePassed").GetBoolean());
        Assert.IsFalse(gateResult.GetProperty("EndpointImplementationFinalApproved").GetBoolean());
        var safety = doc.RootElement.GetProperty("SafetyAudit");
        Assert.IsTrue(safety.TryGetProperty("NoImplementationCodeWritten", out _));
    }

    [TestMethod]
    public void GeneratorParity_RunGeneratorAndValidateOutput()
    {
        var assembly = typeof(ContextCore.Evaluation.Commands.EvalCommand).Assembly;
        var type = assembly.GetType("ContextCore.Evaluation.Commands.EvalCommand")!;
        var method = type.GetMethod("ExecuteV16_18NativeProductionTraceExecutionEndpointFinalApprovalAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var task = method.Invoke(null, [Array.Empty<string>(), CancellationToken.None]) as Task;
        Assert.IsNotNull(task);
        task!.GetAwaiter().GetResult();

        var path = ResolveArtifactPath("native-production-trace-execution-endpoint-implementation-final-approval.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var result = doc.RootElement.GetProperty("FinalApprovalResult");
        Assert.IsTrue(result.GetProperty("EndpointImplementationFinalApprovalReady").GetBoolean());
        Assert.IsFalse(result.GetProperty("EndpointImplementationFinalApproved").GetBoolean());
        Assert.IsTrue(doc.RootElement.TryGetProperty("SafetyAudit", out _));
    }

    [TestMethod]
    public void NoJsonlFilesExist()
    {
        var p = ResolveArtifactPath("native-production-trace-execution-endpoint-implementation-final-approval.json");
        Assert.AreEqual(0, Directory.GetFiles(System.IO.Path.GetDirectoryName(p)!, "*.jsonl").Length);
    }

    // -----------------------------------------------------------------
    // Boundary freeze & non-implementation ledger tests
    // -----------------------------------------------------------------

    [TestMethod]
    public void BoundaryFreeze_ReadsAndValidates_FrozenReadyButNotApproved()
    {
        var path = ResolveArtifactPath("native-production-trace-execution-endpoint-final-boundary-freeze.json");
        Assert.IsTrue(File.Exists(path));
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var freeze = doc.RootElement.GetProperty("BoundaryFreeze");

        Assert.AreEqual("ReadyButNotApproved", freeze.GetProperty("FrozenState").GetString());
        Assert.IsTrue(freeze.GetProperty("EndpointImplementationFinalApprovalReady").GetBoolean());
        Assert.IsFalse(freeze.GetProperty("EndpointImplementationFinalApproved").GetBoolean());
        Assert.IsFalse(freeze.GetProperty("EndpointImplementationAllowed").GetBoolean());
        Assert.IsFalse(freeze.GetProperty("EndpointImplemented").GetBoolean());
        Assert.IsFalse(freeze.GetProperty("LiveCaptureExecutionImplemented").GetBoolean());
        Assert.IsFalse(freeze.GetProperty("NativeProductionTraceReady").GetBoolean());
    }

    [TestMethod]
    public void BoundaryFreeze_DoNotMisinterpret_Explicit()
    {
        var path = ResolveArtifactPath("native-production-trace-execution-endpoint-final-boundary-freeze.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.IsTrue(doc.RootElement.TryGetProperty("DoNotMisinterpret", out var dmi));
        Assert.IsTrue(dmi.GetArrayLength() >= 3);
    }

    [TestMethod]
    public void NonImplementationLedger_ReadsAndValidates_FivePhases_AllReadyNotApproved()
    {
        var path = ResolveArtifactPath("native-production-trace-execution-endpoint-non-implementation-ledger.json");
        Assert.IsTrue(File.Exists(path));
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var entries = doc.RootElement.GetProperty("LedgerEntries");

        Assert.AreEqual(5, entries.GetArrayLength());
        foreach (var e in entries.EnumerateArray())
        {
            Assert.IsTrue(e.GetProperty("Ready").GetBoolean(),
                $"{e.GetProperty("Version").GetString()}: Ready must be true.");
            Assert.IsFalse(e.GetProperty("Approved").GetBoolean(),
                $"{e.GetProperty("Version").GetString()}: Approved must be false.");
            Assert.IsFalse(e.GetProperty("Implemented").GetBoolean(),
                $"{e.GetProperty("Version").GetString()}: Implemented must be false.");
        }
    }

    [TestMethod]
    public void NonImplementationLedger_CrossCuttingConfirmation_AllTrue()
    {
        var path = ResolveArtifactPath("native-production-trace-execution-endpoint-non-implementation-ledger.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var conf = doc.RootElement.GetProperty("CrossCuttingConfirmation");
        Assert.IsTrue(conf.GetProperty("NoProductionTraceJsonl").GetBoolean());
        Assert.IsTrue(conf.GetProperty("NoFileRuntimeCandidateTraceSinkWired").GetBoolean());
        Assert.IsTrue(conf.GetProperty("NoRuntimeInfluence").GetBoolean());
        Assert.IsTrue(conf.GetProperty("NoImplementationCodeWritten").GetBoolean());
    }
}
