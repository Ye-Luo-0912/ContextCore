using System.Text.Json;

namespace ContextCore.Tests;

[TestClass]
public class ContextCorePhaseLedgerArtifactParsingTests
{
    private static string ResolveArtifactPath(string fileName) =>
        TestRepoFileResolver.Resolve("learning", "v16_11", fileName);

    [TestMethod]
    public void ArtifactParsing_PhaseLedger_ReadsFromFile()
    {
        var path = ResolveArtifactPath("phase-ledger.json");
        Assert.IsTrue(File.Exists(path), $"phase-ledger.json must exist at: {path}");

        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.AreEqual("V16.11", root.GetProperty("ContractVersion").GetString());
        Assert.AreEqual("PhaseLedger", root.GetProperty("DocumentType").GetString());
    }

    [TestMethod]
    public void ArtifactParsing_PhaseLedger_HighestReadinessLevel_ControlledReplay()
    {
        var path = ResolveArtifactPath("phase-ledger.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        string highestReadiness = root.GetProperty("HighestReadinessLevel").GetString()!;
        string achievedIn = root.GetProperty("HighestReadinessLevelAchievedIn").GetString()!;

        Assert.AreEqual("ControlledReplay", highestReadiness,
            "Highest readiness level in ledger must be ControlledReplay.");
        Assert.AreEqual("V16.7", achievedIn,
            "Highest readiness must be achieved in V16.7.");
    }

    [TestMethod]
    public void ArtifactParsing_PhaseLedger_TopLevelGates_AllFalse()
    {
        var path = ResolveArtifactPath("phase-ledger.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        Assert.IsFalse(root.GetProperty("NativeProductionTraceReady").GetBoolean());
        Assert.IsFalse(root.GetProperty("ProductionGeneralizationReady").GetBoolean());
        Assert.IsFalse(root.GetProperty("LiveCaptureExecutionImplemented").GetBoolean());
        Assert.IsFalse(root.GetProperty("RuntimeInfluenceAllowed").GetBoolean());
        Assert.IsFalse(root.GetProperty("PackageOutputChanged").GetBoolean());
        Assert.IsFalse(root.GetProperty("RuntimePromotionApplied").GetBoolean());
        Assert.IsFalse(root.GetProperty("VectorBindingChanged").GetBoolean());
    }

    [TestMethod]
    public void ArtifactParsing_PhaseLedger_PhaseTransitionRules()
    {
        var path = ResolveArtifactPath("phase-ledger.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        string nextAllowed = root.GetProperty("NextAllowedPhase").GetString()!;
        string nextDisallowed = root.GetProperty("NextDisallowedPhase").GetString()!;

        Assert.AreEqual("NativeProductionTraceExecutionDesignReview", nextAllowed,
            "NextAllowedPhase must be NativeProductionTraceExecutionDesignReview.");
        Assert.AreEqual("V17 Runtime influence activation", nextDisallowed,
            "NextDisallowedPhase must be V17 Runtime influence activation.");
    }

    [TestMethod]
    public void ArtifactParsing_PhaseLedger_CoversTenPhases()
    {
        var path = ResolveArtifactPath("phase-ledger.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var phases = root.GetProperty("Phases");
        Assert.AreEqual(10, phases.GetArrayLength(),
            "Phase ledger must have exactly 10 phases (V16.2–V16.11).");

        var versions = new List<string>();
        foreach (var p in phases.EnumerateArray())
            versions.Add(p.GetProperty("Version").GetString()!);

        CollectionAssert.Contains(versions, "V16.2");
        CollectionAssert.Contains(versions, "V16.7");
        CollectionAssert.Contains(versions, "V16.11");
    }

    [TestMethod]
    public void ArtifactParsing_PhaseLedger_AllPhasesHaveBlockedClaims()
    {
        var path = ResolveArtifactPath("phase-ledger.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        foreach (var phase in root.GetProperty("Phases").EnumerateArray())
        {
            var version = phase.GetProperty("Version").GetString();

            Assert.IsTrue(phase.TryGetProperty("BlockedClaims", out _),
                $"{version}: must have BlockedClaims (not BlockedState).");

            var status = phase.GetProperty("Status").GetString();
            Assert.IsTrue(status!.Contains("Accepted"), $"{version}: Status must be Accepted, got: {status}");

            Assert.IsTrue(phase.TryGetProperty("AcceptedState", out _),
                $"{version}: must have AcceptedState.");
        }
    }

    [TestMethod]
    public void ArtifactParsing_PhaseLedger_NoAmbiguousReadinessTrueUnderBlockedClaims()
    {
        var path = ResolveArtifactPath("phase-ledger.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var forbiddenReadinessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "NativeProductionTraceReady",
            "ProductionGeneralizationReady",
            "RuntimeInfluenceAllowed",
        };

        foreach (var phase in root.GetProperty("Phases").EnumerateArray())
        {
            var version = phase.GetProperty("Version").GetString();
            var blockedClaims = phase.GetProperty("BlockedClaims");

            foreach (var claim in blockedClaims.EnumerateObject())
            {
                string claimName = claim.Name;

                Assert.IsTrue(claimName.EndsWith("Blocked", StringComparison.OrdinalIgnoreCase)
                    || claimName == "LiveCaptureBlocked",
                    $"{version}: BlockedClaim '{claimName}' must end with 'Blocked' suffix or be 'LiveCaptureBlocked'.");

                Assert.IsFalse(forbiddenReadinessNames.Contains(claimName),
                    $"{version}: Ambiguous readiness name '{claimName}' found in BlockedClaims. " +
                    "Readiness fields must not appear directly under BlockedClaims without a Blocked suffix.");
            }
        }
    }

    [TestMethod]
    public void ArtifactParsing_PhaseLedger_V16_7_HighestProven()
    {
        var path = ResolveArtifactPath("phase-ledger.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        JsonElement? v16_7 = null;
        foreach (var p in root.GetProperty("Phases").EnumerateArray())
        {
            if (p.GetProperty("Version").GetString() == "V16.7")
            { v16_7 = p; break; }
        }

        Assert.IsNotNull(v16_7);
        var phase = v16_7!.Value;

        Assert.AreEqual("ControlledReplay", phase.GetProperty("HighestReadinessLevel").GetString());
        Assert.IsTrue(phase.GetProperty("Status").GetString()!.Contains("HIGHEST PROVEN"));

        var accepted = phase.GetProperty("AcceptedState");
        Assert.IsTrue(accepted.GetProperty("NativeControlledReplayTraceReady").GetBoolean());
        Assert.IsTrue(accepted.GetProperty("ControlledReplayMetricQualityReady").GetBoolean());
        Assert.IsTrue(accepted.GetProperty("ControlledReplayTraceSufficient").GetBoolean());

        var blocked = phase.GetProperty("BlockedClaims");
        Assert.IsTrue(blocked.GetProperty("NativeProductionTraceReadyBlocked").GetBoolean());
        Assert.IsTrue(blocked.GetProperty("FileSystemStoreOnlyBlocked").GetBoolean());
    }

    [TestMethod]
    public void ArtifactParsing_FinalAcceptanceBoundaryGate_ReadsFromFile()
    {
        var path = ResolveArtifactPath("final-acceptance-boundary-gate.json");
        Assert.IsTrue(File.Exists(path), $"final-acceptance-boundary-gate.json must exist at: {path}");

        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.AreEqual("V16.11", root.GetProperty("ContractVersion").GetString());
        Assert.AreEqual("FinalAcceptanceBoundaryGate", root.GetProperty("DocumentType").GetString());

        var boundary = root.GetProperty("BoundaryDefinition");
        Assert.AreEqual("ControlledReplay", boundary.GetProperty("HighestReadinessLevel").GetString());
        Assert.AreEqual("V16.7", boundary.GetProperty("HighestReadinessLevelAchievedBy").GetString());
    }

    [TestMethod]
    public void ArtifactParsing_FinalAcceptanceBoundaryGate_AllHardLimitsFalse()
    {
        var path = ResolveArtifactPath("final-acceptance-boundary-gate.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var limits = root.GetProperty("GateHardLimits");

        Assert.IsFalse(limits.GetProperty("NativeProductionTraceReady").GetBoolean());
        Assert.IsFalse(limits.GetProperty("ProductionGeneralizationReady").GetBoolean());
        Assert.IsFalse(limits.GetProperty("LiveCaptureExecutionImplemented").GetBoolean());
        Assert.IsFalse(limits.GetProperty("RuntimeInfluenceAllowed").GetBoolean());
        Assert.IsFalse(limits.GetProperty("PackageOutputChanged").GetBoolean());
        Assert.IsFalse(limits.GetProperty("RuntimePromotionApplied").GetBoolean());
        Assert.IsFalse(limits.GetProperty("VectorBindingChanged").GetBoolean());

        Assert.IsTrue(limits.GetProperty("RuntimeInfluenceAllowedPermanent").GetBoolean(),
            "RuntimeInfluenceAllowedPermanent must be true.");
    }

    [TestMethod]
    public void ArtifactParsing_FinalAcceptanceBoundaryGate_PhaseTransitionRules()
    {
        var path = ResolveArtifactPath("final-acceptance-boundary-gate.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var rules = root.GetProperty("PhaseTransitionRules");

        Assert.AreEqual("NativeProductionTraceExecutionDesignReview",
            rules.GetProperty("NextAllowedPhase").GetString());
        Assert.AreEqual("V17 Runtime influence activation",
            rules.GetProperty("NextDisallowedPhase").GetString());
    }

    [TestMethod]
    public void ArtifactParsing_FinalAcceptanceBoundaryGate_ControlledReplayNotProductionReadiness()
    {
        var path = ResolveArtifactPath("final-acceptance-boundary-gate.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var boundary = root.GetProperty("BoundaryDefinition");

        Assert.AreEqual("ControlledReplay", boundary.GetProperty("HighestReadinessLevel").GetString());
        Assert.AreEqual("ControlledReplay", boundary.GetProperty("ReadinessCapAt").GetString());
        Assert.IsTrue(boundary.GetProperty("ReadinessCapReason").GetString()!
            .Contains("NativeProductionTraceReady requires actual production trace capture"),
            "ReadinessCapReason must state that ControlledReplay != production readiness.");

        Assert.IsFalse(root.GetProperty("GateHardLimits").GetProperty("NativeProductionTraceReady").GetBoolean());
        Assert.IsFalse(root.GetProperty("GateHardLimits").GetProperty("ProductionGeneralizationReady").GetBoolean());
    }

    [TestMethod]
    public void ArtifactParsing_FinalAcceptanceBoundaryGate_VersionOrderingClarification()
    {
        var path = ResolveArtifactPath("final-acceptance-boundary-gate.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var clarification = root.GetProperty("VersionOrderingClarification");

        Assert.IsTrue(clarification.GetProperty("DoNotInferReadinessFromLatestCommitMessage").GetBoolean(),
            "Must clarify: do not infer readiness from commit message.");
        Assert.IsTrue(clarification.GetProperty("DoNotInferReadinessFromVersionNumberOrdering").GetBoolean(),
            "Must clarify: do not infer readiness from version number ordering.");
        Assert.IsTrue(clarification.GetProperty("LedgerCoversAllV16_2_ThroughV16_11").GetBoolean(),
            "Ledger coverage must be explicit.");
    }
}

