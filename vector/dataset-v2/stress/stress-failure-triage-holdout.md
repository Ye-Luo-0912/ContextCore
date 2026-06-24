# Retrieval Dataset V2 stress-failure-triage-holdout

- DatasetId: `rdsv2-stress-a9f2c86e8d1df488`
- SampleCount: `24`
- FailureCount: `9`
- HoldoutFailureCount: `9`
- DenseOnlyWinCount: `0`
- HybridWinCount: `0`
- AnchorRegressionCount: `1`
- MustHitBelowTopKCount: `8`
- MustHitMissingFromCandidateSetCount: `0`
- EligibilityBlockedCount: `0`
- UseForRuntime: `False`
- FormalRetrievalAllowed: `False`
- Recommendation: `NeedsHybridUnionScoringRepair`

## Failure By Split
| Key | Count |
|---|---:|
| holdout | 9 |

## Failure By Difficulty
| Key | Count |
|---|---:|
| ambiguous_target_section | 3 |
| cross_domain_distractor | 3 |
| near_duplicate_distractor | 3 |

## Failure By Reason
| Key | Count |
|---|---:|
| AnchorRankingRegression | 1 |
| MustHitBelowTopK | 8 |

## Profile Comparisons
| Comparison | Left | Right | LeftRecall | RightRecall | LeftOnlyWins | RightOnlyWins | BothMiss |
|---|---|---|---:|---:|---:|---:|---:|
| dense-only-vs-hybrid-full | dense-only | hybrid-full | 62.50% | 62.50% | 0 | 0 | 9 |
| dense-only-vs-hybrid-with-anchor-shuffle | dense-only | hybrid-with-anchor-shuffle | 62.50% | 62.50% | 0 | 0 | 9 |
| dense-only-vs-hybrid-with-metadata-anchor-removed | dense-only | hybrid-with-metadata-anchor-removed | 62.50% | 62.50% | 0 | 0 | 9 |
| anchor-only-vs-hybrid-full | anchor-only | hybrid-full | 37.50% | 62.50% | 1 | 7 | 8 |

## Failure Details
| Sample | Split | Difficulty | Reason | MustHitRank | NearestWrong | Repair |
|---|---|---|---|---:|---|---|
| rdsv2-stress-holdout-sample-0097 | holdout | ambiguous_target_section | MustHitBelowTopK | 10 | rdsv2-stress-dev-item-0007 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-holdout-sample-0107 | holdout | ambiguous_target_section | MustHitBelowTopK | 11 | rdsv2-stress-dev-item-0007 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-holdout-sample-0117 | holdout | ambiguous_target_section | MustHitBelowTopK | 12 | rdsv2-stress-dev-item-0007 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-holdout-sample-0099 | holdout | cross_domain_distractor | MustHitBelowTopK | 24 | rdsv2-stress-dev-item-0007 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-holdout-sample-0109 | holdout | cross_domain_distractor | MustHitBelowTopK | 25 | rdsv2-stress-dev-item-0007 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-holdout-sample-0119 | holdout | cross_domain_distractor | AnchorRankingRegression | 26 | rdsv2-stress-holdout-item-0113 | Inspect hybrid anchor weighting and avoid anchor score suppressing dense winners. |
| rdsv2-stress-holdout-sample-0098 | holdout | near_duplicate_distractor | MustHitBelowTopK | 22 | rdsv2-stress-holdout-item-0099 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-holdout-sample-0108 | holdout | near_duplicate_distractor | MustHitBelowTopK | 23 | rdsv2-stress-holdout-item-0099 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-holdout-sample-0118 | holdout | near_duplicate_distractor | MustHitBelowTopK | 24 | rdsv2-stress-holdout-item-0099 | Tune ranking so present mustHit candidates are promoted into topK. |
