# Formal Adapter Input Contract

Generated: `2026-06-19T17:05:48.2033258+00:00`
OperationId: `formal-adapter-input-contract-a79bdf066d7a4937a9d8743d496dbae1`

## Summary
- ContractPassed: `True`
- GatePassed: `False`
- Recommendation: `ReadyForFormalAdapterInputContractFreeze`
- ContractVersion: `formal-adapter-input-contract-v1`
- AllowedMode: `ContractOnly`
- RequiredNextPhase: `FormalAdapterImplementationPreflight`
- RuntimeInputTypeCount: `5`
- RuntimeInputFieldCount: `34`
- DeniedFieldCount: `25`
- ContractForbiddenPropertyCount: `0`
- FormalSourceForbiddenReadCount: `0`
- EvalOnlyForbiddenReadCount: `194`
- DatasetEvalFieldsBlocked: `True`
- GoldLabelsBlocked: `True`
- SampleMetadataBlocked: `True`
- ShadowArtifactFieldsBlocked: `True`
- CurrentShadowAdapterEvalOnly: `True`
- FormalRetrievalAllowed: `False`
- RuntimeSwitchAllowed: `False`
- ReadyForRuntimeSwitch: `False`
- UseForRuntime: `False`
- FormalPackageWritten: `False`
- PackageOutputChanged: `False`
- PackingPolicyChanged: `False`
- RuntimeMutated: `False`
- VectorStoreBindingChanged: `False`

## Runtime Input Types
- `FormalAdapterRuntimeInputEnvelope`
- `FormalAdapterRuntimePackageContext`
- `FormalAdapterRuntimeCandidateInput`
- `FormalAdapterRuntimeProvenanceInput`
- `FormalAdapterRuntimeRelationEvidenceInput`

## Allowed Runtime Inputs
- `request.requestId` (Request) source=`formal request envelope` usage=`trace correlation only` required=`True`
- `request.workspaceId` (Scope) source=`runtime request scope` usage=`allowlist and provider scope isolation` required=`True`
- `request.collectionId` (Scope) source=`runtime request scope` usage=`allowlist and provider scope isolation` required=`True`
- `request.queryText` (Query) source=`runtime user query` usage=`query tokenization and candidate scoring` required=`True`
- `request.queryAnchors` (Query) source=`runtime-derived anchors` usage=`optional source-aware scoring signals` required=`False`
- `package.baselinePackageId` (PackageContext) source=`current package snapshot` usage=`shadow comparison identity only` required=`False`
- `package.baselineCandidateIds` (PackageContext) source=`current formal selected set snapshot` usage=`selected-set preserving comparison` required=`False`
- `package.sectionTokenBudgets` (PackageContext) source=`runtime package constraints` usage=`token budget shadow validation` required=`True`
- `package.sectionOccupancy` (PackageContext) source=`runtime package snapshot` usage=`section occupancy comparison` required=`False`
- `package.totalTokenBudget` (PackageContext) source=`runtime package constraints` usage=`budget guard` required=`True`
- `candidate.candidateId` (Candidate) source=`candidate provider output` usage=`identity and stable tie-break` required=`True`
- `candidate.itemId` (Candidate) source=`candidate provider output` usage=`identity and stable tie-break only; no business special case` required=`True`
- `candidate.sourceId` (Candidate) source=`source metadata` usage=`source trace and dedupe` required=`False`
- `candidate.content` (Candidate) source=`runtime item content` usage=`dense/lexical scoring` required=`True`
- `candidate.itemKind` (CandidateMetadata) source=`runtime item metadata` usage=`generic source-aware scoring and filtering` required=`True`
- `candidate.sourceKind` (CandidateMetadata) source=`runtime source metadata` usage=`generic source-aware scoring and filtering` required=`True`
- `candidate.layer` (CandidateMetadata) source=`runtime item metadata` usage=`eligibility and routing gate` required=`False`
- `candidate.lifecycle` (Eligibility) source=`runtime lifecycle metadata` usage=`lifecycle gate` required=`True`
- `candidate.reviewStatus` (Eligibility) source=`runtime review metadata` usage=`eligibility gate` required=`True`
- `candidate.replacementState` (Eligibility) source=`runtime replacement metadata` usage=`superseded guard` required=`True`
- `candidate.targetSection` (Routing) source=`runtime item metadata` usage=`section routing and risk gate` required=`True`
- `candidate.tags` (CandidateMetadata) source=`runtime item metadata` usage=`generic anchor/source scoring` required=`False`
- `candidate.anchors` (CandidateMetadata) source=`runtime item metadata` usage=`generic anchor/source scoring` required=`False`
- `candidate.sourceRefs` (Evidence) source=`runtime item metadata` usage=`source evidence projection` required=`False`
- `candidate.evidenceRefs` (Evidence) source=`runtime item metadata` usage=`evidence projection` required=`False`
- `candidate.provenance.recordId` (Provenance) source=`runtime provenance metadata` usage=`trace and audit` required=`False`
- `candidate.provenance.sourceFingerprint` (Provenance) source=`runtime provenance metadata` usage=`source identity and trace` required=`False`
- `candidate.provenance.ingestionBatchId` (Provenance) source=`runtime provenance metadata` usage=`trace and audit` required=`False`
- `candidate.relations` (RelationEvidence) source=`read-only relation evidence` usage=`graph expansion and confidence gate` required=`False`
- `candidate.estimatedTokens` (PackageContext) source=`runtime package estimator` usage=`shadow token budget validation` required=`False`
- `candidate.score` (Candidate) source=`candidate provider output` usage=`pre-fusion score only` required=`False`
- `candidate.denseRank` (Candidate) source=`candidate provider output` usage=`dense winner preservation` required=`False`
- `candidate.lexicalRank` (Candidate) source=`candidate provider output` usage=`source contribution diagnostics` required=`False`
- `candidate.anchorRank` (Candidate) source=`candidate provider output` usage=`source contribution diagnostics` required=`False`

