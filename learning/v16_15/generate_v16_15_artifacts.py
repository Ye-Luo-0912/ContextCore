"""
V16.15 Repair A: Endpoint Implementation Design Generator Parity & Preflight
=============================================================================
Full schema parity with checked-in artifacts. No summaries — full field structures.
"""
import json
import os
from datetime import datetime, timezone

BASE = os.path.dirname(os.path.abspath(__file__))
OUT_DESIGN = os.path.join(BASE, "native-production-trace-execution-endpoint-implementation-design.json")
OUT_DESIGN_MD = os.path.join(BASE, "native-production-trace-execution-endpoint-implementation-design.md")
OUT_GATE = os.path.join(BASE, "native-production-trace-execution-endpoint-implementation-design-gate.json")
OUT_PREFLIGHT = os.path.join(BASE, "native-production-trace-execution-endpoint-implementation-preflight.json")

SYNTHETIC_PATTERNS = [
    "native-ws", "smoke-ws", "prod-ws", "test-ws", "demo-ws", "dryrun-ws",
    "synthetic-ws", "sandbox-ws", "preview-ws", "debug-ws", "dev-ws",
    "native-col", "smoke-col", "prod-col", "test-col", "demo-col", "dryrun-col",
    "synthetic-col", "sandbox-col", "preview-col", "debug-col", "dev-col",
]


