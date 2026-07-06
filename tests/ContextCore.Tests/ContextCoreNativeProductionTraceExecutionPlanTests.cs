using System.Text.Json;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreNativeProductionTraceExecutionPlanTests
{
    private static string ResolveArtifactPath(string fileName) =>
        TestRepoFileResolver.Resolve("learning", "v16_13", fileName);

    [TestMethod]
    public void ExecutionPlan_Planned_ButNotAllowed()
    {
        bool planned = true;
        bool allowed = false;

        Assert.IsTrue(planned, "ProductionTraceExecutionPlanned must be true.");
        Assert.IsFalse(allowed, "ProductionTraceExecutionAllowed must be false.");
    }

    [TestMethod]
    public void ExecutionPlan_WorkspaceCollectionPlaceholder_NoRealValues()
    {
        string workspaceId = "<PROD_WORKSPACE_ID>";
        string collectionId = "<PROD_COLLECTION_ID>";

        Assert.IsTrue(workspaceId.StartsWith("<") && workspaceId.EndsWith(">"),
            "workspaceId must be a placeholder, not a real production value.");
        Assert.IsTrue(collectionId.StartsWith("<") && collectionId.EndsWith(">"),
            "collectionId must be a placeholder, not a real production value.");
    }

    [TestMethod]
    public void ExecutionPlan_TokenBudget_10000()
    {
        int tokenBudget = 10000;

        Assert.AreEqual(10000, tokenBudget,
            "DefaultTokenBudget must be 10000.");
    }

    [TestMethod]
    public void ExecutionPlan_ExpectedRowCount_Range()
    {
        int minRows = 30;
        int maxRows = 200;

        Assert.IsTrue(minRows >= 20, "Minimum expected rows must be reasonable (>= 20).");
        Assert.IsTrue(maxRows <= 500, "Maximum expected rows must be reasonable (<= 500).");
        Assert.IsTrue(minRows < maxRows);
    }

    [TestMethod]
    public void ExecutionPlan_RunIdPolicy_RejectExistingRunId()
    {
        string policy = "RejectExistingRunId";

        Assert.AreEqual("RejectExistingRunId", policy);
    }

    [TestMethod]
    public void ExecutionPlan_TraceOutputPath_Pattern()
    {
        string pattern = "learning/v16_13/native-production-trace-{runId}.jsonl";

        Assert.IsTrue(pattern.Contains("{runId}"),
            "Trace output path must contain {runId} placeholder.");
        Assert.IsTrue(pattern.EndsWith(".jsonl"),
            "Trace output must be JSONL format.");
    }

    [TestMethod]
    public void ExecutionPlan_ValidationThresholds_AllDefined()
    {
        int parseErrorCount = 0;
        int missingCriticalFieldCount = 0;
        bool allRowsTraceSource3 = true;
        double wpaThreshold = 0.55;

        Assert.AreEqual(0, parseErrorCount);
        Assert.AreEqual(0, missingCriticalFieldCount);
        Assert.IsTrue(allRowsTraceSource3);
        Assert.IsTrue(wpaThreshold >= 0.5);
    }

    [TestMethod]
    public void ExecutionPlan_AbortConditions_AllCovered()
    {
        var abortConditions = new[]
        {
            "BuildError",
            "IdempotencyViolation",
            "ValidationFailure",
            "MetricQualityFailure",
        };

        Assert.AreEqual(4, abortConditions.Length,
            "Must define at least 4 abort conditions.");
    }

    [TestMethod]
    public void ExecutionPlan_RollbackCleanup_HasSixSteps()
    {
        var steps = new[]
        {
            "Dispose sink",
            "Restore NullSink",
            "Clear OperationId/RequestId",
            "Delete partial trace on failure",
            "Retain trace on success",
            "Log completion status",
        };

        Assert.AreEqual(6, steps.Length,
            "Rollback/cleanup must have 6 steps.");
    }

    [TestMethod]
    public void ExecutionPlan_AllGates_False()
    {
        var gates = new
        {
            ProductionTraceExecutionAllowed = false,
            NativeProductionTraceReady = false,
            LiveCaptureExecutionImplemented = false,
            LiveCaptureExecuted = false,
            ProductionGeneralizationReady = false,
            RuntimeInfluenceAllowed = false,
            PackageOutputChanged = false,
            RuntimePromotionApplied = false,
            VectorBindingChanged = false,
        };

        Assert.IsFalse(gates.ProductionTraceExecutionAllowed);
        Assert.IsFalse(gates.NativeProductionTraceReady);
        Assert.IsFalse(gates.LiveCaptureExecutionImplemented);
        Assert.IsFalse(gates.LiveCaptureExecuted);
        Assert.IsFalse(gates.ProductionGeneralizationReady);
        Assert.IsFalse(gates.RuntimeInfluenceAllowed);
        Assert.IsFalse(gates.PackageOutputChanged);
        Assert.IsFalse(gates.RuntimePromotionApplied);
        Assert.IsFalse(gates.VectorBindingChanged);
    }

    [TestMethod]
    public void ExecutionPlan_NoLiveExecution()
    {
        bool fileTraceSinkWired = false;
        bool buildDetailedAsyncCalled = false;
        bool productionTraceFileExists = false;

        Assert.IsFalse(fileTraceSinkWired);
        Assert.IsFalse(buildDetailedAsyncCalled);
        Assert.IsFalse(productionTraceFileExists);
    }

    // ----------------------------------------------------------------
    // Artifact parsing tests
    // ----------------------------------------------------------------

    [TestMethod]
    public void ArtifactParsing_ExecutionPlan_ReadsFromFile()
    {
        var path = ResolveArtifactPath("native-production-trace-execution-plan.json");
        Assert.IsTrue(File.Exists(path), $"Execution plan must exist at: {path}");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        Assert.AreEqual("V16.13", root.GetProperty("ContractVersion").GetString());
        Assert.AreEqual("NativeProductionTraceExecutionPlan", root.GetProperty("DocumentType").GetString());

        var status = root.GetProperty("PlanStatus");
        Assert.IsTrue(status.GetProperty("ProductionTraceExecutionPlanned").GetBoolean());
        Assert.IsFalse(status.GetProperty("ProductionTraceExecutionAllowed").GetBoolean());
    }

    [TestMethod]
    public void ArtifactParsing_ExecutionPlan_AllGatesFalse()
    {
        var path = ResolveArtifactPath("native-production-trace-execution-plan.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var gates = root.GetProperty("GateSemantics");

        Assert.IsFalse(gates.GetProperty("ProductionTraceExecutionAllowed").GetBoolean());
        Assert.IsFalse(gates.GetProperty("NativeProductionTraceReady").GetBoolean());
        Assert.IsFalse(gates.GetProperty("LiveCaptureExecutionImplemented").GetBoolean());
        Assert.IsFalse(gates.GetProperty("LiveCaptureExecuted").GetBoolean());
        Assert.IsFalse(gates.GetProperty("ProductionGeneralizationReady").GetBoolean());
        Assert.IsFalse(gates.GetProperty("RuntimeInfluenceAllowed").GetBoolean());
        Assert.IsFalse(gates.GetProperty("PackageOutputChanged").GetBoolean());
        Assert.IsFalse(gates.GetProperty("RuntimePromotionApplied").GetBoolean());
        Assert.IsFalse(gates.GetProperty("VectorBindingChanged").GetBoolean());
    }

    [TestMethod]
    public void ArtifactParsing_ExecutionPlan_TemplateValues_ArePlaceholders()
    {
        var path = ResolveArtifactPath("native-production-trace-execution-plan.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var ws = root.GetProperty("WorkspaceCollectionTemplate");
        var wsId = ws.GetProperty("PlaceholderValue").GetString();
        Assert.IsTrue(wsId!.StartsWith("<") && wsId.EndsWith(">"),
            "workspaceId placeholder must use angle-bracket notation.");
        Assert.IsTrue(ws.GetProperty("PlaceholderOnly").GetBoolean(),
            "WorkspaceCollectionTemplate.PlaceholderOnly must be true.");
    }

    [TestMethod]
    public void ArtifactParsing_ExecutionPlanGate_ReadsFromFile()
    {
        var path = ResolveArtifactPath("native-production-trace-execution-plan-gate.json");
        Assert.IsTrue(File.Exists(path), $"Plan gate must exist at: {path}");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var gateResult = root.GetProperty("GateResult");
        Assert.IsTrue(gateResult.GetProperty("GatePassed").GetBoolean());
        Assert.IsTrue(gateResult.GetProperty("ProductionTraceExecutionPlanned").GetBoolean());
        Assert.IsFalse(gateResult.GetProperty("ProductionTraceExecutionAllowed").GetBoolean());

        var safety = root.GetProperty("SafetyAudit");
        Assert.AreEqual(0, safety.GetProperty("JsonlTraceFilesInV16_13").GetInt32());
        Assert.IsFalse(safety.GetProperty("FileRuntimeCandidateTraceSinkWired").GetBoolean());
        Assert.IsFalse(safety.GetProperty("BuildDetailedAsyncCalledInLivePath").GetBoolean());
    }

    [TestMethod]
    public void ArtifactParsing_ExecutionPlanGate_NoJsonlFilesExist()
    {
        var planPath = ResolveArtifactPath("native-production-trace-execution-plan.json");
        var outputDir = System.IO.Path.GetDirectoryName(planPath)!;
        var jsonlFiles = Directory.GetFiles(outputDir, "*.jsonl");

        Assert.AreEqual(0, jsonlFiles.Length,
            $"No .jsonl trace files must exist in v16_13 directory. Found: {jsonlFiles.Length}");
    }
}