## Denied Inputs
- `RetrievalDatasetV2Sample` (DatasetEvalField) reason=`formal adapter must not accept eval sample DTOs`
- `RetrievalDatasetV2GeneratedDataset` (DatasetEvalField) reason=`formal adapter must not accept generated dataset containers`
- `SampleId` (SampleMetadata) reason=`sample identity is an eval artifact, not runtime input`
- `SourceEvalSet` (SampleMetadata) reason=`eval set identity must not affect runtime retrieval`
- `Split` (SampleMetadata) reason=`train/dev/test/holdout split is eval-only`
- `Difficulty` (SampleMetadata) reason=`difficulty labels are eval-only`
- `TaskKind` (SampleMetadata) reason=`task kind labels from generated samples are eval-only`
- `Intent` (SampleMetadata) reason=`sample intent labels are eval-only unless produced by runtime router contract`
- `Rationale` (SampleMetadata) reason=`rationale must not enter indexed/runtime scoring text`
- `MustHitItemIds` (GoldLabel) reason=`must-hit labels are gold labels`
- `MustNotHitItemIds` (GoldLabel) reason=`must-not labels are gold labels`
- `NegativeDistractorIds` (GoldLabel) reason=`negative distractor labels are gold labels`
- `ExpectedTargetSection` (GoldLabel) reason=`expected section is an eval label`
- `RequiredRelations` (EvalAnnotation) reason=`required relations from sample labels are eval-only`
- `sample.SourceRefs` (EvalAnnotation) reason=`source refs from samples are eval annotations; use item/source metadata`
- `sample.EvidenceRefs` (EvalAnnotation) reason=`evidence refs from samples are eval annotations; use item/source metadata`
- `sample.Metadata` (SampleMetadata) reason=`free-form sample metadata must not be runtime adapter input`
- `ShadowFormalRetrievalAdapterReport` (ShadowArtifact) reason=`shadow reports are eval artifacts`
- `FormalAdapterPackageShadowComparisonReport` (ShadowArtifact) reason=`package shadow reports are eval artifacts`
- `OutputTokenPriorityShadowGateReport` (ShadowArtifact) reason=`output-token shadow reports are eval artifacts`
- `SourceAwareRankingRepairReport` (ShadowArtifact) reason=`source-aware repair reports are eval artifacts`
- `RetrievalEvalProtocolGateReport` (ShadowArtifact) reason=`eval protocol reports are not runtime input`
- `BlindHoldout` (ShadowArtifact) reason=`blind holdout artifacts are eval-only`
- `GatePassed` (ShadowArtifact) reason=`gate result fields must not drive runtime scoring`
- `Recommendation` (ShadowArtifact) reason=`recommendation fields must not drive runtime scoring`

