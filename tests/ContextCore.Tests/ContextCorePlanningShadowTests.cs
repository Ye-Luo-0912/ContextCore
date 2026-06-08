using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.ControlRoom.Services;
using ContextCore.Core.Services;
using ContextCore.Storage.InMemory;

namespace ContextCore.Tests;

[TestClass]
public sealed class ContextCorePlanningShadowTests
{
    [TestMethod]
    public async Task ShadowExecution_ShouldNotAffectLegacyOutput()
    {
        var contextStore = new InMemoryContextStore();
        await contextStore.SaveAsync(Item("ctx-shadow-1", "planning shadow legacy output boundary"));
        await contextStore.SaveAsync(Item("ctx-shadow-2", "planning shadow secondary candidate"));
        var retriever = new HybridContextRetriever(
            contextStore,
            memoryStore: null,
            relationStore: null,
            embeddingProvider: null,
            vectorStore: null,
            traceStore: null,
            attentionRerankOptions: new RetrievalAttentionRerankOptions());
        var executor = new ShadowRetrievalPlanExecutor(contextStore);
        var request = Request("planning shadow", includeVector: false);

        var legacyBefore = await retriever.RetrieveAsync(request);
        _ = await executor.ExecuteAsync(Proposal("proposal-shadow-1"), request);
        var legacyAfter = await retriever.RetrieveAsync(request);

        CollectionAssert.AreEqual(
            legacyBefore.SelectedItems.Select(item => item.SourceId).ToArray(),
            legacyAfter.SelectedItems.Select(item => item.SourceId).ToArray());
    }

    [TestMethod]
    public async Task RetrievalPlanningOptions_DefaultOff_ShouldNotChangeRetrievalOutput()
    {
        var contextStore = new InMemoryContextStore();
        await contextStore.SaveAsync(Item("ctx-default-a", "default off current task dominant dominant"));
        await contextStore.SaveAsync(Item("ctx-default-b", "default off secondary"));
        var baseline = new HybridContextRetriever(
            contextStore,
            memoryStore: null,
            relationStore: null,
            embeddingProvider: null,
            vectorStore: null,
            traceStore: null,
            attentionRerankOptions: new RetrievalAttentionRerankOptions());
        var planned = CreatePlanningRetriever(
            contextStore,
            new RetrievalPlanningOptions());
        var request = Request("current task", includeVector: false, topK: 1);

        var legacy = await baseline.RetrieveAsync(request);
        var result = await planned.RetrieveAsync(request);

        CollectionAssert.AreEqual(
            legacy.SelectedItems.Select(item => item.SourceId).ToArray(),
            result.SelectedItems.Select(item => item.SourceId).ToArray());
        Assert.AreEqual(RetrievalPlanningOptions.OffMode, result.Trace.Metadata["planningMode"]);
        Assert.AreEqual("Legacy", result.Trace.Metadata["planningExecutionStatus"]);
    }

    [TestMethod]
    public async Task RetrievalPlanning_ApplyGuarded_NonOptInIntent_ShouldUseLegacy()
    {
        var contextStore = new InMemoryContextStore();
        await contextStore.SaveAsync(Item("legacy-current-task", "当前任务 下一步 dominant dominant"));
        await contextStore.SaveAsync(Item("must-hit-current-task", "reserved candidate not matching query"));
        var request = Request("当前任务 下一步", includeVector: false, topK: 1);
        request.Metadata["attention.mustHit"] = "must-hit-current-task";
        var legacy = new HybridContextRetriever(contextStore);
        var planned = CreatePlanningRetriever(
            contextStore,
            new RetrievalPlanningOptions
            {
                Mode = RetrievalPlanningOptions.ApplyGuardedMode,
                OptInIntents = [PlanningIntentDetector.AutomationRecovery]
            });

        var legacyResult = await legacy.RetrieveAsync(request);
        var plannedResult = await planned.RetrieveAsync(request);

        CollectionAssert.AreEqual(
            legacyResult.SelectedItems.Select(item => item.SourceId).ToArray(),
            plannedResult.SelectedItems.Select(item => item.SourceId).ToArray());
        Assert.AreEqual(PlanningIntentDetector.CurrentTask, plannedResult.Trace.Metadata["planningIntent"]);
        Assert.AreEqual("false", plannedResult.Trace.Metadata["planningOptInMatched"]);
        Assert.AreEqual("Legacy", plannedResult.Trace.Metadata["planningExecutionStatus"]);
    }

    [TestMethod]
    public async Task RetrievalPlanning_ApplyGuarded_OptInCurrentTask_ShouldUseProposalPath()
    {
        var contextStore = new InMemoryContextStore();
        await contextStore.SaveAsync(Item("legacy-current-task", "当前任务 下一步 dominant dominant"));
        await contextStore.SaveAsync(Item("must-hit-current-task", "reserved candidate not matching query"));
        var request = Request("当前任务 下一步", includeVector: false, topK: 1);
        request.Metadata["attention.mustHit"] = "must-hit-current-task";
        var planned = CreatePlanningRetriever(
            contextStore,
            new RetrievalPlanningOptions
            {
                Mode = RetrievalPlanningOptions.ApplyGuardedMode,
                OptInIntents = [PlanningIntentDetector.CurrentTask]
            });

        var result = await planned.RetrieveAsync(request);

        CollectionAssert.Contains(
            result.SelectedItems.Select(item => item.SourceId).ToArray(),
            "must-hit-current-task");
        Assert.AreEqual("true", result.Trace.Metadata["planningOptInMatched"]);
        Assert.AreEqual(RetrievalPlanningOptions.ApplyGuardedMode, result.Trace.Metadata["planningExecutionStatus"]);
        Assert.AreEqual("false", result.Trace.Metadata["planningFallbackUsed"]);
        StringAssert.Contains(result.Trace.Metadata["planningFinalSelected"], "must-hit-current-task");
    }

