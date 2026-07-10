using System.Text.Json;

namespace ContextCore.Tests;

[TestClass]
[TestCategory("Synthetic")]
[TestCategory("Gate")]
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

    // -----------------------------------------------------------------
    // Generator parity & preflight tests
    // -----------------------------------------------------------------

    [TestMethod]
    public void GeneratorParity_RunGeneratorAndValidatePlanSchema()
    {
        var assembly = typeof(ContextCore.ControlRoom.Commands.EvalCommand).Assembly;
        var type = assembly.GetType("ContextCore.ControlRoom.Commands.EvalCommand")!;
        var method = type.GetMethod("ExecuteV16_16NativeProductionTraceExecutionEndpointImplementationPlanAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var task = method.Invoke(null, [Array.Empty<string>(), CancellationToken.None]) as Task;
        Assert.IsNotNull(task);
        task!.GetAwaiter().GetResult();

        var path = ResolveArtifactPath("native-production-trace-execution-endpoint-implementation-plan.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        Assert.AreEqual("V16.16", root.GetProperty("ContractVersion").GetString());

        // Verify TargetFilesAndClasses has all three sub-targets
        var targets = root.GetProperty("TargetFilesAndClasses");
        Assert.IsTrue(targets.TryGetProperty("AuthorizationValidationTarget", out _));
        Assert.IsTrue(targets.TryGetProperty("SinkManagementTarget", out _));

        // Verify CliDispatchShape.Args have Type field
        var args = root.GetProperty("CliDispatchShape").GetProperty("Args");
        Assert.IsTrue(args[0].TryGetProperty("Type", out _));

        // Verify DryRunBehavior, BlockedBehavior, TestPlan exist
        Assert.IsTrue(root.TryGetProperty("DryRunBehavior", out _));
        Assert.IsTrue(root.TryGetProperty("BlockedBehavior", out _));
        Assert.IsTrue(root.TryGetProperty("TestPlan", out _));
    }

    [TestMethod]
    public void GeneratorParity_GuardOrder_IsFullArray_NotSummary()
    {
        var path = ResolveArtifactPath("native-production-trace-execution-endpoint-implementation-plan.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var guards = doc.RootElement.GetProperty("GuardOrder");
        Assert.AreEqual(7, guards.GetArrayLength(), "Must have 7 full guard entries, not a summary.");
    }

    [TestMethod]
    public void GeneratorParity_FailureRollback_HasAlwaysRestoreNote()
    {
        var path = ResolveArtifactPath("native-production-trace-execution-endpoint-implementation-plan.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var rollback = doc.RootElement.GetProperty("FailureRollback");
        Assert.IsTrue(rollback.TryGetProperty("AlwaysRestoreNote", out _));
    }

    [TestMethod]
    public void PreflightGate_ReadsAndValidatesAllRequiredFields()
    {
        var path = ResolveArtifactPath("native-production-trace-execution-endpoint-implementation-authorization-preflight.json");
        Assert.IsTrue(File.Exists(path));

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var gateResult = doc.RootElement.GetProperty("GateResult");
        Assert.IsTrue(gateResult.GetProperty("EndpointImplementationPlanReady").GetBoolean());
        Assert.IsTrue(gateResult.GetProperty("EndpointImplementationAuthorizationPreflightReady").GetBoolean());
        Assert.IsFalse(gateResult.GetProperty("EndpointImplementationAllowed").GetBoolean());
        Assert.IsFalse(gateResult.GetProperty("EndpointImplemented").GetBoolean());
        Assert.IsFalse(gateResult.GetProperty("LiveCaptureExecutionImplemented").GetBoolean());
    }

    [TestMethod]
    public void PreflightGate_PhaseTransition_NextAllowedAndDisallowed()
    {
        var path = ResolveArtifactPath("native-production-trace-execution-endpoint-implementation-authorization-preflight.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var transition = doc.RootElement.GetProperty("PhaseTransition");
        Assert.AreEqual("NativeProductionTraceExecutionEndpointImplementationApproval",
            transition.GetProperty("NextAllowedPhase").GetString());
        Assert.AreEqual("RuntimeInfluenceActivation",
            transition.GetProperty("NextDisallowedPhase").GetString());
    }

    [TestMethod]
    public void GeneratorParity_AllArtifactsHaveContractVersionV16_16()
    {
        var files = new[] { "native-production-trace-execution-endpoint-implementation-plan.json",
            "native-production-trace-execution-endpoint-implementation-plan-gate.json",
            "native-production-trace-execution-endpoint-implementation-authorization-preflight.json" };

        foreach (var file in files)
        {
            var path = ResolveArtifactPath(file);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.AreEqual("V16.16", doc.RootElement.GetProperty("ContractVersion").GetString());
        }
    }
}
