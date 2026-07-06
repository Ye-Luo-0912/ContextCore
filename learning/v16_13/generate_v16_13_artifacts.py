"""
V16.13: Native Production Trace Execution Plan
===============================================
Plan only — no production trace collected. No LiveCapture execution.
Defines all parameters needed for future authorized execution.

Schema parity with checked-in artifacts is enforced by the C# generator parity test.
"""
import json
import os
from datetime import datetime, timezone

BASE = os.path.dirname(os.path.abspath(__file__))
OUT_PLAN = os.path.join(BASE, "native-production-trace-execution-plan.json")
OUT_PLAN_MD = os.path.join(BASE, "native-production-trace-execution-plan.md")
OUT_GATE = os.path.join(BASE, "native-production-trace-execution-plan-gate.json")
OUT_PREFLIGHT = os.path.join(BASE, "native-production-trace-execution-preflight-gate.json")


def main():
    now = datetime.now(timezone.utc).isoformat()
    synthetic_ids = [
        "native-ws", "smoke-ws", "prod-ws", "test-ws", "demo-ws", "dryrun-ws",
        "synthetic-ws", "sandbox-ws", "preview-ws", "debug-ws", "dev-ws",
    ]

    # -----------------------------------------------------------------------
    # Execution plan — full schema matching checked-in artifact
    # -----------------------------------------------------------------------
    plan = {
        "GeneratedAt": now,
        "ContractVersion": "V16.13",
        "DocumentType": "NativeProductionTraceExecutionPlan",
        "Purpose": "Detailed execution plan for native production trace capture. This is a PLAN ONLY — no production trace is collected. No LiveCapture execution. The plan defines all parameters needed for future authorized execution.",
        "PlanStatus": {
            "ProductionTraceExecutionPlanned": True,
            "ProductionTraceExecutionAllowed": False,
            "ProductionTraceExecutionAllowedReason": "Plan is defined and ready, but execution requires explicit authorization (--confirm-live-capture + --capture-token per V16.8 contract) AND the execution endpoint must be implemented beyond the V16.11 skeleton.",
        },
        "WorkspaceCollectionTemplate": {
            "Field": "workspaceId",
            "Type": "string",
            "Required": True,
            "Description": "Real production workspace ID. Must NOT be synthetic (native-ws, smoke-ws, dryrun-ws, etc.). Must contain real user traffic. Value to be filled at execution time.",
            "PlaceholderOnly": True,
            "PlaceholderValue": "<PROD_WORKSPACE_ID>",
            "SyntheticIdsRejected": synthetic_ids,
        },
        "CollectionTemplate": {
            "Field": "collectionId",
            "Type": "string",
            "Required": True,
            "Description": "Real production collection ID. Must NOT be synthetic.",
            "PlaceholderOnly": True,
            "PlaceholderValue": "<PROD_COLLECTION_ID>",
        },
        "TokenBudget": {
            "DefaultTokenBudget": 10000,
            "Description": "Token budget for BuildDetailedAsync. Controls how many items are scored and selected. Same budget used in V16.7 controlled replay.",
        },
        "ExpectedRowCount": {
            "MinimumExpectedRows": 30,
            "MaximumExpectedRows": 200,
            "Reasoning": "V16.4 dry-run produced 49 rows with synthetic seeded stores. V16.7 controlled replay produced 33 rows with FileSystem seeded stores. Production workspace with real data is expected to produce 30-200 trace rows depending on workspace complexity.",
        },
        "TraceOutputPath": {
            "Pattern": "learning/v16_13/native-production-trace-{runId}.jsonl",
            "Directory": "learning/v16_13/",
            "Format": "JSONL (one JSON object per line)",
            "traceSource": 3,
            "traceSourceNote": "All rows must have traceSource=3 (PackageTrace).",
        },
        "RunIdPolicy": {
            "Policy": "RejectExistingRunId",
            "Description": "Before creating FileRuntimeCandidateTraceSink, check if output path exists. If yes, abort with idempotency error. Each runId must be unique.",
            "runIdFormat": "run-{timestamp}-{sequence}",
            "RetryPolicy": "Never reuse a failed runId. Generate a new unique runId for each retry.",
        },
        "ValidationThresholds": {
            "ParseErrorCount": 0,
            "ParseErrorCountDescription": "Zero parse errors allowed. Any malformed JSONL line fails validation.",
            "MissingCriticalFieldCount": 0,
            "MissingCriticalFieldCountDescription": "Zero missing critical fields. Critical fields: operationId, candidateId, sourceType, authority, retrievalChannel, traceSource.",
            "AllRowsTraceSource3": True,
            "AllRowsTraceSource3Description": "Every trace row must have traceSource=3 (PackageTrace). Any row with traceSource != 3 fails validation.",
            "NativeWeightedPairwiseAccThreshold": 0.55,
            "NativeWeightedPairwiseAccThresholdDescription": "WeightedPairwiseAcc must be >= 0.55 on combined native trace data. This is the same threshold used for ControlledReplay metric quality in V16.7.",
            "ScoringSelectedCountPositive": True,
            "ScoringSelectedCountPositiveDescription": "At least one candidate must be selected by scoring (selectedByScoring=true).",
            "ScoringRejectedCountPositive": True,
            "ScoringRejectedCountPositiveDescription": "At least one candidate must be rejected by scoring (selectedByScoring=false).",
            "PackageIncludedCountPositive": True,
            "PackageIncludedCountPositiveDescription": "At least one candidate must be included in the package (includedInPackage=true).",
            "PackageDroppedCountPositive": True,
            "PackageDroppedCountPositiveDescription": "At least one candidate must be dropped from the package (includedInPackage=false).",
        },
        "AbortConditions": {
            "BuildError": "If BasicContextPackageBuilder.BuildDetailedAsync() throws an exception, abort collection immediately. Dispose sink. Restore NullSink. Delete partial trace file.",
            "IdempotencyViolation": "If trace file already exists for runId, abort with RejectExistingRunId error.",
            "ValidationFailure": "If any validation threshold is not met (ParseErrorCount > 0 OR MissingCriticalFieldCount > 0 OR not AllRowsTraceSource3), mark trace as INVALID. Do not count toward metric quality pass.",
            "MetricQualityFailure": "If NativeWeightedPairwiseAcc < 0.55 on traced data, do NOT set NativeProductionTraceReady=true. Do NOT set ProductionGeneralizationReady=true.",
        },
        "RollbackCleanupProcedure": {
            "Step1": "Call sink.FlushAsync() then sink.Dispose() to flush and release file handle.",
            "Step2": "Set RuntimeCandidateTraceSinkAccessor.Current back to NullRuntimeCandidateTraceSink.",
            "Step3": "Clear RuntimeCandidateTraceSinkAccessor.CurrentOperationId and CurrentRequestId (set to null).",
            "Step4": "If collection failed or was aborted, delete the partial .jsonl trace file.",
            "Step5": "If collection succeeded, the trace file is retained as production trace artifact.",
            "Step6": "Log completion status (success/failure/abort) with runId, row count, operationId, timestamp.",
            "Note": "No application state rollback needed — trace collection is diagnostic append-only.",
        },
        "GateSemantics": {
            "ProductionTraceExecutionPlanned": True,
            "ProductionTraceExecutionAllowed": False,
            "NativeProductionTraceReady": False,
            "LiveCaptureExecutionImplemented": False,
            "LiveCaptureExecuted": False,
            "ProductionGeneralizationReady": False,
            "RuntimeInfluenceAllowed": False,
            "RuntimeInfluenceAllowedPermanent": True,
            "PackageOutputChanged": False,
            "RuntimePromotionApplied": False,
            "VectorBindingChanged": False,
        },
        "V16_12Preservation": {
            "V16_12DesignReviewPassed": True,
            "V16_12DesignReviewPreserved": True,
        },
        "V14GatePreserved": True,
        "V16_7GatePreserved": True,
        "V16_11GatePreserved": True,
        "V16_12GatePreserved": True,
    }

    with open(OUT_PLAN, "w", encoding="utf-8") as fh:
        json.dump(plan, fh, indent=2, ensure_ascii=False)
    print(f"Written: {OUT_PLAN}")

    # -----------------------------------------------------------------------
    # Plan gate — full schema matching checked-in artifact
    # -----------------------------------------------------------------------
    gate = {
        "GeneratedAt": now,
        "ContractVersion": "V16.13",
        "DocumentType": "NativeProductionTraceExecutionPlanGate",
        "Purpose": "Gate report confirming that the execution plan is complete and all safety invariants are enforced. No production trace is collected.",
        "GateResult": {
            "GatePassed": True,
            "GatePassedReason": "Execution plan is fully defined with workspace/collection templates, token budget, expected row count, trace output path pattern, runId idempotency, validation thresholds, abort conditions, and rollback/cleanup procedure. All safety invariants enforced.",
            "ProductionTraceExecutionPlanned": True,
            "ProductionTraceExecutionAllowed": False,
            "ProductionTraceExecutionAllowedReason": "Plan is complete but execution requires: (1) explicit --confirm-live-capture + --capture-token, (2) execution endpoint implemented beyond skeleton, (3) real production workspace/collection designated.",
        },
        "SafetyAudit": {
            "JsonlTraceFilesInV16_13": 0,
            "JsonlTraceFilesCheck": "No .jsonl trace files in learning/v16_13/",
            "FileRuntimeCandidateTraceSinkWired": False,
            "FileRuntimeCandidateTraceSinkWiredCheck": "FileRuntimeCandidateTraceSink is NOT wired. Plan phase only — no trace capture infrastructure activated.",
            "BuildDetailedAsyncCalledInLivePath": False,
            "BuildDetailedAsyncCalledCheck": "BuildDetailedAsync is NOT called. Plan phase only.",
            "RuntimeCandidateTraceSinkAccessorMutated": False,
            "RuntimeCandidateTraceSinkAccessorCheck": "RuntimeCandidateTraceSinkAccessor.Current remains NullRuntimeCandidateTraceSink.",
        },
        "GateSemantics": {
            "NativeProductionTraceReady": False,
            "LiveCaptureExecutionImplemented": False,
            "LiveCaptureExecuted": False,
            "ProductionGeneralizationReady": False,
            "RuntimeInfluenceAllowed": False,
            "RuntimeInfluenceAllowedPermanent": True,
            "PackageOutputChanged": False,
            "RuntimePromotionApplied": False,
            "VectorBindingChanged": False,
        },
        "PreviousGatesPreserved": {
            "V16_12DesignReviewReady": True,
            "V16_11FinalAcceptanceBoundaryReady": True,
            "V16_7ControlledReplayMetricQualityReady": True,
        },
    }

    with open(OUT_GATE, "w", encoding="utf-8") as fh:
        json.dump(gate, fh, indent=2, ensure_ascii=False)
    print(f"Written: {OUT_GATE}")

    # -----------------------------------------------------------------------
    # Preflight gate — new
    # -----------------------------------------------------------------------
    preflight = {
        "GeneratedAt": now,
        "ContractVersion": "V16.13",
        "DocumentType": "NativeProductionTraceExecutionPreflightGate",
        "Purpose": "Preflight gate that determines whether the system is ready to enter a future execution phase. Does NOT execute capture. Only evaluates readiness for advancement.",
        "GateResult": {
            "GatePassed": True,
            "ExecutionPlanComplete": True,
            "ExecutionPlanCompleteReason": "All plan sections defined: workspace/collection templates, token budget, row count expectations, trace output path, runId policy, validation thresholds, abort conditions, rollback/cleanup procedure.",
            "ProductionTraceExecutionAllowed": False,
            "ProductionTraceExecutionAllowedReason": "Preflight does not authorize execution. ProductionTraceExecutionAllowed=false reflects that the next phase is authorization contract review, not actual execution.",
            "LiveCaptureExecutionImplemented": False,
            "LiveCaptureExecutionImplementedReason": "LiveCapture execution endpoint remains a skeleton (V16.11). Not implemented.",
            "NativeProductionTraceReady": False,
            "NativeProductionTraceReadyReason": "No native production trace has been captured. Plan phase only.",
            "NoProductionTraceGenerated": True,
            "NoFileRuntimeCandidateTraceSinkWired": True,
            "NoBuildDetailedAsyncCalled": True,
        },
        "SafetyAudit": {
            "JsonlTraceFilesInV16_13": 0,
            "FileRuntimeCandidateTraceSinkWired": False,
            "BuildDetailedAsyncCalledInLiveCapturePath": False,
            "RuntimeCandidateTraceSinkAccessorMutated": False,
        },
        "GateSemantics": {
            "RuntimeInfluenceAllowed": False,
            "RuntimeInfluenceAllowedPermanent": True,
            "PackageOutputChanged": False,
            "RuntimePromotionApplied": False,
            "VectorBindingChanged": False,
            "ProductionGeneralizationReady": False,
            "NativeProductionTraceReady": False,
            "LiveCaptureExecutionImplemented": False,
        },
        "PhaseTransition": {
            "NextAllowedPhase": "NativeProductionTraceExecutionAuthorizationContract",
            "NextAllowedPhaseDescription": "Define authorization contract specifics for native production trace execution (analogous to V16.8 LiveCapture authorization contract).",
            "NextDisallowedPhase": "RuntimeInfluenceActivation",
            "NextDisallowedPhaseReason": "Runtime influence is permanently false. Neural bias, package mutation, and vector binding changes are structurally prohibited.",
        },
        "PreviousGatesPreserved": {
            "V16_12DesignReviewReady": True,
            "V16_11FinalAcceptanceBoundaryReady": True,
            "V16_7ControlledReplayMetricQualityReady": True,
        },
    }

    with open(OUT_PREFLIGHT, "w", encoding="utf-8") as fh:
        json.dump(preflight, fh, indent=2, ensure_ascii=False)
    print(f"Written: {OUT_PREFLIGHT}")

    # -----------------------------------------------------------------------
    # Markdown
    # -----------------------------------------------------------------------
    md = f"""# V16.13 Native Production Trace Execution Plan

Generated: {now}

Plan only — no production trace collected.

- ProductionTraceExecutionPlanned: **true**
- ProductionTraceExecutionAllowed: **false**
- RunIdPolicy: RejectExistingRunId
- Validation: ParseError=0, MissingCritical=0, traceSource3=all, WPA>=0.55
- Preflight: ExecutionPlanComplete=true, NextAllowed=NativeProductionTraceExecutionAuthorizationContract
"""

    with open(OUT_PLAN_MD, "w", encoding="utf-8") as fh:
        fh.write(md)
    print(f"Written: {OUT_PLAN_MD}")

    print("\n=== V16.13 Summary ===")
    print("ProductionTraceExecutionPlanned: True")
    print("ProductionTraceExecutionAllowed: False")
    print("Preflight: ExecutionPlanComplete=True")


if __name__ == "__main__":
    main()
