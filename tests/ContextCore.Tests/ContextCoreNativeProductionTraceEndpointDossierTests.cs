using System.Text.Json;

namespace ContextCore.Tests;

[TestClass]
[TestCategory("Synthetic")]
[TestCategory("Gate")]
public class ContextCoreNativeProductionTraceEndpointDossierTests
{
    private static string ResolveArtifactPath(string fileName) =>
        TestRepoFileResolver.Resolve("learning", "v16_19", fileName);

    // Dossier tests
    [TestMethod]
    public void Dossier_GoDecision_False()
    {
        var path = ResolveArtifactPath("native-production-trace-endpoint-authorization-dossier.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var summary = doc.RootElement.GetProperty("DossierSummary");
        Assert.IsFalse(summary.GetProperty("GoDecision").GetBoolean());
        Assert.AreEqual("FinalApprovedFalse", summary.GetProperty("NoGoReason").GetString());
    }

    [TestMethod]
    public void Dossier_ChainSummary_FivePhases_AllReady_NoneAuthorized()
    {
        var path = ResolveArtifactPath("native-production-trace-endpoint-authorization-dossier.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var chain = doc.RootElement.GetProperty("ChainSummary");
        Assert.AreEqual(5, chain.GetArrayLength());
        foreach (var c in chain.EnumerateArray())
        {
            Assert.IsTrue(c.GetProperty("Ready").GetBoolean());
            Assert.IsFalse(c.TryGetProperty("Authorized", out var a) && a.GetBoolean());
            Assert.IsFalse(c.TryGetProperty("Approved", out var ap) && ap.GetBoolean());
            Assert.IsFalse(c.TryGetProperty("Implemented", out var im) && im.GetBoolean());
            Assert.IsFalse(c.TryGetProperty("Allowed", out var al) && al.GetBoolean());
        }
    }

    [TestMethod]
    public void Dossier_CrossChainInvariants_AllTrue()
    {
        var path = ResolveArtifactPath("native-production-trace-endpoint-authorization-dossier.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var inv = doc.RootElement.GetProperty("CrossChainInvariants");
        Assert.IsTrue(inv.GetProperty("NoImplementationCodeWritten").GetBoolean());
        Assert.IsTrue(inv.GetProperty("NoProductionTraceJsonl").GetBoolean());
        Assert.IsTrue(inv.GetProperty("AllRuntimeInfluenceAllowed_False").GetBoolean());
    }

    // Go/No-Go protocol tests
    [TestMethod]
    public void GoNoGo_GoDecision_False_NoGoReason_FinalApprovedFalse()
    {
        var path = ResolveArtifactPath("native-production-trace-endpoint-go-no-go-protocol.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.IsFalse(doc.RootElement.GetProperty("GoDecision").GetBoolean());
        Assert.AreEqual("FinalApprovedFalse", doc.RootElement.GetProperty("NoGoReason").GetString());
    }

    [TestMethod]
    public void GoNoGo_GoConditions_AtLeastNine()
    {
        var path = ResolveArtifactPath("native-production-trace-endpoint-go-no-go-protocol.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.IsTrue(doc.RootElement.GetProperty("GoConditions").GetArrayLength() >= 9);
    }

    [TestMethod]
    public void GoNoGo_NoGoConditions_AtLeastEight()
    {
        var path = ResolveArtifactPath("native-production-trace-endpoint-go-no-go-protocol.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.IsTrue(doc.RootElement.GetProperty("NoGoConditions").GetArrayLength() >= 8);
    }

    // Risk matrix tests
    [TestMethod]
    public void RiskMatrix_TwelveRisks_AllMitigated()
    {
        var path = ResolveArtifactPath("native-production-trace-endpoint-risk-matrix.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var risks = doc.RootElement.GetProperty("Risks");
        Assert.IsTrue(risks.GetArrayLength() >= 12);
        foreach (var r in risks.EnumerateArray())
            Assert.AreEqual("Mitigated", r.GetProperty("Status").GetString());
    }

    [TestMethod]
    public void RiskMatrix_ResidualRisk_Low()
    {
        var path = ResolveArtifactPath("native-production-trace-endpoint-risk-matrix.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.AreEqual("Low", doc.RootElement.GetProperty("RiskSummary").GetProperty("ResidualRiskLevel").GetString());
    }

    // Handoff ledger tests
    [TestMethod]
    public void HandoffLedger_ImplementationNotAuthorized()
    {
        var path = ResolveArtifactPath("native-production-trace-endpoint-handoff-ledger.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var st = doc.RootElement.GetProperty("CurrentState");
        Assert.IsFalse(st.GetProperty("ImplementationAllowed").GetBoolean());
        Assert.IsFalse(st.GetProperty("EndpointImplemented").GetBoolean());
        Assert.AreEqual(0, st.GetProperty("ApprovedFiles").GetArrayLength());
    }

    [TestMethod]
    public void HandoffLedger_ForbiddenChanges_AtLeastSeven()
    {
        var path = ResolveArtifactPath("native-production-trace-endpoint-handoff-ledger.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.IsTrue(doc.RootElement.GetProperty("ForbiddenChanges").GetArrayLength() >= 7);
    }

    // Dossier gate tests
    [TestMethod]
    public void DossierGate_ReadsAllFlagsCorrectly()
    {
        var path = ResolveArtifactPath("native-production-trace-endpoint-dossier-gate.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var gr = doc.RootElement.GetProperty("GateResult");
        Assert.IsTrue(gr.GetProperty("GatePassed").GetBoolean());
        Assert.IsTrue(gr.GetProperty("AuthorizationDossierReady").GetBoolean());
        Assert.IsTrue(gr.GetProperty("GoNoGoProtocolReady").GetBoolean());
        Assert.IsTrue(gr.GetProperty("RiskMatrixReady").GetBoolean());
        Assert.IsTrue(gr.GetProperty("HandoffLedgerReady").GetBoolean());
        Assert.IsFalse(gr.GetProperty("GoDecision").GetBoolean());
        Assert.IsFalse(gr.GetProperty("EndpointImplementationFinalApproved").GetBoolean());
    }

    [TestMethod]
    public void DossierGate_PhaseTransition_NextAllowedAndDisallowed()
    {
        var path = ResolveArtifactPath("native-production-trace-endpoint-dossier-gate.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var pt = doc.RootElement.GetProperty("PhaseTransition");
        Assert.AreEqual("NativeProductionTraceEndpointImplementationAuthorizationDecision",
            pt.GetProperty("NextAllowedPhase").GetString());
        Assert.AreEqual("RuntimeInfluenceActivation",
            pt.GetProperty("NextDisallowedPhase").GetString());
    }

    // Cross-version chain tests
    [TestMethod]
    public void ChainConsistency_AllV16_14_Through_V16_19_NoImplementation()
    {
        var v16_19Dir = System.IO.Path.GetDirectoryName(
            ResolveArtifactPath("native-production-trace-endpoint-authorization-dossier.json"))!;
        var learningDir = System.IO.Path.GetDirectoryName(v16_19Dir)!;

        foreach (var d in new[] { "v16_14", "v16_15", "v16_16", "v16_17", "v16_18", "v16_19" })
        {
            var dirPath = System.IO.Path.Combine(learningDir, d);
            var jsonl = Directory.GetFiles(dirPath, "*.jsonl");
            Assert.AreEqual(0, jsonl.Length, $"{d}: must have zero .jsonl trace files.");
        }
    }

    // Generator parity test
    [TestMethod]
    public void GeneratorParity_RunGeneratorAndValidateAllArtifacts()
    {
        var assembly = typeof(ContextCore.Evaluation.Commands.EvalCommand).Assembly;
        var type = assembly.GetType("ContextCore.Evaluation.Commands.EvalCommand")!;
        var method = type.GetMethod("ExecuteV16_19NativeProductionTraceEndpointDossierAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var task = method.Invoke(null, [Array.Empty<string>(), CancellationToken.None]) as Task;
        Assert.IsNotNull(task);
        task!.GetAwaiter().GetResult();

        var files = new[] { "native-production-trace-endpoint-authorization-dossier.json",
            "native-production-trace-endpoint-go-no-go-protocol.json",
            "native-production-trace-endpoint-risk-matrix.json",
            "native-production-trace-endpoint-handoff-ledger.json",
            "native-production-trace-endpoint-dossier-gate.json" };

        foreach (var file in files)
        {
            var path = ResolveArtifactPath(file);
            Assert.IsTrue(File.Exists(path), $"Generator must produce {file}");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.AreEqual("V16.19", doc.RootElement.GetProperty("ContractVersion").GetString(),
                $"{file}: ContractVersion must be V16.19.");
        }
    }
}
