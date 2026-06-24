# Output Token Priority Shadow Gate

OperationId: `vector-output-token-priority-shadow-gate-c9ef1458df18413390129f810d822b0c`
CreatedAt: `2026-06-19T17:00:18.4247027+00:00`

## Summary
- ShadowPassed: `True`
- GatePassed: `True`
- Recommendation: `ReadyForOutputPolicyShadowFreeze`
- ProfileName: `combined-safe`
- Protocol: `retrieval-eval-protocol-v1` topK vector/merged/final=`5/8/5`
- SampleCount: `144`
- CorpusItemCount: `144`
- BlindHoldoutSampleCount: `24`

## Package Shadow Metrics
- BaselinePackageCount: `720`
- ShadowPackageCount: `720`
- BaselineTokenTotal: `36318`
- ShadowTokenTotal: `36444`
- TokenDeltaTotal: `126`
- TokenDeltaMax: `8`
- TokenDeltaP95: `8`
- TokenBudgetExceededCount: `0`
- SectionBudgetExceededCount: `0`
- PriorityDeltaCount: `310`
- PriorityInversionCount: `0`
- MandatoryCoverageBaseline: `0.6806`
- MandatoryCoverageShadow: `0.9028`
- MandatoryCoverageDelta: `+0.2222`
- DroppedRequiredCandidateCount: `0`
- SectionMismatchCount: `0`

## Safety Invariants
- Risk/mustNot/lifecycle: `0` / `0` / `0`
- FormalOutputChanged: `0`
- FormalSelectedSetChanged: `False`
- FormalPackageWritten: `False`
- PackageOutputChanged: `False`
- PackingPolicyChanged: `False`
- RuntimeMutated: `False`
- VectorStoreBindingChanged: `False`
- FormalRetrievalAllowed: `False`
- RuntimeSwitchAllowed: `False`
- ReadyForRuntimeSwitch: `False`
- UseForRuntime: `False`


## Baseline Section Occupancy
| section | item count | token total | token max |
|---|---:|---:|---:|
| `normal_context` | 625 | 31633 | 52 |
| `historical_context` | 95 | 4685 | 50 |

## Shadow Section Occupancy
| section | item count | token total | token max |
|---|---:|---:|---:|
| `normal_context` | 625 | 31759 | 53 |
| `historical_context` | 95 | 4685 | 50 |

## Blocked Reasons
- none

## Source Reports
- `blindCorpus`: `vector\v5\source-aware-ranking-blind-holdout-corpus.jsonl`
- `blindSamples`: `vector\v5\source-aware-ranking-blind-holdout-samples.jsonl`
- `corpus`: `vector\dataset-v2\stress\corpus.jsonl`
- `runtimeChangeGate`: `learning\readiness\learning-runtime-change-readiness-gate.json`
- `samples`: `vector\dataset-v2\stress\samples.jsonl`
- `v511ProtocolGate`: `vector\v5\retrieval-eval-protocol-gate.json`
- `v514SourceAwareRankingGate`: `vector\v5\source-aware-ranking-repair-gate.json`

V5.15 shadow only. The locked `combined-safe` preview profile and V5.11 eval protocol are evaluated without formal selected-set, package output, PackingPolicy, runtime, or vector binding mutation.
