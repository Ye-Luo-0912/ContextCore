# Retrieval Dataset V2 stress-failure-clusters

- DatasetId: `rdsv2-stress-a9f2c86e8d1df488`
- SampleCount: `120`
- FailureCount: `68`
- HoldoutFailureCount: `9`
- DenseOnlyWinCount: `5`
- HybridWinCount: `0`
- AnchorRegressionCount: `1`
- MustHitBelowTopKCount: `59`
- MustHitMissingFromCandidateSetCount: `0`
- EligibilityBlockedCount: `0`
- UseForRuntime: `False`
- FormalRetrievalAllowed: `False`
- Recommendation: `NeedsHybridUnionScoringRepair`

## Failure By Split
| Key | Count |
|---|---:|
| dev | 13 |
| holdout | 9 |
| test | 16 |
| train | 30 |

## Failure By Difficulty
| Key | Count |
|---|---:|
| ambiguous_target_section | 7 |
| cross_domain_distractor | 12 |
| direct_lexical | 6 |
| metadata_anchor | 6 |
| must_not_negative_constraint | 6 |
| near_duplicate_distractor | 12 |
| paraphrase_semantic | 6 |
| query_with_sparse_tokens | 7 |
| relation_multi_hop | 6 |

## Failure By Reason
| Key | Count |
|---|---:|
| AnchorRankingRegression | 1 |
| HybridUnionRankingRegression | 5 |
| MustHitBelowTopK | 59 |
| NegativeDistractorOutranksMustHit | 3 |

## Profile Comparisons
| Comparison | Left | Right | LeftRecall | RightRecall | LeftOnlyWins | RightOnlyWins | BothMiss |
|---|---|---|---:|---:|---:|---:|---:|
| dense-only-vs-hybrid-full | dense-only | hybrid-full | 47.50% | 43.33% | 5 | 0 | 63 |
| dense-only-vs-hybrid-with-anchor-shuffle | dense-only | hybrid-with-anchor-shuffle | 47.50% | 47.50% | 0 | 0 | 63 |
| dense-only-vs-hybrid-with-metadata-anchor-removed | dense-only | hybrid-with-metadata-anchor-removed | 47.50% | 47.50% | 0 | 0 | 63 |
| anchor-only-vs-hybrid-full | anchor-only | hybrid-full | 27.50% | 43.33% | 1 | 20 | 67 |

