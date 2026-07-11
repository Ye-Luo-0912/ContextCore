using System.Text.Json;

namespace ContextCore.Tests;

[TestClass]
[TestCategory("Synthetic")]
[TestCategory("Gate")]
public class ContextCoreNativeProductionTraceExecutionEndpointApprovalTests
{
    private static string ResolveArtifactPath(string fileName) =>
        TestRepoFileResolver.Resolve("learning", "v16_17", fileName);

    [TestMethod]
    public void Approval_Ready_ButNotApproved_NotImplemented()
    {
        var path = ResolveArtifactPath("native-production-trace-execution-endpoint-implementation-approval.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var result = doc.RootElement.GetProperty("ApprovalResult");

        Assert.IsTrue(result.GetProperty("EndpointImplementationApprovalReady").GetBoolean());
        Assert.IsFalse(result.GetProperty("EndpointImplementationApproved").GetBoolean());
        Assert.IsFalse(result.GetProperty("EndpointImplementationAllowed").GetBoolean());
        Assert.IsFalse(result.GetProperty("EndpointImplemented").GetBoolean());
        Assert.IsFalse(result.GetProperty("ProductionTraceExecutionAuthorized").GetBoolean());
        Assert.IsFalse(result.GetProperty("ProductionTraceExecutionAllowed").GetBoolean());
    }

    [TestMethod]
    public void Approval_AllEightCriteriaSatisfied()
    {
        var path = ResolveArtifactPath("native-production-trace-execution-endpoint-implementation-approval.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var criteria = doc.RootElement.GetProperty("ApprovalCriteria");

        Assert.AreEqual(8, criteria.GetArrayLength());
        foreach (var c in criteria.EnumerateArray())
            Assert.AreEqual("Satisfied", c.GetProperty("Status").GetString(), c.GetProperty("Criterion").GetString());
    }

    [TestMethod]
    public void Approval_AllGatesFalse()
    {
        var path = ResolveArtifactPath("native-production-trace-execution-endpoint-implementation-approval.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var gates = doc.RootElement.GetProperty("GateSemantics");

        Assert.IsFalse(gates.GetProperty("LiveCaptureExecutionImplemented").GetBoolean());
        Assert.IsFalse(gates.GetProperty("LiveCaptureExecuted").GetBoolean());
        Assert.IsFalse(gates.GetProperty("NativeProductionTraceReady").GetBoolean());
        Assert.IsFalse(gates.GetProperty("ProductionGeneralizationReady").GetBoolean());
        Assert.IsFalse(gates.GetProperty("RuntimeInfluenceAllowed").GetBoolean());
        Assert.IsTrue(gates.GetProperty("RuntimeInfluenceAllowedPermanent").GetBoolean());
        Assert.IsFalse(gates.GetProperty("PackageOutputChanged").GetBoolean());
        Assert.IsFalse(gates.GetProperty("RuntimePromotionApplied").GetBoolean());
        Assert.IsFalse(gates.GetProperty("VectorBindingChanged").GetBoolean());
    }

    [TestMethod]
    public void Approval_SafetyAudit_AllClean()
    {
        var path = ResolveArtifactPath("native-production-trace-execution-endpoint-implementation-approval.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var safety = doc.RootElement.GetProperty("SafetyAudit");

        Assert.AreEqual(0, safety.GetProperty("JsonlTraceFilesInV16_17").GetInt32());
        Assert.IsFalse(safety.GetProperty("FileRuntimeCandidateTraceSinkWired").GetBoolean());
        Assert.IsFalse(safety.GetProperty("BuildDetailedAsyncCalledInLiveCapturePath").GetBoolean());
        Assert.IsFalse(safety.GetProperty("RuntimeCandidateTraceSinkAccessorMutated").GetBoolean());
        Assert.IsTrue(safety.GetProperty("NoImplementationCodeWritten").GetBoolean());
    }

    [TestMethod]
    public void ApprovalGate_ReadsFromFile()
    {
        var path = ResolveArtifactPath("native-production-trace-execution-endpoint-implementation-approval-gate.json");
        Assert.IsTrue(File.Exists(path));
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var gateResult = doc.RootElement.GetProperty("GateResult");
        Assert.IsTrue(gateResult.GetProperty("GatePassed").GetBoolean());
        Assert.IsTrue(gateResult.GetProperty("EndpointImplementationApprovalReady").GetBoolean());
        Assert.IsFalse(gateResult.GetProperty("EndpointImplementationApproved").GetBoolean());

        var safety = doc.RootElement.GetProperty("SafetyAudit");
        Assert.IsTrue(safety.TryGetProperty("NoImplementationCodeWritten", out _));
    }

    [TestMethod]
    public void DecisionBoundary_ReadsAndValidatesAllRequiredFields()
    {
        var path = ResolveArtifactPath("native-production-trace-execution-endpoint-implementation-decision-boundary.json");
        Assert.IsTrue(File.Exists(path));
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var result = doc.RootElement.GetProperty("GateResult");

        Assert.IsTrue(result.GetProperty("EndpointImplementationApprovalReady").GetBoolean());
        Assert.IsFalse(result.GetProperty("EndpointImplementationApproved").GetBoolean());
        Assert.IsFalse(result.GetProperty("EndpointImplementationDecisionAllowed").GetBoolean());
        Assert.IsFalse(result.GetProperty("EndpointImplementationAllowed").GetBoolean());
        Assert.IsFalse(result.GetProperty("EndpointImplemented").GetBoolean());
        Assert.IsFalse(result.GetProperty("LiveCaptureExecutionImplemented").GetBoolean());
        Assert.IsFalse(result.GetProperty("LiveCaptureExecuted").GetBoolean());
        Assert.IsFalse(result.GetProperty("NativeProductionTraceReady").GetBoolean());
    }

    [TestMethod]
    public void DecisionBoundary_PhaseTransition_NextAllowedAndDisallowed()
    {
        var path = ResolveArtifactPath("native-production-trace-execution-endpoint-implementation-decision-boundary.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var transition = doc.RootElement.GetProperty("PhaseTransition");
        Assert.AreEqual("NativeProductionTraceExecutionEndpointImplementationFinalApproval",
            transition.GetProperty("NextAllowedPhase").GetString());
        Assert.AreEqual("RuntimeInfluenceActivation",
            transition.GetProperty("NextDisallowedPhase").GetString());
    }

    [TestMethod]
    public void GeneratorParity_RunGeneratorAndValidateOutput()
    {
        var assembly = typeof(ContextCore.Evaluation.Commands.EvalCommand).Assembly;
        var type = assembly.GetType("ContextCore.Evaluation.Commands.EvalCommand")!;
        var method = type.GetMethod("ExecuteV16_17NativeProductionTraceExecutionEndpointApprovalAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var task = method.Invoke(null, [Array.Empty<string>(), CancellationToken.None]) as Task;
        Assert.IsNotNull(task);
        task!.GetAwaiter().GetResult();

        var approvalPath = ResolveArtifactPath("native-production-trace-execution-endpoint-implementation-approval.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(approvalPath));
        var result = doc.RootElement.GetProperty("ApprovalResult");
        Assert.IsTrue(result.GetProperty("EndpointImplementationApprovalReady").GetBoolean());
        Assert.IsFalse(result.GetProperty("EndpointImplementationApproved").GetBoolean());
        Assert.IsTrue(doc.RootElement.TryGetProperty("SafetyAudit", out _));
        Assert.IsTrue(doc.RootElement.TryGetProperty("PreviousGatesPreserved", out _));
    }

    [TestMethod]
    public void NoJsonlFilesExist()
    {
        var p = ResolveArtifactPath("native-production-trace-execution-endpoint-implementation-approval.json");
        Assert.AreEqual(0, Directory.GetFiles(System.IO.Path.GetDirectoryName(p)!, "*.jsonl").Length);
    }
}
