# Legacy Retrieval Dataset Limitation Report

- BatchId: `vlmrb-47f13317240164d7`
- ReviewCandidateCount: `32`
- MissingEvidenceSourceProvenanceCandidateCount: `32`
- EvidenceBackfillRecommendation: `NeedsIngestionMetadataBackfill`
- LegacyDatasetSuitableForPrimaryRecallRepair: `False`
- GeneratesFormalDataset: `False`
- FormalRetrievalAllowed: `False`
- UseForRuntime: `False`
- Recommendation: `NeedsIngestionMetadataBackfill`

## Limitations
- 32 lifecycle metadata review candidates lack evidence/source/provenance required by Dataset V2.
- Legacy eval corpus can explain recall loss but cannot safely justify lifecycle repair decisions.
- Recall repair should move to Dataset V2 ingestion metadata rather than manual repair of legacy labels.

## Required Next Data Work
- Backfill sourceRefs/evidenceRefs/provenance at ingestion time.
- Add lifecycle/reviewStatus/replacementState metadata to corpus items.
- Attach relation evidence for deprecation and replacement state.
- Validate split isolation and query label hygiene before using a dataset for recall repair.
