using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreShadowCandidateMergePreviewTests
{
    [TestMethod]
    public void ShadowCandidateMergePreview_GatePassesWithCleanV66Gate()
    {
        var report = new ShadowCandidateMergePreviewRunner().RunPreview(
            CleanV66Gate(),
            new ShadowCandidateMergePreviewOptions { GateMode = true });

        Assert.IsTrue(report.PreviewPassed, string.Join(",", report.BlockedReasons));
        Assert.IsTrue(report.GatePassed);
        Assert.AreEqual(ShadowCandidateMergePreviewRecommendations.ReadyForShadowMergeObservation, report.Recommendation);
        Assert.IsTrue(report.PreviewMergedSetGenerated);
        Assert.IsTrue(report.PreviewAddCount > 0);
        Assert.IsTrue(report.PreviewRemoveCount > 0);
        Assert.AreEqual(0, report.AppliedAddCount);
        Assert.AreEqual(0, report.AppliedRemoveCount);
        Assert.IsFalse(report.FormalSelectedSetChanged);
        Assert.AreEqual(0, report.FormalOutputChanged);
        Assert.IsFalse(report.FormalPackageWritten);
        Assert.IsFalse(report.PackageOutputChanged);
        Assert.IsFalse(report.PackingPolicyChanged);
        Assert.IsFalse(report.RuntimeMutated);
        Assert.IsFalse(report.VectorStoreBindingChanged);
        Assert.AreEqual(0, report.RiskAfterPolicy);
        Assert.AreEqual(0, report.MustNotHitRiskAfterPolicy);
        Assert.AreEqual(0, report.LifecycleRiskAfterPolicy);
        Assert.AreEqual(0, report.PriorityInversionCount);
        Assert.AreEqual(0, report.SectionMismatchCount);
    }

    [TestMethod]
    public void ShadowCandidateMergePreview_MissingV66GateBlocks()
    {
        var report = new ShadowCandidateMergePreviewRunner().RunPreview(
            null,
            new ShadowCandidateMergePreviewOptions { GateMode = true });

        Assert.IsFalse(report.PreviewPassed);
        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(ShadowCandidateMergePreviewRecommendations.BlockedByMissingV66Gate, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "V66GateMissingOrNotPassed");
    }

    [TestMethod]
    public void ShadowCandidateMergePreview_RuntimeMutationAttemptBlocks()
    {
        var report = new ShadowCandidateMergePreviewRunner().RunPreview(
            CleanV66Gate(),
            new ShadowCandidateMergePreviewOptions
            {
                GateMode = true,
                RuntimeMutated = true
            });

        Assert.IsFalse(report.PreviewPassed);
        Assert.AreEqual(ShadowCandidateMergePreviewRecommendations.BlockedByPackageInvariant, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RuntimeOrFormalInvariantChanged");
    }

    [TestMethod]
    public void ShadowCandidateMergePreview_AppliedDeltaBlocks()
    {
        var report = new ShadowCandidateMergePreviewRunner().RunPreview(
            WithAppliedDelta(CleanV66Gate()),
            new ShadowCandidateMergePreviewOptions { GateMode = true });

        Assert.IsFalse(report.PreviewPassed);
        Assert.AreEqual(ShadowCandidateMergePreviewRecommendations.BlockedByPackageInvariant, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "AppliedDeltaDetected");
    }

    [TestMethod]
    public void ShadowCandidateMergePreview_TokenBudgetBlocks()
    {
        var report = new ShadowCandidateMergePreviewRunner().RunPreview(
            CleanV66Gate(),
            new ShadowCandidateMergePreviewOptions
            {
                GateMode = true,
                TokenDeltaBudget = -1
            });

        Assert.IsFalse(report.PreviewPassed);
        Assert.AreEqual(ShadowCandidateMergePreviewRecommendations.BlockedByTokenBudget, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "TokenDeltaBudgetExceeded");
    }

    [TestMethod]
    public void ShadowCandidateMergePreview_SourceDoesNotSpecialCaseSamplesOrItems()
    {
        var source = File.ReadAllText(ResolveRepoFile("src", "ContextCore.Core", "Services", "Vector", "ShadowCandidateMergePreviewRunner.cs"));
        Assert.IsFalse(source.Contains("sample.SampleId ==", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("item.ItemId ==", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("mustHitItemId ==", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-shadow", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-audit", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-repair", StringComparison.Ordinal));
    }


    [TestMethod]
    public void ShadowCandidateMergePreviewObservation_GatePassesAcrossRuns()
    {
        var v66 = CleanV66Gate();
        var v67 = CleanV67Gate(v66);
        var report = new ShadowCandidateMergePreviewObservationRunner().RunObservation(
            v66,
            v67,
            new ShadowCandidateMergePreviewObservationOptions
            {
                GateMode = true,
                ObservationRunCount = 5,
                MinimumObservationRunCount = 3
            });

        Assert.IsTrue(report.ObservationPassed, string.Join(",", report.BlockedReasons));
        Assert.IsTrue(report.GatePassed);
        Assert.AreEqual(ShadowCandidateMergePreviewObservationRecommendations.ReadyForShadowMergeStabilityFreeze, report.Recommendation);
        Assert.IsTrue(report.DeterministicPreviewStable);
        Assert.IsTrue(report.PreviewAddRemoveStable);
        Assert.AreEqual(1, report.DistinctStableSignatureCount);
        Assert.AreEqual(5, report.ObservationRunCount);
        Assert.AreEqual(0, report.RiskAfterPolicyMax);
        Assert.AreEqual(0, report.MustNotHitRiskAfterPolicyMax);
        Assert.AreEqual(0, report.LifecycleRiskAfterPolicyMax);
        Assert.AreEqual(0, report.PriorityInversionCountTotal);
        Assert.AreEqual(0, report.SectionMismatchCountTotal);
        Assert.AreEqual(0, report.TokenDeltaTotalMax);
        Assert.AreEqual(0, report.TokenDeltaMaxMax);
        Assert.AreEqual(0, report.AppliedAddCountMax);
        Assert.AreEqual(0, report.AppliedRemoveCountMax);
        Assert.IsFalse(report.FormalSelectedSetChanged);
        Assert.AreEqual(0, report.FormalOutputChangedMax);
        Assert.IsFalse(report.FormalPackageWritten);
        Assert.IsFalse(report.PackageOutputChanged);
        Assert.IsFalse(report.PackingPolicyChanged);
        Assert.IsFalse(report.RuntimeMutated);
        Assert.IsFalse(report.VectorStoreBindingChanged);
    }

    [TestMethod]
    public void ShadowCandidateMergePreviewObservation_MissingV67GateBlocks()
    {
        var report = new ShadowCandidateMergePreviewObservationRunner().RunObservation(
            CleanV66Gate(),
            null,
            new ShadowCandidateMergePreviewObservationOptions { GateMode = true });

        Assert.IsFalse(report.ObservationPassed);
        Assert.AreEqual(ShadowCandidateMergePreviewObservationRecommendations.BlockedByMissingV67Gate, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "V67GateMissingOrNotPassed");
    }

    [TestMethod]
    public void ShadowCandidateMergePreviewObservation_InsufficientRunsBlocks()
    {
        var v66 = CleanV66Gate();
        var report = new ShadowCandidateMergePreviewObservationRunner().RunObservation(
            v66,
            CleanV67Gate(v66),
            new ShadowCandidateMergePreviewObservationOptions
            {
                GateMode = true,
                ObservationRunCount = 1,
                MinimumObservationRunCount = 3
            });

        Assert.IsFalse(report.ObservationPassed);
        Assert.AreEqual(ShadowCandidateMergePreviewObservationRecommendations.NeedsMoreObservation, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "InsufficientObservationRuns");
    }

    [TestMethod]
    public void ShadowCandidateMergePreviewObservation_RuntimeMutationAttemptBlocks()
    {
        var v66 = CleanV66Gate();
        var report = new ShadowCandidateMergePreviewObservationRunner().RunObservation(
            v66,
            CleanV67Gate(v66),
            new ShadowCandidateMergePreviewObservationOptions
            {
                GateMode = true,
                RuntimeMutated = true
            });

        Assert.IsFalse(report.ObservationPassed);
        Assert.AreEqual(ShadowCandidateMergePreviewObservationRecommendations.BlockedByRuntimeInvariant, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RuntimeOrFormalInvariantChanged");
    }

    [TestMethod]
    public void ShadowCandidateMergePreviewObservation_SourceDoesNotSpecialCaseSamplesOrItems()
    {
        var source = File.ReadAllText(ResolveRepoFile("src", "ContextCore.Core", "Services", "Vector", "ShadowCandidateMergePreviewObservationRunner.cs"));
        Assert.IsFalse(source.Contains("sample.SampleId ==", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("item.ItemId ==", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("mustHitItemId ==", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-shadow", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-audit", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-repair", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ShadowMergeStabilityFreeze_PassesWithCleanObservation()
    {
        var v66 = CleanV66Gate();
        var v67 = CleanV67Gate(v66);
        var observation = CleanObservationGate(v66, v67);
        var report = new ShadowMergeStabilityFreezeRunner().BuildFreeze(
            v66,
            v67,
            observation,
            CleanRuntimeChangeGate());

        Assert.IsTrue(report.FreezePassed, string.Join(",", report.BlockedReasons));
        Assert.IsFalse(report.PromotionDecisionPassed);
        Assert.AreEqual(ShadowMergeStabilityFreezeRecommendations.ReadyForShadowMergePromotionDecision, report.Recommendation);
        Assert.AreEqual(ShadowMergePromotionDecisions.ReadyForControlledMergeProposal, report.PromotionDecision);
        Assert.AreEqual("ControlledMergeProposal", report.NextAllowedPhase);
        Assert.AreEqual(10, report.ObservationRunCount);
        Assert.IsTrue(report.SampleObservationCount >= 120);
        Assert.IsTrue(report.DeterministicPreviewStable);
        Assert.AreEqual(1, report.DistinctStableSignatureCount);
        Assert.AreEqual(report.PreviewAddCountMin, report.PreviewAddCountMax);
        Assert.AreEqual(report.PreviewRemoveCountMin, report.PreviewRemoveCountMax);
        Assert.AreEqual(0, report.RiskAfterPolicyMax);
        Assert.AreEqual(0, report.AppliedAddCountMax);
        Assert.AreEqual(0, report.AppliedRemoveCountMax);
        Assert.IsFalse(report.FormalSelectedSetChanged);
        Assert.AreEqual(0, report.FormalOutputChangedMax);
        Assert.IsFalse(report.PackageOutputChanged);
        Assert.IsFalse(report.PackingPolicyChanged);
        Assert.IsFalse(report.RuntimeMutated);
        Assert.IsFalse(report.VectorStoreBindingChanged);
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.RuntimeSwitchAllowed);
        Assert.IsFalse(report.ReadyForRuntimeSwitch);
    }

    [TestMethod]
    public void ShadowMergePromotionDecision_PassesAfterFreezeInputs()
    {
        var v66 = CleanV66Gate();
        var v67 = CleanV67Gate(v66);
        var report = new ShadowMergeStabilityFreezeRunner().BuildPromotionDecision(
            v66,
            v67,
            CleanObservationGate(v66, v67),
            CleanRuntimeChangeGate());

        Assert.IsTrue(report.FreezePassed, string.Join(",", report.BlockedReasons));
        Assert.IsTrue(report.PromotionDecisionPassed);
        Assert.AreEqual(ShadowMergeStabilityFreezeRecommendations.ReadyForControlledMergeProposal, report.Recommendation);
        Assert.AreEqual(ShadowMergePromotionDecisions.ReadyForControlledMergeProposal, report.PromotionDecision);
        Assert.AreEqual("ControlledMergeProposalOnly", report.AllowedMode);
        CollectionAssert.Contains(report.ForbiddenActions.ToList(), "applied merge");
        CollectionAssert.Contains(report.ForbiddenActions.ToList(), "runtime switch");
    }

    [TestMethod]
    public void ShadowMergeStabilityFreeze_MissingObservationBlocks()
    {
        var v66 = CleanV66Gate();
        var report = new ShadowMergeStabilityFreezeRunner().BuildFreeze(
            v66,
            CleanV67Gate(v66),
            null,
            CleanRuntimeChangeGate());

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(ShadowMergeStabilityFreezeRecommendations.BlockedByMissingGate, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "ObservationGateMissingOrNotPassed");
    }

    [TestMethod]
    public void ShadowMergeStabilityFreeze_RiskBlocks()
    {
        var v66 = CleanV66Gate();
        var v67 = CleanV67Gate(v66);
        var report = new ShadowMergeStabilityFreezeRunner().BuildFreeze(
            v66,
            v67,
            WithObservationRisk(CleanObservationGate(v66, v67)),
            CleanRuntimeChangeGate());

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(ShadowMergeStabilityFreezeRecommendations.BlockedByRisk, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RiskDetected");
    }

    [TestMethod]
    public void ShadowMergeStabilityFreeze_RuntimeSwitchAttemptBlocks()
    {
        var v66 = CleanV66Gate();
        var v67 = CleanV67Gate(v66);
        var report = new ShadowMergeStabilityFreezeRunner().BuildPromotionDecision(
            v66,
            v67,
            CleanObservationGate(v66, v67),
            CleanRuntimeChangeGate(),
            new ShadowMergeStabilityFreezeOptions { RuntimeSwitchAllowed = true });

        Assert.IsFalse(report.FreezePassed);
        Assert.IsFalse(report.PromotionDecisionPassed);
        Assert.AreEqual(ShadowMergeStabilityFreezeRecommendations.BlockedByRuntimeInvariant, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RuntimeSwitchAttemptDetected");
    }

    [TestMethod]
    public void ShadowMergeStabilityFreeze_SourceDoesNotSpecialCaseSamplesOrItems()
    {
        var source = File.ReadAllText(ResolveRepoFile("src", "ContextCore.Core", "Services", "Vector", "ShadowMergeStabilityFreezeRunner.cs"));
        Assert.IsFalse(source.Contains("sample.SampleId ==", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("item.ItemId ==", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("mustHitItemId ==", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-shadow", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-audit", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-repair", StringComparison.Ordinal));
    }
    [TestMethod]
    public void ControlledShadowMergeProposal_GatePassesAfterPromotionDecision()
    {
        var v66 = CleanV66Gate();
        var v67 = CleanV67Gate(v66);
        var promotion = CleanPromotionDecision(v66, v67);
        var report = new ControlledShadowMergeProposalRunner().BuildGate(
            v66,
            v67,
            CleanObservationGate(v66, v67),
            promotion,
            CleanRuntimeChangeGate(),
            CleanControlledShadowMergeProposalOptions());

        Assert.IsTrue(report.ProposalPassed, string.Join(",", report.BlockedReasons));
        Assert.IsTrue(report.GatePassed);
        Assert.AreEqual(ControlledShadowMergeProposalRecommendations.ReadyForControlledMergePreviewPlan, report.Recommendation);
        Assert.AreEqual("ControlledMergePreviewPlan", report.NextAllowedPhase);
        Assert.IsTrue(report.ScopeCount > 0);
        Assert.IsTrue(report.RollbackPlanPresent);
        Assert.IsTrue(report.KillSwitchPlanPresent);
        Assert.AreEqual(0, report.RiskAfterPolicy);
        Assert.AreEqual(0, report.AppliedAddCount);
        Assert.AreEqual(0, report.AppliedRemoveCount);
        Assert.IsFalse(report.FormalPackageWritten);
        Assert.IsFalse(report.PackageOutputChanged);
        Assert.IsFalse(report.PackingPolicyChanged);
        Assert.IsFalse(report.RuntimeMutated);
        Assert.IsFalse(report.VectorStoreBindingChanged);
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.RuntimeSwitchAllowed);
        Assert.IsFalse(report.ReadyForRuntimeSwitch);
    }

    [TestMethod]
    public void ControlledShadowMergeProposal_MissingScopeBlocks()
    {
        var v66 = CleanV66Gate();
        var v67 = CleanV67Gate(v66);
        var report = new ControlledShadowMergeProposalRunner().BuildGate(
            v66,
            v67,
            CleanObservationGate(v66, v67),
            CleanPromotionDecision(v66, v67),
            CleanRuntimeChangeGate(),
            new ControlledShadowMergeProposalOptions());

        Assert.IsFalse(report.ProposalPassed);
        Assert.AreEqual(ControlledShadowMergeProposalRecommendations.NeedsScopeConfiguration, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "SelectedScopeNotConfigured");
    }

    [TestMethod]
    public void ControlledShadowMergeProposal_RuntimeMutationAttemptBlocks()
    {
        var v66 = CleanV66Gate();
        var v67 = CleanV67Gate(v66);
        var report = new ControlledShadowMergeProposalRunner().BuildGate(
            v66,
            v67,
            CleanObservationGate(v66, v67),
            CleanPromotionDecision(v66, v67),
            CleanRuntimeChangeGate(),
            CleanControlledShadowMergeProposalOptions(runtimeMutated: true));

        Assert.IsFalse(report.ProposalPassed);
        Assert.AreEqual(ControlledShadowMergeProposalRecommendations.BlockedByRuntimeInvariant, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RuntimeOrFormalInvariantChanged");
    }

    [TestMethod]
    public void ControlledShadowMergeProposal_ForbiddenAppliedMergeBlocks()
    {
        var v66 = CleanV66Gate();
        var v67 = CleanV67Gate(v66);
        var report = new ControlledShadowMergeProposalRunner().BuildGate(
            v66,
            v67,
            CleanObservationGate(v66, v67),
            CleanPromotionDecision(v66, v67),
            CleanRuntimeChangeGate(),
            CleanControlledShadowMergeProposalOptions(allowAppliedMerge: true));

        Assert.IsFalse(report.ProposalPassed);
        Assert.AreEqual(ControlledShadowMergeProposalRecommendations.BlockedByRuntimeInvariant, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "ForbiddenActionAllowed");
    }


    [TestMethod]
    public void ControlledShadowMergeDryRunGate_AppliesProposalConstraints()
    {
        var v66 = CleanV66Gate();
        var v67 = CleanV67Gate(v66);
        var observation = CleanObservationGate(v66, v67);
        var proposal = CleanControlledShadowMergeProposalReport(v66, v67, observation);

        var report = new ControlledShadowMergeDryRunGateRunner().RunGate(
            proposal,
            v66,
            v67,
            observation,
            CleanRuntimeChangeGate());

        Assert.IsTrue(report.DryRunPassed, string.Join(",", report.BlockedReasons));
        Assert.IsTrue(report.GatePassed);
        Assert.IsTrue(report.ProposalConstraintsApplied);
        Assert.IsTrue(report.RequestDurationErrorLimitEnforced);
        Assert.IsTrue(report.ObservationWindowLimitEnforced);
        Assert.IsTrue(report.ObservationPlanConstraintPresent);
        Assert.IsTrue(report.AddRemoveLimitEnforced);
        Assert.IsTrue(report.TokenSectionPriorityGatePassed);
        Assert.IsTrue(report.RollbackVerified);
        Assert.IsTrue(report.KillSwitchVerified);
        Assert.AreEqual(0, report.AppliedAddCount);
        Assert.AreEqual(0, report.AppliedRemoveCount);
        Assert.AreEqual(0, report.FormalOutputChanged);
        Assert.IsFalse(report.FormalSelectedSetChanged);
        Assert.IsFalse(report.PackageOutputChanged);
        Assert.IsFalse(report.PackingPolicyChanged);
        Assert.IsFalse(report.RuntimeMutated);
        Assert.IsFalse(report.VectorStoreBindingChanged);
    }

    [TestMethod]
    public void ControlledShadowMergeDryRunGate_TokenSectionPriorityBlocks()
    {
        var v66 = CleanV66Gate();
        var v67 = CleanV67Gate(v66);
        var observation = CleanObservationGate(v66, v67);
        var proposal = CleanControlledShadowMergeProposalReport(v66, v67, observation);

        var report = new ControlledShadowMergeDryRunGateRunner().RunGate(
            proposal,
            v66,
            v67,
            observation,
            CleanRuntimeChangeGate(),
            new ControlledShadowMergeDryRunGateOptions { SimulatedPriorityInversionCount = 1 });

        Assert.IsFalse(report.DryRunPassed);
        Assert.AreEqual(ControlledShadowMergeDryRunGateRecommendations.BlockedByTokenSectionPriority, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "TokenSectionPriorityGateViolation");
    }

    [TestMethod]
    public void ControlledShadowMergeDryRunGate_RollbackOrKillSwitchBlocks()
    {
        var v66 = CleanV66Gate();
        var v67 = CleanV67Gate(v66);
        var observation = CleanObservationGate(v66, v67);
        var proposal = CleanControlledShadowMergeProposalReport(v66, v67, observation);

        var report = new ControlledShadowMergeDryRunGateRunner().RunGate(
            proposal,
            v66,
            v67,
            observation,
            CleanRuntimeChangeGate(),
            new ControlledShadowMergeDryRunGateOptions { KillSwitchUnavailable = true });

        Assert.IsFalse(report.DryRunPassed);
        Assert.AreEqual(ControlledShadowMergeDryRunGateRecommendations.BlockedByRollbackOrKillSwitch, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "KillSwitchUnavailable");
    }

    [TestMethod]
    public void ControlledShadowMergeDryRunGate_AppliedDeltaBlocks()
    {
        var v66 = CleanV66Gate();
        var v67 = CleanV67Gate(v66);
        var observation = CleanObservationGate(v66, v67);
        var proposal = CleanControlledShadowMergeProposalReport(v66, v67, observation);

        var report = new ControlledShadowMergeDryRunGateRunner().RunGate(
            proposal,
            v66,
            v67,
            observation,
            CleanRuntimeChangeGate(),
            new ControlledShadowMergeDryRunGateOptions { SimulatedAppliedAddCount = 1 });

        Assert.IsFalse(report.DryRunPassed);
        Assert.AreEqual(ControlledShadowMergeDryRunGateRecommendations.BlockedByRuntimeInvariant, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "AppliedDeltaDetected");
    }

    [TestMethod]
    public void ControlledShadowMergeDryRunGate_RequestDurationErrorLimitBlocks()
    {
        var v66 = CleanV66Gate();
        var v67 = CleanV67Gate(v66);
        var observation = CleanObservationGate(v66, v67);
        var proposal = CleanControlledShadowMergeProposalReport(v66, v67, observation);

        var report = new ControlledShadowMergeDryRunGateRunner().RunGate(
            proposal,
            v66,
            v67,
            observation,
            CleanRuntimeChangeGate(),
            new ControlledShadowMergeDryRunGateOptions { SimulatedErrorCount = 1 });

        Assert.IsFalse(report.DryRunPassed);
        Assert.AreEqual(ControlledShadowMergeDryRunGateRecommendations.BlockedByConstraintViolation, report.Recommendation);
        Assert.IsFalse(report.RequestDurationErrorLimitEnforced);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RequestDurationErrorLimitViolation");
    }

    [TestMethod]
    public void ControlledShadowMergeObservationWindow_GatePassesUnderDryRunConstraints()
    {
        var v66 = CleanV66Gate();
        var v67 = CleanV67Gate(v66);
        var observation = CleanObservationGate(v66, v67);
        var proposal = CleanControlledShadowMergeProposalReport(v66, v67, observation);
        var dryRunGate = CleanControlledShadowMergeDryRunGateReport(proposal, v66, v67, observation);

        var report = new ControlledShadowMergeObservationWindowRunner().RunGate(
            proposal,
            v66,
            v67,
            observation,
            dryRunGate,
            CleanRuntimeChangeGate());

        Assert.IsTrue(report.ObservationPassed, string.Join(",", report.BlockedReasons));
        Assert.IsTrue(report.GatePassed);
        Assert.AreEqual(ControlledShadowMergeObservationWindowRecommendations.ReadyForControlledShadowMergeObservationFreeze, report.Recommendation);
        Assert.IsTrue(report.ProposalConstraintsApplied);
        Assert.AreEqual(10, report.ObservationRunCount);
        Assert.AreEqual(120, report.RequestCountTotal);
        Assert.AreEqual(120, report.MaxRequestCount);
        Assert.IsTrue(report.RequestDurationErrorWindowEnforced);
        Assert.IsTrue(report.ObservationWindowLimitEnforced);
        Assert.IsTrue(report.DeterministicDryRunStable);
        Assert.IsTrue(report.PreviewAddRemoveStable);
        Assert.IsTrue(report.AppliedDeltaZero);
        Assert.AreEqual(0, report.RiskAfterPolicyMax);
        Assert.AreEqual(0, report.MustNotHitRiskAfterPolicyMax);
        Assert.AreEqual(0, report.LifecycleRiskAfterPolicyMax);
        Assert.AreEqual(0, report.FormalOutputChangedMax);
        Assert.IsFalse(report.FormalSelectedSetChanged);
        Assert.IsFalse(report.PackageOutputChanged);
        Assert.IsFalse(report.PackingPolicyChanged);
        Assert.IsFalse(report.RuntimeMutated);
        Assert.IsFalse(report.VectorStoreBindingChanged);
    }

    [TestMethod]
    public void ControlledShadowMergeObservationWindow_InsufficientRunsBlocks()
    {
        var v66 = CleanV66Gate();
        var v67 = CleanV67Gate(v66);
        var observation = CleanObservationGate(v66, v67);
        var proposal = CleanControlledShadowMergeProposalReport(v66, v67, observation);
        var dryRunGate = CleanControlledShadowMergeDryRunGateReport(proposal, v66, v67, observation);

        var report = new ControlledShadowMergeObservationWindowRunner().RunGate(
            proposal,
            v66,
            v67,
            observation,
            dryRunGate,
            CleanRuntimeChangeGate(),
            new ControlledShadowMergeObservationWindowOptions { ObservationRunCount = 3 });

        Assert.IsFalse(report.ObservationPassed);
        Assert.AreEqual(ControlledShadowMergeObservationWindowRecommendations.BlockedByConstraintViolation, report.Recommendation);
        Assert.IsFalse(report.ObservationWindowLimitEnforced);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "ObservationWindowConstraintViolation");
    }

    [TestMethod]
    public void ControlledShadowMergeObservationWindow_AppliedDeltaBlocks()
    {
        var v66 = CleanV66Gate();
        var v67 = CleanV67Gate(v66);
        var observation = CleanObservationGate(v66, v67);
        var proposal = CleanControlledShadowMergeProposalReport(v66, v67, observation);
        var dryRunGate = CleanControlledShadowMergeDryRunGateReport(proposal, v66, v67, observation);

        var report = new ControlledShadowMergeObservationWindowRunner().RunGate(
            proposal,
            v66,
            v67,
            observation,
            dryRunGate,
            CleanRuntimeChangeGate(),
            new ControlledShadowMergeObservationWindowOptions { SimulatedAppliedAddCount = 1 });

        Assert.IsFalse(report.ObservationPassed);
        Assert.AreEqual(ControlledShadowMergeObservationWindowRecommendations.BlockedByRuntimeInvariant, report.Recommendation);
        Assert.IsFalse(report.AppliedDeltaZero);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "AppliedDeltaDetected");
    }

    [TestMethod]
    public void ControlledShadowMergeObservationWindow_RequestWindowBlocks()
    {
        var v66 = CleanV66Gate();
        var v67 = CleanV67Gate(v66);
        var observation = CleanObservationGate(v66, v67);
        var proposal = CleanControlledShadowMergeProposalReport(v66, v67, observation);
        var dryRunGate = CleanControlledShadowMergeDryRunGateReport(proposal, v66, v67, observation);

        var report = new ControlledShadowMergeObservationWindowRunner().RunGate(
            proposal,
            v66,
            v67,
            observation,
            dryRunGate,
            CleanRuntimeChangeGate(),
            new ControlledShadowMergeObservationWindowOptions { ObservationRunCount = 11 });

        Assert.IsFalse(report.ObservationPassed);
        Assert.AreEqual(ControlledShadowMergeObservationWindowRecommendations.BlockedByConstraintViolation, report.Recommendation);
        Assert.IsFalse(report.RequestDurationErrorWindowEnforced);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RequestDurationErrorWindowViolation");
    }

    [TestMethod]
    public void ControlledShadowMergeObservationWindow_SourceDoesNotSpecialCaseSamplesOrItems()
    {
        var source = File.ReadAllText(ResolveRepoFile("src", "ContextCore.Core", "Services", "Vector", "ControlledShadowMergeObservationWindowRunner.cs"));
        Assert.IsFalse(source.Contains("sample.SampleId ==", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("item.ItemId ==", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("mustHitItemId ==", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-shadow", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-audit", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-repair", StringComparison.Ordinal));
    }
    [TestMethod]
    public void ControlledShadowMergeDryRunGate_SourceDoesNotSpecialCaseSamplesOrItems()
    {
        var source = File.ReadAllText(ResolveRepoFile("src", "ContextCore.Core", "Services", "Vector", "ControlledShadowMergeDryRunGateRunner.cs"));
        Assert.IsFalse(source.Contains("sample.SampleId ==", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("item.ItemId ==", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("mustHitItemId ==", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-shadow", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-audit", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-repair", StringComparison.Ordinal));
    }
    [TestMethod]
    public void ControlledShadowMergeProposal_SourceDoesNotSpecialCaseSamplesOrItems()
    {
        var source = File.ReadAllText(ResolveRepoFile("src", "ContextCore.Core", "Services", "Vector", "ControlledShadowMergeProposalRunner.cs"));
        Assert.IsFalse(source.Contains("sample.SampleId ==", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("item.ItemId ==", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("mustHitItemId ==", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-shadow", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-audit", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-repair", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ControlledShadowMergeFreeze_PassesAndKeepsRuntimeDisabled()
    {
        var v66 = CleanV66Gate();
        var v67 = CleanV67Gate(v66);
        var observation = CleanObservationGate(v66, v67);
        var proposal = CleanControlledShadowMergeProposalReport(v66, v67, observation);
        var dryRunGate = CleanControlledShadowMergeDryRunGateReport(proposal, v66, v67, observation);
        var windowGate = CleanControlledShadowMergeObservationWindowGateReport(proposal, v66, v67, observation, dryRunGate);

        var report = new ControlledShadowMergeFreezeRunner().BuildPromotionDecision(
            windowGate,
            CleanRuntimeChangeGate());

        Assert.IsTrue(report.FreezePassed, string.Join(",", report.BlockedReasons));
        Assert.IsTrue(report.PromotionDecisionPassed);
        Assert.AreEqual(ControlledShadowMergeFreezeRecommendations.ReadyForControlledAppliedMergeProposal, report.Recommendation);
        Assert.AreEqual(ControlledShadowMergePromotionDecisions.ReadyForControlledAppliedMergeProposal, report.PromotionDecision);
        Assert.AreEqual("ControlledAppliedMergeProposal", report.NextAllowedPhase);
        Assert.IsTrue(report.ProposalConstraintsApplied);
        Assert.IsTrue(report.RequestDurationErrorWindowEnforced);
        Assert.IsTrue(report.ObservationWindowLimitEnforced);
        Assert.IsTrue(report.DeterministicDryRunStable);
        Assert.IsTrue(report.PreviewAddRemoveStable);
        Assert.AreEqual(report.PreviewAddCountMin, report.PreviewAddCountMax);
        Assert.AreEqual(report.PreviewRemoveCountMin, report.PreviewRemoveCountMax);
        Assert.AreEqual(0, report.AppliedAddCountMax);
        Assert.AreEqual(0, report.AppliedRemoveCountMax);
        Assert.AreEqual(0, report.RiskAfterPolicyMax);
        Assert.AreEqual(0, report.MustNotHitRiskAfterPolicyMax);
        Assert.AreEqual(0, report.LifecycleRiskAfterPolicyMax);
        Assert.AreEqual(0, report.FormalOutputChangedMax);
        Assert.IsFalse(report.FormalSelectedSetChanged);
        Assert.IsFalse(report.FormalPackageWritten);
        Assert.IsFalse(report.PackageOutputChanged);
        Assert.IsFalse(report.PackingPolicyChanged);
        Assert.IsFalse(report.RuntimeMutated);
        Assert.IsFalse(report.VectorStoreBindingChanged);
        Assert.IsFalse(report.UseForRuntime);
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.RuntimeSwitchAllowed);
        Assert.IsFalse(report.ReadyForRuntimeSwitch);
        CollectionAssert.Contains(report.ForbiddenActions.ToList(), "applied merge");
        CollectionAssert.Contains(report.ForbiddenActions.ToList(), "runtime switch");
    }

    [TestMethod]
    public void ControlledShadowMergeFreeze_MissingObservationBlocks()
    {
        var report = new ControlledShadowMergeFreezeRunner().BuildFreeze(
            null,
            CleanRuntimeChangeGate());

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(ControlledShadowMergeFreezeRecommendations.BlockedByMissingGate, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "ObservationWindowGateMissingOrNotPassed");
    }

    [TestMethod]
    public void ControlledShadowMergeFreeze_RiskBlocks()
    {
        var v66 = CleanV66Gate();
        var v67 = CleanV67Gate(v66);
        var observation = CleanObservationGate(v66, v67);
        var proposal = CleanControlledShadowMergeProposalReport(v66, v67, observation);
        var dryRunGate = CleanControlledShadowMergeDryRunGateReport(proposal, v66, v67, observation);
        var windowGate = CleanControlledShadowMergeObservationWindowGateReport(proposal, v66, v67, observation, dryRunGate);

        var report = new ControlledShadowMergeFreezeRunner().BuildFreeze(
            windowGate,
            CleanRuntimeChangeGate(),
            new ControlledShadowMergeFreezeOptions { RiskAfterPolicyMax = 1 });

        Assert.IsFalse(report.FreezePassed);
        Assert.AreEqual(ControlledShadowMergeFreezeRecommendations.BlockedByRisk, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "RiskDetected");
    }

    [TestMethod]
    public void ControlledShadowMergeFreeze_AppliedDeltaBlocks()
    {
        var v66 = CleanV66Gate();
        var v67 = CleanV67Gate(v66);
        var observation = CleanObservationGate(v66, v67);
        var proposal = CleanControlledShadowMergeProposalReport(v66, v67, observation);
        var dryRunGate = CleanControlledShadowMergeDryRunGateReport(proposal, v66, v67, observation);
        var windowGate = CleanControlledShadowMergeObservationWindowGateReport(proposal, v66, v67, observation, dryRunGate);

        var report = new ControlledShadowMergeFreezeRunner().BuildPromotionDecision(
            windowGate,
            CleanRuntimeChangeGate(),
            new ControlledShadowMergeFreezeOptions { AppliedAddCountMax = 1 });

        Assert.IsFalse(report.FreezePassed);
        Assert.IsFalse(report.PromotionDecisionPassed);
        Assert.AreEqual(ControlledShadowMergeFreezeRecommendations.BlockedByRuntimeInvariant, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "AppliedDeltaDetected");
    }

    [TestMethod]
    public void ControlledShadowMergeFreeze_SourceDoesNotSpecialCaseSamplesOrItems()
    {
        var source = File.ReadAllText(ResolveRepoFile("src", "ContextCore.Core", "Services", "Vector", "ControlledShadowMergeFreezeRunner.cs"));
        Assert.IsFalse(source.Contains("sample.SampleId ==", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("item.ItemId ==", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("mustHitItemId ==", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-shadow", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-audit", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-repair", StringComparison.Ordinal));
    }    [TestMethod]
    public void ControlledAppliedMergeProposal_GatePassesAfterV613PromotionDecision()
    {
        var v66 = CleanV66Gate();
        var v67 = CleanV67Gate(v66);
        var observation = CleanObservationGate(v66, v67);
        var proposal = CleanControlledShadowMergeProposalReport(v66, v67, observation);
        var dryRunGate = CleanControlledShadowMergeDryRunGateReport(proposal, v66, v67, observation);
        var windowGate = CleanControlledShadowMergeObservationWindowGateReport(proposal, v66, v67, observation, dryRunGate);
        var promotionDecision = new ControlledShadowMergeFreezeRunner().BuildPromotionDecision(windowGate, CleanRuntimeChangeGate());

        var report = new ControlledAppliedMergeProposalRunner().BuildGate(
            promotionDecision,
            CleanRuntimeChangeGate(),
            CleanControlledAppliedMergeProposalOptions());

        Assert.IsTrue(report.ProposalPassed, string.Join(",", report.BlockedReasons));
        Assert.IsTrue(report.GatePassed);
        Assert.AreEqual(ControlledAppliedMergeProposalRecommendations.ReadyForControlledAppliedMergeDryRunGate, report.Recommendation);
        Assert.AreEqual(ControlledAppliedMergeApprovalModes.ControlledAppliedMergePreview, report.RequiredApprovalMode);
        Assert.AreEqual("ControlledAppliedMergeDryRunGate", report.NextAllowedPhase);
        Assert.AreEqual(7, report.StablePreviewAddCount);
        Assert.AreEqual(7, report.StablePreviewRemoveCount);
        Assert.AreEqual(0, report.AppliedAddCount);
        Assert.AreEqual(0, report.AppliedRemoveCount);
        Assert.IsTrue(report.ManualApprovalRequired);
        Assert.IsTrue(report.ApprovalPlanPresent);
        Assert.IsTrue(report.RollbackPlanPresent);
        Assert.IsTrue(report.KillSwitchPlanPresent);
        Assert.IsFalse(report.AppliedMergeAllowed);
        Assert.IsFalse(report.FormalSelectedSetChanged);
        Assert.IsFalse(report.FormalPackageWritten);
        Assert.IsFalse(report.PackageOutputChanged);
        Assert.IsFalse(report.PackingPolicyChanged);
        Assert.IsFalse(report.RuntimeMutated);
        Assert.IsFalse(report.VectorStoreBindingChanged);
        Assert.IsFalse(report.UseForRuntime);
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.RuntimeSwitchAllowed);
        Assert.IsFalse(report.ReadyForRuntimeSwitch);
        CollectionAssert.Contains(report.ForbiddenActions.ToList(), "applied merge before explicit later gate");
    }

    [TestMethod]
    public void ControlledAppliedMergeProposal_MissingApprovalPlanBlocks()
    {
        var v66 = CleanV66Gate();
        var v67 = CleanV67Gate(v66);
        var observation = CleanObservationGate(v66, v67);
        var proposal = CleanControlledShadowMergeProposalReport(v66, v67, observation);
        var dryRunGate = CleanControlledShadowMergeDryRunGateReport(proposal, v66, v67, observation);
        var windowGate = CleanControlledShadowMergeObservationWindowGateReport(proposal, v66, v67, observation, dryRunGate);
        var promotionDecision = new ControlledShadowMergeFreezeRunner().BuildPromotionDecision(windowGate, CleanRuntimeChangeGate());

        var report = new ControlledAppliedMergeProposalRunner().BuildGate(
            promotionDecision,
            CleanRuntimeChangeGate(),
            CleanControlledAppliedMergeProposalOptions(manualApprovalRequired: false));

        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(ControlledAppliedMergeProposalRecommendations.BlockedByMissingApprovalPlan, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "ApprovalPlanMissing");
    }

    [TestMethod]
    public void ControlledAppliedMergeProposal_MissingScopeBlocks()
    {
        var report = new ControlledAppliedMergeProposalRunner().BuildGate(
            null,
            CleanRuntimeChangeGate(),
            new ControlledAppliedMergeProposalOptions());

        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(ControlledAppliedMergeProposalRecommendations.NeedsScopeConfiguration, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "SelectedScopeNotConfigured");
    }

    [TestMethod]
    public void ControlledAppliedMergeProposal_AppliedMergeAttemptBlocks()
    {
        var v66 = CleanV66Gate();
        var v67 = CleanV67Gate(v66);
        var observation = CleanObservationGate(v66, v67);
        var proposal = CleanControlledShadowMergeProposalReport(v66, v67, observation);
        var dryRunGate = CleanControlledShadowMergeDryRunGateReport(proposal, v66, v67, observation);
        var windowGate = CleanControlledShadowMergeObservationWindowGateReport(proposal, v66, v67, observation, dryRunGate);
        var promotionDecision = new ControlledShadowMergeFreezeRunner().BuildPromotionDecision(windowGate, CleanRuntimeChangeGate());

        var report = new ControlledAppliedMergeProposalRunner().BuildGate(
            promotionDecision,
            CleanRuntimeChangeGate(),
            CleanControlledAppliedMergeProposalOptions(allowAppliedMerge: true));

        Assert.IsFalse(report.GatePassed);
        Assert.AreEqual(ControlledAppliedMergeProposalRecommendations.BlockedByRuntimeInvariant, report.Recommendation);
        CollectionAssert.Contains(report.BlockedReasons.ToList(), "ForbiddenActionAllowed");
    }

    [TestMethod]
    public void ControlledAppliedMergeProposal_SourceDoesNotSpecialCaseSamplesOrItems()
    {
        var source = File.ReadAllText(ResolveRepoFile("src", "ContextCore.Core", "Services", "Vector", "ControlledAppliedMergeProposalRunner.cs"));
        Assert.IsFalse(source.Contains("sample.SampleId ==", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("item.ItemId ==", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("mustHitItemId ==", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-shadow", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-audit", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("sample-repair", StringComparison.Ordinal));
    }
    private static SourceDiverseShadowAdapterValidationReport CleanV66Gate() =>
        new SourceDiverseShadowAdapterValidationRunner().RunValidation(
            new ShadowAdapterDeltaDiagnosticsReport
            {
                DiagnosticsPassed = true,
                Recommendations = "ReadyForShadowAdapterDeltaTriage"
            },
            options: new SourceDiverseShadowAdapterValidationOptions { GateMode = true });

    private static ShadowCandidateMergePreviewReport CleanV67Gate(SourceDiverseShadowAdapterValidationReport v66) =>
        new ShadowCandidateMergePreviewRunner().RunPreview(
            v66,
            new ShadowCandidateMergePreviewOptions { GateMode = true });

    private static SourceDiverseShadowAdapterValidationReport WithAppliedDelta(SourceDiverseShadowAdapterValidationReport source) => new()
    {
        ValidationPassed = source.ValidationPassed,
        GatePassed = source.GatePassed,
        Recommendation = source.Recommendation,
        V65GatePassed = source.V65GatePassed,
        ValidationSetSourceDiverse = source.ValidationSetSourceDiverse,
        AllowlistedScopeMetadataPresent = source.AllowlistedScopeMetadataPresent,
        SampleCount = source.SampleCount,
        BaselineCandidateCount = source.BaselineCandidateCount,
        ShadowExpandedCandidateCount = source.ShadowExpandedCandidateCount,
        ShadowFinalCandidateCount = source.ShadowFinalCandidateCount,
        ShadowOnlyCount = source.ShadowOnlyCount,
        HypotheticalAddCount = source.HypotheticalAddCount,
        HypotheticalRemoveCount = source.HypotheticalRemoveCount,
        AppliedAddCount = 1,
        AppliedRemoveCount = source.AppliedRemoveCount,
        RiskAfterPolicy = source.RiskAfterPolicy,
        MustNotHitRiskAfterPolicy = source.MustNotHitRiskAfterPolicy,
        LifecycleRiskAfterPolicy = source.LifecycleRiskAfterPolicy,
        SampleResults = source.SampleResults
    };


    private static ShadowCandidateMergePreviewObservationReport CleanObservationGate(
        SourceDiverseShadowAdapterValidationReport v66,
        ShadowCandidateMergePreviewReport v67) =>
        new ShadowCandidateMergePreviewObservationRunner().RunObservation(
            v66,
            v67,
            new ShadowCandidateMergePreviewObservationOptions
            {
                GateMode = true,
                ObservationRunCount = 10,
                MinimumObservationRunCount = 3
            });

    private static LearningRuntimeChangeReadinessGateReport CleanRuntimeChangeGate() => new()
    {
        Passed = true,
        Recommendation = "RuntimeChangeRulesSatisfied"
    };

    private static ShadowMergeStabilityFreezeReport CleanPromotionDecision(
        SourceDiverseShadowAdapterValidationReport v66,
        ShadowCandidateMergePreviewReport v67) =>
        new ShadowMergeStabilityFreezeRunner().BuildPromotionDecision(
            v66,
            v67,
            CleanObservationGate(v66, v67),
            CleanRuntimeChangeGate());


    private static ControlledShadowMergeDryRunGateReport CleanControlledShadowMergeDryRunGateReport(
        ControlledShadowMergeProposalReport proposal,
        SourceDiverseShadowAdapterValidationReport v66,
        ShadowCandidateMergePreviewReport v67,
        ShadowCandidateMergePreviewObservationReport observation) =>
        new ControlledShadowMergeDryRunGateRunner().RunGate(
            proposal,
            v66,
            v67,
            observation,
            CleanRuntimeChangeGate());
    private static ControlledShadowMergeObservationWindowReport CleanControlledShadowMergeObservationWindowGateReport(
        ControlledShadowMergeProposalReport proposal,
        SourceDiverseShadowAdapterValidationReport v66,
        ShadowCandidateMergePreviewReport v67,
        ShadowCandidateMergePreviewObservationReport observation,
        ControlledShadowMergeDryRunGateReport dryRunGate) =>
        new ControlledShadowMergeObservationWindowRunner().RunGate(
            proposal,
            v66,
            v67,
            observation,
            dryRunGate,
            CleanRuntimeChangeGate());
    private static ControlledShadowMergeProposalReport CleanControlledShadowMergeProposalReport(
        SourceDiverseShadowAdapterValidationReport v66,
        ShadowCandidateMergePreviewReport v67,
        ShadowCandidateMergePreviewObservationReport observation) =>
        new ControlledShadowMergeProposalRunner().BuildGate(
            v66,
            v67,
            observation,
            CleanPromotionDecision(v66, v67),
            CleanRuntimeChangeGate(),
            CleanControlledShadowMergeProposalOptions());
    private static ControlledAppliedMergeProposalOptions CleanControlledAppliedMergeProposalOptions(
        bool allowAppliedMerge = false,
        bool manualApprovalRequired = true) => new()
    {
        WorkspaceAllowlist = ["contextcore-foundation"],
        CollectionAllowlist = ["source-diverse-shadow-validation"],
        EvalScopeAllowlist = ["v6-source-diverse-shadow-validation"],
        AllowAppliedMerge = allowAppliedMerge,
        ManualApprovalRequired = manualApprovalRequired
    };

    private static ControlledShadowMergeProposalOptions CleanControlledShadowMergeProposalOptions(
        bool runtimeMutated = false,
        bool allowAppliedMerge = false) => new()
    {
        WorkspaceAllowlist = ["contextcore-foundation"],
        CollectionAllowlist = ["source-diverse-shadow-validation"],
        EvalScopeAllowlist = ["v6-source-diverse-shadow-validation"],
        RuntimeMutated = runtimeMutated,
        AllowAppliedMerge = allowAppliedMerge
    };
    private static ShadowCandidateMergePreviewObservationReport WithObservationRisk(ShadowCandidateMergePreviewObservationReport source) => new()
    {
        OperationId = source.OperationId,
        CreatedAt = source.CreatedAt,
        ObservationPassed = source.ObservationPassed,
        GatePassed = source.GatePassed,
        Recommendation = source.Recommendation,
        V67GatePassed = source.V67GatePassed,
        ObservationRunCount = source.ObservationRunCount,
        MinimumObservationRunCount = source.MinimumObservationRunCount,
        SampleObservationCount = source.SampleObservationCount,
        FailedRunCount = source.FailedRunCount,
        DistinctStableSignatureCount = source.DistinctStableSignatureCount,
        DeterministicPreviewStable = source.DeterministicPreviewStable,
        PreviewAddRemoveStable = source.PreviewAddRemoveStable,
        PreviewAddCountMin = source.PreviewAddCountMin,
        PreviewAddCountMax = source.PreviewAddCountMax,
        PreviewRemoveCountMin = source.PreviewRemoveCountMin,
        PreviewRemoveCountMax = source.PreviewRemoveCountMax,
        PreviewAddCountTotal = source.PreviewAddCountTotal,
        PreviewRemoveCountTotal = source.PreviewRemoveCountTotal,
        AppliedAddCountMax = source.AppliedAddCountMax,
        AppliedRemoveCountMax = source.AppliedRemoveCountMax,
        RiskAfterPolicyMax = 1,
        MustNotHitRiskAfterPolicyMax = source.MustNotHitRiskAfterPolicyMax,
        LifecycleRiskAfterPolicyMax = source.LifecycleRiskAfterPolicyMax,
        TokenDeltaTotalMax = source.TokenDeltaTotalMax,
        TokenDeltaMaxMax = source.TokenDeltaMaxMax,
        TokenDeltaWithinBudget = source.TokenDeltaWithinBudget,
        PriorityInversionCountTotal = source.PriorityInversionCountTotal,
        SectionMismatchCountTotal = source.SectionMismatchCountTotal,
        FormalSelectedSetChanged = source.FormalSelectedSetChanged,
        FormalOutputChangedMax = source.FormalOutputChangedMax,
        FormalPackageWritten = source.FormalPackageWritten,
        PackageOutputChanged = source.PackageOutputChanged,
        PackingPolicyChanged = source.PackingPolicyChanged,
        RuntimeMutated = source.RuntimeMutated,
        VectorStoreBindingChanged = source.VectorStoreBindingChanged,
        UseForRuntime = source.UseForRuntime,
        FormalRetrievalAllowed = source.FormalRetrievalAllowed,
        RuntimeSwitchAllowed = source.RuntimeSwitchAllowed,
        ReadyForRuntimeSwitch = source.ReadyForRuntimeSwitch,
        Runs = source.Runs,
        BlockedReasons = source.BlockedReasons
    };
    private static string ResolveRepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException("Repository file not found", Path.Combine(parts));
    }
}









