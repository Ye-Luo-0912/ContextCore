# Candidate Reranker Shadow Failure Audit

Generated: 2026-06-12T06:03:14.3054305+00:00

## A3

- Samples: `50`
- RegressionCount: `20`
- ScoreContractStatus: `Passed`
- RankableCandidateCount: `243`
- BlockedCandidateCount: `71`
- RiskCandidateInShadowTopK: `0`
- RecommendedNextAction: `Backfill lifecycle metadata and rerun offline audit.`

### Regression Reasons
- `MissingFeatureMetadata`: `15`
- `ScoreScaleMismatch`: `5`

| Sample | Mode | Intent | FormalTop1 | ShadowTop1 | FormalHit | ShadowHit | RiskTopK | Reason | Action |
|---|---|---|---|---|---:|---:|---:|---|---|
| `chat-sample-001` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | False | False | 0 | `MissingFeatureMetadata` | Align lifecycle and replacement metadata before weight tuning. |
| `chat-sample-003` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | False | False | 0 | `MissingFeatureMetadata` | Align lifecycle and replacement metadata before weight tuning. |
| `chat-sample-004` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | False | False | 0 | `MissingFeatureMetadata` | Align lifecycle and replacement metadata before weight tuning. |
| `chat-sample-009` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | False | False | 0 | `ScoreScaleMismatch` | Normalize score scale before comparing listwise ranks. |
| `chat-sample-010` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | False | False | 0 | `MissingFeatureMetadata` | Align lifecycle and replacement metadata before weight tuning. |
| `project-sample-001` | `ProjectMode` | `Unknown` | `const:project-security` | `const:project-security` | False | False | 0 | `MissingFeatureMetadata` | Align lifecycle and replacement metadata before weight tuning. |
| `project-sample-002` | `ProjectMode` | `Unknown` | `const:project-security` | `const:project-security` | False | False | 0 | `MissingFeatureMetadata` | Align lifecycle and replacement metadata before weight tuning. |
| `project-sample-003` | `ProjectMode` | `Unknown` | `const:project-security` | `const:project-security` | False | False | 0 | `MissingFeatureMetadata` | Align lifecycle and replacement metadata before weight tuning. |
| `project-sample-009` | `ProjectMode` | `Unknown` | `const:project-security` | `const:project-security` | False | False | 0 | `ScoreScaleMismatch` | Normalize score scale before comparing listwise ranks. |
| `novel-sample-002` | `NovelMode` | `Unknown` | `const:novel-rules` | `const:novel-rules` | False | False | 0 | `MissingFeatureMetadata` | Align lifecycle and replacement metadata before weight tuning. |
| `novel-sample-007` | `NovelMode` | `Unknown` | `const:novel-rules` | `const:novel-rules` | False | False | 0 | `MissingFeatureMetadata` | Align lifecycle and replacement metadata before weight tuning. |
| `novel-sample-009` | `NovelMode` | `Unknown` | `const:novel-rules` | `const:novel-rules` | False | False | 0 | `ScoreScaleMismatch` | Normalize score scale before comparing listwise ranks. |
| `automation-sample-001` | `AutomationMode` | `Unknown` | `const:automation-rules` | `const:automation-rules` | False | False | 0 | `MissingFeatureMetadata` | Align lifecycle and replacement metadata before weight tuning. |
| `automation-sample-002` | `AutomationMode` | `Unknown` | `const:automation-rules` | `const:automation-rules` | False | False | 0 | `MissingFeatureMetadata` | Align lifecycle and replacement metadata before weight tuning. |
| `automation-sample-003` | `AutomationMode` | `Unknown` | `const:automation-rules` | `const:automation-rules` | False | False | 0 | `MissingFeatureMetadata` | Align lifecycle and replacement metadata before weight tuning. |
| `automation-sample-007` | `AutomationMode` | `Unknown` | `const:automation-rules` | `const:automation-rules` | False | False | 0 | `MissingFeatureMetadata` | Align lifecycle and replacement metadata before weight tuning. |
| `automation-sample-009` | `AutomationMode` | `Unknown` | `const:automation-rules` | `const:automation-rules` | False | False | 0 | `ScoreScaleMismatch` | Normalize score scale before comparing listwise ranks. |
| `coding-sample-002` | `CodingMode` | `Unknown` | `const:coding-conventions` | `const:coding-conventions` | False | False | 0 | `MissingFeatureMetadata` | Align lifecycle and replacement metadata before weight tuning. |
| `coding-sample-007` | `CodingMode` | `Unknown` | `const:coding-conventions` | `const:coding-conventions` | False | False | 0 | `MissingFeatureMetadata` | Align lifecycle and replacement metadata before weight tuning. |
| `coding-sample-009` | `CodingMode` | `Unknown` | `const:coding-conventions` | `const:coding-conventions` | False | False | 0 | `ScoreScaleMismatch` | Normalize score scale before comparing listwise ranks. |

