"""
V16.8: Production Trace Capture Authorization Contract & Pilot Boundary
======================================================================
Defines strong authorization contract for production trace capture.
LiveCapture requires hard authorization tokens. Default is PreviewOnly.
"""

import json
import os
from datetime import datetime, timezone

BASE = os.path.dirname(os.path.abspath(__file__))

OUT_CONTRACT_JSON = os.path.join(BASE, "production-capture-authorization-contract.json")
OUT_CONTRACT_MD = os.path.join(BASE, "production-capture-authorization-contract.md")


def main():
    now = datetime.now(timezone.utc).isoformat()

    contract = {
        "GeneratedAt": now,
        "ContractVersion": "V16.8",
        "ContractPurpose": "Define hard authorization boundary for production candidate-scoring trace capture. LiveCapture must not be achievable without explicit multi-factor authorization.",
        "AuthorizationModes": {
            "PreviewOnly": {
                "Level": 0,
                "Default": True,
                "Description": "Artifact/plan generation only. No trace collection occurs. Safety gates reviewed but no runtime impact.",
                "Activation": "No additional parameters required. This is the default mode.",
                "RuntimeInfluenceAllowed": False,
                "TraceCaptured": False,
            },
            "ControlledReplay": {
                "Level": 1,
                "Default": False,
                "Description": "Replays trace collection from FileSystem-backed stores with seeded corpus or pre-existing workspace data. Uses FileRuntimeCandidateTraceSink. runId-scoped, idempotent output.",
                "Activation": "eval v16_7-controlled-replay-native-trace [--workspaceId <id>] [--collectionId <id>] [--runId <id>]",
                "ActivationRequirements": [
                    "workspaceId (optional, defaults to v16_7-rich-replay)",
                    "collectionId (optional, defaults to rich-corpus)",
                    "runId (optional, auto-generated timestamp)",
                ],
                "HardGates": [
                    "FileSystem-backed stores only",
                    "RejectExistingRunId idempotency",
                    "RuntimeInfluenceAllowed=false",
                    "PackageOutputChanged=false",
                    "VectorBindingChanged=false",
                ],
                "Status": "Implemented — V16.7 Repair passed sufficiency gate (33 rows, 8 sections, 4 channels)",
            },
            "LiveCaptureCandidate": {
                "Level": 2,
                "Default": False,
                "Description": "Live production trace capture is REQUESTED but NOT YET AUTHORIZED. All authorization parameters must be present but the capture token has not been validated. This mode generates a readiness report but does NOT execute capture.",
                "Activation": "eval v16_6-native-production-trace-plan --mode LiveCapture --workspaceId <id> --collectionId <id>",
                "ActivationRequirements": [
                    "--mode LiveCapture",
                    "--workspaceId <real-workspace>",
                    "--collectionId <real-collection>",
                    "Must NOT be synthetic IDs (native-ws, smoke-ws, prod-ws, etc.)",
                ],
                "HardBlocks": [
                    "LiveCaptureBlocked=true until --confirm-live-capture AND --capture-token provided",
                    "RuntimeInfluenceAllowed=false (permanent, not negotiable)",
                    "NeuralBiasActive=false (permanent, not negotiable)",
                ],
                "Status": "Defined — authorization contract complete. Execution blocked pending capture token.",
            },
            "LiveCaptureAuthorized": {
                "Level": 3,
                "Default": False,
                "Description": "Live production trace capture is FULLY AUTHORIZED. All five authorization parameters validated. Capture proceeds with all safety gates enforced.",
                "Activation": (
                    "eval v16_6-native-production-trace-plan "
                    "--mode LiveCapture "
                    "--confirm-live-capture "
                    "--capture-token <token> "
                    "--workspaceId <real-workspace> "
                    "--collectionId <real-collection> "
                    "--runId <unique-id>"
                ),
                "ActivationRequirements": [
                    "--mode LiveCapture",
                    "--confirm-live-capture (explicit confirmation gate)",
                    "--capture-token <token> (hard authorization token)",
                    "--workspaceId <real-workspace>",
                    "--collectionId <real-collection>",
                    "--runId <unique-id>",
                ],
                "AllParametersRequired": True,
                "MissingAnyParameterBlocksCapture": True,
                "RuntimeInfluenceAllowed": False,
                "RuntimeInfluenceAllowedPermanent": True,
                "RuntimeInfluenceAllowedNote": "Even under LiveCaptureAuthorized, RuntimeInfluenceAllowed is PERMANENTLY FALSE. Neural bias is never activated at runtime. Package output is never modified.",
                "Status": "NOT IMPLEMENTED — authorization contract defined but execution endpoint not built. Requires explicit implementation with all safety gates.",
            },
        },
        "LiveCaptureAuthorizationBarrier": {
            "Title": "LiveCapture Five-Factor Authorization Barrier",
            "Factors": [
                {"factor": "--mode LiveCapture", "type": "mode_declaration", "required": True, "description": "Declares intent to capture live traces."},
                {"factor": "--confirm-live-capture", "type": "confirmation_gate", "required": True, "description": "Explicit confirmation token. Prevents accidental activation."},
                {"factor": "--capture-token <token>", "type": "hard_authorization", "required": True, "description": "Hard authorization token. Must be validated before capture proceeds."},
                {"factor": "--workspaceId <real>", "type": "target_identification", "required": True, "description": "Real workspace ID. Synthetic IDs rejected."},
                {"factor": "--collectionId <real>", "type": "target_identification", "required": True, "description": "Real collection ID. Synthetic IDs rejected."},
                {"factor": "--runId <unique>", "type": "idempotency", "required": True, "description": "Unique run identifier. Prevents duplicate captures."},
            ],
            "AllFiveRequired": True,
            "MissingAnyEffect": "LiveCaptureBlocked=true. No trace captured. No runtime impact. Error message with missing factors listed.",
        },
        "PilotBoundary": {
            "NativeProductionTracePilotReady": False,
            "NativeProductionTracePilotReadyReason": "Pilot readiness requires: (1) ControlledReplay sufficiency passed (achieved), (2) LiveCapture authorization contract defined (achieved), (3) LiveCapture execution endpoint implemented (NOT achieved), (4) LiveCapture executed against real production workspace (NOT achieved).",
            "ProductionCaptureAuthorizationReady": True,
            "ProductionCaptureAuthorizationReadyReason": "All four authorization modes defined with clear activation requirements, hard gates, and five-factor LiveCapture barrier. Contract is complete and enforceable.",
            "ControlledReplayMetricQualityReady": True,
            "ControlledReplayMetricQualityReadyProof": "V16.7 rich-001: 33 rows, 8 sections, 4 channels, WeightedPairwiseAcc=0.6504 >= 0.55",
            "NativeProductionTraceReady": False,
            "NativeProductionTraceReadyNote": "Even with authorization contract ready, NativeProductionTraceReady requires actual production trace capture and metric evaluation. Blocked until LiveCaptureAuthorized execution completes.",
            "ProductionGeneralizationReady": False,
            "ProductionGeneralizationReadyNote": "Production generalization requires production-native trace collection + metric quality pass. Neither fulfilled.",
        },
        "SafetyGates": {
            "RuntimeInfluenceAllowed": False,
            "RuntimeInfluenceAllowedPermanent": True,
            "PackageOutputChanged": False,
            "RuntimePromotionApplied": False,
            "VectorBindingChanged": False,
            "NeuralBiasActive": False,
            "V14GatePreserved": True,
            "V16_5GatePreserved": True,
            "V16_6GatePreserved": True,
            "V16_7GatePreserved": True,
        },
    }

    with open(OUT_CONTRACT_JSON, "w", encoding="utf-8") as fh:
        json.dump(contract, fh, indent=2, ensure_ascii=False)
    print(f"Written: {OUT_CONTRACT_JSON}")

    md = f"""# V16.8 Production Trace Capture Authorization Contract
Generated: {now}

## Authorization Modes

| Level | Mode | Default | Status |
|---|---|---|---|
| 0 | PreviewOnly | Yes | Active |
| 1 | ControlledReplay | No | Implemented (V16.7 sufficient) |
| 2 | LiveCaptureCandidate | No | Defined — blocked pending token |
| 3 | LiveCaptureAuthorized | No | NOT IMPLEMENTED |

## LiveCapture Five-Factor Authorization Barrier

LiveCaptureAuthorized requires ALL FIVE factors. Missing any one = blocked.

| # | Factor | Type |
|---|---|---|
| 1 | `--mode LiveCapture` | Mode declaration |
| 2 | `--confirm-live-capture` | Confirmation gate |
| 3 | `--capture-token <token>` | Hard authorization |
| 4 | `--workspaceId <real>` | Target identification |
| 5 | `--collectionId <real>` | Target identification |
| 6 | `--runId <unique>` | Idempotency |

**All five required.** Any missing → `LiveCaptureBlocked=true`.

## Pilot Boundary

| Gate | Value |
|---|---|
| `NativeProductionTracePilotReady` | false |
| `ProductionCaptureAuthorizationReady` | true |
| `ControlledReplayMetricQualityReady` | true (WeightedPairwiseAcc=0.6504) |
| `NativeProductionTraceReady` | false |
| `ProductionGeneralizationReady` | false |

## Permanent Safety Gates
- RuntimeInfluenceAllowed: **PERMANENTLY FALSE**
- PackageOutputChanged: false
- VectorBindingChanged: false
- NeuralBiasActive: false
"""

    with open(OUT_CONTRACT_MD, "w", encoding="utf-8") as fh:
        fh.write(md)
    print(f"Written: {OUT_CONTRACT_MD}")

    print("\n=== V16.8 Summary ===")
    print(f"Authorization modes: 4 (Level 0-3)")
    print(f"LiveCapture requires 5-factor authorization")
    print(f"ProductionCaptureAuthorizationReady: True")
    print(f"NativeProductionTraceReady: False")
    print(f"RuntimeInfluenceAllowed: Permanently False")


if __name__ == "__main__":
    main()