    [TestMethod]
    public async Task RetrievalPlanning_ApplyGuarded_Violation_ShouldFallbackToLegacy()
    {
        var contextStore = new InMemoryContextStore();
        await contextStore.SaveAsync(Item("legacy-safe-current-task", "当前任务 下一步 dominant dominant"));
        await contextStore.SaveAsync(Item("must-hit-current-task", "reserved candidate not matching query"));
        var request = Request("当前任务 下一步", includeVector: false, topK: 1);
        request.Metadata["attention.mustHit"] = "must-hit-current-task";
        request.Metadata["eval.expectedConstraints"] = "missing-hard-constraint";
        var legacy = new HybridContextRetriever(contextStore);
        var planned = CreatePlanningRetriever(
            contextStore,
            new RetrievalPlanningOptions
            {
                Mode = RetrievalPlanningOptions.ApplyGuardedMode,
                OptInIntents = [PlanningIntentDetector.CurrentTask],
                FallbackToLegacyOnViolation = true
            });

        var legacyResult = await legacy.RetrieveAsync(request);
        var plannedResult = await planned.RetrieveAsync(request);

        CollectionAssert.AreEqual(
            legacyResult.SelectedItems.Select(item => item.SourceId).ToArray(),
            plannedResult.SelectedItems.Select(item => item.SourceId).ToArray());
        Assert.AreEqual("true", plannedResult.Trace.Metadata["planningFallbackUsed"]);
        StringAssert.Contains(plannedResult.Trace.Metadata["planningFallbackReason"], "hard_constraint_missing");
        Assert.AreEqual(
            string.Join(",", legacyResult.SelectedItems.Select(item => item.SourceId)),
            plannedResult.Trace.Metadata["planningFinalSelected"]);
    }