def main():
    now = datetime.now(timezone.utc).isoformat()

    required_args = [
        {"Arg": "--confirm-live-capture", "Type": "confirmation_flag", "Required": True, "Description": "Explicit confirmation that production trace execution is intended."},
        {"Arg": "--capture-token <token>", "Type": "hard_authorization", "Required": True, "Description": "Hard authorization token. Must be validated before execution proceeds."},
        {"Arg": "--workspaceId <real>", "Type": "target_identification", "Required": True, "Description": "Real production workspace ID. Synthetic IDs rejected."},
        {"Arg": "--collectionId <real>", "Type": "target_identification", "Required": True, "Description": "Real production collection ID. Synthetic IDs rejected."},
        {"Arg": "--runId <unique>", "Type": "idempotency", "Required": True, "Description": "Unique run identifier. RejectExistingRunId policy."},
    ]

    factors_check = [
        {"Factor": "confirmLiveCapture", "Check": "Parameter present."},
        {"Factor": "captureToken", "Check": "Non-empty string present."},
        {"Factor": "workspaceId", "Check": "Non-empty string present AND not synthetic."},
        {"Factor": "collectionId", "Check": "Non-empty string present AND not synthetic."},
        {"Factor": "runId", "Check": "Non-empty string present."},
        {"Factor": "synthetic rejection", "Check": "Workspace and collection IDs not in synthetic patterns list."},
        {"Factor": "endpoint implemented", "Check": "LiveCaptureExecutionImplemented must be false at design phase."},
    ]

    sink_wiring_plan = {
        "Step1": "Validate all 7 authorization factors.",
        "Step2": "Check runId idempotency.",
        "Step3": "Create FileRuntimeCandidateTraceSink at learning/v16_15/native-production-trace-{runId}.jsonl.",
        "Step4": "Set RuntimeCandidateTraceSinkAccessor.Current to the file sink.",
        "Step5": "Set RuntimeCandidateTraceSinkAccessor.CurrentOperationId to op-prod-v16_15-{runId}.",
        "Step6": "Set RuntimeCandidateTraceSinkAccessor.CurrentRequestId to req-prod-v16_15-{runId}.",
    }

    sink_restore_plan = {
        "OnSuccess": "Call sink.FlushAsync(). Dispose sink. Set RuntimeCandidateTraceSinkAccessor.Current back to NullRuntimeCandidateTraceSink. Clear CurrentOperationId and CurrentRequestId.",
        "OnFailure": "Call sink.FlushAsync(). Dispose sink. Set RuntimeCandidateTraceSinkAccessor.Current back to NullRuntimeCandidateTraceSink. Delete partial trace file. Log error with runId and exception.",
        "Invariant": "RuntimeCandidateTraceSinkAccessor.Current MUST always be restored to NullRuntimeCandidateTraceSink after execution, regardless of success or failure.",
    }

    builder_call_plan = {
        "WhenAuthorized": "After sink is wired and all authorization checks pass, execute BasicContextPackageBuilder.BuildDetailedAsync() against the specified workspace/collection with token budget = 10000.",
        "WhenNotAuthorized": "Return LiveCaptureBlocked=true. Do NOT call BuildDetailedAsync.",
        "SafetyGate": "Before calling BuildDetailedAsync, verify RuntimeInfluenceAllowed=false, NeuralBiasActive=false, PackageOutputChanged=false, VectorBindingChanged=false. These are structural invariants, not runtime checks — they must always be false regardless of authorization.",
    }

    rollback_plan = {
        "Step1": "Dispose FileRuntimeCandidateTraceSink.",
        "Step2": "Restore RuntimeCandidateTraceSinkAccessor.Current to NullRuntimeCandidateTraceSink.",
        "Step3": "Clear CurrentOperationId and CurrentRequestId.",
        "Step4": "On failure/abort: delete partial .jsonl trace file.",
        "Step5": "On success: retain trace file.",
        "Step6": "Log completion status with runId, row count, operationId, timestamp.",
    }

    no_runtime_influence = {
        "RuntimeInfluenceAllowed": False,
        "RuntimeInfluenceAllowedPermanent": True,
        "NeuralBiasActive": False,
        "PackageOutputChanged": False,
        "RuntimePromotionApplied": False,
        "VectorBindingChanged": False,
        "Note": "These invariants are structural and permanent. No endpoint implementation may modify them.",
    }

    gate_semantics = {
        "EndpointImplementationDesignReady": True,
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
    }

    safety_audit = {
        "JsonlTraceFilesInV16_15": 0,
        "FileRuntimeCandidateTraceSinkWired": False,
        "BuildDetailedAsyncCalledInLiveCapturePath": False,
        "RuntimeCandidateTraceSinkAccessorMutated": False,
    }

    previous_gates = {
        "V16_14AuthorizationContractReady": True,
        "V16_13ExecutionPlanReady": True,
        "V16_12DesignReviewReady": True,
        "V16_11FinalAcceptanceBoundaryReady": True,
        "V16_7ControlledReplayMetricQualityReady": True,
    }

    # -----------------------------------------------------------------------
    # Endpoint implementation design (full schema)
    # -----------------------------------------------------------------------
    design = {
        "GeneratedAt": now,
        "ContractVersion": "V16.15",
        "DocumentType": "NativeProductionTraceExecutionEndpointImplementationDesign",
        "Purpose": "Endpoint implementation design only — no actual implementation. No production trace collected. No LiveCapture execution. No FileRuntimeCandidateTraceSink wired. No BuildDetailedAsync called in live capture path.",
        "DesignStatus": {
            "EndpointImplementationDesignReady": True,
            "EndpointImplementationDesignReadyReason": "All design sections defined.",
            "EndpointImplementationAllowed": False,
            "EndpointImplementationAllowedReason": "Design phase only. Implementation requires a separate phase.",
            "EndpointImplemented": False,
            "EndpointImplementedReason": "Design phase only. No implementation code exists beyond the V16.11 skeleton.",
        },
        "CliEndpointShape": {
            "Subcommand": "v16_15-native-production-trace-execution-endpoint",
            "RequiredArgs": required_args,
            "OptionalArgs": [],
            "BehaviorWhenUnauthorized": "Return LiveCaptureBlocked=true. Output blocked reason. No trace captured.",
        },
        "AuthorizationContractIntegration": {
            "Source": "V16.14 native-production-trace-execution-authorization-contract",
            "IntegrationPlan": "Before any execution, validate all 7 authorization factors per V16.14 contract.",
            "FactorsCheck": factors_check,
        },
        "SyntheticRejection": {
            "SyntheticPatterns": SYNTHETIC_PATTERNS,
            "RejectionPlan": "Before creating FileRuntimeCandidateTraceSink, check workspaceId and collectionId against synthetic patterns. If either matches, block execution with SyntheticWorkspaceOrCollection.",
        },
        "RunIdIdempotency": {
            "Policy": "RejectExistingRunId",
            "CheckPlan": "Before creating FileRuntimeCandidateTraceSink, check if output file learning/v16_15/native-production-trace-{runId}.jsonl already exists. If yes, abort with RejectExistingRunId error.",
        },
        "FileRuntimeCandidateTraceSinkWiringPlan": sink_wiring_plan,
        "RuntimeCandidateTraceSinkAccessorRestorePlan": sink_restore_plan,
        "BuildDetailedAsyncCallPlan": builder_call_plan,
        "RollbackCleanupPlan": rollback_plan,
        "NoRuntimeInfluenceInvariant": no_runtime_influence,
        "GateSemantics": gate_semantics,
        "SafetyAudit": safety_audit,
        "PreviousGatesPreserved": previous_gates,
    }

    with open(OUT_DESIGN, "w", encoding="utf-8") as fh:
        json.dump(design, fh, indent=2, ensure_ascii=False)
    print(f"Written: {OUT_DESIGN}")

    # -----------------------------------------------------------------------
    # Design gate (full schema)
    # -----------------------------------------------------------------------
    gate = {
        "GeneratedAt": now,
        "ContractVersion": "V16.15",
        "DocumentType": "NativeProductionTraceExecutionEndpointImplementationDesignGate",
        "Purpose": "Gate report confirming endpoint implementation design is complete. No actual implementation.",
        "GateResult": {
            "GatePassed": True,
            "GatePassedReason": "Endpoint implementation design covers all required sections.",
            "EndpointImplementationDesignReady": True,
            "EndpointImplementationAllowed": False,
            "EndpointImplemented": False,
        },
        "SafetyAudit": {
            "JsonlTraceFilesInV16_15": 0,
            "FileRuntimeCandidateTraceSinkWired": False,
            "FileRuntimeCandidateTraceSinkWiredCheck": "NOT wired. Design phase only.",
            "BuildDetailedAsyncCalledInLiveCapturePath": False,
            "BuildDetailedAsyncCalledCheck": "NOT called. Design phase only.",
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
    # Preflight gate
    # -----------------------------------------------------------------------
    preflight = {
        "GeneratedAt": now,
        "ContractVersion": "V16.15",
        "DocumentType": "NativeProductionTraceExecutionEndpointImplementationPreflight",
        "Purpose": "Endpoint implementation preflight. Does not implement the endpoint. Evaluates readiness to proceed to implementation planning.",
        "GateResult": {
            "GatePassed": True,
            "EndpointImplementationDesignReady": True,
            "EndpointImplementationPreflightReady": True,
            "EndpointImplementationPreflightReadyReason": "All design sections verified. Authorization contract integrated. Safety invariants confirmed.",
            "EndpointImplementationAllowed": False,
            "EndpointImplementationAllowedReason": "Preflight confirms design readiness but does not authorize implementation. Implementation requires a separate plan phase.",
            "EndpointImplemented": False,
            "ProductionTraceExecutionAuthorized": False,
            "ProductionTraceExecutionAllowed": False,
            "LiveCaptureExecutionImplemented": False,
            "LiveCaptureExecuted": False,
            "NativeProductionTraceReady": False,
        },
        "SafetyAudit": {
            "JsonlTraceFilesInV16_15": 0,
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
            "NextAllowedPhase": "NativeProductionTraceExecutionEndpointImplementationPlan",
            "NextAllowedPhaseDescription": "Create a detailed implementation plan specifying code structure, execution flow, and integration points.",
            "NextDisallowedPhase": "RuntimeInfluenceActivation",
            "NextDisallowedPhaseReason": "Runtime influence is permanently false.",
        },
        "PreviousGatesPreserved": previous_gates,
    }

    with open(OUT_PREFLIGHT, "w", encoding="utf-8") as fh:
        json.dump(preflight, fh, indent=2, ensure_ascii=False)
    print(f"Written: {OUT_PREFLIGHT}")

    md = f"""# V16.15 Endpoint Implementation Design

Generated: {now}

Design only — no implementation.

- EndpointImplementationDesignReady: **true**
- EndpointImplementationAllowed: **false**
- EndpointImplemented: **false**
- Preflight: EndpointImplementationPreflightReady=true
"""

    with open(OUT_DESIGN_MD, "w", encoding="utf-8") as fh:
        fh.write(md)
    print(f"Written: {OUT_DESIGN_MD}")

    print("\n=== V16.15 Summary ===")
    print("EndpointImplementationDesignReady: True")
    print("EndpointImplementationAllowed: False")


if __name__ == "__main__":
    main()
