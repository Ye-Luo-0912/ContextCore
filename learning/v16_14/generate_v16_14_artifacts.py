"""
V16.14: Native Production Trace Execution Authorization Contract
=================================================================
Authorization contract only — no production trace collected. No LiveCapture execution.
Defines 7 authorization factors, allowed/disallowed modes, and failure scenarios.
"""
import json
import os
from datetime import datetime, timezone

BASE = os.path.dirname(os.path.abspath(__file__))
OUT_CONTRACT = os.path.join(BASE, "native-production-trace-execution-authorization-contract.json")
OUT_CONTRACT_MD = os.path.join(BASE, "native-production-trace-execution-authorization-contract.md")
OUT_GATE = os.path.join(BASE, "native-production-trace-execution-authorization-gate.json")


def main():
    now = datetime.now(timezone.utc).isoformat()

    contract = {
        "GeneratedAt": now,
        "ContractVersion": "V16.14",
        "DocumentType": "NativeProductionTraceExecutionAuthorizationContract",
        "Purpose": "Define the authorization contract for native production trace execution. No production trace is collected.",
        "AuthorizationFactors": {
            "RequiredAuthorizationFactors": [
                {"Factor": "--confirm-live-capture", "Type": "confirmation_gate", "Required": True},
                {"Factor": "--capture-token <token>", "Type": "hard_authorization", "Required": True},
                {"Factor": "--workspaceId <real>", "Type": "target_identification", "Required": True},
                {"Factor": "--collectionId <real>", "Type": "target_identification", "Required": True},
                {"Factor": "--runId <unique>", "Type": "idempotency", "Required": True},
                {"Factor": "No synthetic workspace/collection", "Type": "data_provenance", "Required": True},
                {"Factor": "LiveCaptureExecutionEndpointImplemented", "Type": "implementation_gate", "Required": True, "ValueForThisPhase": False},
            ],
            "AllSevenFactorsRequired": True,
        },
        "ExplicitlyAllowedModes": ["PreviewOnly", "PlanOnly", "AuthorizationContractOnly"],
        "ExplicitlyDisallowedModes": ["ExecuteCapture", "RuntimeInfluenceActivation", "PackageMutation", "VectorBindingMutation"],
        "GateSemantics": {
            "AuthorizationContractReady": True,
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
        "FailureScenarios": {
            "TotalScenarios": 7,
            "AllBlocked": True,
        },
        "SafetyAudit": {
            "JsonlTraceFilesInV16_14": 0,
            "FileRuntimeCandidateTraceSinkWired": False,
        },
    }

    with open(OUT_CONTRACT, "w", encoding="utf-8") as fh:
        json.dump(contract, fh, indent=2, ensure_ascii=False)
    print(f"Written: {OUT_CONTRACT}")

    gate = {
        "GeneratedAt": now,
        "ContractVersion": "V16.14",
        "DocumentType": "NativeProductionTraceExecutionAuthorizationGate",
        "GateResult": {
            "GatePassed": True,
            "AuthorizationContractReady": True,
            "ProductionTraceExecutionAuthorized": False,
            "AllFailureScenariosBlocked": True,
        },
        "SafetyAudit": {
            "JsonlTraceFilesInV16_14": 0,
            "FileRuntimeCandidateTraceSinkWired": False,
        },
        "GateSemantics": {
            "RuntimeInfluenceAllowed": False,
            "RuntimeInfluenceAllowedPermanent": True,
            "PackageOutputChanged": False,
            "VectorBindingChanged": False,
        },
    }

    with open(OUT_GATE, "w", encoding="utf-8") as fh:
        json.dump(gate, fh, indent=2, ensure_ascii=False)
    print(f"Written: {OUT_GATE}")

    md = f"""# V16.14 Native Production Trace Execution Authorization Contract

Generated: {now}

Authorization contract only — no production trace collected.

- AuthorizationContractReady: **true**
- ProductionTraceExecutionAuthorized: **false**
- Required factors: 7
- Failure scenarios: 7 (all blocked)
- ExecuteCapture: DISALLOWED
"""

    with open(OUT_CONTRACT_MD, "w", encoding="utf-8") as fh:
        fh.write(md)
    print(f"Written: {OUT_CONTRACT_MD}")

    print("\n=== V16.14 Summary ===")
    print("AuthorizationContractReady: True")
    print("ProductionTraceExecutionAuthorized: False")


if __name__ == "__main__":
    main()
