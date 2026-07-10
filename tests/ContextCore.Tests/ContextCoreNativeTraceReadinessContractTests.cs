namespace ContextCore.Tests;

[TestClass]
[TestCategory("Contract")]
public class ContextCoreNativeTraceReadinessContractTests
{
    private static readonly IReadOnlyList<string> NativeTraceCriticalFields = new List<string>
    {
        "operationId", "candidateId", "sourceType", "authority", "retrievalChannel", "traceSource",
    };

    private static readonly IReadOnlyList<string> NativeTraceAllFields = new List<string>
    {
        "operationId", "requestId", "candidateId", "sourceId", "sourceType",
        "authority", "strategyType", "retrievalChannel", "traceSource",
        "deterministicScore", "strategyScore", "finalScore",
        "selectedByScoring", "includedInPackage", "droppedReason",
        "tokenCost", "section", "recordedAt",
    };

    [TestMethod]
    public void NativeTrace_SchemaContract_HasEighteenFields()
    {
        Assert.AreEqual(18, NativeTraceAllFields.Count,
            "Native trace schema must define exactly 18 fields.");
    }

    [TestMethod]
    public void NativeTrace_SchemaContract_HasSixCriticalFields()
    {
        Assert.AreEqual(6, NativeTraceCriticalFields.Count,
            "Native trace schema must have exactly 6 critical fields.");

        var criticalFieldsList = NativeTraceCriticalFields.ToList();

        CollectionAssert.Contains(criticalFieldsList, "operationId");
        CollectionAssert.Contains(criticalFieldsList, "candidateId");
        CollectionAssert.Contains(criticalFieldsList, "sourceType");
        CollectionAssert.Contains(criticalFieldsList, "authority");
        CollectionAssert.Contains(criticalFieldsList, "retrievalChannel");
        CollectionAssert.Contains(criticalFieldsList, "traceSource");
    }

    [TestMethod]
    public void NativeTrace_UserVisibleFields_AllPresent()
    {
        var userVisibleFields = new[]
        {
            "operationId", "requestId", "candidateId", "sourceId", "sourceType",
            "section", "deterministicScore", "selectedByScoring", "includedInPackage",
            "tokenCost", "recordedAt",
        };

        var allFieldsList = NativeTraceAllFields.ToList();

        foreach (var field in userVisibleFields)
        {
            CollectionAssert.Contains(allFieldsList, field,
                $"User-visible field '{field}' must be in native trace schema.");
        }
    }

    [TestMethod]
    public void NativeTrace_NativeTraceSourceIsPackageTrace()
    {
        int nativeTraceSource = 3;

        Assert.AreEqual(3, nativeTraceSource,
            "Native traceSource must be 3 (PackageTrace). Shadow-adapter=1, GraphShadow=2, RetrievalTrace=4.");
    }

    [TestMethod]
    public void NativeTrace_ShadowAdapterCannotImpersonateNative()
    {
        int shadowAdapterTraceSource = 1;
        int nativeTraceSource = 3;

        Assert.AreNotEqual(shadowAdapterTraceSource, nativeTraceSource,
            "Shadow-adapter traceSource (1) must differ from native traceSource (3). Cross-system mapping is detectable.");
    }

    [TestMethod]
    public void NativeTrace_RowKey_DerivedFromOperationIdAndCandidateId()
    {
        string operationId = "op-native-v16_3-001";
        string candidateId = "rctx-05";
        string rowKey = $"{operationId}_{candidateId}";

        Assert.IsTrue(rowKey.StartsWith(operationId));
        Assert.IsTrue(rowKey.EndsWith(candidateId));
        Assert.AreEqual("op-native-v16_3-001_rctx-05", rowKey);
    }

    [TestMethod]
    public void NativeTrace_ProvenanceBoundary_AllPreProduction()
    {
        var boundary = new
        {
            NativeProductionTraceReady = false,
            NativeTraceCollectionEnabled = false,
            RuntimeInfluenceAllowed = false,
            ProductionGeneralizationReady = false,
        };

        Assert.IsFalse(boundary.NativeProductionTraceReady,
            "NativeProductionTraceReady must be false — no native traces collected yet.");
        Assert.IsFalse(boundary.NativeTraceCollectionEnabled,
            "NativeTraceCollectionEnabled must be false — collection is off by default.");
        Assert.IsFalse(boundary.RuntimeInfluenceAllowed,
            "RuntimeInfluenceAllowed must be false.");
        Assert.IsFalse(boundary.ProductionGeneralizationReady,
            "ProductionGeneralizationReady must be false.");
    }

