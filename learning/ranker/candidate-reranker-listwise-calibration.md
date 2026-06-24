# Candidate Reranker Listwise Calibration

Generated: 2026-06-12T06:57:53.6246230+00:00

## A3

- Samples: `50`
- CandidateCount: `243`
- MustHitCandidateCount: `41`
- RegressionCount: `20`
- LowMarginDecisionCount: `5`
- FormalPriorityMismatchCount: `3`
- AverageTop1Margin: `31.2068`
- AverageTopKOverlap: `0.5732`
- FormalOutputChanged: `False`
- Recommendation: `NeedsFeatureTuning`

### Calibration Issues
- `KeepFormalRanking`: `30`
- `MissingIntentFeature`: `15`
- `LowMarginAmbiguity`: `5`

| Sample | Mode | Intent | FormalTop1 | ShadowTop1 | FormalBest | ShadowBest | Margin | Overlap | Issue | Action |
|---|---|---|---|---|---:|---:|---:|---:|---|---|
| `chat-sample-001` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | 2 | 5 | 49.6 | 0.7 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
| `chat-sample-003` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | 2 | 3 | 38.2 | 0.8 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
| `chat-sample-004` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | 2 | 3 | 51.35 | 0.8 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
| `chat-sample-009` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | 2 | 0 | 92 | 0.5 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
| `chat-sample-010` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | 2 | 3 | 49.6 | 0.5 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
| `project-sample-001` | `ProjectMode` | `Unknown` | `const:project-security` | `const:project-security` | 2 | 0 | 48.45 | 0.4 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
| `project-sample-002` | `ProjectMode` | `Unknown` | `const:project-security` | `const:project-security` | 2 | 0 | 36.45 | 0.4 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
| `project-sample-003` | `ProjectMode` | `Unknown` | `const:project-security` | `const:project-security` | 2 | 0 | 41.65 | 0.4 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
| `project-sample-009` | `ProjectMode` | `Unknown` | `const:project-security` | `const:project-security` | 2 | 0 | 0 | 0.5 | `LowMarginAmbiguity` | Keep formal ranking and collect more listwise labels for low-margin cases. |
| `novel-sample-002` | `NovelMode` | `Unknown` | `const:novel-rules` | `const:novel-rules` | 2 | 0 | 42.7 | 0.3333 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
| `novel-sample-007` | `NovelMode` | `Unknown` | `const:novel-rules` | `const:novel-rules` | 2 | 0 | 0 | 0.3333 | `LowMarginAmbiguity` | Keep formal ranking and collect more listwise labels for low-margin cases. |
| `novel-sample-009` | `NovelMode` | `Unknown` | `const:novel-rules` | `const:novel-rules` | 2 | 0 | 0 | 0.3333 | `LowMarginAmbiguity` | Keep formal ranking and collect more listwise labels for low-margin cases. |
| `automation-sample-001` | `AutomationMode` | `Unknown` | `const:automation-rules` | `const:automation-rules` | 2 | 5 | 33.8 | 0.7 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
| `automation-sample-002` | `AutomationMode` | `Unknown` | `const:automation-rules` | `const:automation-rules` | 2 | 0 | 38.3 | 0.5556 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
| `automation-sample-003` | `AutomationMode` | `Unknown` | `const:automation-rules` | `const:automation-rules` | 2 | 6 | 33.8 | 0.7 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
| `automation-sample-007` | `AutomationMode` | `Unknown` | `const:automation-rules` | `const:automation-rules` | 2 | 0 | 60.065 | 0.2 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
| `automation-sample-009` | `AutomationMode` | `Unknown` | `const:automation-rules` | `const:automation-rules` | 2 | 0 | 0 | 0.5 | `LowMarginAmbiguity` | Keep formal ranking and collect more listwise labels for low-margin cases. |
| `coding-sample-002` | `CodingMode` | `Unknown` | `const:coding-conventions` | `const:coding-conventions` | 2 | 0 | 53.99 | 0.4286 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
| `coding-sample-007` | `CodingMode` | `Unknown` | `const:coding-conventions` | `const:coding-conventions` | 2 | 0 | 40.98 | 0.2 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
| `coding-sample-009` | `CodingMode` | `Unknown` | `const:coding-conventions` | `const:coding-conventions` | 2 | 0 | 0 | 0.5 | `LowMarginAmbiguity` | Keep formal ranking and collect more listwise labels for low-margin cases. |

## Extended

- Samples: `113`
- CandidateCount: `1947`
- MustHitCandidateCount: `135`
- RegressionCount: `30`
- LowMarginDecisionCount: `1`
- FormalPriorityMismatchCount: `27`
- AverageTop1Margin: `27.5602`
- AverageTopKOverlap: `0.6666`
- FormalOutputChanged: `False`
- Recommendation: `NeedsFeatureTuning`

### Calibration Issues
- `KeepFormalRanking`: `83`
- `MissingIntentFeature`: `29`
- `LowMarginAmbiguity`: `1`

