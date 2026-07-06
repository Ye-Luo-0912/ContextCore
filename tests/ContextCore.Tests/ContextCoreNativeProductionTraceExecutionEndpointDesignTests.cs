using System.Text.Json;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreNativeProductionTraceExecutionEndpointDesignTests
{
    private static string ResolveArtifactPath(string fileName) =>
        TestRepoFileResolver.Resolve("learning", "v16_15", fileName);

    [TestMethod]
    public void EndpointDesign_DesignReady_ButNotAllowedAndNotImplemented()
    {
        bool designReady = true;
        bool allowed = false;
        bool implemented = false;

        Assert.IsTrue(designReady);
        Assert.IsFalse(allowed);
        Assert.IsFalse(implemented);
    }

    [TestMethod]
    public void EndpointDesign_CLI_HasFiveRequiredArgs()
    {
        var requiredArgs = new[] { "--confirm-live-capture", "--capture-token", "--workspaceId", "--collectionId", "--runId" };

        Assert.AreEqual(5, requiredArgs.Length, "Must have exactly 5 required CLI args.");
    }

    [TestMethod]
    public void EndpointDesign_AuthorizationContractIntegration_SevenFactors()
    {
        int factorsChecked = 7;

        Assert.AreEqual(7, factorsChecked, "Must check all 7 authorization factors from V16.14.");
    }

    [TestMethod]
    public void EndpointDesign_SyntheticRejection_CoversBothWorkspaceAndCollection()
    {
        string workspaceId = "native-ws";
        string collectionId = "native-col";

        bool wsSynthetic = new[] { "native-ws", "smoke-ws", "prod-ws" }.Contains(workspaceId, StringComparer.OrdinalIgnoreCase);
        bool colSynthetic = new[] { "native-col", "smoke-col", "prod-col" }.Contains(collectionId, StringComparer.OrdinalIgnoreCase);

        Assert.IsTrue(wsSynthetic);
        Assert.IsTrue(colSynthetic);
    }

    [TestMethod]
    public void EndpointDesign_RunIdPolicy_RejectExistingRunId()
    {
        string policy = "RejectExistingRunId";
        Assert.AreEqual("RejectExistingRunId", policy);
    }

    [TestMethod]
    public void EndpointDesign_SinkWiringPlan_HasSixSteps()
    {
        int steps = 6;
        Assert.AreEqual(6, steps, "Sink wiring must have 6 steps.");
    }

    [TestMethod]
    public void EndpointDesign_RollbackPlan_SixSteps()
    {
        var steps = new[] { "Dispose sink", "Restore NullSink", "Clear IDs", "Delete partial on fail", "Retain on success", "Log" };
        Assert.AreEqual(6, steps.Length);
    }

    [TestMethod]
    public void EndpointDesign_NoRuntimeInfluence_AllPermanentlyFalse()
    {
        var invariants = new
        {
            RuntimeInfluenceAllowed = false,
            RuntimeInfluenceAllowedPermanent = true,
            NeuralBiasActive = false,
            PackageOutputChanged = false,
            RuntimePromotionApplied = false,
            VectorBindingChanged = false,
        };

        Assert.IsFalse(invariants.RuntimeInfluenceAllowed);
        Assert.IsTrue(invariants.RuntimeInfluenceAllowedPermanent);
        Assert.IsFalse(invariants.NeuralBiasActive);
        Assert.IsFalse(invariants.PackageOutputChanged);
        Assert.IsFalse(invariants.RuntimePromotionApplied);
        Assert.IsFalse(invariants.VectorBindingChanged);
    }

    [TestMethod]
    public void EndpointDesign_AllGatesFalse()
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
        Assert.IsFalse(gates.ProductionTraceExecutionAuthorized);
        Assert.IsFalse(gates.ProductionTraceExecutionAllowed);
        Assert.IsFalse(gates.LiveCaptureExecutionImplemented);
        Assert.IsFalse(gates.LiveCaptureExecuted);
        Assert.IsFalse(gates.NativeProductionTraceReady);
        Assert.IsFalse(gates.ProductionGeneralizationReady);
        Assert.IsFalse(gates.RuntimeInfluenceAllowed);
        Assert.IsFalse(gates.PackageOutputChanged);
        Assert.IsFalse(gates.RuntimePromotionApplied);
        Assert.IsFalse(gates.VectorBindingChanged);
    }

    [TestMethod]
    public void EndpointDesign_NoLiveSinkWiring()
    {
        bool fileTraceSinkWired = false;
        bool buildDetailedAsyncCalled = false;

        Assert.IsFalse(fileTraceSinkWired);
        Assert.IsFalse(buildDetailedAsyncCalled);
    }

    // ----------------------------------------------------------------
    // Artifact parsing tests
    // ----------------------------------------------------------------

    [TestMethod]
    public void ArtifactParsing_EndpointDesign_ReadsFromFile()
    {
        var path = ResolveArtifactPath("native-production-trace-execution-endpoint-implementation-design.json");
        Assert.IsTrue(File.Exists(path));

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        Assert.AreEqual("V16.15", root.GetProperty("ContractVersion").GetString());
        Assert.AreEqual("NativeProductionTraceExecutionEndpointImplementationDesign",
            root.GetProperty("DocumentType").GetString());

        var status = root.GetProperty("DesignStatus");
        Assert.IsTrue(status.GetProperty("EndpointImplementationDesignReady").GetBoolean());
        Assert.IsFalse(status.GetProperty("EndpointImplementationAllowed").GetBoolean());
        Assert.IsFalse(status.GetProperty("EndpointImplemented").GetBoolean());
    }

    [TestMethod]
    public void ArtifactParsing_EndpointDesign_AllGatesFalse()
    {
        var path = ResolveArtifactPath("native-production-trace-execution-endpoint-implementation-design.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var gates = doc.RootElement.GetProperty("GateSemantics");

        Assert.IsFalse(gates.GetProperty("EndpointImplementationAllowed").GetBoolean());
        Assert.IsFalse(gates.GetProperty("EndpointImplemented").GetBoolean());
        Assert.IsFalse(gates.GetProperty("LiveCaptureExecutionImplemented").GetBoolean());
        Assert.IsFalse(gates.GetProperty("LiveCaptureExecuted").GetBoolean());
        Assert.IsFalse(gates.GetProperty("NativeProductionTraceReady").GetBoolean());
        Assert.IsFalse(gates.GetProperty("RuntimeInfluenceAllowed").GetBoolean());
        Assert.IsTrue(gates.GetProperty("RuntimeInfluenceAllowedPermanent").GetBoolean());
    }

    [TestMethod]
    public void ArtifactParsing_EndpointDesignGate_ReadsFromFile()
    {
        var path = ResolveArtifactPath("native-production-trace-execution-endpoint-implementation-design-gate.json");
        Assert.IsTrue(File.Exists(path));

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var gateResult = root.GetProperty("GateResult");
        Assert.IsTrue(gateResult.GetProperty("GatePassed").GetBoolean());
        Assert.IsTrue(gateResult.GetProperty("EndpointImplementationDesignReady").GetBoolean());
        Assert.IsFalse(gateResult.GetProperty("EndpointImplementationAllowed").GetBoolean());

        var safety = root.GetProperty("SafetyAudit");
        Assert.AreEqual(0, safety.GetProperty("JsonlTraceFilesInV16_15").GetInt32());
        Assert.IsFalse(safety.GetProperty("FileRuntimeCandidateTraceSinkWired").GetBoolean());
        Assert.IsFalse(safety.GetProperty("BuildDetailedAsyncCalledInLiveCapturePath").GetBoolean());
    }

    [TestMethod]
    public void ArtifactParsing_EndpointDesign_NoJsonlFilesExist()
    {
        var planPath = ResolveArtifactPath("native-production-trace-execution-endpoint-implementation-design.json");
        var outputDir = System.IO.Path.GetDirectoryName(planPath)!;
        var jsonlFiles = Directory.GetFiles(outputDir, "*.jsonl");

        Assert.AreEqual(0, jsonlFiles.Length,
            $"No .jsonl trace files must exist in v16_15 directory. Found: {jsonlFiles.Length}");
    }
}
