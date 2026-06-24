# Candidate Reranker Shadow Eval

Generated: 2026-06-12T06:57:34.5439917+00:00

## A3

- Samples: `50`
- ShadowProfile: `baseline-lifecycle-aware`
- CandidateCount: `466`
- FormalTop1Accuracy: `0.00 %`
- ShadowTop1Accuracy: `6.00 %`
- FormalMRR: `0.5`
- ShadowMRR: `0.3613`
- WouldImproveCount: `3`
- WouldRegressCount: `20`
- WouldApplyCount: `3`
- AbstainCount: `0`
- NetGainAfterAbstain: `3`
- FormalPriorityRecoveredCount: `0`
- UnexplainedRegressionCount: `0`
- RiskCount: `0`
- NetGain: `-17`
- ScoreContractStatus: `Passed`
- EligibilityGuardStatus: `Guarded`
- FeatureCompletenessRate: `88.02 %`
- MissingFeatureMetadataCount: `204`
- RiskCandidateInRawTopK: `116`
- RiskCandidateInShadowTopK: `0`
- RiskCandidateBlockedBeforeRerank: `152`
- Recommendation: `KeepFormalRanking`
- RegressionReasonSummary: `MissingFeatureMetadata=20`

| Sample | Mode | Intent | FormalTop | ShadowTop | FormalMRR | ShadowMRR | Apply | Abstain | Improve | Regress | Risk |
|---|---|---|---|---|---:|---:|---:|---:|---:|---:|---:|
| `chat-sample-001` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | 0.5 | 0.2 | False | False | False | True | 0 |
| `chat-sample-002` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | 0.5 | 0.5 | False | False | False | False | 0 |
| `chat-sample-003` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | 0.5 | 0.3333 | False | False | False | True | 0 |
| `chat-sample-004` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | 0.5 | 0.3333 | False | False | False | True | 0 |
| `chat-sample-005` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | 0.5 | 0.5 | False | False | False | False | 0 |
| `chat-sample-006` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | 0.5 | 0.5 | False | False | False | False | 0 |
| `chat-sample-007` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | 0.5 | 0.5 | False | False | False | False | 0 |
| `chat-sample-008` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | 0.5 | 0.5 | False | False | False | False | 0 |
| `chat-sample-009` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | 0.5 | 0 | False | False | False | True | 0 |
| `chat-sample-010` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | 0.5 | 0.3333 | False | False | False | True | 0 |
| `project-sample-001` | `ProjectMode` | `Unknown` | `const:project-security` | `const:project-security` | 0.5 | 0 | False | False | False | True | 0 |
| `project-sample-002` | `ProjectMode` | `Unknown` | `const:project-security` | `const:project-security` | 0.5 | 0 | False | False | False | True | 0 |
| `project-sample-003` | `ProjectMode` | `Unknown` | `const:project-security` | `const:project-security` | 0.5 | 0 | False | False | False | True | 0 |
| `project-sample-004` | `ProjectMode` | `Unknown` | `const:project-security` | `const:project-security` | 0.5 | 0.5 | False | False | False | False | 0 |
| `project-sample-005` | `ProjectMode` | `Unknown` | `const:project-security` | `const:project-security` | 0.5 | 0.5 | False | False | False | False | 0 |
| `project-sample-006` | `ProjectMode` | `Unknown` | `const:project-security` | `const:project-security` | 0.5 | 0.5 | False | False | False | False | 0 |
| `project-sample-007` | `ProjectMode` | `Unknown` | `const:project-security` | `const:project-security` | 0.5 | 0.5 | False | False | False | False | 0 |
| `project-sample-008` | `ProjectMode` | `Unknown` | `const:project-security` | `memory:project-pool-v2` | 0.5 | 1 | True | False | True | False | 0 |
| `project-sample-009` | `ProjectMode` | `Unknown` | `const:project-security` | `const:project-security` | 0.5 | 0 | False | False | False | True | 0 |
| `project-sample-010` | `ProjectMode` | `Unknown` | `const:project-security` | `const:project-security` | 0.5 | 0.5 | False | False | False | False | 0 |
| `novel-sample-001` | `NovelMode` | `Unknown` | `const:novel-rules` | `const:novel-rules` | 0.5 | 0.5 | False | False | False | False | 0 |
| `novel-sample-002` | `NovelMode` | `Unknown` | `const:novel-rules` | `const:novel-rules` | 0.5 | 0 | False | False | False | True | 0 |
| `novel-sample-003` | `NovelMode` | `Unknown` | `const:novel-rules` | `const:novel-rules` | 0.5 | 0.5 | False | False | False | False | 0 |
| `novel-sample-004` | `NovelMode` | `Unknown` | `const:novel-rules` | `const:novel-rules` | 0.5 | 0.5 | False | False | False | False | 0 |
| `novel-sample-005` | `NovelMode` | `Unknown` | `const:novel-rules` | `const:novel-rules` | 0.5 | 0.5 | False | False | False | False | 0 |
| `novel-sample-006` | `NovelMode` | `Unknown` | `const:novel-rules` | `const:novel-rules` | 0.5 | 0.5 | False | False | False | False | 0 |
| `novel-sample-007` | `NovelMode` | `Unknown` | `const:novel-rules` | `const:novel-rules` | 0.5 | 0 | False | False | False | True | 0 |
| `novel-sample-008` | `NovelMode` | `Unknown` | `const:novel-rules` | `const:novel-rules` | 0.5 | 0.5 | False | False | False | False | 0 |
| `novel-sample-009` | `NovelMode` | `Unknown` | `const:novel-rules` | `const:novel-rules` | 0.5 | 0 | False | False | False | True | 0 |
| `novel-sample-010` | `NovelMode` | `Unknown` | `const:novel-rules` | `const:novel-rules` | 0.5 | 0.5 | False | False | False | False | 0 |