| Sample | Mode | Intent | FormalTop1 | ShadowTop1 | FormalBest | ShadowBest | Margin | Overlap | Issue | Action |
|---|---|---|---|---|---:|---:|---:|---:|---|---|
| `chat-sample-001` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | 2 | 9 | 49.6 | 1 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
| `chat-sample-003` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | 2 | 3 | 38.2 | 0.7 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
| `chat-sample-004` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | 2 | 7 | 31.35 | 0.6 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
| `chat-sample-009` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | 2 | 0 | 66.05 | 0.5 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
| `chat-sample-010` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | 2 | 3 | 49.6 | 0.6 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
| `chat-20260529-001` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | 2 | 0 | 31.15 | 0.5 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
| `chat-20260529-002` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | 2 | 5 | 50.95 | 0.5 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
| `chat-20260529-003` | `ChatMode` | `Unknown` | `constraint:gap:constraint-gap-chat-20260529-003-no-promote-repetition` | `constraint:gap:constraint-gap-chat-20260529-003-no-promote-repetition` | 3 | 0 | 3.75 | 0.5 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
| `project-sample-001` | `ProjectMode` | `Unknown` | `const:project-security` | `const:project-security` | 2 | 0 | 48.45 | 0.8 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
| `project-sample-002` | `ProjectMode` | `Unknown` | `const:project-security` | `const:project-security` | 2 | 0 | 36.45 | 0.8 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
| `project-sample-003` | `ProjectMode` | `Unknown` | `const:project-security` | `const:project-security` | 2 | 0 | 41.65 | 0.8 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
| `project-sample-007` | `ProjectMode` | `Unknown` | `const:project-security` | `const:project-security` | 2 | 4 | 46 | 0.4 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
| `project-sample-009` | `ProjectMode` | `Unknown` | `const:project-security` | `const:project-security` | 2 | 0 | 0 | 0.5 | `LowMarginAmbiguity` | Keep formal ranking and collect more listwise labels for low-margin cases. |
| `project-20260529-003` | `ProjectMode` | `Unknown` | `const:project-security` | `const:project-security` | 2 | 3 | 27.05 | 0.7 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
| `novel-sample-002` | `NovelMode` | `Unknown` | `const:novel-rules` | `const:novel-rules` | 2 | 0 | 42.7 | 0.5556 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
| `novel-sample-007` | `NovelMode` | `Unknown` | `const:novel-rules` | `const:novel-rules` | 2 | 0 | 48.565 | 0.4 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
| `novel-sample-009` | `NovelMode` | `Unknown` | `const:novel-rules` | `const:novel-rules` | 2 | 0 | 72.8 | 0.6667 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
| `novel-sample-010` | `NovelMode` | `Unknown` | `const:novel-rules` | `const:novel-rules` | 2 | 4 | 32.115 | 0.7 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
| `automation-sample-001` | `AutomationMode` | `Unknown` | `const:automation-rules` | `const:automation-rules` | 2 | 9 | 18.75 | 0.5 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
| `automation-sample-002` | `AutomationMode` | `Unknown` | `const:automation-rules` | `const:automation-rules` | 2 | 0 | 25.25 | 0.6 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
| `automation-sample-003` | `AutomationMode` | `Unknown` | `const:automation-rules` | `const:automation-rules` | 2 | 0 | 18.75 | 0.6 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
| `automation-sample-007` | `AutomationMode` | `Unknown` | `const:automation-rules` | `const:automation-rules` | 2 | 0 | 33.53 | 0.3 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
| `automation-sample-009` | `AutomationMode` | `Unknown` | `const:automation-rules` | `const:automation-rules` | 2 | 0 | 84.4 | 0.6667 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
| `automation-20260529-001` | `AutomationMode` | `Unknown` | `const:automation-rules` | `const:automation-rules` | 2 | 3 | 18.57 | 0.7 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
| `automation-20260529-002` | `AutomationMode` | `Unknown` | `const:automation-rules` | `const:automation-rules` | 2 | 3 | 34.6 | 0.6 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
| `coding-sample-002` | `CodingMode` | `Unknown` | `const:coding-conventions` | `const:coding-conventions` | 2 | 0 | 53.99 | 0.6 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
| `coding-sample-007` | `CodingMode` | `Unknown` | `const:coding-conventions` | `const:coding-conventions` | 2 | 0 | 40.98 | 0.3 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
| `coding-sample-009` | `CodingMode` | `Unknown` | `const:coding-conventions` | `const:coding-conventions` | 2 | 0 | 84.4 | 0.8 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
| `coding-20260529-007` | `CodingMode` | `Unknown` | `const:coding-conventions` | `const:coding-conventions` | 2 | 3 | 35.9 | 1 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
| `coding-20260529-010` | `CodingMode` | `Unknown` | `const:coding-conventions` | `const:coding-conventions` | 2 | 4 | 50.18 | 0.9 | `MissingIntentFeature` | Add intent feature coverage to offline ranker input before tuning. |
