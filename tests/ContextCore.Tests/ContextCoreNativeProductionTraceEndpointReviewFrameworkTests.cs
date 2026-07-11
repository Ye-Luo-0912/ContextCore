using System.Text.Json;

namespace ContextCore.Tests;

[TestClass]
[TestCategory("Synthetic")]
[TestCategory("Gate")]
public class ContextCoreNativeProductionTraceEndpointReviewFrameworkTests
{
    private static string Resolve(string f) => TestRepoFileResolver.Resolve("learning", "v16_22", f);

    [TestMethod]
    public void ReviewFramework_NoArtifactToReview()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-explicit-approval-artifact-review-framework.json")));
        var st = doc.RootElement.GetProperty("ReviewFrameworkStatus");
        Assert.AreEqual("NoArtifactToReview", st.GetProperty("ApprovalArtifactReviewStatus").GetString());
        Assert.IsFalse(st.GetProperty("ApprovalArtifactExists").GetBoolean());
        Assert.IsFalse(st.GetProperty("GoDecision").GetBoolean());
    }

    [TestMethod]
    public void ValidationRules_FourteenRules()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-artifact-validation-rules.json")));
        Assert.AreEqual(14, doc.RootElement.GetProperty("Rules").GetArrayLength());
        Assert.IsFalse(doc.RootElement.GetProperty("ValidationSummary").GetProperty("ArtifactToValidateExists").GetBoolean());
    }

    [TestMethod]
    public void AbsenceReview_NoArtifactPresent()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-artifact-absence-review-record.json")));
        var rr = doc.RootElement.GetProperty("ReviewRecord");
        Assert.IsFalse(rr.GetProperty("ArtifactExists").GetBoolean());
        Assert.AreEqual("NoArtifactPresent", rr.GetProperty("ReviewOutcome").GetString());
        Assert.IsTrue(rr.GetProperty("NoGoContinues").GetBoolean());
    }

    [TestMethod]
    public void RejectionPolicy_FifteenReasons_MissingArtifactTriggered()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-artifact-rejection-policy.json")));
        var reasons = doc.RootElement.GetProperty("RejectionReasons");
        Assert.IsTrue(reasons.GetArrayLength() >= 14);
        bool missingTriggered = false;
        foreach (var r in reasons.EnumerateArray())
            if (r.GetProperty("Reason").GetString() == "MissingArtifact")
                missingTriggered = r.GetProperty("Triggered").GetBoolean();
        Assert.IsTrue(missingTriggered);
    }

    [TestMethod]
    public void ChangeControl_NoGoToImplementation_Forbidden()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-authorization-change-control.json")));
        Assert.AreEqual("NoGo", doc.RootElement.GetProperty("CurrentState").GetString());
        var forbidden = doc.RootElement.GetProperty("ForbiddenTransitions");
        Assert.IsTrue(forbidden.GetArrayLength() >= 3);
    }

    [TestMethod]
    public void PreGoQuarantine_Active_ZeroClearanceSatisfied()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-pre-go-quarantine-policy.json")));
        Assert.AreEqual("Active", doc.RootElement.GetProperty("QuarantineStatus").GetString());
        Assert.AreEqual(0, doc.RootElement.GetProperty("QuarantineSummary").GetProperty("ClearanceConditionsSatisfied").GetInt32());
    }

    [TestMethod]
    public void V16_22Gate_AllFlagsCorrect()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-v16-22-gate.json")));
        var gr = doc.RootElement.GetProperty("GateResult");
        Assert.IsTrue(gr.GetProperty("GatePassed").GetBoolean());
        Assert.IsTrue(gr.GetProperty("ApprovalArtifactReviewFrameworkReady").GetBoolean());
        Assert.IsTrue(gr.GetProperty("PreGoQuarantinePolicyReady").GetBoolean());
        Assert.AreEqual("Active", gr.GetProperty("QuarantineStatus").GetString());
        Assert.IsFalse(gr.GetProperty("GoDecision").GetBoolean());
    }

    [TestMethod]
    public void NoJsonlAcrossV16_14_V16_22()
    {
        var vDir = System.IO.Path.GetDirectoryName(Resolve("native-production-trace-endpoint-v16-22-gate.json"))!;
        var learningDir = System.IO.Path.GetDirectoryName(vDir)!;
        foreach (var d in new[] { "v16_14", "v16_15", "v16_16", "v16_17", "v16_18", "v16_19", "v16_20", "v16_21", "v16_22" })
            Assert.AreEqual(0, Directory.GetFiles(System.IO.Path.Combine(learningDir, d), "*.jsonl").Length, $"{d}: jsonl must be 0.");
    }

    [TestMethod]
    public void CheckedInGate_HasReviewFrameworkGeneratorParityFields_BeforeGeneratorRuns()
    {
        // Verify the checked-in gate has the new parity evidence fields BEFORE the generator overwrites.
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-v16-22-gate.json")));
        var gr = doc.RootElement.GetProperty("GateResult");
        Assert.IsTrue(gr.TryGetProperty("ReviewFrameworkGeneratorParityEvidenceReady", out _),
            "Checked-in gate MUST have ReviewFrameworkGeneratorParityEvidenceReady before generator runs.");
        Assert.IsTrue(gr.TryGetProperty("ReviewFrameworkGeneratorParityPassed", out _),
            "Checked-in gate MUST have ReviewFrameworkGeneratorParityPassed before generator runs.");
        var pt = doc.RootElement.GetProperty("PhaseTransition");
        Assert.IsTrue(pt.TryGetProperty("NextAllowedPhaseDescription", out _));
        Assert.IsTrue(pt.TryGetProperty("NextDisallowedPhaseReason", out _));
        var pg = doc.RootElement.GetProperty("PreviousGatesPreserved");
        Assert.IsTrue(pg.TryGetProperty("V16_22ReviewFrameworkGeneratorParityReady", out _));
    }

    [TestMethod]
    public void GeneratorParity_RunGeneratorAndCheckKeyFields()
    {
        var assembly = typeof(ContextCore.Evaluation.Commands.EvalCommand).Assembly;
        var type = assembly.GetType("ContextCore.Evaluation.Commands.EvalCommand")!;
        var method = type.GetMethod("ExecuteV16_22NativeProductionTraceEndpointReviewFrameworkAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var task = method.Invoke(null, [Array.Empty<string>(), CancellationToken.None]) as Task;
        Assert.IsNotNull(task);
        task!.GetAwaiter().GetResult();

        // Review framework: check ProductionTraceExecutionAllowed and EndpointImplemented
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-explicit-approval-artifact-review-framework.json")));
        var rfs = doc.RootElement.GetProperty("ReviewFrameworkStatus");
        Assert.IsTrue(rfs.TryGetProperty("EndpointImplemented", out _));
        Assert.IsTrue(rfs.TryGetProperty("ProductionTraceExecutionAllowed", out _));

        // Absence review: check ReviewTimestamp
        using var absDoc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-artifact-absence-review-record.json")));
        Assert.IsTrue(absDoc.RootElement.GetProperty("ReviewRecord").TryGetProperty("ReviewTimestamp", out _));

        // Rejection: check Description on each reason
        using var rejDoc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-artifact-rejection-policy.json")));
        Assert.IsTrue(rejDoc.RootElement.GetProperty("RejectionReasons")[0].TryGetProperty("Description", out _));

        // Change control: check ValidTransitions are objects with From/To/Requires/Allowed
        using var ccDoc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-authorization-change-control.json")));
        var vt = ccDoc.RootElement.GetProperty("ValidTransitions")[0];
        Assert.IsTrue(vt.TryGetProperty("From", out _));
        Assert.IsTrue(vt.TryGetProperty("To", out _));
        Assert.IsTrue(vt.TryGetProperty("Requires", out _));
        Assert.IsTrue(vt.GetProperty("Allowed").GetBoolean());
        var ft0 = ccDoc.RootElement.GetProperty("ForbiddenTransitions")[0];
        Assert.IsTrue(ft0.TryGetProperty("From", out _));
        Assert.IsTrue(ft0.TryGetProperty("To", out _));
        Assert.IsTrue(ft0.TryGetProperty("Reason", out _));

        // Gate: check new fields
        using var gateDoc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-v16-22-gate.json")));
        var gr = gateDoc.RootElement.GetProperty("GateResult");
        Assert.IsTrue(gr.GetProperty("ReviewFrameworkGeneratorParityEvidenceReady").GetBoolean());
        Assert.IsTrue(gr.GetProperty("ReviewFrameworkGeneratorParityPassed").GetBoolean());
        Assert.IsTrue(gateDoc.RootElement.GetProperty("PhaseTransition").TryGetProperty("NextAllowedPhaseDescription", out _));

        // Parity evidence file exists
        Assert.IsTrue(File.Exists(Resolve("native-production-trace-endpoint-review-framework-generator-parity-evidence.json")));
        using var peDoc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-review-framework-generator-parity-evidence.json")));
        var results = peDoc.RootElement.GetProperty("ComparisonResults");
        Assert.AreEqual(7, results.GetArrayLength());
        foreach (var r in results.EnumerateArray())
            Assert.IsTrue(r.GetProperty("ParityPassed").GetBoolean());
    }
}
