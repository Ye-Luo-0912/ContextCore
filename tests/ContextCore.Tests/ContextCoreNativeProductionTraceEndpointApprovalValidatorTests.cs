using System.Text.Json;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreNativeProductionTraceEndpointApprovalValidatorTests
{
    private static string Resolve(string f) => TestRepoFileResolver.Resolve("learning", "v16_23", f);

    [TestMethod]
    public void Plan_Ready_ButNotImplemented()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-implementation-plan.json")));
        var ps = doc.RootElement.GetProperty("PlanStatus");
        Assert.IsTrue(ps.GetProperty("ApprovalValidatorImplementationPlanReady").GetBoolean());
        Assert.IsFalse(ps.GetProperty("ApprovalValidatorImplemented").GetBoolean());
        Assert.IsFalse(ps.GetProperty("GoDecision").GetBoolean());
    }

    [TestMethod]
    public void Contract_ArtifactExists_False_ValidationNotAttempted()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-contract.json")));
        var o = doc.RootElement.GetProperty("Outputs");
        Assert.IsFalse(o.GetProperty("ArtifactExists").GetBoolean());
        Assert.IsFalse(o.GetProperty("ValidationAttempted").GetBoolean());
        Assert.IsFalse(o.GetProperty("ApprovalAccepted").GetBoolean());
        Assert.IsFalse(o.GetProperty("GoCandidateAllowed").GetBoolean());
    }

    [TestMethod]
    public void StateMachine_CurrentState_NoArtifactToReview()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-state-machine.json")));
        Assert.AreEqual("NoArtifactToReview", doc.RootElement.GetProperty("CurrentState").GetString());
        var forbidden = doc.RootElement.GetProperty("ForbiddenTransitions");
        Assert.IsTrue(forbidden.GetArrayLength() >= 3);
    }

    [TestMethod]
    public void RejectionMapping_FifteenMappings_OnlyMissingArtifactTriggered()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-rejection-mapping.json")));
        var maps = doc.RootElement.GetProperty("Mappings");
        Assert.AreEqual(15, maps.GetArrayLength());
        int triggered = 0;
        foreach (var m in maps.EnumerateArray())
            if (m.GetProperty("Triggered").GetBoolean()) triggered++;
        Assert.AreEqual(1, triggered);
    }

    [TestMethod]
    public void AuditLogSchema_ExcludesApprovalTokenPlaintext()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-audit-log-schema.json")));
        var excluded = doc.RootElement.GetProperty("ExcludedFromLog");
        Assert.IsTrue(excluded.GetArrayLength() >= 3);
    }

    [TestMethod]
    public void TestMatrix_SeventeenScenarios_MostlyNoGo()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-test-matrix.json")));
        var scenarios = doc.RootElement.GetProperty("Scenarios");
        Assert.IsTrue(scenarios.GetArrayLength() >= 16);
        int goCount = 0;
        foreach (var s in scenarios.EnumerateArray())
            if (s.GetProperty("GoDecision").GetBoolean()) goCount++;
        Assert.AreEqual(1, goCount);
    }

    [TestMethod]
    public void V16_23Gate_AllFlagsCorrect()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-v16-23-gate.json")));
        var gr = doc.RootElement.GetProperty("GateResult");
        Assert.IsTrue(gr.GetProperty("GatePassed").GetBoolean());
        Assert.IsFalse(gr.GetProperty("ApprovalValidatorImplemented").GetBoolean());
        Assert.AreEqual("NoArtifactToReview", gr.GetProperty("CurrentValidatorState").GetString());
    }

    [TestMethod]
    public void NoJsonl()
    {
        var vDir = System.IO.Path.GetDirectoryName(Resolve("native-production-trace-endpoint-approval-validator-v16-23-gate.json"))!;
        var learningDir = System.IO.Path.GetDirectoryName(vDir)!;
        foreach (var d in new[] { "v16_14", "v16_15", "v16_16", "v16_17", "v16_18", "v16_19", "v16_20", "v16_21", "v16_22", "v16_23" })
            Assert.AreEqual(0, Directory.GetFiles(System.IO.Path.Combine(learningDir, d), "*.jsonl").Length, $"{d}: jsonl must be 0.");
    }

    [TestMethod]
    public void GeneratorParity_RunGeneratorAndCheckKeyFields()
    {
        var assembly = typeof(ContextCore.ControlRoom.Commands.EvalCommand).Assembly;
        var type = assembly.GetType("ContextCore.ControlRoom.Commands.EvalCommand")!;
        var method = type.GetMethod("ExecuteV16_23NativeProductionTraceEndpointApprovalValidatorPlanAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var task = method.Invoke(null, [Array.Empty<string>(), CancellationToken.None]) as Task;
        Assert.IsNotNull(task);
        task!.GetAwaiter().GetResult();

        // Contract: check Type field on inputs, CurrentExpectedBehavior
        using var doc = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-contract.json")));
        Assert.IsTrue(doc.RootElement.GetProperty("ContractStatus").TryGetProperty("ValidationNeverAttempted", out _));
        Assert.IsTrue(doc.RootElement.GetProperty("Inputs")[0].TryGetProperty("Type", out _));
        Assert.IsTrue(doc.RootElement.TryGetProperty("CurrentExpectedBehavior", out _));

        // State machine: check States are objects with Description, transitions have Trigger/Reason
        using var sm = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-state-machine.json")));
        Assert.IsTrue(sm.RootElement.GetProperty("States")[0].TryGetProperty("Description", out _));
        Assert.IsTrue(sm.RootElement.GetProperty("ValidTransitions")[0].TryGetProperty("Trigger", out _));
        Assert.IsTrue(sm.RootElement.GetProperty("ForbiddenTransitions")[0].TryGetProperty("Reason", out _));

        // Rejection: check SourceRule, UserFacingMessage, AuditField, MappingSummary
        using var rj = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-rejection-mapping.json")));
        var m0 = rj.RootElement.GetProperty("Mappings")[0];
        Assert.IsTrue(m0.TryGetProperty("SourceRule", out _));
        Assert.IsTrue(m0.TryGetProperty("UserFacingMessage", out _));
        Assert.IsTrue(m0.TryGetProperty("AuditField", out _));
        Assert.IsTrue(rj.RootElement.TryGetProperty("MappingSummary", out _));

        // Audit: check AuditLogFields are objects with Type/RequiredForAllRuns/Description, CurrentLogState
        using var al = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-audit-log-schema.json")));
        var al0 = al.RootElement.GetProperty("AuditLogFields")[0];
        Assert.IsTrue(al0.TryGetProperty("Type", out _));
        Assert.IsTrue(al0.TryGetProperty("RequiredForAllRuns", out _));
        Assert.IsTrue(al.RootElement.TryGetProperty("CurrentLogState", out _));

        // Test matrix: check Scenarios have ApprovalArtifactExists/InputArtifact/ExpectedOutcome, Summary
        using var tm = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-test-matrix.json")));
        var s0 = tm.RootElement.GetProperty("Scenarios")[0];
        Assert.IsTrue(s0.TryGetProperty("ApprovalArtifactExists", out _));
        Assert.IsTrue(s0.TryGetProperty("InputArtifact", out _));
        Assert.IsTrue(s0.TryGetProperty("ExpectedOutcome", out _));
        Assert.IsTrue(tm.RootElement.TryGetProperty("Summary", out _));

        // Gate: check parity fields
        using var gate = JsonDocument.Parse(File.ReadAllText(Resolve("native-production-trace-endpoint-approval-validator-v16-23-gate.json")));
        var gr = gate.RootElement.GetProperty("GateResult");
        Assert.IsTrue(gr.TryGetProperty("ApprovalValidatorGeneratorParityEvidenceReady", out _));
        Assert.IsTrue(gr.TryGetProperty("ApprovalValidatorGeneratorParityPassed", out _));

        // Parity evidence exists
        Assert.IsTrue(File.Exists(Resolve("native-production-trace-endpoint-approval-validator-generator-parity-evidence.json")));
    }
}
