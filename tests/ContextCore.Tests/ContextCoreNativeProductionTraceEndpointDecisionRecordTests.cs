using System.Text.Json;

namespace ContextCore.Tests;

[TestClass]
[TestCategory("Synthetic")]
[TestCategory("Gate")]
public class ContextCoreNativeProductionTraceEndpointDecisionRecordTests
{
    private static string ResolveArtifactPath(string fileName) =>
        TestRepoFileResolver.Resolve("learning", "v16_20", fileName);

    // 清理 bin 目录下上一轮测试残留的生成器输出，避免测试顺序不确定导致读到旧 artifact。
    // GeneratorParity 测试运行时会重新生成 fresh artifact；其他测试在 bin 无残留时
    // 通过 TestRepoFileResolver 向上回溯读取仓库提交的 artifact。
    [ClassInitialize]
    public static void ClassInitialize(TestContext _)
    {
        var binLearningDir = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "learning", "v16_20");
        if (System.IO.Directory.Exists(binLearningDir))
        {
            System.IO.Directory.Delete(binLearningDir, recursive: true);
        }
    }

    [TestMethod]
    public void DecisionRecord_AuthorizationDecision_NoGo()
    {
        var path = ResolveArtifactPath("native-production-trace-endpoint-authorization-decision-record.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.AreEqual("NoGo", doc.RootElement.GetProperty("AuthorizationDecision").GetString());
        Assert.IsFalse(doc.RootElement.GetProperty("GoDecision").GetBoolean());
        Assert.AreEqual("MissingExplicitHumanApprovalArtifact", doc.RootElement.GetProperty("NoGoReason").GetString());
    }

    [TestMethod]
    public void DecisionRecord_AllFlagsFalse()
    {
        var path = ResolveArtifactPath("native-production-trace-endpoint-authorization-decision-record.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var st = doc.RootElement.GetProperty("CurrentStateFlags");
        Assert.IsFalse(st.GetProperty("EndpointImplementationFinalApproved").GetBoolean());
        Assert.IsFalse(st.GetProperty("EndpointImplementationAllowed").GetBoolean());
        Assert.IsFalse(st.GetProperty("EndpointImplemented").GetBoolean());
        Assert.IsFalse(st.GetProperty("RuntimeInfluenceAllowed").GetBoolean());
    }

    [TestMethod]
    public void NoGoEnforcementPolicy_ElevenBlockedOperations()
    {
        var path = ResolveArtifactPath("native-production-trace-endpoint-no-go-enforcement-policy.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var ops = doc.RootElement.GetProperty("BlockedOperations");
        Assert.IsTrue(ops.GetArrayLength() >= 11);
        foreach (var op in ops.EnumerateArray())
            Assert.IsTrue(op.GetProperty("Blocked").GetBoolean());
    }

    [TestMethod]
    public void ApprovalArtifactSchema_HasFourteenRequiredFields()
    {
        var path = ResolveArtifactPath("native-production-trace-endpoint-approval-artifact-schema.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var fields = doc.RootElement.GetProperty("RequiredFields");
        Assert.AreEqual(14, fields.GetArrayLength());
        Assert.IsFalse(doc.RootElement.GetProperty("SchemaExists").GetBoolean());
    }

    [TestMethod]
    public void StaticScanProtocol_NineItems_AllPassed()
    {
        var path = ResolveArtifactPath("native-production-trace-endpoint-pre-implementation-static-scan-protocol.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var items = doc.RootElement.GetProperty("ScanItems");
        Assert.AreEqual(9, items.GetArrayLength());
        foreach (var item in items.EnumerateArray())
            Assert.IsTrue(item.GetProperty("Passed").GetBoolean());
    }

    [TestMethod]
    public void GoTransitionChecklist_NotReady_OneOfSevenSatisfied()
    {
        var path = ResolveArtifactPath("native-production-trace-endpoint-go-transition-checklist.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.AreEqual("NotReadyForGo", doc.RootElement.GetProperty("ChecklistStatus").GetString());
        Assert.IsFalse(doc.RootElement.GetProperty("GoTransitionPossible").GetBoolean());
        var items = doc.RootElement.GetProperty("GoTransitionBlockedBy");
        Assert.AreEqual(7, items.GetArrayLength());
        int satisfied = 0;
        foreach (var i in items.EnumerateArray())
            if (i.GetProperty("CurrentlySatisfied").GetBoolean()) satisfied++;
        Assert.AreEqual(1, satisfied, "Only static scan should be pre-satisfied.");
    }

    [TestMethod]
    public void V16_20Gate_ReadsCorrectly()
    {
        var path = ResolveArtifactPath("native-production-trace-endpoint-v16-20-gate.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var gr = doc.RootElement.GetProperty("GateResult");
        Assert.IsTrue(gr.GetProperty("GatePassed").GetBoolean());
        Assert.AreEqual("NoGo", gr.GetProperty("AuthorizationDecision").GetString());
        Assert.IsFalse(gr.GetProperty("GoDecision").GetBoolean());
        var pt = doc.RootElement.GetProperty("PhaseTransition");
        Assert.AreEqual("NativeProductionTraceEndpointExplicitApprovalArtifactReview", pt.GetProperty("NextAllowedPhase").GetString());
    }

    [TestMethod]
    public void ChainConsistency_NoJsonlAnywhere()
    {
        var v16_20Dir = System.IO.Path.GetDirectoryName(ResolveArtifactPath("native-production-trace-endpoint-authorization-decision-record.json"))!;
        var learningDir = System.IO.Path.GetDirectoryName(v16_20Dir)!;
        foreach (var d in new[] { "v16_14", "v16_15", "v16_16", "v16_17", "v16_18", "v16_19", "v16_20" })
            Assert.AreEqual(0, Directory.GetFiles(System.IO.Path.Combine(learningDir, d), "*.jsonl").Length,
                $"{d}: must have zero .jsonl trace files.");
    }

    [TestMethod]
    public void GeneratorParity_RunGeneratorAndValidateAllArtifacts()
    {
        var assembly = typeof(ContextCore.Evaluation.Commands.EvalCommand).Assembly;
        var type = assembly.GetType("ContextCore.Evaluation.Commands.EvalCommand")!;
        var method = type.GetMethod("ExecuteV16_20NativeProductionTraceEndpointDecisionRecordAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var task = method.Invoke(null, [Array.Empty<string>(), CancellationToken.None]) as Task;
        Assert.IsNotNull(task);
        task!.GetAwaiter().GetResult();

        var files = new[] { "native-production-trace-endpoint-authorization-decision-record.json",
            "native-production-trace-endpoint-no-go-enforcement-policy.json",
            "native-production-trace-endpoint-approval-artifact-schema.json",
            "native-production-trace-endpoint-pre-implementation-static-scan-protocol.json",
            "native-production-trace-endpoint-go-transition-checklist.json",
            "native-production-trace-endpoint-v16-20-gate.json" };
        foreach (var f in files)
        {
            var p = ResolveArtifactPath(f);
            Assert.IsTrue(File.Exists(p), $"Generator must produce {f}");
            using var doc = JsonDocument.Parse(File.ReadAllText(p));
            Assert.AreEqual("V16.20", doc.RootElement.GetProperty("ContractVersion").GetString());
        }
    }
}
