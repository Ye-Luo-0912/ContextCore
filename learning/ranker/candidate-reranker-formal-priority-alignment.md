# Candidate Reranker Formal Priority Alignment

Generated: 2026-06-12T06:56:40.2782572+00:00

## A3

- Samples: `50`
- RegressionCount: `20`
- FormalPriorityMismatchCount: `3`
- RecoveredCount: `0`
- RecoveredByLayerPriority: `0`
- RecoveredBySourcePriority: `0`
- RecoveredByCurrentTaskBoost: `0`
- RecoveredByConstraintRelevance: `0`
- RecoveredByStableMemoryBias: `0`
- UnexplainedMismatchCount: `20`
- AbstainCount: `0`
- NetGainAfterAbstain: `3`
- Recommendation: `NeedsFeatureTuning`

| Sample | Mode | Intent | FormalTop | BaselineShadowTop | FormalPriorityTop | Recovered | Abstain | Unexplained | Action |
|---|---|---|---|---|---|---:|---:|---:|---|
| `chat-sample-001` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | `const:chat-output-lang` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `chat-sample-003` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | `const:chat-output-lang` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `chat-sample-004` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | `const:chat-output-lang` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `chat-sample-009` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | `const:chat-output-lang` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `chat-sample-010` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | `const:chat-output-lang` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `project-sample-001` | `ProjectMode` | `Unknown` | `const:project-security` | `const:project-security` | `const:project-security` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `project-sample-002` | `ProjectMode` | `Unknown` | `const:project-security` | `const:project-security` | `const:project-security` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `project-sample-003` | `ProjectMode` | `Unknown` | `const:project-security` | `const:project-security` | `const:project-security` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `project-sample-009` | `ProjectMode` | `Unknown` | `const:project-security` | `const:project-security` | `const:project-security` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `novel-sample-002` | `NovelMode` | `Unknown` | `const:novel-rules` | `const:novel-rules` | `const:novel-rules` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `novel-sample-007` | `NovelMode` | `Unknown` | `const:novel-rules` | `const:novel-rules` | `const:novel-rules` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `novel-sample-009` | `NovelMode` | `Unknown` | `const:novel-rules` | `const:novel-rules` | `const:novel-rules` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `automation-sample-001` | `AutomationMode` | `Unknown` | `const:automation-rules` | `const:automation-rules` | `const:automation-rules` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `automation-sample-002` | `AutomationMode` | `Unknown` | `const:automation-rules` | `const:automation-rules` | `const:automation-rules` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `automation-sample-003` | `AutomationMode` | `Unknown` | `const:automation-rules` | `const:automation-rules` | `const:automation-rules` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `automation-sample-007` | `AutomationMode` | `Unknown` | `const:automation-rules` | `const:automation-rules` | `const:automation-rules` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `automation-sample-009` | `AutomationMode` | `Unknown` | `const:automation-rules` | `const:automation-rules` | `const:automation-rules` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `coding-sample-002` | `CodingMode` | `Unknown` | `const:coding-conventions` | `const:coding-conventions` | `const:coding-conventions` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `coding-sample-007` | `CodingMode` | `Unknown` | `const:coding-conventions` | `const:coding-conventions` | `const:coding-conventions` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `coding-sample-009` | `CodingMode` | `Unknown` | `const:coding-conventions` | `const:coding-conventions` | `const:coding-conventions` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |

## Extended

- Samples: `113`
- RegressionCount: `30`
- FormalPriorityMismatchCount: `27`
- RecoveredCount: `0`
- RecoveredByLayerPriority: `0`
- RecoveredBySourcePriority: `0`
- RecoveredByCurrentTaskBoost: `0`
- RecoveredByConstraintRelevance: `0`
- RecoveredByStableMemoryBias: `0`
- UnexplainedMismatchCount: `30`
- AbstainCount: `2`
- NetGainAfterAbstain: `23`
- Recommendation: `NeedsFeatureTuning`

