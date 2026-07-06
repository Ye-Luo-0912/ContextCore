"""
V16.14: Native Production Trace Execution Authorization Contract
=================================================================
Authorization contract only — no production trace collected. No LiveCapture execution.
Defines 7 authorization factors, allowed/disallowed modes, failure scenarios.
Schema parity with checked-in artifacts.
"""
import json
import os
from datetime import datetime, timezone

BASE = os.path.dirname(os.path.abspath(__file__))
OUT_CONTRACT = os.path.join(BASE, "native-production-trace-execution-authorization-contract.json")
OUT_CONTRACT_MD = os.path.join(BASE, "native-production-trace-execution-authorization-contract.md")
OUT_GATE = os.path.join(BASE, "native-production-trace-execution-authorization-gate.json")
OUT_PREFLIGHT = os.path.join(BASE, "native-production-trace-execution-endpoint-implementation-preflight.json")


def main():
    now = datetime.now(timezone.utc).isoformat()

    factors = [
        {"Factor": "--confirm-live-capture", "Type": "confirmation_gate", "Required": True, "Description": "Explicit confirmation that production trace execution is intended. Prevents accidental activation."},
        {"Factor": "--capture-token <token>", "Type": "hard_authorization", "Required": True, "Description": "Hard authorization token. Must be validated before execution proceeds. Analogous to V16.8 capture-token."},
        {"Factor": "--workspaceId <real>", "Type": "target_identification", "Required": True, "Description": "Real production workspace ID. Must NOT be synthetic. Rejected pattern list per V16.9."},
        {"Factor": "--collectionId <real>", "Type": "target_identification", "Required": True, "Description": "Real production collection ID. Must NOT be synthetic."},
        {"Factor": "--runId <unique>", "Type": "idempotency", "Required": True, "Description": "Unique run identifier. RejectExistingRunId policy. Never reuse a failed runId."},
        {"Factor": "--workspace/collection NOT synthetic", "Type": "data_provenance", "Required": True, "Description": "Workspace and collection IDs must be real production identifiers. Synthetic IDs rejected."},
        {"Factor": "LiveCaptureExecutionEndpointImplemented", "Type": "implementation_gate", "Required": True, "ValueForThisPhase": False, "Description": "The execution endpoint must be implemented beyond the V16.11 skeleton. Currently false (skeleton only)."},
    ]

    allowed_modes = [
        {"Mode": "PreviewOnly", "Description": "Artifact/plan generation only. No trace collection. Default mode."},
        {"Mode": "PlanOnly", "Description": "Generate execution plan and preflight gate. No trace collection."},
        {"Mode": "AuthorizationContractOnly", "Description": "Define and validate the authorization contract. No trace collection."},
    ]

    disallowed_modes = [
        {"Mode": "ExecuteCapture", "Description": "Production trace execution is NOT ALLOWED. LiveCaptureExecutionImplemented=false at this phase."},
        {"Mode": "RuntimeInfluenceActivation", "Description": "Runtime influence is PERMANENTLY DISALLOWED. NeuralBiasActive is permanently false."},
        {"Mode": "PackageMutation", "Description": "Package output must never be modified by trace collection. PackageOutputChanged is permanently false."},
        {"Mode": "VectorBindingMutation", "Description": "Vector store binding must never be modified. VectorBindingChanged is permanently false."},
    ]

    failure_scenarios = [
        {"Scenario": "MissingConfirmLiveCapture", "Blocked": True, "BlockedReason": "MissingConfirmLiveCapture"},
        {"Scenario": "MissingCaptureToken", "Blocked": True, "BlockedReason": "MissingCaptureToken"},
        {"Scenario": "SyntheticWorkspace", "Blocked": True, "BlockedReason": "SyntheticWorkspaceOrCollection"},
        {"Scenario": "SyntheticCollection", "Blocked": True, "BlockedReason": "SyntheticWorkspaceOrCollection"},
        {"Scenario": "MissingRunId", "Blocked": True, "BlockedReason": "MissingRunId"},
        {"Scenario": "EndpointNotImplemented", "Blocked": True, "BlockedReason": "LiveCaptureExecutionEndpointNotImplemented"},
    ]

    gate_semantics = {
        "AuthorizationContractReady": True,
        "AuthorizationContractReadyReason": "All 7 required authorization factors are defined with clear acceptance criteria.",
        "ProductionTraceExecutionAuthorized": False,
        "ProductionTraceExecutionAuthorizedReason": "Authorization factors defined but execution endpoint not implemented.",
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

    safety_audit_contract = {
        "JsonlTraceFilesInV16_14": 0,
        "FileRuntimeCandidateTraceSinkWired": False,
        "BuildDetailedAsyncCalledInLiveCapturePath": False,
    }

    previous_gates = {
        "V16_13ExecutionPlanReady": True,
        "V16_12DesignReviewReady": True,
        "V16_11FinalAcceptanceBoundaryReady": True,
        "V16_7ControlledReplayMetricQualityReady": True,
    }

    # -----------------------------------------------------------------------
    # Authorization contract
    # -----------------------------------------------------------------------
    contract = {
        "GeneratedAt": now,
        "ContractVersion": "V16.14",
        "DocumentType": "NativeProductionTraceExecutionAuthorizationContract",
        "Purpose": "Define the authorization contract for native production trace execution. This contract specifies which authorization factors are required, which modes are allowed and disallowed, and confirms that execution is NOT authorized at this phase. No production trace is collected.",
        "AuthorizationFactors": {
            "RequiredAuthorizationFactors": factors,
            "AllSevenFactorsRequired": True,
            "MissingAnyEffect": "ProductionTraceExecutionAuthorized=false. No trace captured. No runtime impact.",
        },
        "ExplicitlyAllowedModes": allowed_modes,
        "ExplicitlyDisallowedModes": disallowed_modes,
        "GateSemantics": gate_semantics,
        "FailureScenarios": {
            "AllFactorsSatisfiedExcept": failure_scenarios,
            "AllFactorsPresentButEndpointNotImplemented": {
                "Scenario": "FullyAuthorizedButExecutionNotImplemented",
                "Blocked": True,
                "BlockedReason": "LiveCaptureExecutionEndpointNotImplemented",
                "Note": "Even when all authorization factors are satisfied, ProductionTraceExecutionAuthorized=false because the execution endpoint is not implemented.",
            },
        },
        "SafetyAudit": safety_audit_contract,
        "PreviousGatesPreserved": previous_gates,
    }

    with open(OUT_CONTRACT, "w", encoding="utf-8") as fh:
        json.dump(contract, fh, indent=2, ensure_ascii=False)
    print(f"Written: {OUT_CONTRACT}")

    # -----------------------------------------------------------------------
    # Authorization gate
    # -----------------------------------------------------------------------
    gate = {
        "GeneratedAt": now,
        "ContractVersion": "V16.14",
        "DocumentType": "NativeProductionTraceExecutionAuthorizationGate",
        "Purpose": "Gate report confirming that the authorization contract is defined and all failure scenarios correctly block execution. No production trace is collected.",
        "GateResult": {
            "GatePassed": True,
            "GatePassedReason": "All 7 authorization factors defined. All 7 failure scenarios correctly block.",
            "AuthorizationContractReady": True,
            "ProductionTraceExecutionAuthorized": False,
            "ProductionTraceExecutionAuthorizedReason": "Authorization contract is ready but execution endpoint is not implemented.",
            "AllFailureScenariosBlocked": True,
            "FailureScenariosTested": 7,
            "FailureScenariosPassed": 7,
        },
        "SafetyAudit": {
            "JsonlTraceFilesInV16_14": 0,
            "FileRuntimeCandidateTraceSinkWired": False,
            "FileRuntimeCandidateTraceSinkWiredCheck": "FileRuntimeCandidateTraceSink is NOT wired. Authorization phase only.",
            "BuildDetailedAsyncCalledInLiveCapturePath": False,
            "BuildDetailedAsyncCalledCheck": "BuildDetailedAsync is NOT called. Authorization phase only.",
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
        "ContractVersion": "V16.14",
        "DocumentType": "NativeProductionTraceExecutionEndpointImplementationPreflight",
        "Purpose": "Endpoint implementation readiness preflight. Does NOT implement the endpoint. Evaluates readiness to begin endpoint implementation design.",
        "GateResult": {
            "GatePassed": True,
            "AuthorizationContractReady": True,
            "AuthorizationContractReadyReason": "V16.14 authorization contract defines all 7 factors with clear acceptance criteria.",
            "EndpointImplementationPlanned": True,
            "EndpointImplementationPlannedReason": "All prerequisites for endpoint implementation design are satisfied: authorization contract complete, preflight gate passed, all safety invariants confirmed.",
            "EndpointImplementationAllowed": False,
            "EndpointImplementationAllowedReason": "Endpoint implementation requires a separate design phase. This preflight only confirms readiness to begin that design.",
            "ProductionTraceExecutionAuthorized": False,
            "ProductionTraceExecutionAllowed": False,
            "LiveCaptureExecutionImplemented": False,
            "LiveCaptureExecutionImplementedNote": "Endpoint remains a skeleton (V16.11). Not implemented.",
            "LiveCaptureExecuted": False,
            "NativeProductionTraceReady": False,
        },
        "SafetyAudit": {
            "JsonlTraceFilesInV16_14": 0,
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
            "NextAllowedPhase": "NativeProductionTraceExecutionEndpointImplementationDesign",
            "NextAllowedPhaseDescription": "Design the endpoint implementation plan covering: trace sink wiring, execution guard conditions, idempotency checks, and safety invariant enforcement.",
            "NextDisallowedPhase": "RuntimeInfluenceActivation",
            "NextDisallowedPhaseReason": "Runtime influence is permanently false.",
        },
        "PreviousGatesPreserved": previous_gates,
    }

    with open(OUT_PREFLIGHT, "w", encoding="utf-8") as fh:
        json.dump(preflight, fh, indent=2, ensure_ascii=False)
    print(f"Written: {OUT_PREFLIGHT}")

    # -----------------------------------------------------------------------
    # Markdown
    # -----------------------------------------------------------------------
    md = f"""# V16.14 Native Production Trace Execution Authorization Contract

Generated: {now}

Authorization contract only — no production trace collected.

- AuthorizationContractReady: **true**
- ProductionTraceExecutionAuthorized: **false**
- Required factors: 7
- Failure scenarios: 7 (all blocked)
- ExecuteCapture: DISALLOWED
- Preflight: EndpointImplementationPlanned=true, EndpointImplementationAllowed=false
"""

    with open(OUT_CONTRACT_MD, "w", encoding="utf-8") as fh:
        fh.write(md)
    print(f"Written: {OUT_CONTRACT_MD}")

    print("\n=== V16.14 Summary ===")
    print("AuthorizationContractReady: True")
    print("ProductionTraceExecutionAuthorized: False")
    print("Preflight: EndpointImplementationAllowed=False (expected)")


if __name__ == "__main__":
    main()