## Source Scan
- ScanPerformed: `True`
- FormalSourceFileCount: `0`
- EvalOnlySourceFileCount: `3`
- FormalSourceForbiddenReadCount: `0`
- EvalOnlyForbiddenReadCount: `194`
- `src/ContextCore.Core/Services/Vector/FormalAdapterPackageShadowComparisonRunner.cs` token=`FormalAdapterPackageShadowComparisonReport` category=`ShadowArtifact` formal=`False`
- `src/ContextCore.Core/Services/Vector/FormalAdapterPackageShadowComparisonRunner.cs` token=`FormalAdapterPackageShadowComparisonReport` category=`ShadowArtifact` formal=`False`
- `src/ContextCore.Core/Services/Vector/FormalAdapterPackageShadowComparisonRunner.cs` token=`FormalAdapterPackageShadowComparisonReport` category=`ShadowArtifact` formal=`False`
- `src/ContextCore.Core/Services/Vector/FormalAdapterPackageShadowComparisonRunner.cs` token=`FormalAdapterPackageShadowComparisonReport` category=`ShadowArtifact` formal=`False`
- `src/ContextCore.Core/Services/Vector/FormalAdapterPackageShadowComparisonRunner.cs` token=`FormalAdapterPackageShadowComparisonReport` category=`ShadowArtifact` formal=`False`
- `src/ContextCore.Core/Services/Vector/FormalAdapterPackageShadowComparisonRunner.cs` token=`GatePassed` category=`ShadowArtifact` formal=`False`
- `src/ContextCore.Core/Services/Vector/FormalAdapterPackageShadowComparisonRunner.cs` token=`GatePassed` category=`ShadowArtifact` formal=`False`
- `src/ContextCore.Core/Services/Vector/FormalAdapterPackageShadowComparisonRunner.cs` token=`GatePassed` category=`ShadowArtifact` formal=`False`
- `src/ContextCore.Core/Services/Vector/FormalAdapterPackageShadowComparisonRunner.cs` token=`GatePassed` category=`ShadowArtifact` formal=`False`
- `src/ContextCore.Core/Services/Vector/FormalAdapterPackageShadowComparisonRunner.cs` token=`GatePassed` category=`ShadowArtifact` formal=`False`
- `src/ContextCore.Core/Services/Vector/FormalAdapterPackageShadowComparisonRunner.cs` token=`GatePassed` category=`ShadowArtifact` formal=`False`
- `src/ContextCore.Core/Services/Vector/FormalAdapterPackageShadowComparisonRunner.cs` token=`Recommendation` category=`ShadowArtifact` formal=`False`
- `src/ContextCore.Core/Services/Vector/FormalAdapterPackageShadowComparisonRunner.cs` token=`Recommendation` category=`ShadowArtifact` formal=`False`
- `src/ContextCore.Core/Services/Vector/FormalAdapterPackageShadowComparisonRunner.cs` token=`Recommendation` category=`ShadowArtifact` formal=`False`
- `src/ContextCore.Core/Services/Vector/FormalAdapterPackageShadowComparisonRunner.cs` token=`Recommendation` category=`ShadowArtifact` formal=`False`
- `src/ContextCore.Core/Services/Vector/FormalAdapterPackageShadowComparisonRunner.cs` token=`Recommendation` category=`ShadowArtifact` formal=`False`
- `src/ContextCore.Core/Services/Vector/FormalAdapterPackageShadowComparisonRunner.cs` token=`Recommendation` category=`ShadowArtifact` formal=`False`
- `src/ContextCore.Core/Services/Vector/FormalAdapterPackageShadowComparisonRunner.cs` token=`Recommendation` category=`ShadowArtifact` formal=`False`
- `src/ContextCore.Core/Services/Vector/FormalAdapterPackageShadowComparisonRunner.cs` token=`Recommendation` category=`ShadowArtifact` formal=`False`
- `src/ContextCore.Core/Services/Vector/FormalAdapterPackageShadowComparisonRunner.cs` token=`Recommendation` category=`ShadowArtifact` formal=`False`
- `src/ContextCore.Core/Services/Vector/FormalAdapterPackageShadowComparisonRunner.cs` token=`Recommendation` category=`ShadowArtifact` formal=`False`
- `src/ContextCore.Core/Services/Vector/FormalAdapterPackageShadowComparisonRunner.cs` token=`Recommendation` category=`ShadowArtifact` formal=`False`
- `src/ContextCore.Core/Services/Vector/FormalAdapterPackageShadowComparisonRunner.cs` token=`Recommendation` category=`ShadowArtifact` formal=`False`
- `src/ContextCore.Core/Services/Vector/FormalAdapterPackageShadowComparisonRunner.cs` token=`Recommendation` category=`ShadowArtifact` formal=`False`
- `src/ContextCore.Core/Services/Vector/FormalAdapterPackageShadowComparisonRunner.cs` token=`Recommendation` category=`ShadowArtifact` formal=`False`
- `src/ContextCore.Core/Services/Vector/FormalAdapterPackageShadowComparisonRunner.cs` token=`Recommendation` category=`ShadowArtifact` formal=`False`
- `src/ContextCore.Core/Services/Vector/FormalAdapterPackageShadowComparisonRunner.cs` token=`Recommendation` category=`ShadowArtifact` formal=`False`
- `src/ContextCore.Core/Services/Vector/FormalAdapterPackageShadowComparisonRunner.cs` token=`Recommendation` category=`ShadowArtifact` formal=`False`
- `src/ContextCore.Core/Services/Vector/FormalAdapterPackageShadowComparisonRunner.cs` token=`Recommendation` category=`ShadowArtifact` formal=`False`
- `src/ContextCore.Core/Services/Vector/FormalAdapterPackageShadowComparisonRunner.cs` token=`Recommendation` category=`ShadowArtifact` formal=`False`
- `src/ContextCore.Core/Services/Vector/FormalAdapterPackageShadowComparisonRunner.cs` token=`Recommendation` category=`ShadowArtifact` formal=`False`
- `src/ContextCore.Core/Services/Vector/FormalAdapterPackageShadowComparisonRunner.cs` token=`RetrievalDatasetV2GeneratedDataset` category=`DatasetEvalField` formal=`False`
- `src/ContextCore.Core/Services/Vector/FormalAdapterPackageShadowComparisonRunner.cs` token=`RetrievalDatasetV2GeneratedDataset` category=`DatasetEvalField` formal=`False`
- `src/ContextCore.Core/Services/Vector/FormalAdapterPackageShadowComparisonRunner.cs` token=`RetrievalDatasetV2GeneratedDataset` category=`DatasetEvalField` formal=`False`
- `src/ContextCore.Core/Services/Vector/FormalAdapterPackageShadowComparisonRunner.cs` token=`RetrievalDatasetV2Sample` category=`DatasetEvalField` formal=`False`
- `src/ContextCore.Core/Services/Vector/FormalAdapterPackageShadowComparisonRunner.cs` token=`RetrievalDatasetV2Sample` category=`DatasetEvalField` formal=`False`
- `src/ContextCore.Core/Services/Vector/FormalAdapterPackageShadowComparisonRunner.cs` token=`RetrievalDatasetV2Sample` category=`DatasetEvalField` formal=`False`
- `src/ContextCore.Core/Services/Vector/FormalAdapterPackageShadowComparisonRunner.cs` token=`RetrievalDatasetV2Sample` category=`DatasetEvalField` formal=`False`
- `src/ContextCore.Core/Services/Vector/FormalAdapterPackageShadowComparisonRunner.cs` token=`RetrievalDatasetV2Sample` category=`DatasetEvalField` formal=`False`
- `src/ContextCore.Core/Services/Vector/FormalAdapterPackageShadowComparisonRunner.cs` token=`RetrievalDatasetV2Sample` category=`DatasetEvalField` formal=`False`

## Blocked Reasons
- (empty)

## Source Reports
- runtimeChangeGate: `learning\readiness\learning-runtime-change-readiness-gate.json`
- v515OutputTokenPriorityShadowGate: `vector\v5\output-token-priority-shadow-gate.json`
- v51ShadowFormalRetrievalAdapterPlanGate: `vector\v5\shadow-formal-retrieval-adapter-plan-gate.json`

This is a contract/enforcement artifact only. Existing Dataset V2 and shadow reports may remain in eval runners, but they are not allowed as future formal adapter runtime inputs.
