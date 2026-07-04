# V16.6 Native Production Trace Acquisition Plan
Generated: 2026-07-04T18:35:31.2109046+00:00 | Mode: PreviewOnly | PreviewOnly: True

## Acquisition Modes
| Mode | Status | Description |
|---|---|---|
| PreviewOnly | Active | Plan generation only. No trace collection. Default. |
| ControlledReplay | Inactive | Collects traces with safety gates enforced. |
| LiveCapture | Not authorized | Requires explicit --workspaceId + --collectionId. |

## Controlled Replay Safety
- RuntimeInfluenceGated: true
- IdempotencyEnforced: true (RejectExistingRunId + RunScopedTracePath)
- TraceCaptureOnly: true
- PackageOutputChanged: false
- RuntimePromotionApplied: false
- VectorBindingChanged: false

## Production Trace Criteria (all must be met)
1. Real workspace (not synthetic 'native-ws')
2. Real collection (not synthetic 'native-col')
3. Real query/task patterns (not seeded content)
4. traceSource=3 for all rows
5. Validation errors = 0
6. Multiple runs (not single dry-run)
7. WeightedPairwiseAcc >= 0.55 on production data

## Current State
- HasRealWorkspace: false
- HasRealCollection: false
- MultipleRuns: false
- WeightedPairwiseAccSufficient: false (current=0.5192, need >= 0.55)

## Acquisition Steps (PreviewOnly)
1. Identify target workspace/collection
2. Verify RuntimeInfluenceAllowed=false
3. Wire FileRuntimeCandidateTraceSink
4. Set unique operation ID
5. Execute BuildDetailedAsync()
6. Flush, validate, run V16.5 evaluator
7. Check NativeWeightedPairwiseAcc >= 0.55
8. If quality passes, consider ProductionGeneralizationReady (still gated)

## Safety
RuntimeInfluenceAllowed: false | PackageOutputChanged: false | VectorBindingChanged: false