## Extended

- Samples: `113`
- ShadowProfile: `baseline-lifecycle-aware`
- CandidateCount: `2627`
- FormalTop1Accuracy: `0.00 %`
- ShadowTop1Accuracy: `23.89 %`
- FormalMRR: `0.4985`
- ShadowMRR: `0.5161`
- WouldImproveCount: `27`
- WouldRegressCount: `30`
- WouldApplyCount: `27`
- AbstainCount: `0`
- NetGainAfterAbstain: `27`
- FormalPriorityRecoveredCount: `0`
- UnexplainedRegressionCount: `0`
- RiskCount: `0`
- NetGain: `-3`
- ScoreContractStatus: `Passed`
- EligibilityGuardStatus: `Guarded`
- FeatureCompletenessRate: `95.02 %`
- MissingFeatureMetadataCount: `658`
- RiskCandidateInRawTopK: `23`
- RiskCandidateInShadowTopK: `0`
- RiskCandidateBlockedBeforeRerank: `373`
- Recommendation: `KeepFormalRanking`
- RegressionReasonSummary: `MissingFeatureMetadata=30`

| Sample | Mode | Intent | FormalTop | ShadowTop | FormalMRR | ShadowMRR | Apply | Abstain | Improve | Regress | Risk |
|---|---|---|---|---|---:|---:|---:|---:|---:|---:|---:|
| `chat-sample-001` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | 0.5 | 0.1111 | False | False | False | True | 0 |
| `chat-sample-002` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | 0.5 | 0.5 | False | False | False | False | 0 |
| `chat-sample-003` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | 0.5 | 0.3333 | False | False | False | True | 0 |
| `chat-sample-004` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | 0.5 | 0.1429 | False | False | False | True | 0 |
| `chat-sample-005` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | 0.5 | 0.5 | False | False | False | False | 0 |
| `chat-sample-006` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | 0.5 | 0.5 | False | False | False | False | 0 |
| `chat-sample-007` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | 0.5 | 0.5 | False | False | False | False | 0 |
| `chat-sample-008` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | 0.5 | 0.5 | False | False | False | False | 0 |
| `chat-sample-009` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | 0.5 | 0 | False | False | False | True | 0 |
| `chat-sample-010` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | 0.5 | 0.3333 | False | False | False | True | 0 |
| `chat-20260529-001` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | 0.5 | 0 | False | False | False | True | 0 |
| `chat-20260529-002` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | 0.5 | 0.2 | False | False | False | True | 0 |
| `chat-20260529-003` | `ChatMode` | `Unknown` | `constraint:gap:constraint-gap-chat-20260529-003-no-promote-repetition` | `constraint:gap:constraint-gap-chat-20260529-003-no-promote-repetition` | 0.3333 | 0 | False | False | False | True | 0 |
| `chat-20260529-004` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `pref:answer-conclusion-first` | 0.5 | 1 | True | False | True | False | 0 |
| `chat-20260529-005` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | 0.5 | 0.5 | False | False | False | False | 0 |
| `chat-20260529-006` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `task:current-active` | 0.5 | 1 | True | False | True | False | 0 |
| `chat-20260529-007` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `pattern:repeated-user-preference` | 0.5 | 1 | True | False | True | False | 0 |
| `chat-20260529-008` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | 0.5 | 0.5 | False | False | False | False | 0 |
| `chat-20260529-009` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | 0.5 | 0.5 | False | False | False | False | 0 |
| `chat-20260529-010` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `constraint:no-secret-commit` | 0.5 | 1 | True | False | True | False | 0 |
| `chat-20260529-011` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `scope:current-session` | 0.5 | 1 | True | False | True | False | 0 |
| `chat-20260529-012` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | 0.5 | 0.5 | False | False | False | False | 0 |
| `chat-20260529-013` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | 0.5 | 0.5 | False | False | False | False | 0 |
| `chat-20260529-014` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | 0.5 | 0.5 | False | False | False | False | 0 |
| `chat-20260529-015` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | 0.5 | 0.5 | False | False | False | False | 0 |
| `chat-20260529-016` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `pref:actionable-concise` | 0.5 | 1 | True | False | True | False | 0 |
| `chat-20260529-017` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `pref:admit-uncertainty` | 0.5 | 1 | True | False | True | False | 0 |
| `chat-20260529-018` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `const:chat-output-lang` | 0.5 | 0.5 | False | False | False | False | 0 |
| `chat-20260529-019` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `task:resume-context` | 0.5 | 1 | True | False | True | False | 0 |
| `chat-20260529-020` | `ChatMode` | `Unknown` | `const:chat-output-lang` | `scope:project-only` | 0.5 | 1 | True | False | True | False | 0 |
