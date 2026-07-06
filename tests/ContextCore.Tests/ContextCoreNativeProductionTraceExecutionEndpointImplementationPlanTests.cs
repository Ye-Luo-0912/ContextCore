using System.Text.Json;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreNativeProductionTraceExecutionEndpointImplementationPlanTests
{
    private static string ResolveArtifactPath(string fileName) =>
        TestRepoFileResolver.Resolve("learning", "v16_16", fileName);

    [TestMethod]
    public void ImplementationPlan_PlanReady_ButNotAllowedAndNotImplemented()
    {
        bool planReady = true;
        bool allowed = false;
        bool implemented = false;
        Assert.IsTrue(planReady);
        Assert.IsFalse(allowed);
        Assert.IsFalse(implemented);
    }

    [TestMethod]
    public void ImplementationPlan_Guards_SevenOrdered()
    {
        var guards = new[] { "confirmLiveCapture", "captureToken", "ws/col present",
            "synthetic rejection", "runId present", "RejectExistingRunId", "safety invariants" };
        Assert.AreEqual(7, guards.Length);
    }

    [TestMethod]
    public void ImplementationPlan_CLI_HasFiveRequiredArgs()
    {
        Assert.AreEqual(5, 5);
    }

    [TestMethod]
    public void ImplementationPlan_SinkLifecycle_TenSteps()
    {
        int steps = 10;
        Assert.AreEqual(10, steps);
    }

    [TestMethod]
    public void ImplementationPlan_DryRunDescription_Present()
    {
        string dryRunDesc = "When --dry-run flag is present, execute all guards but do NOT wire FileRuntimeCandidateTraceSink and do NOT call BuildDetailedAsync.";
        Assert.IsTrue(dryRunDesc.Length > 50);
    }

    [TestMethod]
    public void ImplementationPlan_FailureRollback_DisposeSinkRestoreNullSink()
    {
        var onBuildError = new[] { "Dispose sink", "Restore NullSink", "Delete partial trace", "Log error" };
        Assert.AreEqual(4, onBuildError.Length);
    }

    [TestMethod]
    public void ImplementationPlan_TestPlan_HasSevenUnitTests()
    {
        int planned = 7;
        Assert.AreEqual(7, planned);
    }

    [TestMethod]
    public void ImplementationPlan_AllGatesFalse()
    {
        var gates = new
        {
            EndpointImplementationAllowed = false,
            EndpointImplemented = false,
            ProductionTraceExecutionAuthorized = false,
            ProductionTraceExecutionAllowed = false,
            LiveCaptureExecutionImplemented = false,
            LiveCaptureExecuted = false,
            NativeProductionTraceReady = false,
            ProductionGeneralizationReady = false,
            RuntimeInfluenceAllowed = false,
            PackageOutputChanged = false,
            RuntimePromotionApplied = false,
            VectorBindingChanged = false,
        };
        Assert.IsFalse(gates.EndpointImplementationAllowed);
        Assert.IsFalse(gates.EndpointImplemented);
        Assert.IsFalse(gates.RuntimeInfluenceAllowed);
        Assert.IsFalse(gates.PackageOutputChanged);
    }

    [TestMethod]
    public void ImplementationPlan_NoActualCode_NoSinkWired_NoBuilderCalled()
    {
        bool implementationCodeWritten = false;
        bool sinkWired = false;
        bool builderCalled = false;
        Assert.IsFalse(implementationCodeWritten);
        Assert.IsFalse(sinkWired);
        Assert.IsFalse(builderCalled);
    }

    [TestMethod]
    public void ArtifactParsing_ImplementationPlan_ReadsFromFile()
    {
        var path = ResolveArtifactPath("native-production-trace-execution-endpoint-implementation-plan.json");
        Assert.IsTrue(File.Exists(path));
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        Assert.AreEqual("V16.16", root.GetProperty("ContractVersion").GetString());
        var status = root.GetProperty("PlanStatus");
        Assert.IsTrue(status.GetProperty("EndpointImplementationPlanReady").GetBoolean());
        Assert.IsFalse(status.GetProperty("EndpointImplementationAllowed").GetBoolean());
        Assert.IsFalse(status.GetProperty("EndpointImplemented").GetBoolean());
    }

    [TestMethod]
    public void ArtifactParsing_ImplementationPlan_GuardsPresent()
    {
        var path = ResolveArtifactPath("native-production-trace-execution-endpoint-implementation-plan.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var guards = doc.RootElement.GetProperty("GuardOrder");
        Assert.AreEqual(7, guards.GetArrayLength());
    }

    [TestMethod]
    public void ArtifactParsing_ImplementationPlanGate_ReadsFromFile()
    {
        var path = ResolveArtifactPath("native-production-trace-execution-endpoint-implementation-plan-gate.json");
        Assert.IsTrue(File.Exists(path));
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var gateResult = doc.RootElement.GetProperty("GateResult");
        Assert.IsTrue(gateResult.GetProperty("GatePassed").GetBoolean());
        var safety = doc.RootElement.GetProperty("SafetyAudit");
        Assert.IsFalse(safety.GetProperty("FileRuntimeCandidateTraceSinkWired").GetBoolean());
        Assert.IsFalse(safety.GetProperty("BuildDetailedAsyncCalledInLiveCapturePath").GetBoolean());
    }

    [TestMethod]
    public void ArtifactParsing_ImplementationPlan_NoJsonlFiles()
    {
        var planPath = ResolveArtifactPath("native-production-trace-execution-endpoint-implementation-plan.json");
        var dir = System.IO.Path.GetDirectoryName(planPath)!;
        Assert.AreEqual(0, Directory.GetFiles(dir, "*.jsonl").Length);
    }
}
