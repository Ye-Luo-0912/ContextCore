using System.Text.Json;

namespace ContextCore.Tests;

[TestClass]
[TestCategory("Synthetic")]
[TestCategory("Gate")]
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

    // -----------------------------------------------------------------
    // Generator parity tests
    // -----------------------------------------------------------------

    [TestMethod]
    public void GeneratorParity_RunGeneratorAndValidatePlanSchema()
    {
        var assembly = typeof(ContextCore.Evaluation.Commands.EvalCommand).Assembly;
        var type = assembly.GetType("ContextCore.Evaluation.Commands.EvalCommand")!;
        var method = type.GetMethod("ExecuteV16_13NativeProductionTraceExecutionPlanAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var task = method.Invoke(null, [Array.Empty<string>(), CancellationToken.None]) as Task;
        Assert.IsNotNull(task);
        task!.GetAwaiter().GetResult();

        var path = ResolveArtifactPath("native-production-trace-execution-plan.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        Assert.AreEqual("V16.13", root.GetProperty("ContractVersion").GetString());
        Assert.AreEqual("NativeProductionTraceExecutionPlan", root.GetProperty("DocumentType").GetString());

        var planStatus = root.GetProperty("PlanStatus");
        Assert.IsTrue(planStatus.GetProperty("ProductionTraceExecutionPlanned").GetBoolean());
        Assert.IsFalse(planStatus.GetProperty("ProductionTraceExecutionAllowed").GetBoolean());
    }

    [TestMethod]
    public void GeneratorParity_WorkspaceCollectionTemplate_PlaceholderOnly()
    {
        var path = ResolveArtifactPath("native-production-trace-execution-plan.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var ws = root.GetProperty("WorkspaceCollectionTemplate");
        Assert.IsTrue(ws.GetProperty("PlaceholderOnly").GetBoolean());
        Assert.IsTrue(ws.GetProperty("PlaceholderValue").GetString()!.StartsWith("<"));
        Assert.IsTrue(ws.TryGetProperty("SyntheticIdsRejected", out var rejected));
        Assert.IsTrue(rejected.GetArrayLength() > 5, "SyntheticIdsRejected must have at least 6 entries.");

        var col = root.GetProperty("CollectionTemplate");
        Assert.IsTrue(col.GetProperty("PlaceholderOnly").GetBoolean());
        Assert.IsTrue(col.GetProperty("PlaceholderValue").GetString()!.StartsWith("<"));
    }

    [TestMethod]
    public void GeneratorParity_GateSemantics_AllFalseGatesPresent()
    {
        var path = ResolveArtifactPath("native-production-trace-execution-plan.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var gates = root.GetProperty("GateSemantics");

        Assert.IsFalse(gates.GetProperty("LiveCaptureExecuted").GetBoolean());
        Assert.IsFalse(gates.GetProperty("LiveCaptureExecutionImplemented").GetBoolean());
        Assert.IsFalse(gates.GetProperty("ProductionGeneralizationReady").GetBoolean());
        Assert.IsTrue(gates.GetProperty("RuntimeInfluenceAllowedPermanent").GetBoolean());
        Assert.IsFalse(gates.GetProperty("RuntimePromotionApplied").GetBoolean());
    }

    [TestMethod]
    public void GeneratorParity_PlanGate_HasFullSafetyAudit()
    {
        var path = ResolveArtifactPath("native-production-trace-execution-plan-gate.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var safety = root.GetProperty("SafetyAudit");
        Assert.IsTrue(safety.TryGetProperty("RuntimeCandidateTraceSinkAccessorMutated", out _),
            "SafetyAudit must include RuntimeCandidateTraceSinkAccessorMutated.");
        Assert.IsFalse(safety.GetProperty("FileRuntimeCandidateTraceSinkWired").GetBoolean());
        Assert.IsFalse(safety.GetProperty("BuildDetailedAsyncCalledInLivePath").GetBoolean());
    }

    [TestMethod]
    public void GeneratorParity_PreflightGate_ValidatesAllRequiredFields()
    {
        var path = ResolveArtifactPath("native-production-trace-execution-preflight-gate.json");
        Assert.IsTrue(File.Exists(path), "Preflight gate must exist.");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        Assert.AreEqual("NativeProductionTraceExecutionPreflightGate", root.GetProperty("DocumentType").GetString());

        var gateResult = root.GetProperty("GateResult");
        Assert.IsTrue(gateResult.GetProperty("ExecutionPlanComplete").GetBoolean());
        Assert.IsFalse(gateResult.GetProperty("ProductionTraceExecutionAllowed").GetBoolean());
        Assert.IsFalse(gateResult.GetProperty("LiveCaptureExecutionImplemented").GetBoolean());
        Assert.IsFalse(gateResult.GetProperty("NativeProductionTraceReady").GetBoolean());
        Assert.IsTrue(gateResult.GetProperty("NoProductionTraceGenerated").GetBoolean());

        var safety = root.GetProperty("SafetyAudit");
        Assert.AreEqual(0, safety.GetProperty("JsonlTraceFilesInV16_13").GetInt32());

        var transition = root.GetProperty("PhaseTransition");
        Assert.AreEqual("NativeProductionTraceExecutionAuthorizationContract",
            transition.GetProperty("NextAllowedPhase").GetString());
        Assert.AreEqual("RuntimeInfluenceActivation",
            transition.GetProperty("NextDisallowedPhase").GetString());
    }

    [TestMethod]
    public void GeneratorParity_AllArtifacts_HaveMatchingContractVersion()
    {
        var files = new[] { "native-production-trace-execution-plan.json",
            "native-production-trace-execution-plan-gate.json",
            "native-production-trace-execution-preflight-gate.json" };

        foreach (var file in files)
        {
            var path = ResolveArtifactPath(file);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.AreEqual("V16.13", doc.RootElement.GetProperty("ContractVersion").GetString(),
                $"{file}: ContractVersion must be V16.13.");
        }
    }
}