## Failure Details
| Sample | Split | Difficulty | Reason | MustHitRank | NearestWrong | Repair |
|---|---|---|---|---:|---|---|
| rdsv2-stress-dev-sample-0017 | dev | ambiguous_target_section | NegativeDistractorOutranksMustHit | 9 | rdsv2-stress-dev-item-0007 | Strengthen must-not negative policy in preview scoring. |
| rdsv2-stress-dev-sample-0047 | dev | ambiguous_target_section | MustHitBelowTopK | 6 | rdsv2-stress-dev-item-0007 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-dev-sample-0067 | dev | ambiguous_target_section | MustHitBelowTopK | 7 | rdsv2-stress-dev-item-0007 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-dev-sample-0087 | dev | ambiguous_target_section | NegativeDistractorOutranksMustHit | 8 | rdsv2-stress-dev-item-0007 | Strengthen must-not negative policy in preview scoring. |
| rdsv2-stress-dev-sample-0008 | dev | near_duplicate_distractor | HybridUnionRankingRegression | 16 | rdsv2-stress-holdout-item-0099 | Preview hybrid union scoring repair so dense wins are preserved in topK. |
| rdsv2-stress-dev-sample-0018 | dev | near_duplicate_distractor | MustHitBelowTopK | 20 | rdsv2-stress-holdout-item-0099 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-dev-sample-0028 | dev | near_duplicate_distractor | HybridUnionRankingRegression | 17 | rdsv2-stress-holdout-item-0099 | Preview hybrid union scoring repair so dense wins are preserved in topK. |
| rdsv2-stress-dev-sample-0038 | dev | near_duplicate_distractor | HybridUnionRankingRegression | 13 | rdsv2-stress-holdout-item-0099 | Preview hybrid union scoring repair so dense wins are preserved in topK. |
| rdsv2-stress-dev-sample-0048 | dev | near_duplicate_distractor | MustHitBelowTopK | 18 | rdsv2-stress-holdout-item-0099 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-dev-sample-0058 | dev | near_duplicate_distractor | HybridUnionRankingRegression | 14 | rdsv2-stress-holdout-item-0099 | Preview hybrid union scoring repair so dense wins are preserved in topK. |
| rdsv2-stress-dev-sample-0068 | dev | near_duplicate_distractor | MustHitBelowTopK | 19 | rdsv2-stress-holdout-item-0099 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-dev-sample-0078 | dev | near_duplicate_distractor | HybridUnionRankingRegression | 15 | rdsv2-stress-holdout-item-0099 | Preview hybrid union scoring repair so dense wins are preserved in topK. |
| rdsv2-stress-dev-sample-0088 | dev | near_duplicate_distractor | MustHitBelowTopK | 19 | rdsv2-stress-holdout-item-0099 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-holdout-sample-0097 | holdout | ambiguous_target_section | MustHitBelowTopK | 10 | rdsv2-stress-dev-item-0007 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-holdout-sample-0107 | holdout | ambiguous_target_section | MustHitBelowTopK | 11 | rdsv2-stress-dev-item-0007 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-holdout-sample-0117 | holdout | ambiguous_target_section | MustHitBelowTopK | 12 | rdsv2-stress-dev-item-0007 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-holdout-sample-0099 | holdout | cross_domain_distractor | MustHitBelowTopK | 24 | rdsv2-stress-dev-item-0007 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-holdout-sample-0109 | holdout | cross_domain_distractor | MustHitBelowTopK | 25 | rdsv2-stress-dev-item-0007 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-holdout-sample-0119 | holdout | cross_domain_distractor | AnchorRankingRegression | 26 | rdsv2-stress-holdout-item-0113 | Inspect hybrid anchor weighting and avoid anchor score suppressing dense winners. |
| rdsv2-stress-holdout-sample-0098 | holdout | near_duplicate_distractor | MustHitBelowTopK | 22 | rdsv2-stress-holdout-item-0099 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-holdout-sample-0108 | holdout | near_duplicate_distractor | MustHitBelowTopK | 23 | rdsv2-stress-holdout-item-0099 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-holdout-sample-0118 | holdout | near_duplicate_distractor | MustHitBelowTopK | 24 | rdsv2-stress-holdout-item-0099 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-test-sample-0009 | test | cross_domain_distractor | MustHitBelowTopK | 20 | rdsv2-stress-holdout-item-0104 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-test-sample-0019 | test | cross_domain_distractor | MustHitBelowTopK | 27 | rdsv2-stress-holdout-item-0100 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-test-sample-0029 | test | cross_domain_distractor | MustHitBelowTopK | 32 | rdsv2-stress-holdout-item-0100 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-test-sample-0039 | test | cross_domain_distractor | MustHitBelowTopK | 17 | rdsv2-stress-holdout-item-0104 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-test-sample-0049 | test | cross_domain_distractor | MustHitBelowTopK | 21 | rdsv2-stress-holdout-item-0104 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-test-sample-0059 | test | cross_domain_distractor | MustHitBelowTopK | 29 | rdsv2-stress-holdout-item-0100 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-test-sample-0069 | test | cross_domain_distractor | MustHitBelowTopK | 34 | rdsv2-stress-holdout-item-0100 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-test-sample-0079 | test | cross_domain_distractor | MustHitBelowTopK | 19 | rdsv2-stress-holdout-item-0104 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-test-sample-0089 | test | cross_domain_distractor | MustHitBelowTopK | 23 | rdsv2-stress-holdout-item-0104 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-test-sample-0010 | test | query_with_sparse_tokens | MustHitBelowTopK | 8 | rdsv2-stress-holdout-item-0100 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-test-sample-0030 | test | query_with_sparse_tokens | MustHitBelowTopK | 9 | rdsv2-stress-holdout-item-0100 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-test-sample-0050 | test | query_with_sparse_tokens | NegativeDistractorOutranksMustHit | 10 | rdsv2-stress-holdout-item-0100 | Strengthen must-not negative policy in preview scoring. |
| rdsv2-stress-test-sample-0060 | test | query_with_sparse_tokens | MustHitBelowTopK | 6 | rdsv2-stress-holdout-item-0100 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-test-sample-0070 | test | query_with_sparse_tokens | MustHitBelowTopK | 11 | rdsv2-stress-holdout-item-0100 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-test-sample-0080 | test | query_with_sparse_tokens | MustHitBelowTopK | 7 | rdsv2-stress-holdout-item-0100 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-test-sample-0090 | test | query_with_sparse_tokens | MustHitBelowTopK | 12 | rdsv2-stress-holdout-item-0100 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-train-sample-0021 | train | direct_lexical | MustHitBelowTopK | 6 | rdsv2-stress-holdout-item-0103 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-train-sample-0031 | train | direct_lexical | MustHitBelowTopK | 8 | rdsv2-stress-holdout-item-0101 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-train-sample-0041 | train | direct_lexical | MustHitBelowTopK | 9 | rdsv2-stress-holdout-item-0105 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-train-sample-0051 | train | direct_lexical | MustHitBelowTopK | 11 | rdsv2-stress-holdout-item-0103 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-train-sample-0081 | train | direct_lexical | MustHitBelowTopK | 6 | rdsv2-stress-holdout-item-0103 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-train-sample-0091 | train | direct_lexical | MustHitBelowTopK | 8 | rdsv2-stress-holdout-item-0101 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-train-sample-0023 | train | metadata_anchor | MustHitBelowTopK | 6 | rdsv2-stress-holdout-item-0105 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-train-sample-0033 | train | metadata_anchor | MustHitBelowTopK | 8 | rdsv2-stress-holdout-item-0103 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-train-sample-0043 | train | metadata_anchor | MustHitBelowTopK | 10 | rdsv2-stress-holdout-item-0101 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-train-sample-0053 | train | metadata_anchor | MustHitBelowTopK | 11 | rdsv2-stress-holdout-item-0105 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-train-sample-0083 | train | metadata_anchor | MustHitBelowTopK | 6 | rdsv2-stress-holdout-item-0105 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-train-sample-0093 | train | metadata_anchor | MustHitBelowTopK | 8 | rdsv2-stress-holdout-item-0103 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-train-sample-0026 | train | must_not_negative_constraint | MustHitBelowTopK | 7 | rdsv2-stress-holdout-item-0102 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-train-sample-0036 | train | must_not_negative_constraint | MustHitBelowTopK | 8 | rdsv2-stress-holdout-item-0106 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-train-sample-0046 | train | must_not_negative_constraint | MustHitBelowTopK | 10 | rdsv2-stress-holdout-item-0104 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-train-sample-0056 | train | must_not_negative_constraint | MustHitBelowTopK | 12 | rdsv2-stress-holdout-item-0102 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-train-sample-0086 | train | must_not_negative_constraint | MustHitBelowTopK | 7 | rdsv2-stress-holdout-item-0102 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-train-sample-0096 | train | must_not_negative_constraint | MustHitBelowTopK | 8 | rdsv2-stress-holdout-item-0106 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-train-sample-0022 | train | paraphrase_semantic | MustHitBelowTopK | 6 | rdsv2-stress-holdout-item-0104 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-train-sample-0032 | train | paraphrase_semantic | MustHitBelowTopK | 8 | rdsv2-stress-holdout-item-0102 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-train-sample-0042 | train | paraphrase_semantic | MustHitBelowTopK | 9 | rdsv2-stress-holdout-item-0106 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-train-sample-0052 | train | paraphrase_semantic | MustHitBelowTopK | 11 | rdsv2-stress-holdout-item-0104 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-train-sample-0082 | train | paraphrase_semantic | MustHitBelowTopK | 6 | rdsv2-stress-holdout-item-0104 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-train-sample-0092 | train | paraphrase_semantic | MustHitBelowTopK | 8 | rdsv2-stress-holdout-item-0102 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-train-sample-0024 | train | relation_multi_hop | MustHitBelowTopK | 6 | rdsv2-stress-holdout-item-0106 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-train-sample-0034 | train | relation_multi_hop | MustHitBelowTopK | 8 | rdsv2-stress-holdout-item-0104 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-train-sample-0044 | train | relation_multi_hop | MustHitBelowTopK | 10 | rdsv2-stress-holdout-item-0102 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-train-sample-0054 | train | relation_multi_hop | MustHitBelowTopK | 11 | rdsv2-stress-holdout-item-0106 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-train-sample-0084 | train | relation_multi_hop | MustHitBelowTopK | 6 | rdsv2-stress-holdout-item-0106 | Tune ranking so present mustHit candidates are promoted into topK. |
| rdsv2-stress-train-sample-0094 | train | relation_multi_hop | MustHitBelowTopK | 8 | rdsv2-stress-holdout-item-0104 | Tune ranking so present mustHit candidates are promoted into topK. |
