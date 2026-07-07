using System.Text.Json;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreNativeProductionTraceEndpointV16_21Tests
{
    private static string ResolveArtifactPath(string fileName) =>
        TestRepoFileResolver.Resolve("learning", "v16_21", fileName);

    [TestMethod]
    public void EnforcementValidation_ElevenOperations_ZeroViolations()
    {
        var path = ResolveArtifactPath("native-production-trace-endpoint-no-go-enforcement-validation.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var ops = doc.RootElement.GetProperty("ValidatedOperations");
        Assert.AreEqual(11, ops.GetArrayLength());
        foreach (var op in ops.EnumerateArray())
        {
            Assert.IsTrue(op.GetProperty("PolicyBlocked").GetBoolean());
            Assert.IsFalse(op.GetProperty("ViolationFound").GetBoolean());
        }
        var summary = doc.RootElement.GetProperty("ValidationSummary");
        Assert.AreEqual(0, summary.GetProperty("Violations").GetInt32());
        Assert.IsTrue(summary.GetProperty("EnforcementEffective").GetBoolean());
    }

    [TestMethod]
    public void StaticScanEvidence_NinePatterns_ZeroDisallowed()
    {
        var path = ResolveArtifactPath("native-production-trace-endpoint-static-scan-evidence.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var evidence = doc.RootElement.GetProperty("Evidence");
        Assert.AreEqual(9, evidence.GetArrayLength());
        foreach (var e in evidence.EnumerateArray())
            Assert.AreEqual(0, e.GetProperty("DisallowedMatchCount").GetInt32());
        var result = doc.RootElement.GetProperty("ScanResult");
        Assert.AreEqual(0, result.GetProperty("DisallowedMatchCount").GetInt32());
    }

    [TestMethod]
    public void ApprovalAbsenceProof_ArtifactMissing()
    {
        var path = ResolveArtifactPath("native-production-trace-endpoint-approval-artifact-absence-proof.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.IsFalse(doc.RootElement.GetProperty("ArtifactExists").GetBoolean());
        Assert.AreEqual("ApprovalArtifactMissing", doc.RootElement.GetProperty("Conclusion").GetString());
        Assert.IsFalse(doc.RootElement.GetProperty("GoDecision").GetBoolean());
    }

    [TestMethod]
    public void PolicyComplianceReport_AllComponentsCompliant()
    {
        var path = ResolveArtifactPath("native-production-trace-endpoint-policy-compliance-report.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.AreEqual("CompliantNoGo", doc.RootElement.GetProperty("ReportSummary").GetProperty("CurrentCompliance").GetString());
        Assert.IsFalse(doc.RootElement.GetProperty("ReportSummary").GetProperty("GoDecision").GetBoolean());
        var components = doc.RootElement.GetProperty("ComplianceComponents");
        Assert.AreEqual(5, components.GetArrayLength());
        foreach (var c in components.EnumerateArray())
            Assert.IsTrue(c.GetProperty("Compliant").GetBoolean());
    }

    [TestMethod]
    public void GeneratorParityClosure_SixArtifacts_FullParity()
    {
        var path = ResolveArtifactPath("native-production-trace-endpoint-generator-parity-closure.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var artifacts = doc.RootElement.GetProperty("ArtifactsValidated");
        Assert.AreEqual(6, artifacts.GetArrayLength());
        foreach (var a in artifacts.EnumerateArray())
        {
            Assert.IsTrue(a.GetProperty("FullFieldParity").GetBoolean());
            Assert.AreEqual(0, a.GetProperty("MissingFields").GetArrayLength());
        }
    }

    [TestMethod]
    public void V16_21Gate_AllReadyFlags_NoGoDecision()
    {
        var path = ResolveArtifactPath("native-production-trace-endpoint-v16-21-gate.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var gr = doc.RootElement.GetProperty("GateResult");
        Assert.IsTrue(gr.GetProperty("GatePassed").GetBoolean());
        Assert.AreEqual("NoGo", gr.GetProperty("AuthorizationDecision").GetString());
        Assert.IsFalse(gr.GetProperty("GoDecision").GetBoolean());
        Assert.IsFalse(gr.GetProperty("ApprovalArtifactExists").GetBoolean());
        Assert.AreEqual(0, gr.GetProperty("DisallowedMatchCount").GetInt32());
    }

    [TestMethod]
    public void ChainConsistency_AllV16_14_V16_21_NoJsonl()
    {
        var vDir = System.IO.Path.GetDirectoryName(ResolveArtifactPath("native-production-trace-endpoint-no-go-enforcement-validation.json"))!;
        var learningDir = System.IO.Path.GetDirectoryName(vDir)!;
        foreach (var d in new[] { "v16_14", "v16_15", "v16_16", "v16_17", "v16_18", "v16_19", "v16_20", "v16_21" })
            Assert.AreEqual(0, Directory.GetFiles(System.IO.Path.Combine(learningDir, d), "*.jsonl").Length, $"{d}: jsonl must be 0.");
    }

    [TestMethod]
    public void GeneratorParity_RunGeneratorAndValidateAllArtifacts()
    {
        var assembly = typeof(ContextCore.ControlRoom.Commands.EvalCommand).Assembly;
        var type = assembly.GetType("ContextCore.ControlRoom.Commands.EvalCommand")!;
        var method = type.GetMethod("ExecuteV16_21NativeProductionTraceEndpointEnforcementValidationAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var task = method.Invoke(null, [Array.Empty<string>(), CancellationToken.None]) as Task;
        Assert.IsNotNull(task);
        task!.GetAwaiter().GetResult();

        var files = new[] { "native-production-trace-endpoint-no-go-enforcement-validation.json",
            "native-production-trace-endpoint-static-scan-evidence.json",
            "native-production-trace-endpoint-approval-artifact-absence-proof.json",
            "native-production-trace-endpoint-policy-compliance-report.json",
            "native-production-trace-endpoint-generator-parity-closure.json",
            "native-production-trace-endpoint-v16-21-gate.json" };
        foreach (var f in files)
        {
            var p = ResolveArtifactPath(f);
            Assert.IsTrue(File.Exists(p), $"Generator must produce {f}");
            using var doc = JsonDocument.Parse(File.ReadAllText(p));
            Assert.AreEqual("V16.21", doc.RootElement.GetProperty("ContractVersion").GetString());
        }
    }
}
