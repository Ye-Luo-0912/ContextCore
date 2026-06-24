# Learning Dataset Quality Report

Generated: 2026-06-06 08:04:27 +00:00
Feature directory: `$(repo-root)\learning\features`
Policy version: `learning-dataset-quality/v1`

## Summary

- Policy feedback features: `0`
- Ranking pairs: `253`
- Router intent examples: `163`
- Positive / Negative / Neutral: `0` / `0` / `0`

## Data Risks

- `NoPolicyFeedback`
- `EvalOnlyDataset`
- `MissingNegativeSamples`

## Source Type Counts

| Key | Count |
|---|---:|
| PlanningShadowComparison | 163 |
| RankingPair | 253 |

## Mode Counts

| Key | Count |
|---|---:|
| AutomationMode | 86 |
| ChatMode | 91 |
| CodingMode | 82 |
| NovelMode | 98 |
| ProjectMode | 59 |

## Intent Counts

| Key | Count |
|---|---:|
| AuditDeprecated | 41 |
| AutomationRecovery | 75 |
| CodingTask | 93 |
| ConflictCheck | 5 |
| CurrentTask | 15 |
| FuzzyQuestion | 113 |
| LongTermPreference | 3 |
| NovelGeneration | 71 |

## Label Counts

| Key | Count |
|---|---:|
| AuditDeprecated | 19 |
| AutomationRecovery | 25 |
| CodingTask | 35 |
| ConflictCheck | 2 |
| CurrentTask | 6 |
| FuzzyQuestion | 46 |
| LongTermPreference | 1 |
| NovelGeneration | 29 |

## Task Readiness

| Task | Status | Ready | Reasons | Next Action |
|---|---|---:|---|---|
| AttentionScorer | NotReady | no | attentionFeedbackExamples=0 | Collect explicit attention order quality feedback before scorer work. |
| CandidateReranker | Ready | yes | rankingPairs=253; modes=5 | Use for offline reranker analysis only; do not change retrieval scoring. |
| ConstraintGapJudge | NotReady | no | sourceExamples=0; positive=0; negative=0 | Collect accepted and rejected constraint gap / candidate constraint review examples. |
| PromotionJudge | NotReady | no | sourceExamples=0; positive=0; negative=0 | Collect accepted and rejected promotion review examples. |
| RouterIntentClassifier | Ready | yes | routerIntentExamples=163; intents=8; modes=5 | Use only for offline router-intent analysis; keep online router disabled. |

## Recommended Next Action

Collect and export human review feedback before PromotionJudge, ConstraintGapJudge, or AttentionScorer work.
