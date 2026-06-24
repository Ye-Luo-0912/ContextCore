# Retrieval Dataset V2 Hybrid Shadow Eval

| Profile | Samples | Corpus | Candidates | Recall | MRR | Risk | MustNotRisk | LifecycleRisk | Dense | Lexical | Anchor | Union | EligibilityBlocked | Recommendation |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| hybrid-dense-only | 21 | 28 | 102 | 80.95% | 0.6825 | 0 | 0 | 0 | 567 | 567 | 341 | 567 | 141 | ReadyForDatasetV2RetrievalCandidate |
| hybrid-dense-plus-lexical | 21 | 28 | 102 | 80.95% | 0.6825 | 0 | 0 | 0 | 567 | 567 | 341 | 567 | 141 | ReadyForDatasetV2RetrievalCandidate |
| hybrid-dense-plus-anchor | 21 | 28 | 102 | 100.00% | 1.0000 | 0 | 0 | 0 | 567 | 567 | 341 | 567 | 141 | ReadyForDatasetV2RetrievalCandidate |
| hybrid-dense-plus-lexical-anchor | 21 | 28 | 102 | 100.00% | 1.0000 | 0 | 0 | 0 | 567 | 567 | 341 | 567 | 141 | ReadyForDatasetV2RetrievalCandidate |
| lexical-only | 21 | 28 | 102 | 80.95% | 0.6825 | 0 | 0 | 0 | 567 | 567 | 341 | 567 | 141 | ReadyForDatasetV2RetrievalCandidate |
| anchor-only | 21 | 28 | 74 | 100.00% | 0.8373 | 0 | 0 | 0 | 567 | 567 | 341 | 341 | 105 | ReadyForDatasetV2RetrievalCandidate |

- UseForRuntime: `false`
- FormalRetrievalAllowed: `false`
