# Retrieval Dataset V2 Stress Readiness Gate

- DatasetId: `rdsv2-stress-a9f2c86e8d1df488`
- CorpusItemCount: `120`
- SampleCount: `120`
- ValidationIssueCount: `0`
- LeakageIssueCount: `0`
- UniqueAnchorLeakageCount: `0`
- ItemIdLeakageCount: `0`
- RationaleLeakageCount: `0`
- SplitLeakageCount: `0`
- AnchorDominanceScore: `0.0000`
- AnchorAblationRecallDelta: `0.00%`
- AnchorShuffleRecallDelta: `0.00%`
- DenseRecall: `47.50%`
- LexicalRecall: `47.50%`
- AnchorRecall: `27.50%`
- HybridRecall: `43.33%`
- HoldoutHybridRecall: `62.50%`
- RiskAfterPolicy: `0`
- MustNotHitRiskAfterPolicy: `0`
- LifecycleRiskAfterPolicy: `0`
- FormalOutputChanged: `0`
- UseForRuntime: `False`
- FormalRetrievalAllowed: `False`
- Recommendation: `BlockedByHoldoutRecall`
- BlockedReasons: `HoldoutHybridRecallBelowThreshold`

## Split Breakdown
| Key | Count |
|---|---:|
| dev | 18 |
| holdout | 24 |
| test | 18 |
| train | 60 |

## Difficulty Breakdown
| Key | Count |
|---|---:|
| ambiguous_target_section | 12 |
| cross_domain_distractor | 12 |
| direct_lexical | 12 |
| lifecycle_deprecated_trap | 12 |
| metadata_anchor | 12 |
| must_not_negative_constraint | 12 |
| near_duplicate_distractor | 12 |
| paraphrase_semantic | 12 |
| query_with_sparse_tokens | 12 |
| relation_multi_hop | 12 |

## Ablation Profiles
| Profile | Samples | Recall | MRR | Risk | MustNotRisk | LifecycleRisk | Candidates |
|---|---:|---:|---:|---:|---:|---:|---:|
| dense-only | 120 | 47.50% | 0.2101 | 4 | 4 | 0 | 600 |
| lexical-only | 120 | 47.50% | 0.2101 | 4 | 4 | 0 | 600 |
| anchor-only | 120 | 27.50% | 0.1208 | 3 | 3 | 0 | 525 |
| hybrid-full | 120 | 43.33% | 0.1911 | 4 | 4 | 0 | 600 |
| hybrid-without-unique-tags | 120 | 43.33% | 0.1911 | 4 | 4 | 0 | 600 |
| hybrid-with-anchor-shuffle | 120 | 47.50% | 0.2101 | 4 | 4 | 0 | 600 |
| hybrid-with-metadata-anchor-removed | 120 | 47.50% | 0.2101 | 4 | 4 | 0 | 600 |
| hybrid-on-holdout-only | 24 | 62.50% | 0.4722 | 0 | 0 | 0 | 120 |