| Sample | Mode | Intent | FormalTop | BaselineShadowTop | FormalPriorityTop | Recovered | Abstain | Unexplained | Action |
|---|---|---|---|---|---|---:|---:|---:|---|
| `chat-sample-001` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | `const:chat-output-lang` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `chat-sample-003` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | `const:chat-output-lang` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `chat-sample-004` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | `const:chat-output-lang` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `chat-sample-009` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | `const:chat-output-lang` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `chat-sample-010` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | `const:chat-output-lang` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `chat-20260529-001` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | `const:chat-output-lang` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `chat-20260529-002` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | `const:chat-output-lang` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `chat-20260529-003` | `ChatMode` | `Unknown` | `constraint:gap:constraint-gap-chat-20260529-003-no-promote-repetition` | `constraint:gap:constraint-gap-chat-20260529-003-no-promote-repetition` | `constraint:gap:constraint-gap-chat-20260529-003-no-promote-repetition` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `project-sample-001` | `ProjectMode` | `Unknown` | `const:project-security` | `const:project-security` | `const:project-security` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `project-sample-002` | `ProjectMode` | `Unknown` | `const:project-security` | `const:project-security` | `const:project-security` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `project-sample-003` | `ProjectMode` | `Unknown` | `const:project-security` | `const:project-security` | `const:project-security` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `project-sample-007` | `ProjectMode` | `Unknown` | `const:project-security` | `const:project-security` | `const:project-security` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `project-sample-009` | `ProjectMode` | `Unknown` | `const:project-security` | `const:project-security` | `const:project-security` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `project-20260529-003` | `ProjectMode` | `Unknown` | `const:project-security` | `const:project-security` | `const:project-security` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `novel-sample-002` | `NovelMode` | `Unknown` | `const:novel-rules` | `const:novel-rules` | `const:novel-rules` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `novel-sample-007` | `NovelMode` | `Unknown` | `const:novel-rules` | `const:novel-rules` | `const:novel-rules` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `novel-sample-009` | `NovelMode` | `Unknown` | `const:novel-rules` | `const:novel-rules` | `const:novel-rules` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `novel-sample-010` | `NovelMode` | `Unknown` | `const:novel-rules` | `const:novel-rules` | `const:novel-rules` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `novel-20260529-014` | `NovelMode` | `Unknown` | `const:novel-rules` | `plot:mainline-current` | `plot:mainline-current` | False | True | False | Keep formal ranking for low-margin decisions; abstain before any guarded opt-in. |
| `automation-sample-001` | `AutomationMode` | `Unknown` | `const:automation-rules` | `const:automation-rules` | `const:automation-rules` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `automation-sample-002` | `AutomationMode` | `Unknown` | `const:automation-rules` | `const:automation-rules` | `const:automation-rules` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `automation-sample-003` | `AutomationMode` | `Unknown` | `const:automation-rules` | `const:automation-rules` | `const:automation-rules` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `automation-sample-007` | `AutomationMode` | `Unknown` | `const:automation-rules` | `const:automation-rules` | `const:automation-rules` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `automation-sample-009` | `AutomationMode` | `Unknown` | `const:automation-rules` | `const:automation-rules` | `const:automation-rules` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `automation-20260529-001` | `AutomationMode` | `Unknown` | `const:automation-rules` | `const:automation-rules` | `const:automation-rules` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `automation-20260529-002` | `AutomationMode` | `Unknown` | `const:automation-rules` | `const:automation-rules` | `const:automation-rules` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `coding-sample-002` | `CodingMode` | `Unknown` | `const:coding-conventions` | `const:coding-conventions` | `const:coding-conventions` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `coding-sample-007` | `CodingMode` | `Unknown` | `const:coding-conventions` | `const:coding-conventions` | `const:coding-conventions` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `coding-sample-009` | `CodingMode` | `Unknown` | `const:coding-conventions` | `const:coding-conventions` | `const:coding-conventions` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `coding-20260529-005` | `CodingMode` | `Unknown` | `const:coding-conventions` | `api:public-contract` | `api:public-contract` | False | True | False | Keep formal ranking for low-margin decisions; abstain before any guarded opt-in. |
| `coding-20260529-007` | `CodingMode` | `Unknown` | `const:coding-conventions` | `const:coding-conventions` | `const:coding-conventions` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
| `coding-20260529-010` | `CodingMode` | `Unknown` | `const:coding-conventions` | `const:coding-conventions` | `const:coding-conventions` | False | False | True | Keep formal ranking and add missing mode / intent / package priority features offline. |
