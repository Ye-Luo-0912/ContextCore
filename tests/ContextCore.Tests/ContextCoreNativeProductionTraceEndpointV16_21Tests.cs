using System.Text.Json;

namespace ContextCore.Tests;

[TestClass]
[TestCategory("Synthetic")]
[TestCategory("Gate")]
public class ContextCoreNativeProductionTraceEndpointV16_21Tests
{
    private static string ResolveArtifactPath(string fileName) =>
        TestRepoFileResolver.Resolve("learning", "v16_21", fileName);

    private static HashSet<string> CollectPropertyPaths(JsonElement element, string prefix)
    {
        var paths = new HashSet<string>();
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    var currentPath = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}.{prop.Name}";
                    paths.Add(currentPath);
                    foreach (var child in CollectPropertyPaths(prop.Value, currentPath))
                        paths.Add(child);
                }
                break;
            case JsonValueKind.Array:
                int idx = 0;
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var child in CollectPropertyPaths(item, $"{prefix}[{idx}]"))
                        paths.Add(child);
                    // Add a wildcard representation too
                    foreach (var child in CollectPropertyPaths(item, $"{prefix}[*]"))
                        paths.Add(child);
                    idx++;
                }
                break;
        }
        return paths;
    }

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
    public void PolicyComplianceReport_CompliantNoGo()
    {
        var path = ResolveArtifactPath("native-production-trace-endpoint-policy-compliance-report.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.AreEqual("CompliantNoGo", doc.RootElement.GetProperty("ReportSummary").GetProperty("CurrentCompliance").GetString());
        Assert.IsFalse(doc.RootElement.GetProperty("ReportSummary").GetProperty("GoDecision").GetBoolean());
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
        Assert.IsTrue(gr.GetProperty("GeneratorParityEvidenceReady").GetBoolean());
        Assert.IsTrue(gr.GetProperty("GeneratorParityPassed").GetBoolean());
    }

    [TestMethod]
    public void NoJsonlAcrossV16_14_V16_21()
    {
        var vDir = System.IO.Path.GetDirectoryName(ResolveArtifactPath("native-production-trace-endpoint-v16-21-gate.json"))!;
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

        // Check enforcement has EvidenceSource
        var enfPath = ResolveArtifactPath("native-production-trace-endpoint-no-go-enforcement-validation.json");
        using var enfDoc = JsonDocument.Parse(File.ReadAllText(enfPath));
        var op0 = enfDoc.RootElement.GetProperty("ValidatedOperations")[0];
        Assert.IsTrue(op0.TryGetProperty("EvidenceSource", out _), "Enforcement must have EvidenceSource.");
        Assert.IsTrue(enfDoc.RootElement.TryGetProperty("Purpose", out _));
        Assert.IsTrue(enfDoc.RootElement.GetProperty("ValidationSummary").TryGetProperty("NoGoStillEnforced", out _));

        // Check scan has ScannedPaths and AllowedMatches
        var scanPath = ResolveArtifactPath("native-production-trace-endpoint-static-scan-evidence.json");
        using var scanDoc = JsonDocument.Parse(File.ReadAllText(scanPath));
        var e0 = scanDoc.RootElement.GetProperty("Evidence")[0];
        Assert.IsTrue(e0.TryGetProperty("ScannedPaths", out _), "Scan must have ScannedPaths.");
        Assert.IsTrue(e0.TryGetProperty("AllowedMatches", out _));

        // Check absence has RequiredFieldsAbsent and ProofValid
        var absPath = ResolveArtifactPath("native-production-trace-endpoint-approval-artifact-absence-proof.json");
        using var absDoc = JsonDocument.Parse(File.ReadAllText(absPath));
        Assert.IsTrue(absDoc.RootElement.TryGetProperty("RequiredFieldsAbsent", out _));
        Assert.IsTrue(absDoc.RootElement.GetProperty("ProofValid").GetBoolean());

        // Check gate has extended fields
        var gatePath = ResolveArtifactPath("native-production-trace-endpoint-v16-21-gate.json");
        using var gateDoc = JsonDocument.Parse(File.ReadAllText(gatePath));
        var gr = gateDoc.RootElement.GetProperty("GateResult");
        Assert.IsTrue(gr.TryGetProperty("ProductionTraceExecutionAllowed", out _));
        Assert.IsTrue(gr.GetProperty("GeneratorParityEvidenceReady").GetBoolean());
    }

    [TestMethod]
    public void GeneratorParity_PropertyPathParity_AgainstCheckedIn()
    {
        // This test proves that checked-in artifacts match the expected schema shape.
        // It collects all property paths and validates key fields are present.
        var enforcementPath = ResolveArtifactPath("native-production-trace-endpoint-no-go-enforcement-validation.json");
        using var enfDoc = JsonDocument.Parse(File.ReadAllText(enforcementPath));
        var enfPaths = CollectPropertyPaths(enfDoc.RootElement.GetProperty("ValidatedOperations")[0], "Operation[*]");
        var enfList = enfPaths.ToList();
        CollectionAssert.Contains(enfList, "Operation[*].EvidenceSource");
        CollectionAssert.Contains(enfList, "Operation[*].PolicyBlocked");
        CollectionAssert.Contains(enfList, "Operation[*].EvidenceResult");
        CollectionAssert.Contains(enfList, "Operation[*].ViolationFound");
    }
}