## Extended

- Samples: `113`
- RegressionCount: `30`
- ScoreContractStatus: `Passed`
- RankableCandidateCount: `1947`
- BlockedCandidateCount: `307`
- RiskCandidateInShadowTopK: `0`
- RecommendedNextAction: `Backfill lifecycle metadata and rerun offline audit.`

### Regression Reasons
- `MissingFeatureMetadata`: `25`
- `ScoreScaleMismatch`: `5`

| Sample | Mode | Intent | FormalTop1 | ShadowTop1 | FormalHit | ShadowHit | RiskTopK | Reason | Action |
|---|---|---|---|---|---:|---:|---:|---|---|
| `chat-sample-001` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | False | False | 0 | `MissingFeatureMetadata` | Align lifecycle and replacement metadata before weight tuning. |
| `chat-sample-003` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | False | False | 0 | `MissingFeatureMetadata` | Align lifecycle and replacement metadata before weight tuning. |
| `chat-sample-004` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | False | False | 0 | `MissingFeatureMetadata` | Align lifecycle and replacement metadata before weight tuning. |
| `chat-sample-009` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | False | False | 0 | `ScoreScaleMismatch` | Normalize score scale before comparing listwise ranks. |
| `chat-sample-010` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | False | False | 0 | `MissingFeatureMetadata` | Align lifecycle and replacement metadata before weight tuning. |
| `chat-20260529-001` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | False | False | 0 | `MissingFeatureMetadata` | Align lifecycle and replacement metadata before weight tuning. |
| `chat-20260529-002` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | False | False | 0 | `MissingFeatureMetadata` | Align lifecycle and replacement metadata before weight tuning. |
| `chat-20260529-003` | `ChatMode` | `Unknown` | `constraint:gap:constraint-gap-chat-20260529-003-no-promote-repetition` | `constraint:gap:constraint-gap-chat-20260529-003-no-promote-repetition` | False | False | 0 | `MissingFeatureMetadata` | Align lifecycle and replacement metadata before weight tuning. |
| `project-sample-001` | `ProjectMode` | `Unknown` | `const:project-security` | `const:project-security` | False | False | 0 | `MissingFeatureMetadata` | Align lifecycle and replacement metadata before weight tuning. |
| `project-sample-002` | `ProjectMode` | `Unknown` | `const:project-security` | `const:project-security` | False | False | 0 | `MissingFeatureMetadata` | Align lifecycle and replacement metadata before weight tuning. |
| `project-sample-003` | `ProjectMode` | `Unknown` | `const:project-security` | `const:project-security` | False | False | 0 | `MissingFeatureMetadata` | Align lifecycle and replacement metadata before weight tuning. |
| `project-sample-007` | `ProjectMode` | `Unknown` | `const:project-security` | `const:project-security` | False | False | 0 | `MissingFeatureMetadata` | Align lifecycle and replacement metadata before weight tuning. |
| `project-sample-009` | `ProjectMode` | `Unknown` | `const:project-security` | `const:project-security` | False | False | 0 | `ScoreScaleMismatch` | Normalize score scale before comparing listwise ranks. |
| `project-20260529-003` | `ProjectMode` | `Unknown` | `const:project-security` | `const:project-security` | False | False | 0 | `MissingFeatureMetadata` | Align lifecycle and replacement metadata before weight tuning. |
| `novel-sample-002` | `NovelMode` | `Unknown` | `const:novel-rules` | `const:novel-rules` | False | False | 0 | `MissingFeatureMetadata` | Align lifecycle and replacement metadata before weight tuning. |
| `novel-sample-007` | `NovelMode` | `Unknown` | `const:novel-rules` | `const:novel-rules` | False | False | 0 | `MissingFeatureMetadata` | Align lifecycle and replacement metadata before weight tuning. |
| `novel-sample-009` | `NovelMode` | `Unknown` | `const:novel-rules` | `const:novel-rules` | False | False | 0 | `ScoreScaleMismatch` | Normalize score scale before comparing listwise ranks. |
| `novel-sample-010` | `NovelMode` | `Unknown` | `const:novel-rules` | `const:novel-rules` | False | False | 0 | `MissingFeatureMetadata` | Align lifecycle and replacement metadata before weight tuning. |
| `automation-sample-001` | `AutomationMode` | `Unknown` | `const:automation-rules` | `const:automation-rules` | False | False | 0 | `MissingFeatureMetadata` | Align lifecycle and replacement metadata before weight tuning. |
| `automation-sample-002` | `AutomationMode` | `Unknown` | `const:automation-rules` | `const:automation-rules` | False | False | 0 | `MissingFeatureMetadata` | Align lifecycle and replacement metadata before weight tuning. |
| `automation-sample-003` | `AutomationMode` | `Unknown` | `const:automation-rules` | `const:automation-rules` | False | False | 0 | `MissingFeatureMetadata` | Align lifecycle and replacement metadata before weight tuning. |
| `automation-sample-007` | `AutomationMode` | `Unknown` | `const:automation-rules` | `const:automation-rules` | False | False | 0 | `MissingFeatureMetadata` | Align lifecycle and replacement metadata before weight tuning. |
| `automation-sample-009` | `AutomationMode` | `Unknown` | `const:automation-rules` | `const:automation-rules` | False | False | 0 | `ScoreScaleMismatch` | Normalize score scale before comparing listwise ranks. |
| `automation-20260529-001` | `AutomationMode` | `Unknown` | `const:automation-rules` | `const:automation-rules` | False | False | 0 | `MissingFeatureMetadata` | Align lifecycle and replacement metadata before weight tuning. |
| `automation-20260529-002` | `AutomationMode` | `Unknown` | `const:automation-rules` | `const:automation-rules` | False | False | 0 | `MissingFeatureMetadata` | Align lifecycle and replacement metadata before weight tuning. |
| `coding-sample-002` | `CodingMode` | `Unknown` | `const:coding-conventions` | `const:coding-conventions` | False | False | 0 | `MissingFeatureMetadata` | Align lifecycle and replacement metadata before weight tuning. |
| `coding-sample-007` | `CodingMode` | `Unknown` | `const:coding-conventions` | `const:coding-conventions` | False | False | 0 | `MissingFeatureMetadata` | Align lifecycle and replacement metadata before weight tuning. |
| `coding-sample-009` | `CodingMode` | `Unknown` | `const:coding-conventions` | `const:coding-conventions` | False | False | 0 | `ScoreScaleMismatch` | Normalize score scale before comparing listwise ranks. |
| `coding-20260529-007` | `CodingMode` | `Unknown` | `const:coding-conventions` | `const:coding-conventions` | False | False | 0 | `MissingFeatureMetadata` | Align lifecycle and replacement metadata before weight tuning. |
| `coding-20260529-010` | `CodingMode` | `Unknown` | `const:coding-conventions` | `const:coding-conventions` | False | False | 0 | `MissingFeatureMetadata` | Align lifecycle and replacement metadata before weight tuning. |
