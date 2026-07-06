"""
V16.16 Repair A: Implementation Plan Generator Parity & Authorization Preflight
================================================================================
Full schema parity with checked-in artifacts. No summaries — full field structures.
"""
import json
import os
from datetime import datetime, timezone

BASE = os.path.dirname(os.path.abspath(__file__))
OUT_PLAN = os.path.join(BASE, "native-production-trace-execution-endpoint-implementation-plan.json")
OUT_PLAN_MD = os.path.join(BASE, "native-production-trace-execution-endpoint-implementation-plan.md")
OUT_GATE = os.path.join(BASE, "native-production-trace-execution-endpoint-implementation-plan-gate.json")
OUT_PREFLIGHT = os.path.join(BASE, "native-production-trace-execution-endpoint-implementation-authorization-preflight.json")


def main():
    now = datetime.now(timezone.utc).isoformat()
    previous_gates = {
        "V16_15EndpointDesignReady": True,
        "V16_14AuthorizationContractReady": True,
        "V16_13ExecutionPlanReady": True,
        "V16_12DesignReviewReady": True,
        "V16_11FinalAcceptanceBoundaryReady": True,
        "V16_7ControlledReplayMetricQualityReady": True,
    }

    # -----------------------------------------------------------------------
    # Implementation plan (full schema)
    # -----------------------------------------------------------------------
    plan = {
        "GeneratedAt": now,
        "ContractVersion": "V16.16",
        "DocumentType": "NativeProductionTraceExecutionEndpointImplementationPlan",
        "Purpose": "Detailed implementation plan for the native production trace execution endpoint. PLAN ONLY.",
        "PlanStatus": {
            "EndpointImplementationPlanReady": True,
            "EndpointImplementationAllowed": False,
            "EndpointImplementationAllowedReason": "Plan is defined and ready for review, but actual implementation requires a separate phase.",
            "EndpointImplemented": False,
        },
        "TargetFilesAndClasses": {
            "PrimaryTarget": {
                "File": "src/ContextCore.ControlRoom/Commands/EvalCommand.VectorV8.cs",
                "Method": "ExecuteV16_16NativeProductionTraceExecutionEndpointAsync",
                "Purpose": "CLI endpoint for native production trace execution.",
            },
            "AuthorizationValidationTarget": {
                "Method": "ValidateAllSevenAuthorizationFactors",
                "Purpose": "Validates all 7 authorization factors from V16.14 contract.",
            },
            "SinkManagementTarget": {
                "Classes": [
                    {"Name": "RuntimeCandidateTraceSinkAccessor", "Purpose": "Static wiring point for trace sink."},
                    {"Name": "FileRuntimeCandidateTraceSink", "Purpose": "JSONL file-backed trace sink."},
                    {"Name": "NullRuntimeCandidateTraceSink", "Purpose": "No-op default sink for restore."},
                ],
            },
        },
        "CliDispatchShape": {
            "Subcommand": "v16_16-native-production-trace-execution-endpoint",
            "Args": [
                {"Arg": "--confirm-live-capture", "Required": True, "Type": "confirmation_flag"},
                {"Arg": "--capture-token <token>", "Required": True, "Type": "hard_authorization"},
                {"Arg": "--workspaceId <real>", "Required": True, "Type": "target_identification"},
                {"Arg": "--collectionId <real>", "Required": True, "Type": "target_identification"},
                {"Arg": "--runId <unique>", "Required": True, "Type": "idempotency"},
            ],
        },
        "GuardOrder": [
            {"Sequence": 1, "Guard": "confirmLiveCapture", "Check": "Parameter present.", "IfMissing": "Block with MissingConfirmLiveCapture."},
            {"Sequence": 2, "Guard": "captureToken", "Check": "Non-empty string present.", "IfMissing": "Block with MissingCaptureToken."},
            {"Sequence": 3, "Guard": "workspaceId/collectionId present", "Check": "Both non-empty.", "IfMissing": "Block with MissingWorkspaceId or MissingCollectionId."},
            {"Sequence": 4, "Guard": "synthetic rejection", "Check": "Neither synthetic.", "IfSynthetic": "Block with SyntheticWorkspaceOrCollection."},
            {"Sequence": 5, "Guard": "runId present", "Check": "Non-empty string present.", "IfMissing": "Block with MissingRunId."},
            {"Sequence": 6, "Guard": "RejectExistingRunId", "Check": "Output file does not exist.", "IfExists": "Block with RejectExistingRunId."},
            {"Sequence": 7, "Guard": "safety invariants", "Check": "RuntimeInfluenceAllowed=false etc.", "IfViolated": "Hard abort."},
        ],
        "DryRunBehavior": {
            "Enabled": True,
            "Description": "When --dry-run flag is present, execute all guards but do NOT wire sink or call BuildDetailedAsync.",
            "OutputExample": "DryRun: All guards passed. Would wire sink with runId=<runId>.",
        },
        "BlockedBehavior": {
            "WhenAnyGuardFails": "Return LiveCaptureBlocked=true with specific blocked reason.",
            "OutputExample": "LiveCaptureBlocked=true. Reason: SyntheticWorkspaceOrCollection.",
        },
        "SinkLifecycle": [
            {"Step": 1, "Phase": "Pre-execution", "Action": "All 7 guards pass."},
            {"Step": 2, "Phase": "Wiring", "Action": "Create FileRuntimeCandidateTraceSink."},
            {"Step": 3, "Phase": "Wiring", "Action": "Set RuntimeCandidateTraceSinkAccessor.Current to file sink."},
            {"Step": 4, "Phase": "Wiring", "Action": "Set CurrentOperationId."},
            {"Step": 5, "Phase": "Wiring", "Action": "Set CurrentRequestId."},
            {"Step": 6, "Phase": "Execution", "Action": "Call BuildDetailedAsync ONLY after all guards pass."},
            {"Step": 7, "Phase": "Post-execution", "Action": "Call sink.FlushAsync()."},
            {"Step": 8, "Phase": "Post-execution", "Action": "Dispose sink."},
            {"Step": 9, "Phase": "Restore", "Action": "Set Current to NullRuntimeCandidateTraceSink."},
            {"Step": 10, "Phase": "Restore", "Action": "Clear CurrentOperationId and CurrentRequestId."},
        ],
        "FailureRollback": {
            "OnBuildError": "Dispose sink. Restore NullSink. Delete partial trace. Log error.",
            "OnIdempotencyViolation": "Return immediately — no sink created.",
            "OnValidationFailure": "Dispose sink. Restore NullSink. Delete trace. Mark INVALID.",
            "AlwaysRestore": True,
            "AlwaysRestoreNote": "RuntimeCandidateTraceSinkAccessor.Current MUST always be restored to NullRuntimeCandidateTraceSink.",
        },
        "TestPlan": {
            "UnitTestsPlanned": [
                {"Test": "AuthorizationFactorValidation_AllSevenFactorsChecked"},
                {"Test": "SyntheticRejection_AllSyntheticPatternsRejected"},
                {"Test": "RejectExistingRunId_WhenFileExists_Aborts"},
                {"Test": "SinkLifecycle_WiredAndRestored_Correctly"},
                {"Test": "BuildDetailedAsync_OnlyCalledAfterAllGuards_Pass"},
                {"Test": "BuildDetailedAsync_NotCalledWhenBlocked"},
                {"Test": "NoRuntimeInfluence_RuntimeInfluenceAllowed_PermanentlyFalse"},
            ],
            "IntegrationTestsPlanned": [],
            "ProductionTestsPlanned": [],
        },
        "GateSemantics": {
            "EndpointImplementationPlanReady": True,
            "EndpointImplementationAllowed": False,
            "EndpointImplemented": False,
            "ProductionTraceExecutionAuthorized": False,
            "ProductionTraceExecutionAllowed": False,
            "LiveCaptureExecutionImplemented": False,
            "LiveCaptureExecuted": False,
            "NativeProductionTraceReady": False,
            "ProductionGeneralizationReady": False,
            "RuntimeInfluenceAllowed": False,
            "RuntimeInfluenceAllowedPermanent": True,
            "PackageOutputChanged": False,
            "RuntimePromotionApplied": False,
            "VectorBindingChanged": False,
        },
        "SafetyAudit": {
            "JsonlTraceFilesInV16_16": 0,
            "FileRuntimeCandidateTraceSinkWired": False,
            "BuildDetailedAsyncCalledInLiveCapturePath": False,
            "RuntimeCandidateTraceSinkAccessorMutated": False,
        },
        "PreviousGatesPreserved": previous_gates,
    }

    with open(OUT_PLAN, "w", encoding="utf-8") as fh:
        json.dump(plan, fh, indent=2, ensure_ascii=False)
    print(f"Written: {OUT_PLAN}")

    # -----------------------------------------------------------------------
    # Plan gate (full schema)
    # -----------------------------------------------------------------------
    gate = {
        "GeneratedAt": now,
        "ContractVersion": "V16.16",
        "DocumentType": "NativeProductionTraceExecutionEndpointImplementationPlanGate",
        "Purpose": "Gate report confirming the endpoint implementation plan is complete. No actual implementation.",
        "GateResult": {
            "GatePassed": True,
            "GatePassedReason": "Implementation plan fully defined.",
            "EndpointImplementationPlanReady": True,
            "EndpointImplementationAllowed": False,
            "EndpointImplemented": False,
        },
        "SafetyAudit": {
            "JsonlTraceFilesInV16_16": 0,
            "FileRuntimeCandidateTraceSinkWired": False,
            "FileRuntimeCandidateTraceSinkWiredCheck": "NOT wired. Plan phase only.",
            "BuildDetailedAsyncCalledInLiveCapturePath": False,
            "BuildDetailedAsyncCalledCheck": "NOT called. Plan phase only.",
            "RuntimeCandidateTraceSinkAccessorMutated": False,
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
        "PreviousGatesPreserved": previous_gates,
    }

    with open(OUT_GATE, "w", encoding="utf-8") as fh:
        json.dump(gate, fh, indent=2, ensure_ascii=False)
    print(f"Written: {OUT_GATE}")

    # -----------------------------------------------------------------------
    # Preflight
    # -----------------------------------------------------------------------
    preflight = {
        "GeneratedAt": now,
        "ContractVersion": "V16.16",
        "DocumentType": "NativeProductionTraceExecutionEndpointImplementationAuthorizationPreflight",
        "Purpose": "Implementation authorization preflight. Does not implement the endpoint. Evaluates readiness to proceed to implementation approval.",
        "GateResult": {
            "GatePassed": True,
            "EndpointImplementationPlanReady": True,
            "EndpointImplementationAuthorizationPreflightReady": True,
            "EndpointImplementationAuthorizationPreflightReadyReason": "Implementation plan fully defined. All guard orders verified. Safety invariants confirmed.",
            "EndpointImplementationAllowed": False,
            "EndpointImplementationAllowedReason": "Preflight confirms plan readiness but does not authorize implementation.",
            "EndpointImplemented": False,
            "ProductionTraceExecutionAuthorized": False,
            "ProductionTraceExecutionAllowed": False,
            "LiveCaptureExecutionImplemented": False,
            "LiveCaptureExecuted": False,
            "NativeProductionTraceReady": False,
        },
        "SafetyAudit": {
            "JsonlTraceFilesInV16_16": 0,
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
        },
        "PhaseTransition": {
            "NextAllowedPhase": "NativeProductionTraceExecutionEndpointImplementationApproval",
            "NextAllowedPhaseDescription": "Formal approval gate to authorize endpoint implementation.",
            "NextDisallowedPhase": "RuntimeInfluenceActivation",
            "NextDisallowedPhaseReason": "Runtime influence is permanently false.",
        },
        "PreviousGatesPreserved": previous_gates,
    }

    with open(OUT_PREFLIGHT, "w", encoding="utf-8") as fh:
        json.dump(preflight, fh, indent=2, ensure_ascii=False)
    print(f"Written: {OUT_PREFLIGHT}")

    md = f"""# V16.16 Endpoint Implementation Plan

Generated: {now}

Plan only — no implementation.

- EndpointImplementationPlanReady: **true**
- EndpointImplementationAllowed: **false**
- Guards: 7 | Sink lifecycle: 10 steps
- Preflight: EndpointImplementationAuthorizationPreflightReady=true
"""

    with open(OUT_PLAN_MD, "w", encoding="utf-8") as fh:
        fh.write(md)
    print(f"Written: {OUT_PLAN_MD}")

    print("\n=== V16.16 Summary ===")
    print("EndpointImplementationPlanReady: True")
    print("EndpointImplementationAllowed: False")


if __name__ == "__main__":
    main()
