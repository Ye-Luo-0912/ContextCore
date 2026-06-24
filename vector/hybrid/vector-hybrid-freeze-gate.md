# Hybrid Retrieval Preview Freeze

Generated: 2026-06-15T07:35:11.6938068+00:00
- FreezePassed: `True`
- HybridRetrievalStatus: `KeepPreviewOnly`
- Recommendation: `BlockedByA3Recall`
- LegacyDenseRecallA3: `4.55%`
- HybridDenseOnlyRecallA3: `4.55%`
- HybridBestRecallA3: `4.55%`
- LegacyDenseRecallExtended: `3.12%`
- HybridDenseOnlyRecallExtended: `3.12%`
- HybridBestRecallExtended: `3.12%`
- DenseCandidateDroppedCount: `0`
- EligibilityMismatchCount: `0`
- DedupOverwriteCount: `0`
- RiskAfterPolicy: `0`
- FormalOutputChanged: `0`
- FormalRetrievalAllowed: `False`
- UseForRuntime: `False`
- V4RecheckAllowed: `False`

## RequiredBeforeV4
- `RecallImprovementSourceRequired`
- `A3AndExtendedRecallAtLeast80Percent`

## Notes
- `Current hybrid framework valid but ineffective for recall.`
- `Dense-only recall aligns with legacy dense baseline.`
- `Lexical / anchor union did not improve recall.`
- `Formal retrieval remains disabled.`

## BlockedReasons
- `A3RecallBelow80Percent`
- `ExtendedRecallBelow80Percent`