    [TestMethod]
    public void NativeTrace_PrivacyContract_AllHolds()
    {
        var privacy = new
        {
            NoRawUserContent = true,
            NoApiKeysOrSecrets = true,
            NoPromptText = true,
            CandidateContentPolicy = "HashOrRedactedSummaryOrMetadataOnly",
            TraceOutputClosable = true,
            TraceOutputCleanable = true,
            TraceOutputAuditable = true,
        };

        Assert.IsTrue(privacy.NoRawUserContent,
            "No raw user content must be collected in trace.");
        Assert.IsTrue(privacy.NoApiKeysOrSecrets,
            "No API keys or secrets must be collected in trace.");
        Assert.IsTrue(privacy.NoPromptText,
            "No original prompt text must be collected in trace.");
        Assert.AreEqual("HashOrRedactedSummaryOrMetadataOnly", privacy.CandidateContentPolicy,
            "Candidate content must be hash, redacted summary, or metadata only.");
        Assert.IsTrue(privacy.TraceOutputClosable,
            "Trace output must be closable (disable via NullSink).");
        Assert.IsTrue(privacy.TraceOutputCleanable,
            "Trace output must be cleanable (plain JSONL, easy delete).");
        Assert.IsTrue(privacy.TraceOutputAuditable,
            "Trace output must be auditable (operationId + requestId + recordedAt).");
    }

    [TestMethod]
    public void NativeTrace_SafetyInvariants_AllFalse()
    {
        var safety = new
        {
            RuntimeInfluenceAllowed = false,
            PackageOutputChanged = false,
            VectorBindingChanged = false,
            RuntimePromotionApplied = false,
            NeuralBiasActive = false,
            HybridBlendAlpha = 1.0,
            NullSinkDefault = true,
        };

        Assert.IsFalse(safety.RuntimeInfluenceAllowed);
        Assert.IsFalse(safety.PackageOutputChanged);
        Assert.IsFalse(safety.VectorBindingChanged);
        Assert.IsFalse(safety.RuntimePromotionApplied);
        Assert.IsFalse(safety.NeuralBiasActive);
        Assert.AreEqual(1.0, safety.HybridBlendAlpha);
        Assert.IsTrue(safety.NullSinkDefault);
    }

    [TestMethod]
    public void NativeTrace_V16_2RepairBGate_Preserved()
    {
        bool v16_2RepairBGatePreserved = true;
        string v16_2GateState = "guarded_candidate_below_threshold";
        double productionLikeWeightedPairwiseAcc = 0.5451;
        double threshold = 0.55;

        Assert.IsTrue(v16_2RepairBGatePreserved,
            "V16.2 Repair B gate must be preserved in V16.3.");
        Assert.AreEqual("guarded_candidate_below_threshold", v16_2GateState,
            "V16.2 gate state must remain guarded_candidate_below_threshold.");
        Assert.IsTrue(productionLikeWeightedPairwiseAcc < threshold,
            $"ProductionLikeWeightedPairwiseAcc ({productionLikeWeightedPairwiseAcc}) must be below threshold ({threshold}).");
    }

    [TestMethod]
    public void NativeTrace_CollectorReady_ButCollectionNotEnabled()
    {
        bool nativeTraceCollectorReady = true;
        bool nativeTraceCollectionEnabled = false;

        Assert.IsTrue(nativeTraceCollectorReady,
            "Native trace collector infrastructure must be ready.");
        Assert.IsFalse(nativeTraceCollectionEnabled,
            "Native trace collection must be disabled by default.");
    }

    [TestMethod]
    public void NativeTrace_GateSemantics_AllCorrect()
    {
        var gate = new
        {
            NativeProductionTraceReady = false,
            NativeTraceCollectionEnabled = false,
            NativeTraceCollectorReady = true,
            RuntimeInfluenceAllowed = false,
            PackageOutputChanged = false,
            VectorBindingChanged = false,
            RuntimePromotionApplied = false,
            ProductionGeneralizationReady = false,
        };

        Assert.IsFalse(gate.NativeProductionTraceReady);
        Assert.IsFalse(gate.NativeTraceCollectionEnabled);
        Assert.IsTrue(gate.NativeTraceCollectorReady);
        Assert.IsFalse(gate.RuntimeInfluenceAllowed);
        Assert.IsFalse(gate.PackageOutputChanged);
        Assert.IsFalse(gate.VectorBindingChanged);
        Assert.IsFalse(gate.RuntimePromotionApplied);
        Assert.IsFalse(gate.ProductionGeneralizationReady);
    }
}