    [TestMethod]
    public async Task ShadowExecution_ShouldInjectHardConstraintsInProposalPath()
    {
        var contextStore = new InMemoryContextStore();
        var constraintStore = new InMemoryConstraintStore();
        await contextStore.SaveAsync(Item("normal-current", "当前任务 下一步 dominant dominant"));
        await constraintStore.SaveAsync(new ContextConstraint
        {
            Id = "constraint-language",
            WorkspaceId = "workspace-shadow",
            CollectionId = "collection-shadow",
            Level = ConstraintLevel.Hard,
            Status = ContextMemoryStatus.Verified,
            Scope = ContextScope.Collection,
            Content = "输出必须使用中文。",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        var request = Request("当前任务 下一步", includeVector: false, topK: 1);
        request.Metadata["eval.expectedConstraints"] = "输出必须使用中文";
        var executor = new ShadowRetrievalPlanExecutor(contextStore, constraintStore: constraintStore);

        var result = await executor.ExecuteAsync(
            Proposal("proposal-constraint-inject", finalTopK: 1),
            request);

        var selectedIds = result.ShadowSelectedItems.Select(item => item.SourceId).ToArray();
        CollectionAssert.Contains(selectedIds, "constraint-language");
        Assert.AreEqual("ConstraintRepaired", result.Diagnostics["constraintRepairStatus"]);
        Assert.AreEqual("true", result.ShadowSelectedItems.Single(item => item.SourceId == "constraint-language").Metadata["lockedConstraint"]);
        Assert.AreEqual("constraints", result.ShadowSelectedItems.Single(item => item.SourceId == "constraint-language").Metadata["section"]);
    }

    [TestMethod]
    public async Task ShadowExecution_ShouldKeepLockedConstraintsWhenBudgetIsTight()
    {
        var contextStore = new InMemoryContextStore();
        await contextStore.SaveAsync(Item("normal-current", "当前任务 下一步 dominant dominant"));
        await contextStore.SaveAsync(Item("constraint-budget", "当前任务 下一步 重试前必须进行人工确认 very long constraint body"));
        var request = Request("当前任务 下一步", includeVector: false, topK: 1);
        request.Metadata["eval.expectedConstraints"] = "重试前必须进行人工确认";
        var tightBudgetRequest = new ContextRetrievalRequest
        {
            OperationId = request.OperationId,
            WorkspaceId = request.WorkspaceId,
            CollectionId = request.CollectionId,
            QueryText = request.QueryText,
            TopK = request.TopK,
            CandidateTake = request.CandidateTake,
            IncludeKeywordRecall = request.IncludeKeywordRecall,
            IncludeVectorRecall = false,
            IncludeRelationExpansion = request.IncludeRelationExpansion,
            IncludeWorkingMemory = request.IncludeWorkingMemory,
            IncludeStableMemory = request.IncludeStableMemory,
            TokenBudget = 1,
            Metadata = request.Metadata
        };
        var executor = new ShadowRetrievalPlanExecutor(contextStore);

        var result = await executor.ExecuteAsync(
            Proposal("proposal-constraint-budget", finalTopK: 1),
            tightBudgetRequest);

        CollectionAssert.Contains(
            result.ShadowSelectedItems.Select(item => item.SourceId).ToArray(),
            "constraint-budget");
        Assert.AreEqual("true", result.ShadowSelectedItems.Single(item => item.SourceId == "constraint-budget").Metadata["mandatory"]);
        Assert.AreEqual("ConstraintRepaired", result.Diagnostics["constraintRepairStatus"]);
    }

    [TestMethod]
    public async Task RetrievalPlanning_ApplyGuarded_ConstraintMissing_ShouldRepairBeforeFallback()
    {
        var contextStore = new InMemoryContextStore();
        await contextStore.SaveAsync(Item("legacy-current-task", "当前任务 下一步 dominant dominant"));
        await contextStore.SaveAsync(Item("constraint-language", "当前任务 下一步 输出必须使用中文"));
        var request = Request("当前任务 下一步", includeVector: false, topK: 1);
        request.Metadata["eval.expectedConstraints"] = "输出必须使用中文";
        var planned = CreatePlanningRetriever(
            contextStore,
            new RetrievalPlanningOptions
            {
                Mode = RetrievalPlanningOptions.ApplyGuardedMode,
                OptInIntents = [PlanningIntentDetector.CurrentTask]
            });

        var result = await planned.RetrieveAsync(request);

        Assert.AreEqual("false", result.Trace.Metadata["planningFallbackUsed"]);
        Assert.AreEqual(RetrievalPlanningOptions.ApplyGuardedMode, result.Trace.Metadata["planningExecutionStatus"]);
        CollectionAssert.Contains(
            result.SelectedItems.Select(item => item.SourceId).ToArray(),
            "constraint-language");
        Assert.AreEqual("ConstraintRepaired", result.Trace.Metadata["planningShadow.constraintRepairStatus"]);
        StringAssert.Contains(result.Trace.Metadata["planningSafetyChecks"], "passed=true");
    }

    [TestMethod]
    public async Task ShadowExecution_ShouldDetectWrongSectionConstraint()
    {
        var contextStore = new InMemoryContextStore();
        await contextStore.SaveAsync(Item("constraint-wrong-section", "当前任务 下一步 禁止把明文 API Key 写入项目仓库"));
        var request = Request("当前任务 下一步", includeVector: false, topK: 1);
        request.Metadata["eval.expectedConstraints"] = "禁止把明文 API Key 写入项目仓库";
        var executor = new ShadowRetrievalPlanExecutor(contextStore);

        var result = await executor.ExecuteAsync(
            Proposal("proposal-constraint-section", finalTopK: 1),
            request);

        Assert.AreEqual("禁止把明文 API Key 写入项目仓库", result.Diagnostics["constraintWrongSection"]);
        Assert.AreEqual("constraints", result.ShadowSelectedItems.Single().Metadata["section"]);
        Assert.AreEqual("ConstraintRepaired", result.Diagnostics["constraintRepairStatus"]);
    }

    [TestMethod]
    public async Task RetrievalPlanning_ApplyGuarded_ShouldKeepVectorDisabledAndTraceFinalSelected()
    {
        var contextStore = new InMemoryContextStore();
        await contextStore.SaveAsync(Item("legacy-current-task", "当前任务 下一步 dominant dominant"));
        await contextStore.SaveAsync(Item("must-hit-current-task", "reserved candidate not matching query"));
        var request = Request("当前任务 下一步", includeVector: true, topK: 1);
        request.Metadata["attention.mustHit"] = "must-hit-current-task";
        var planned = CreatePlanningRetriever(
            contextStore,
            new RetrievalPlanningOptions
            {
                Mode = RetrievalPlanningOptions.ApplyGuardedMode,
                OptInIntents = [PlanningIntentDetector.CurrentTask]
            });

        var result = await planned.RetrieveAsync(request);

        Assert.AreEqual("false", result.Trace.Metadata["planningVectorEnabled"]);
        Assert.AreEqual("false", result.Trace.Metadata["planningShadow.shadowVectorEnabled"]);
        StringAssert.Contains(result.Trace.Metadata["planningFinalSelected"], "must-hit-current-task");
        Assert.IsTrue(result.Trace.Metadata.ContainsKey("planningSafetyChecks"));
    }

    [TestMethod]
    public async Task ShadowExecution_InvalidProposal_ShouldFallbackSafely()
    {
        var contextStore = new InMemoryContextStore();
        await contextStore.SaveAsync(Item("ctx-fallback", "planning fallback query"));
        var executor = new ShadowRetrievalPlanExecutor(contextStore);
        var invalidProposal = Proposal(
            "proposal-invalid",
            useExact: false,
            useKeyword: false,
            useWorkingMemory: false,
            useStableMemory: false);

        var shadow = await executor.ExecuteAsync(invalidProposal, Request("planning fallback", includeVector: false));

        Assert.AreEqual("true", shadow.Diagnostics["fallback"]);
        Assert.AreEqual("true", shadow.Diagnostics["fallbackToLegacySafePlan"]);
        CollectionAssert.Contains(shadow.Warnings.ToArray(), "invalid proposal: no retrieval channel enabled");
        CollectionAssert.Contains(shadow.Warnings.ToArray(), "invalid proposal fallback: shadow used LegacySafePlan with vector disabled");
        Assert.IsTrue(shadow.ShadowSelectedItems.Count > 0);
    }

    [TestMethod]
    public async Task ShadowExecution_ShouldKeepLifecycleFilter()
    {
        var memoryStore = new InMemoryMemoryStore();
        await memoryStore.SaveAsync(Memory("memory-active", ContextMemoryStatus.Active, "shadow lifecycle query active"));
        await memoryStore.SaveAsync(Memory("memory-deprecated", ContextMemoryStatus.Deprecated, "shadow lifecycle query deprecated"));
        var executor = new ShadowRetrievalPlanExecutor(new InMemoryContextStore(), memoryStore);
        var proposal = Proposal(
            "proposal-lifecycle",
            useKeyword: false,
            useWorkingMemory: true,
            useStableMemory: false);

        var shadow = await executor.ExecuteAsync(proposal, Request("shadow lifecycle query", includeVector: false));

        CollectionAssert.Contains(
            shadow.ShadowSelectedItems.Select(item => item.SourceId).ToArray(),
            "memory-active");
        CollectionAssert.DoesNotContain(
            shadow.ShadowSelectedItems.Select(item => item.SourceId).ToArray(),
            "memory-deprecated");
    }

    [TestMethod]
    public void RetrievalPlanProposalValidator_ShouldRejectNonAuditDeprecatedPlan()
    {
        var validator = new RetrievalPlanProposalValidator();

        var result = validator.ValidateSelectedItems(
            [Candidate("ctx-deprecated", 1, new Dictionary<string, string> { ["status"] = "deprecated" })],
            Proposal("proposal-normal"),
            Request("deprecated query", includeVector: false));

        Assert.AreEqual(0, result.SelectedItems.Count);
        Assert.IsTrue(result.RejectedReasons.Any(reason =>
            reason.StartsWith("non_audit_lifecycle_blocked:ctx-deprecated", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void RetrievalPlanProposalValidator_ShouldRejectRejectedLifecyclePlan()
    {
        var validator = new RetrievalPlanProposalValidator();

        var result = validator.ValidateSelectedItems(
            [Candidate("ctx-rejected", 1, new Dictionary<string, string> { ["lifecycleStatus"] = "Rejected" })],
            Proposal("proposal-normal"),
            Request("rejected query", includeVector: false));

        Assert.AreEqual(0, result.SelectedItems.Count);
        CollectionAssert.Contains(result.RejectedReasons.ToArray(), "rejected_lifecycle_blocked:ctx-rejected");
    }

    [TestMethod]
    public void RetrievalPlanProposalValidator_ShouldForceVectorDisabled()
    {
        var validator = new RetrievalPlanProposalValidator();

        var result = validator.Validate(
            Proposal("proposal-vector", useVector: true, vectorTopK: 5),
            Request("vector query", includeVector: false));

        Assert.IsTrue(result.ValidatorApplied);
        Assert.IsTrue(result.ValidPlan);
        Assert.IsTrue(result.RepairedPlan);
        Assert.IsFalse(result.FallbackToLegacySafePlan);
        Assert.IsFalse(result.EffectiveProposal.UseVector);
        Assert.AreEqual(0, result.EffectiveProposal.VectorTopK);
        Assert.IsTrue(result.ValidatorRepairReasons.Any(reason =>
            reason.StartsWith("validator.vector.disabled:", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void RetrievalPlanProposalValidator_ShouldRepairHighFinalTopK()
    {
        var validator = new RetrievalPlanProposalValidator();

        var result = validator.Validate(
            Proposal("proposal-high-topk", finalTopK: 50),
            Request("high topk query", includeVector: false));

        Assert.IsTrue(result.ValidPlan);
        Assert.IsTrue(result.RepairedPlan);
        Assert.IsFalse(result.FallbackToLegacySafePlan);
        Assert.AreEqual(5, result.EffectiveProposal.FinalTopK);
        Assert.IsTrue(result.FinalTopKClamped);
        Assert.IsTrue(result.ValidatorRepairReasons.Any(reason =>
            reason.StartsWith("validator.finalTopK.clamped:", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void RetrievalPlanProposalValidator_ShouldFallbackOnlyWhenRepairFails()
    {
        var validator = new RetrievalPlanProposalValidator();

        var repaired = validator.Validate(
            Proposal("proposal-repairable", finalTopK: 20, useVector: true, vectorTopK: 3),
            Request("repairable query", includeVector: false));
        var fallback = validator.Validate(
            Proposal(
                "proposal-unrepairable",
                useExact: false,
                useKeyword: false,
                useWorkingMemory: false,
                useStableMemory: false),
            Request("fallback query", includeVector: false));

        Assert.IsFalse(repaired.FallbackToLegacySafePlan);
        Assert.IsTrue(repaired.RepairedPlan);
        Assert.IsTrue(fallback.FallbackToLegacySafePlan);
        Assert.IsFalse(fallback.ValidPlan);
        CollectionAssert.Contains(fallback.RejectedPlanReasons.ToArray(), "invalid proposal: no retrieval channel enabled");
    }

    [TestMethod]
    public async Task ShadowExecution_RepairedProposal_ShouldKeepMustNotHitViolationZero()
    {
        var contextStore = new InMemoryContextStore();
        await contextStore.SaveAsync(Item("noise-item", "must not hit repair query"));
        await contextStore.SaveAsync(Item("safe-item", "repair query safe context"));
        var executor = new ShadowRetrievalPlanExecutor(contextStore);
        var request = Request("must not hit repair query", includeVector: false);
        request.Metadata["attention.mustNotHit"] = "noise-item";

        var shadow = await executor.ExecuteAsync(
            Proposal("proposal-repaired-shadow", finalTopK: 50, useVector: true, vectorTopK: 5),
            request);

        Assert.AreEqual("false", shadow.Diagnostics["fallbackToLegacySafePlan"]);
        Assert.AreEqual("true", shadow.Diagnostics["repairedPlan"]);
        Assert.AreEqual("0", shadow.Diagnostics["mustNotHitAddedAfterValidation"]);
        CollectionAssert.DoesNotContain(
            shadow.ShadowSelectedItems.Select(item => item.SourceId).ToArray(),
            "noise-item");
    }

    [TestMethod]
    public void ShadowComparison_ShouldReportMustNotHitViolation()
    {
        var comparison = ShadowRetrievalComparisonReportBuilder.BuildSample(
            new ContextEvalSample
            {
                Id = "sample-must-not-hit",
                Mode = "ChatMode",
                MustNotHit = ["noise-item"]
            },
            new ContextRetrievalResult
            {
                OperationId = "legacy-op",
                SelectedItems = []
            },
            new ShadowRetrievalResult
            {
                OperationId = "shadow-op",
                ProposalId = "proposal-1",
                ProposalSummary = "FuzzyQuestion/Chat",
                ShadowSelectedItems = [Candidate("noise-item", 1)],
                Diagnostics = new Dictionary<string, string>()
            });

        Assert.IsTrue(comparison.MustNotHitViolation);
        Assert.AreEqual(1, comparison.MustNotHitViolationCount);
        CollectionAssert.Contains(comparison.MustNotHitViolations.ToArray(), "noise-item");
    }

    [TestMethod]
    public void PlanningShadowDiffTriage_ShouldReportMustNotHitAdded()
    {
        var comparison = ShadowRetrievalComparisonReportBuilder.BuildSample(
            new ContextEvalSample
            {
                Id = "sample-triage-must-not-hit",
                Mode = "ChatMode",
                MustNotHit = ["noise-item"]
            },
            new ContextRetrievalResult
            {
                OperationId = "legacy-op",
                SelectedItems = []
            },
            new ShadowRetrievalResult
            {
                OperationId = "shadow-op",
                ProposalId = "proposal-1",
                ProposalSummary = "FuzzyQuestion/Chat",
                ShadowSelectedItems = [Candidate("noise-item", 1)],
                Diagnostics = new Dictionary<string, string>()
            });

        var report = ShadowRetrievalComparisonReportBuilder.Build("a3", [comparison]);
        var triage = PlanningShadowDiffTriageReportBuilder.Build(report);

        Assert.AreEqual(1, triage.MustNotHitAddedCount);
        Assert.AreEqual("MustNotHitAdded", triage.Samples[0].SuspectedCause);
        CollectionAssert.Contains(triage.Samples[0].MustNotHitAdded.ToArray(), "noise-item");
    }

    [TestMethod]
    public void ShadowComparison_ShouldReportAddedDroppedAndRankDelta()
    {
        var comparison = ShadowRetrievalComparisonReportBuilder.BuildSample(
            new ContextEvalSample
            {
                Id = "sample-rank",
                Mode = "ProjectMode"
            },
            new ContextRetrievalResult
            {
                OperationId = "legacy-op",
                SelectedItems =
                [
                    Candidate("legacy-only", 10),
                    Candidate("shared", 9)
                ]
            },
            new ShadowRetrievalResult
            {
                OperationId = "shadow-op",
                ProposalId = "proposal-1",
                ProposalSummary = "CurrentTask/Chat",
                ShadowSelectedItems =
                [
                    Candidate("shared", 11),
                    Candidate("shadow-only", 8)
                ],
                Diagnostics = new Dictionary<string, string>()
            });

        Assert.AreEqual(2, comparison.SelectedSetDiff);
        Assert.AreEqual(1, comparison.AddedCount);
        Assert.AreEqual(1, comparison.DroppedCount);
        CollectionAssert.Contains(comparison.AddedItems.ToArray(), "shadow-only");
        CollectionAssert.Contains(comparison.DroppedItems.ToArray(), "legacy-only");
        var sharedDelta = comparison.RankDeltas.Single(item => item.SourceId == "shared");
        Assert.AreEqual(2, sharedDelta.LegacyRank);
        Assert.AreEqual(1, sharedDelta.ShadowRank);
        Assert.AreEqual(1, sharedDelta.Delta);
    }

    [TestMethod]
    public void PlanningShadowRecallLossReport_ShouldIdentifyLostMustHit()
    {
        var comparison = ShadowRetrievalComparisonReportBuilder.Build("unit",
        [
            ShadowRetrievalComparisonReportBuilder.BuildSample(
                new ContextEvalSample
                {
                    Id = "sample-recall-loss",
                    Mode = "ChatMode",
                    MustHit = ["must-hit"]
                },
                new ContextRetrievalResult
                {
                    OperationId = "legacy-op",
                    SelectedItems = [Candidate("must-hit", 10, new Dictionary<string, string> { ["channelSources"] = "keyword,vector" })]
                },
                new ShadowRetrievalResult
                {
                    OperationId = "shadow-op",
                    ProposalId = "proposal-1",
                    ProposalSummary = "FuzzyQuestion/Chat",
                    ShadowCandidates =
                    [
                        Candidate("shadow-only", 9, new Dictionary<string, string> { ["channelSources"] = "keyword" }),
                        Candidate("must-hit", 8, new Dictionary<string, string> { ["channelSources"] = "keyword" })
                    ],
                    ShadowSelectedItems = [Candidate("shadow-only", 9, new Dictionary<string, string> { ["channelSources"] = "keyword" })],
                    Diagnostics = new Dictionary<string, string>
                    {
                        ["validatorApplied"] = "true",
                        ["validPlan"] = "true",
                        ["repairedPlan"] = "false",
                        ["fallbackToLegacySafePlan"] = "false",
                        ["auditMode"] = "false",
                        ["conflictMode"] = "false",
                        ["disabledChannels"] = "vector",
                        ["topKCaps"] = "keyword=10|memory=10|relation=0|vector=0|final=1|requestTopK=1|candidateTake=10"
                    }
                })
        ]);

        var report = PlanningShadowRecallLossReportBuilder.Build(comparison);
        var sample = report.Samples.Single();

        Assert.AreEqual(1, report.DegradedSampleCount);
        CollectionAssert.Contains(sample.MustHitLost.ToArray(), "must-hit");
        Assert.AreEqual(1, sample.MustHitRankLegacy["must-hit"]);
        Assert.IsNull(sample.MustHitRankShadow["must-hit"]);
        Assert.AreEqual("LostByTopKCap", sample.SuspectedLossReason);
        CollectionAssert.Contains(sample.DisabledChannels.ToArray(), "vector");
        Assert.AreEqual(1, sample.TopKCaps["final"]);
    }

    [TestMethod]
    public async Task ShadowExecution_CoverageFloor_ShouldPreventHighImportanceMustHitLoss()
    {
        var contextStore = new InMemoryContextStore();
        await contextStore.SaveAsync(Item("distractor-floor", "floor query floor query floor query dominant dominant dominant"));
        await contextStore.SaveAsync(Item("must-hit-floor", "floor query reserve target"));
        var executor = new ShadowRetrievalPlanExecutor(contextStore);
        var request = Request("floor query", includeVector: false, topK: 1, candidateTake: 10);
        request.Metadata["attention.mustHit"] = "must-hit-floor";

        var shadow = await executor.ExecuteAsync(Proposal("proposal-floor", finalTopK: 1), request);

        CollectionAssert.Contains(
            shadow.ShadowSelectedItems.Select(item => item.SourceId).ToArray(),
            "must-hit-floor");
        Assert.AreEqual("true", shadow.Diagnostics["coverageFloorApplied"]);
        Assert.AreEqual("0", shadow.Diagnostics["mustNotHitAddedAfterValidation"]);
    }

    [TestMethod]
    public async Task ShadowExecution_CoverageFloor_ShouldNotReserveMustNotHit()
    {
        var contextStore = new InMemoryContextStore();
        await contextStore.SaveAsync(Item("noise-floor", "noise floor query"));
        await contextStore.SaveAsync(Item("safe-floor", "safe floor query"));
        var executor = new ShadowRetrievalPlanExecutor(contextStore);
        var request = Request("floor query", includeVector: false, topK: 1, candidateTake: 10);
        request.Metadata["attention.mustHit"] = "noise-floor";
        request.Metadata["attention.mustNotHit"] = "noise-floor";

        var shadow = await executor.ExecuteAsync(Proposal("proposal-floor-safe", finalTopK: 1), request);

        CollectionAssert.DoesNotContain(
            shadow.ShadowSelectedItems.Select(item => item.SourceId).ToArray(),
            "noise-floor");
        Assert.AreEqual("0", shadow.Diagnostics["mustNotHitAddedAfterValidation"]);
    }

    [TestMethod]
    public async Task PlanningShadowEvalRunner_ShouldRunA3Successfully()
    {
        var runner = new PlanningShadowEvalRunner();
        var report = await runner.RunAsync(FindContextsRoot());

        Assert.AreEqual("a3", report.SampleSet);
        Assert.AreEqual(50, report.TotalSamples);
        Assert.AreEqual(0, report.MustNotHitViolationCount);
        Assert.AreEqual(50, report.ValidPlanCount);
        Assert.AreEqual(50, report.NativeValidPlanCount);
        Assert.AreEqual(0, report.FallbackToLegacySafePlanCount);
        Assert.AreEqual(0, report.FallbackPlanCount);
        Assert.AreEqual(0, report.RepairedPlanCount);
        Assert.AreEqual(1.0, report.NativeValidRate);
        Assert.AreEqual(0, report.FinalTopKClampCount);
        Assert.IsTrue(report.Samples.Count > 0);
        Assert.IsTrue(report.Samples.All(sample => sample.Diagnostics.ContainsKey("shadowVectorEnabled")));
        Assert.IsTrue(report.Samples.All(sample => sample.ValidatorApplied));
        Assert.IsTrue(report.RepairReasonCounts.ContainsKey("FinalTopKClamped"));
        Assert.IsTrue(report.IntentRepairBreakdown.Count > 0);
        Assert.IsTrue(report.ModeRepairBreakdown.Count > 0);
    }

    [TestMethod]
    public async Task PlanningOptInEvalRunner_ShouldRunA3Successfully()
    {
        var runner = new PlanningShadowEvalRunner();
        var report = await runner.RunOptInAsync(
            FindContextsRoot(),
            [PlanningIntentDetector.CurrentTask, PlanningIntentDetector.AutomationRecovery]);

        Assert.AreEqual("a3", report.SampleSet);
        Assert.AreEqual(50, report.TotalSamples);
        Assert.AreEqual(0, report.MustNotHitViolationCount);
        Assert.AreEqual(0, report.LifecycleViolationCount);
        Assert.IsTrue(report.Samples.Any(sample =>
            sample.Diagnostics.TryGetValue("planningOptInMatched", out var value)
            && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(report.Samples.All(sample =>
            sample.Diagnostics.TryGetValue("shadowVectorEnabled", out var value)
            && string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void PlanningShadowQualityReport_ShouldComputeGlobalDeltas()
    {
        var comparison = ShadowRetrievalComparisonReportBuilder.Build("unit",
        [
            ShadowComparison(
                sampleId: "sample-quality-global",
                mode: "ChatMode",
                intent: "CurrentTask",
                mustHit: ["must-hit"],
                expectedConstraints: ["must-hit"],
                expectedEntities: ["must-hit"],
                expectedUncertainties: ["must-hit"],
                legacySelected: [Candidate("legacy-only", 1)],
                shadowSelected: [Candidate("must-hit", 2)])
        ]);

        var report = PlanningShadowQualityReportBuilder.Build(comparison);

        Assert.AreEqual(1, report.TotalSamples);
        Assert.IsTrue(report.Global.PassRateDelta > 0);
        Assert.IsTrue(report.Global.Recall10Delta > 0);
        Assert.IsTrue(report.Global.MrrDelta > 0);
        Assert.IsTrue(report.Global.ConstraintHitDelta > 0);
        Assert.IsTrue(report.Global.EntityHitDelta > 0);
        Assert.IsTrue(report.Global.UncertaintyHitDelta > 0);
        Assert.AreEqual(0, report.Global.MustNotHitViolationDelta);
        Assert.AreEqual(0, report.Global.LifecycleViolationCount);
    }

    [TestMethod]
    public void PlanningShadowQualityReport_ShouldComputeModeAndIntentDeltas()
    {
        var comparison = ShadowRetrievalComparisonReportBuilder.Build("unit",
        [
            ShadowComparison(
                sampleId: "sample-chat",
                mode: "ChatMode",
                intent: "CurrentTask",
                mustHit: ["chat-hit"],
                legacySelected: [Candidate("legacy-chat", 1)],
                shadowSelected: [Candidate("chat-hit", 2)]),
            ShadowComparison(
                sampleId: "sample-coding",
                mode: "CodingMode",
                intent: "CodingTask",
                mustHit: ["coding-hit"],
                legacySelected: [Candidate("coding-hit", 2)],
                shadowSelected: [Candidate("coding-hit", 3)])
        ]);

        var report = PlanningShadowQualityReportBuilder.Build(comparison);

        Assert.IsTrue(report.ModeBreakdown.ContainsKey("ChatMode"));
        Assert.IsTrue(report.ModeBreakdown.ContainsKey("CodingMode"));
        Assert.IsTrue(report.IntentBreakdown.ContainsKey("CurrentTask"));
        Assert.IsTrue(report.IntentBreakdown.ContainsKey("CodingTask"));
        Assert.AreEqual(1, report.ModeBreakdown["ChatMode"].TotalSamples);
        Assert.AreEqual(1, report.IntentBreakdown["CodingTask"].TotalSamples);
    }

    [TestMethod]
    public void PlanningShadowQualityReport_ShouldMarkRegressedSamples()
    {
        var comparison = ShadowRetrievalComparisonReportBuilder.Build("unit",
        [
            ShadowComparison(
                sampleId: "sample-regressed",
                mode: "ProjectMode",
                intent: "FuzzyQuestion",
                mustHit: ["must-hit"],
                legacySelected: [Candidate("must-hit", 2)],
                shadowSelected: [Candidate("shadow-only", 1)])
        ]);

        var report = PlanningShadowQualityReportBuilder.Build(comparison);
        var sample = report.Samples.Single();

        Assert.IsTrue(sample.Regressed);
        Assert.IsFalse(sample.Improved);
        CollectionAssert.Contains(sample.MustHitLost.ToArray(), "must-hit");
        Assert.AreEqual("MustHitLostByShadow", sample.SuspectedReason);
    }

    [TestMethod]
    public void PlanningShadowQualityReport_ShouldRecommendIntents()
    {
        var comparison = ShadowRetrievalComparisonReportBuilder.Build("unit",
        [
            ShadowComparison(
                sampleId: "sample-optin",
                mode: "ChatMode",
                intent: "CurrentTask",
                mustHit: ["opt-hit"],
                legacySelected: [Candidate("other", 1), Candidate("opt-hit", 2)],
                shadowSelected: [Candidate("opt-hit", 3)]),
            ShadowComparison(
                sampleId: "sample-tuning",
                mode: "ProjectMode",
                intent: "FuzzyQuestion",
                mustHit: ["lost-hit"],
                legacySelected: [Candidate("lost-hit", 2)],
                shadowSelected: [Candidate("other", 1)]),
            ShadowComparison(
                sampleId: "sample-blocked",
                mode: "NovelMode",
                intent: "NovelGeneration",
                mustNotHit: ["noise-item"],
                legacySelected: [],
                shadowSelected: [Candidate("noise-item", 1)])
        ]);

        var report = PlanningShadowQualityReportBuilder.Build(comparison);

        CollectionAssert.Contains(report.Recommendation.OptInCandidateIntents.ToArray(), "CurrentTask");
        CollectionAssert.Contains(report.Recommendation.NeedsTuningIntents.ToArray(), "FuzzyQuestion");
        CollectionAssert.Contains(report.Recommendation.SafeOnlyInShadowIntents.ToArray(), "FuzzyQuestion");
        CollectionAssert.Contains(report.Recommendation.BlockedIntents.ToArray(), "NovelGeneration");
    }

    [TestMethod]
    public void PlanningOptInFallbackAnalysis_ShouldGroupByIntent()
    {
        var comparison = new ShadowRetrievalComparisonReport
        {
            SampleSet = "unit",
            Samples =
            [
                OptInItem("sample-current", PlanningIntentDetector.CurrentTask, applied: true),
                OptInItem("sample-coding", PlanningIntentDetector.CodingTask, optInMatched: true, fallbackUsed: true, fallbackReason: "hard_constraint_missing:must-confirm")
            ]
        };

        var report = PlanningOptInFallbackAnalysisReportBuilder.Build(
            comparison,
            [PlanningIntentDetector.CurrentTask],
            [PlanningIntentDetector.CodingTask]);

        Assert.AreEqual(2, report.TotalSamples);
        Assert.IsTrue(report.IntentSummaries.ContainsKey(PlanningIntentDetector.CurrentTask));
        Assert.IsTrue(report.IntentSummaries.ContainsKey(PlanningIntentDetector.CodingTask));
        Assert.AreEqual(1, report.IntentSummaries[PlanningIntentDetector.CurrentTask].Applied);
        Assert.AreEqual(1, report.IntentSummaries[PlanningIntentDetector.CodingTask].Fallback);
    }

    [TestMethod]
    public void PlanningOptInFallbackAnalysis_ShouldClassifyFallbackReasons()
    {
        Assert.AreEqual(
            PlanningOptInFallbackAnalysisReportBuilder.MustNotHitRisk,
            PlanningOptInFallbackAnalysisReportBuilder.ClassifyFallbackReason("must_not_hit_violation"));
        Assert.AreEqual(
            PlanningOptInFallbackAnalysisReportBuilder.LifecycleRisk,
            PlanningOptInFallbackAnalysisReportBuilder.ClassifyFallbackReason("lifecycle_violation"));
        Assert.AreEqual(
            PlanningOptInFallbackAnalysisReportBuilder.HardConstraintMissing,
            PlanningOptInFallbackAnalysisReportBuilder.ClassifyFallbackReason("hard_constraint_missing:manual approval"));
        Assert.AreEqual(
            PlanningOptInFallbackAnalysisReportBuilder.HardConstraintMissing,
            PlanningOptInFallbackAnalysisReportBuilder.ClassifyFallbackReason(
                "hard_constraint_missing:manual approval",
                new Dictionary<string, string>
                {
                    ["planningSafetyChecks"] = "lifecycle=false;deprecated=false;mustNotHit=false"
                }));
        Assert.AreEqual(
            PlanningOptInFallbackAnalysisReportBuilder.InvalidPlan,
            PlanningOptInFallbackAnalysisReportBuilder.ClassifyFallbackReason("invalid_proposal"));
        Assert.AreEqual(
            PlanningOptInFallbackAnalysisReportBuilder.Unknown,
            PlanningOptInFallbackAnalysisReportBuilder.ClassifyFallbackReason(""));
        Assert.AreEqual(
            PlanningOptInFallbackAnalysisReportBuilder.Unknown,
            PlanningOptInFallbackAnalysisReportBuilder.ClassifyFallbackReason(
                "",
                new Dictionary<string, string>
                {
                    ["mustNotHitViolationCount"] = "0",
                    ["lifecycleViolationCount"] = "0"
                }));
    }

    [TestMethod]
    public void PlanningOptInConstraintSafetyReport_ShouldReportRepairStages()
    {
        var comparison = ShadowRetrievalComparisonReportBuilder.Build(
            "unit",
            [
                new ShadowRetrievalComparisonItem
                {
                    SampleId = "sample-repaired",
                    Mode = "ChatMode",
                    ProposalSummary = $"{PlanningIntentDetector.CurrentTask}/Chat",
                    ExpectedHardConstraints = ["输出必须使用中文"],
                    LegacyConstraints = ["输出必须使用中文"],
                    ProposalConstraints = ["输出必须使用中文"],
                    MissingConstraints = [],
                    Diagnostics = new Dictionary<string, string>
                    {
                        ["planningOptInMatched"] = "true",
                        ["planningExecutionStatus"] = RetrievalPlanningOptions.ApplyGuardedMode,
                        ["planningFallbackUsed"] = "false",
                        ["constraintRepairStatus"] = "ConstraintRepaired",
                        ["constraintDroppedByBudget"] = "输出必须使用中文",
                        ["constraintSource"] = "eval.expectedConstraints"
                    }
                }
            ]);

        var report = PlanningOptInConstraintSafetyReportBuilder.Build(comparison);

        Assert.AreEqual(1, report.AffectedSampleCount);
        Assert.AreEqual(1, report.ConstraintRepairedCount);
        Assert.AreEqual(1, report.ConstraintDroppedByBudgetCount);
        Assert.AreEqual("ConstraintDroppedByBudget", report.Samples.Single().LostAtStage);
    }

    [TestMethod]
    public void PlanningOptInFallbackAnalysis_ShouldGenerateRecommendations()
    {
        var comparison = new ShadowRetrievalComparisonReport
        {
            SampleSet = "unit",
            Samples =
            [
                OptInItem("sample-current", PlanningIntentDetector.CurrentTask, applied: true, mustHitDelta: 1),
                OptInItem("sample-coding", PlanningIntentDetector.CodingTask, applied: true, mustHitDelta: 1),
                OptInItem("sample-long-term", PlanningIntentDetector.LongTermPreference, optInMatched: true, fallbackUsed: true, fallbackReason: "hard_constraint_missing:scope"),
                OptInItem("sample-novel", PlanningIntentDetector.NovelGeneration, optInMatched: true, fallbackUsed: true, fallbackReason: "must_not_hit_violation")
            ]
        };

        var report = PlanningOptInFallbackAnalysisReportBuilder.Build(
            comparison,
            [PlanningIntentDetector.CurrentTask],
            [PlanningIntentDetector.CodingTask, PlanningIntentDetector.LongTermPreference]);

        CollectionAssert.Contains(report.Recommendation.KeepOptIn.ToArray(), PlanningIntentDetector.CurrentTask);
        CollectionAssert.Contains(report.Recommendation.ExpandCandidate.ToArray(), PlanningIntentDetector.CodingTask);
        CollectionAssert.Contains(report.Recommendation.NeedsPolicyTuning.ToArray(), PlanningIntentDetector.LongTermPreference);
        CollectionAssert.Contains(report.Recommendation.Blocked.ToArray(), PlanningIntentDetector.NovelGeneration);
    }

    [TestMethod]
    public async Task PlanningOptInFallbackAnalysis_ShouldNotChangeFormalRetrievalOutput()
    {
        var contextStore = new InMemoryContextStore();
        await contextStore.SaveAsync(Item("formal-legacy", "formal retrieval current task dominant dominant"));
        await contextStore.SaveAsync(Item("formal-reserve", "reserve candidate"));
        var request = Request("current task", includeVector: false, topK: 1);
        request.Metadata["attention.mustHit"] = "formal-reserve";
        var legacy = new HybridContextRetriever(contextStore);
        var planned = CreatePlanningRetriever(
            contextStore,
            new RetrievalPlanningOptions());

        var legacyResult = await legacy.RetrieveAsync(request);
        var plannedResult = await planned.RetrieveAsync(request);

        CollectionAssert.AreEqual(
            legacyResult.SelectedItems.Select(item => item.SourceId).ToArray(),
            plannedResult.SelectedItems.Select(item => item.SourceId).ToArray());
        Assert.AreEqual(RetrievalPlanningOptions.OffMode, plannedResult.Trace.Metadata["planningMode"]);
    }

    private static ContextRetrievalRequest Request(
        string query,
        bool includeVector,
        int topK = 5,
        int candidateTake = 10)
    {
        return new ContextRetrievalRequest
        {
            OperationId = Guid.NewGuid().ToString("N"),
            WorkspaceId = "workspace-shadow",
            CollectionId = "collection-shadow",
            QueryText = query,
            TopK = topK,
            CandidateTake = candidateTake,
            IncludeKeywordRecall = true,
            IncludeVectorRecall = includeVector,
            IncludeRelationExpansion = false,
            IncludeWorkingMemory = true,
            IncludeStableMemory = true,
            TokenBudget = 4000
        };
    }

    private static ShadowRetrievalComparisonItem ShadowComparison(
        string sampleId,
        string mode,
        string intent,
        IReadOnlyList<string>? mustHit = null,
        IReadOnlyList<string>? mustNotHit = null,
        IReadOnlyList<string>? expectedConstraints = null,
        IReadOnlyList<string>? expectedEntities = null,
        IReadOnlyList<string>? expectedUncertainties = null,
        IReadOnlyList<ContextRetrievalCandidate>? legacySelected = null,
        IReadOnlyList<ContextRetrievalCandidate>? shadowSelected = null)
    {
        return ShadowRetrievalComparisonReportBuilder.BuildSample(
            new ContextEvalSample
            {
                Id = sampleId,
                Mode = mode,
                MustHit = mustHit ?? [],
                MustNotHit = mustNotHit ?? [],
                ExpectedConstraints = expectedConstraints ?? [],
                ExpectedEntities = expectedEntities ?? [],
                ExpectedUncertainties = expectedUncertainties ?? []
            },
            new ContextRetrievalResult
            {
                OperationId = $"legacy-{sampleId}",
                SelectedItems = legacySelected ?? []
            },
            new ShadowRetrievalResult
            {
                OperationId = $"shadow-{sampleId}",
                ProposalId = $"proposal-{sampleId}",
                ProposalSummary = $"{intent}/Chat",
                ShadowSelectedItems = shadowSelected ?? [],
                Diagnostics = new Dictionary<string, string>
                {
                    ["validatorApplied"] = "true",
                    ["validPlan"] = "true",
                    ["repairedPlan"] = "false",
                    ["fallbackToLegacySafePlan"] = "false",
                    ["auditMode"] = "false",
                    ["conflictMode"] = "false"
                }
            });
    }

    private static ShadowRetrievalComparisonItem OptInItem(
        string sampleId,
        string intent,
        bool optInMatched = true,
        bool applied = false,
        bool fallbackUsed = false,
        string fallbackReason = "",
        int mustHitDelta = 0)
    {
        var finalSelected = applied
            ? new[] { $"{sampleId}-proposal" }
            : [$"{sampleId}-legacy"];
        return new ShadowRetrievalComparisonItem
        {
            SampleId = sampleId,
            Mode = "ChatMode",
            ProposalSummary = $"{intent}/Chat",
            LegacySelected = [$"{sampleId}-legacy"],
            ShadowSelected = finalSelected,
            MustHitDelta = mustHitDelta,
            Diagnostics = new Dictionary<string, string>
            {
                ["planningOptInMatched"] = optInMatched.ToString().ToLowerInvariant(),
                ["planningExecutionStatus"] = applied
                    ? RetrievalPlanningOptions.ApplyGuardedMode
                    : fallbackUsed ? "FallbackUsed" : "Legacy",
                ["planningFallbackUsed"] = fallbackUsed.ToString().ToLowerInvariant(),
                ["planningFallbackReason"] = fallbackReason,
                ["planningLegacySelected"] = $"{sampleId}-legacy",
                ["planningProposalSelected"] = $"{sampleId}-proposal",
                ["planningFinalSelected"] = string.Join(",", finalSelected),
                ["planningSafetyChecks"] = fallbackUsed ? "passed=false" : "passed=true"
            }
        };
    }

    private static RetrievalPlanProposal Proposal(
        string proposalId,
        bool useExact = true,
        bool useKeyword = true,
        bool useWorkingMemory = true,
        bool useStableMemory = true,
        bool useVector = false,
        int vectorTopK = 0,
        int finalTopK = 5)
    {
        return new RetrievalPlanProposal
        {
            OperationId = proposalId,
            WorkspaceId = "workspace-shadow",
            CollectionId = "collection-shadow",
            Intent = "CurrentTask",
            Mode = "Chat",
            UseExact = useExact,
            UseKeyword = useKeyword,
            UseShortTermMemory = useWorkingMemory,
            UseWorkingMemory = useWorkingMemory,
            UseStableMemory = useStableMemory,
            UseRelations = false,
            UseVector = useVector,
            KeywordTopK = useKeyword ? 10 : 0,
            MemoryTopK = useWorkingMemory || useStableMemory ? 10 : 0,
            RelationTopK = 0,
            VectorTopK = vectorTopK,
            FinalTopK = finalTopK,
            Confidence = 0.8
        };
    }

    private static HybridContextRetriever CreatePlanningRetriever(
        InMemoryContextStore contextStore,
        RetrievalPlanningOptions options)
    {
        var memoryStore = new InMemoryMemoryStore();
        var constraintStore = new InMemoryConstraintStore();
        var relationStore = new InMemoryRelationStore();
        var snapshotService = new PlanningSnapshotService(
            new InMemoryShortTermMemoryStore(new ShortTermMemoryPolicy()),
            memoryStore,
            constraintStore,
            new InMemoryContextLearningStore());
        var safetyProfile = RetrievalPlanSafetyProfile.CreateDefault();
        var proposalService = new RetrievalPlanProposalService(
            snapshotService,
            new PlanningIntentDetector(),
            safetyProfile);
        var shadowExecutor = new ShadowRetrievalPlanExecutor(
            contextStore,
            memoryStore,
            relationStore,
            new RetrievalPlanProposalValidator(safetyProfile));

        return new HybridContextRetriever(
            contextStore,
            memoryStore,
            relationStore,
            embeddingProvider: null,
            vectorStore: null,
            traceStore: null,
            attentionRerankOptions: new RetrievalAttentionRerankOptions(),
            planningOptions: options,
            planningProposalService: proposalService,
            planningShadowExecutor: shadowExecutor);
    }

    private static ContextItem Item(string id, string content)
    {
        return new ContextItem
        {
            Id = id,
            WorkspaceId = "workspace-shadow",
            CollectionId = "collection-shadow",
            Type = "note",
            Content = content,
            Importance = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static ContextMemoryItem Memory(string id, ContextMemoryStatus status, string content)
    {
        return new ContextMemoryItem
        {
            Id = id,
            WorkspaceId = "workspace-shadow",
            CollectionId = "collection-shadow",
            Layer = ContextMemoryLayer.Working,
            Status = status,
            Type = "memory",
            Content = content,
            Importance = 1,
            Confidence = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static ContextRetrievalCandidate Candidate(
        string sourceId,
        double score,
        Dictionary<string, string>? metadata = null)
    {
        return new ContextRetrievalCandidate
        {
            CandidateId = $"ContextItem:{sourceId}",
            SourceId = sourceId,
            Kind = ContextRetrievalCandidateKind.ContextItem,
            Type = "note",
            Content = sourceId,
            Score = score,
            EstimatedTokens = 10,
            Metadata = metadata ?? new Dictionary<string, string>()
        };
    }

    private static string FindContextsRoot()
    {
        var current = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(current))
        {
            var candidate = Path.Combine(current, "eval", "contexts");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            current = Path.GetDirectoryName(current);
        }

        return Path.Combine(Directory.GetCurrentDirectory(), "eval", "contexts");
    }
}
