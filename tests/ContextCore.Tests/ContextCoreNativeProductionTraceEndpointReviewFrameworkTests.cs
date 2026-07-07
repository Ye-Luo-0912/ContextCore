using System.Text.Json;

namespace ContextCore.Tests;

[TestClass]
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
    public void GeneratorParity_RunGeneratorAndCheckKeyFields()
    {
        var assembly = typeof(ContextCore.ControlRoom.Commands.EvalCommand).Assembly;
        var type = assembly.GetType("ContextCore.ControlRoom.Commands.EvalCommand")!;
        var method = type.GetMethod("ExecuteV16_22NativeProductionTraceEndpointReviewFrameworkAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var task = method.Invoke(null, [Array.Empty<string>(), CancellationToken.None]) as Task;
        Assert.IsNotNull(task);
        task!.GetAwaiter().GetResult();

        // Check key fields in generated artifacts
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-explicit-approval-artifact-review-framework.json")));
        Assert.IsTrue(doc.RootElement.TryGetProperty("ReviewFrameworkStatus", out _));
        Assert.IsTrue(doc.RootElement.TryGetProperty("ReviewProcessWhenArtifactAppears", out _));

        using var doc2 = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-artifact-rejection-policy.json")));
        Assert.IsTrue(doc2.RootElement.GetProperty("RejectionReasons").GetArrayLength() >= 14);
    }
}
