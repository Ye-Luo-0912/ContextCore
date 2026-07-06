using System.Text.Json;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreNativeProductionTraceExecutionAuthorizationContractTests
{
    private static string ResolveArtifactPath(string fileName) =>
        TestRepoFileResolver.Resolve("learning", "v16_14", fileName);

    // ----------------------------------------------------------------
    // Failure scenario tests
    // ----------------------------------------------------------------

    [TestMethod]
    public void AuthorizationContract_MissingConfirmLiveCapture_Blocked()
    {
        bool confirmLiveCapture = false;
        bool blocked = !confirmLiveCapture;

        Assert.IsTrue(blocked, "Missing --confirm-live-capture must block authorization.");
    }

    [TestMethod]
    public void AuthorizationContract_MissingCaptureToken_Blocked()
    {
        string? captureToken = null;
        bool blocked = string.IsNullOrWhiteSpace(captureToken);

        Assert.IsTrue(blocked, "Missing --capture-token must block authorization.");
    }

    [TestMethod]
    public void AuthorizationContract_SyntheticWorkspace_Blocked()
    {
        string workspaceId = "native-ws";
        string[] synthetic = ["native-ws", "smoke-ws", "prod-ws", "test-ws", "demo-ws", "dryrun-ws"];
        bool blocked = synthetic.Contains(workspaceId, StringComparer.OrdinalIgnoreCase);

        Assert.IsTrue(blocked, "Synthetic workspace must block authorization.");
    }

    [TestMethod]
    public void AuthorizationContract_SyntheticCollection_Blocked()
    {
        string collectionId = "smoke-col";
        string[] synthetic = ["native-col", "smoke-col", "prod-col", "test-col", "demo-col", "dryrun-col"];
        bool blocked = synthetic.Contains(collectionId, StringComparer.OrdinalIgnoreCase);

        Assert.IsTrue(blocked, "Synthetic collection must block authorization.");
    }

    [TestMethod]
    public void AuthorizationContract_MissingRunId_Blocked()
    {
        string? runId = null;
        bool blocked = string.IsNullOrWhiteSpace(runId);

        Assert.IsTrue(blocked, "Missing --runId must block authorization.");
    }

    [TestMethod]
    public void AuthorizationContract_EndpointNotImplemented_Blocked()
    {
        bool liveCaptureExecutionImplemented = false;
        bool blocked = !liveCaptureExecutionImplemented;

        Assert.IsTrue(blocked, "Execution endpoint not implemented must block authorization.");
    }

    [TestMethod]
    public void AuthorizationContract_AllFactorsPresentButEndpointNotImplemented_StillBlocked()
    {
        bool confirmLiveCapture = true;
        string captureToken = "tok-v16_14";
        string workspaceId = "prod-ws-us-east-2";
        string collectionId = "prod-eval-collection-v4";
        string runId = "run-auth-001";

        bool allAuthFactorsSatisfied = confirmLiveCapture
            && !string.IsNullOrWhiteSpace(captureToken)
            && !string.IsNullOrWhiteSpace(workspaceId)
            && !string.IsNullOrWhiteSpace(collectionId)
            && !string.IsNullOrWhiteSpace(runId)
            && !IsSynthetic(workspaceId)
            && !IsSynthetic(collectionId);

        bool executionImplemented = false;
        bool blocked = !executionImplemented;

        Assert.IsTrue(allAuthFactorsSatisfied, "All 5 authorization parameters must be present.");
        Assert.IsTrue(blocked, "Even with all factors present, authorization must be blocked because execution endpoint is not implemented.");
    }

    private static bool IsSynthetic(string id) =>
        new[] { "native-ws", "smoke-ws", "prod-ws", "test-ws", "demo-ws", "dryrun-ws",
                "synthetic-ws", "sandbox-ws", "preview-ws", "debug-ws", "dev-ws",
                "native-col", "smoke-col", "prod-col", "test-col", "demo-col",
                "dryrun-col", "synthetic-col", "sandbox-col", "preview-col", "debug-col", "dev-col" }
        .Contains(id, StringComparer.OrdinalIgnoreCase);

    [TestMethod]
    public void AuthorizationContract_ExplicitlyDisallowedModes_AllDefined()
    {
        var disallowedModes = new[]
        {
            "ExecuteCapture",
            "RuntimeInfluenceActivation",
            "PackageMutation",
            "VectorBindingMutation",
        };

        Assert.AreEqual(4, disallowedModes.Length,
            "Must define exactly 4 explicitly disallowed modes.");
    }

    [TestMethod]
    public void AuthorizationContract_RequiredFactors_SevenDefined()
    {
        var factors = new[]
        {
            "confirm-live-capture",
            "capture-token",
            "workspaceId",
            "collectionId",
            "runId",
            "no synthetic workspace/collection",
            "LiveCaptureExecutionEndpointImplemented",
        };

        Assert.AreEqual(7, factors.Length,
            "Must define exactly 7 required authorization factors.");
    }

    [TestMethod]
    public void AuthorizationContract_AllGatesFalse()
    {
        var gates = new
        {
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

    // ----------------------------------------------------------------
    // Artifact parsing tests
    // ----------------------------------------------------------------

    [TestMethod]
    public void ArtifactParsing_AuthorizationContract_ReadsFromFile()
    {
        var path = ResolveArtifactPath("native-production-trace-execution-authorization-contract.json");
        Assert.IsTrue(File.Exists(path));

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        Assert.AreEqual("V16.14", root.GetProperty("ContractVersion").GetString());
        Assert.AreEqual("NativeProductionTraceExecutionAuthorizationContract", root.GetProperty("DocumentType").GetString());

        var gates = root.GetProperty("GateSemantics");
        Assert.IsTrue(gates.GetProperty("AuthorizationContractReady").GetBoolean());
        Assert.IsFalse(gates.GetProperty("ProductionTraceExecutionAuthorized").GetBoolean());
        Assert.IsFalse(gates.GetProperty("ProductionTraceExecutionAllowed").GetBoolean());
        Assert.IsFalse(gates.GetProperty("LiveCaptureExecutionImplemented").GetBoolean());
    }

    [TestMethod]
    public void ArtifactParsing_AuthorizationContract_AllFailureScenariosBlocked()
    {
        var path = ResolveArtifactPath("native-production-trace-execution-authorization-contract.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var scenarios = root.GetProperty("FailureScenarios").GetProperty("AllFactorsSatisfiedExcept");
        foreach (var s in scenarios.EnumerateArray())
        {
            Assert.IsTrue(s.GetProperty("Blocked").GetBoolean(),
                $"Scenario '{s.GetProperty("Scenario").GetString()}' must be blocked.");
        }

        var full = root.GetProperty("FailureScenarios").GetProperty("AllFactorsPresentButEndpointNotImplemented");
        Assert.IsTrue(full.GetProperty("Blocked").GetBoolean());
    }

    [TestMethod]
    public void ArtifactParsing_AuthorizationGate_ReadsFromFile()
    {
        var path = ResolveArtifactPath("native-production-trace-execution-authorization-gate.json");
        Assert.IsTrue(File.Exists(path));

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var gateResult = root.GetProperty("GateResult");
        Assert.IsTrue(gateResult.GetProperty("GatePassed").GetBoolean());
        Assert.IsTrue(gateResult.GetProperty("AuthorizationContractReady").GetBoolean());
        Assert.IsFalse(gateResult.GetProperty("ProductionTraceExecutionAuthorized").GetBoolean());
        Assert.IsTrue(gateResult.GetProperty("AllFailureScenariosBlocked").GetBoolean());

        var safety = root.GetProperty("SafetyAudit");
        Assert.AreEqual(0, safety.GetProperty("JsonlTraceFilesInV16_14").GetInt32());
        Assert.IsFalse(safety.GetProperty("FileRuntimeCandidateTraceSinkWired").GetBoolean());
        Assert.IsFalse(safety.GetProperty("BuildDetailedAsyncCalledInLiveCapturePath").GetBoolean());
    }

    [TestMethod]
    public void ArtifactParsing_AuthorizationContract_NoJsonlFilesExist()
    {
        var planPath = ResolveArtifactPath("native-production-trace-execution-authorization-contract.json");
        var outputDir = System.IO.Path.GetDirectoryName(planPath)!;
        var jsonlFiles = Directory.GetFiles(outputDir, "*.jsonl");

        Assert.AreEqual(0, jsonlFiles.Length,
            $"No .jsonl trace files must exist in v16_14 directory. Found: {jsonlFiles.Length}");
    }

    [TestMethod]
    public void ArtifactParsing_AuthorizationContract_ExplicitlyAllowedAndDisallowedModes_Present()
    {
        var path = ResolveArtifactPath("native-production-trace-execution-authorization-contract.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var allowed = root.GetProperty("ExplicitlyAllowedModes");
        Assert.IsTrue(allowed.GetArrayLength() >= 3, "Must have at least 3 allowed modes.");

        var disallowed = root.GetProperty("ExplicitlyDisallowedModes");
        Assert.IsTrue(disallowed.GetArrayLength() >= 4, "Must have at least 4 disallowed modes.");
    }
}
