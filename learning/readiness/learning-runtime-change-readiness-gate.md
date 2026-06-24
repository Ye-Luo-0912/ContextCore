# Learning Runtime Change Readiness Gate

Generated: 2026-06-24T15:08:07.9249467+00:00
PolicyVersion: `learning-readiness-freeze-s0/v1`
Registry: `learning/readiness/learning-readiness-freeze-report.json`

- Passed: `True`
- Recommendation: `RuntimeChangeRulesSatisfied`

| Capability | Condition | Passed | Reason |
|---|---|---:|---|
| Qwen3EmbeddingProvider | NotReadyDoesNotAllowRuntimeModes | True | 未通过 readiness 的 capability 不得允许 ApplyGuarded / RuntimeShadow / DefaultOn。 |
| VectorRetrieval | NotReadyDoesNotAllowRuntimeModes | True | 未通过 readiness 的 capability 不得允许 ApplyGuarded / RuntimeShadow / DefaultOn。 |
| HybridRetrievalPreview | NotReadyDoesNotAllowRuntimeModes | True | 未通过 readiness 的 capability 不得允许 ApplyGuarded / RuntimeShadow / DefaultOn。 |
| RouterIntentClassifier | NotReadyDoesNotAllowRuntimeModes | True | 未通过 readiness 的 capability 不得允许 ApplyGuarded / RuntimeShadow / DefaultOn。 |
| CandidateReranker | NotReadyDoesNotAllowRuntimeModes | True | 未通过 readiness 的 capability 不得允许 ApplyGuarded / RuntimeShadow / DefaultOn。 |
| VectorRetrieval | VectorV4GateBlocksRuntimeShadow | True | Vector V4 gate 未通过时必须禁止 RuntimeShadow / ApplyGuarded。 |
| HybridRetrievalPreview | HybridRetrievalFormalRetrievalSwitchForbiddenWithoutV4Gate | True | HybridRetrieval 未过 V4 gate 时不得接 formal retrieval。 |
| HybridRetrievalPreview | HybridRetrievalPackingPolicyIntegrationForbidden | True | HybridRetrieval preview 不得影响 PackingPolicy / package output。 |
| HybridRetrievalPreview | HybridRetrievalFormalSourceReplacementForbidden | True | HybridRetrieval preview 不得替代正式 retrieval source。 |
| DatasetV2Stress | DatasetV2StressFreezeDoesNotAllowFormalRetrieval | True | Dataset V2 stress freeze 通过也只允许作为 V4 复核输入，不得开启 formal retrieval。 |
| DatasetV2Stress | PostScoringRiskGatedProfileRuntimeUseForbidden | True | post-scoring-risk-gated-v1 不得直接接入 runtime。 |
| DatasetV2Stress | DatasetV2StressPackingPolicyIntegrationForbiddenWithoutV4Gate | True | 未通过 V4 formal readiness gate 前不得改变 PackingPolicy / package output。 |
| VectorV4ReadinessRecheck | VectorV4RecheckDoesNotAllowRuntimeSwitch | True | V4.R 通过也不等于 runtime switch。 |
| VectorV4ReadinessRecheck | VectorV4RecheckFormalRetrievalStillForbidden | True | V4.R 阶段 formal retrieval 仍保持禁用。 |
| VectorV4ReadinessRecheck | VectorV4RecheckPackingPolicyIntegrationForbidden | True | V4.R 不得改变 PackingPolicy / package output。 |
| GuardedFormalRetrievalPreview | GuardedFormalPreviewDoesNotAllowRuntimeSwitch | True | Guarded formal retrieval preview 通过也不等于 runtime switch。 |
| GuardedFormalRetrievalPreview | GuardedFormalPreviewFormalRetrievalStillForbidden | True | V4.1 阶段 formal retrieval 仍保持禁用。 |
| GuardedFormalRetrievalPreview | GuardedFormalPreviewPackageMutationForbidden | True | V4.1 preview 不得改变 PackingPolicy 或写正式 package。 |
| VectorShadowPackageComparison | VectorShadowPackageComparisonDoesNotAllowRuntimeSwitch | True | V4.2 shadow package comparison 通过也不等于 runtime switch。 |
| VectorShadowPackageComparison | VectorShadowPackageComparisonFormalRetrievalStillForbidden | True | V4.2 阶段 formal retrieval 仍保持禁用。 |
| VectorShadowPackageComparison | VectorShadowPackageComparisonPackageMutationForbidden | True | V4.2 shadow package comparison 不得改变 PackingPolicy 或写正式 package。 |
| ScopedFormalPreviewOptIn | ScopedFormalPreviewOptInDoesNotAllowRuntimeSwitch | True | V4.3 scoped formal preview opt-in 通过也不等于 runtime switch。 |
| ScopedFormalPreviewOptIn | ScopedFormalPreviewOptInFormalRetrievalStillForbidden | True | V4.3 阶段 formal retrieval 仍保持禁用。 |
| ScopedFormalPreviewOptIn | ScopedFormalPreviewOptInPackageMutationForbidden | True | V4.3 scoped preview 不得改变 PackingPolicy 或写正式 package。 |
| LimitedFormalPreviewObservation | LimitedFormalPreviewObservationDoesNotAllowRuntimeSwitch | True | V4.4 limited formal preview observation 通过也不等于 runtime switch。 |
| LimitedFormalPreviewObservation | LimitedFormalPreviewObservationFormalRetrievalStillForbidden | True | V4.4 阶段 formal retrieval 仍保持禁用。 |
| LimitedFormalPreviewObservation | LimitedFormalPreviewObservationPackageMutationForbidden | True | V4.4 observation 不得改变 PackingPolicy 或写正式 package。 |
| VectorFormalPreviewFreeze | VectorFormalPreviewFreezeDoesNotAllowRuntimeSwitch | True | V4.F formal preview freeze 通过也不等于 runtime switch。 |
| VectorFormalPreviewFreeze | VectorFormalPreviewFreezeFormalRetrievalStillForbidden | True | V4.F 阶段 formal retrieval 仍保持禁用。 |
| VectorFormalPreviewFreeze | VectorFormalPreviewFreezePackageMutationForbidden | True | V4.F freeze 不得改变 PackingPolicy、写正式 package 或改变 package output。 |
| ScopedRuntimeExperimentHarnessFreeze | ScopedRuntimeExperimentHarnessFreezeDoesNotAllowRuntimeSwitch | True | V4.10 no-op harness freeze 通过也不等于 runtime switch。 |
| ScopedRuntimeExperimentHarnessFreeze | ScopedRuntimeExperimentHarnessFreezeFormalRetrievalStillForbidden | True | V4.10 阶段 formal retrieval 仍保持禁用。 |
| ScopedRuntimeExperimentHarnessFreeze | ScopedRuntimeExperimentHarnessFreezePackageAndBindingMutationForbidden | True | V4.10 freeze 不得写正式 package、改变 DI/vector binding、PackingPolicy 或 package output。 |
| ScopedRuntimeExperimentHarnessFreeze | NoOpHarnessOnlyIsNotRuntimeApproval | True | ApprovalMode=NoOpHarnessOnly 不能被解释为 runtime approval。 |
| RouterIntentClassifier | RouterBreaksBlockGuardedOptIn | True | Router breaks > fixes 时必须禁止 guarded opt-in。 |
| CandidateReranker | CandidateRerankerNetGainBlocksRuntime | True | Candidate reranker netGain <= 0 时必须禁止 runtime shadow / opt-in。 |
| GraphExpansion | GraphNormalCurrentTaskForbidden | True | Graph normal-v1 / current-task-v1 不得默认启用。 |
| RelationGovernance | RelationGovernanceGlobalDefaultOnForbidden | True | Relation governance Postgres provider 不得 global default-on。 |
| RelationGovernance | RelationGovernanceRequiresFallback | True | GuardedPostgresPrimary 必须保留 FileSystem fallback。 |
| RelationGovernance | RelationGovernanceRequiresComparisonTrace | True | GuardedPostgresPrimary 必须保留 comparison trace。 |
| JobQueuePostgres | JobQueueGlobalWorkerProviderSwitchForbidden | True | Job queue Postgres provider 不得 global worker provider switch。 |
| JobQueuePostgres | JobQueueRequiresScopedAllowlist | True | GuardedPostgresPrimary 必须限定 explicit allowlisted worker scopes。 |
| JobQueuePostgres | JobQueueRequiresLeaseQualityGate | True | Job queue scoped worker 必须保留 lease / heartbeat quality gate。 |
| JobQueuePostgres | JobQueueRequiresRetryDeadLetterQualityGate | True | Job queue scoped worker 必须保留 retry / dead-letter quality gate。 |
| VectorPostgresProvider | VectorPostgresFormalRetrievalSwitchForbidden | True | pgvector provider freeze 后仍不得切换正式 vector retrieval。 |
| VectorPostgresProvider | VectorPostgresFormalStoreBindingForbiddenWithoutV4Gate | True | 未通过 V4 gate 前不得把 PostgresVectorIndexStore 绑定为正式 IVectorIndexStore。 |
| VectorPostgresProvider | VectorPostgresPackingPolicyIntegrationForbiddenWithoutV4Gate | True | 未通过 V4 gate 前不得接入 PackingPolicy 或 package output。 |
| VectorPostgresProvider | VectorPostgresRequiresShadowEvalRecallRiskGate | True | pgvector 只可作为 preview/shadow/eval storage，必须有 shadow eval / recall / risk gate。 |
| Qwen3EmbeddingProvider | Qwen3ProviderFormalRetrievalSwitchForbidden | True | 未通过 provider comparison freeze 的 provider 不得切换正式 vector retrieval。 |
| Qwen3EmbeddingProvider | Qwen3ProviderFormalStoreBindingForbidden | True | 未通过 provider comparison freeze 的 provider 不得绑定为正式 IVectorIndexStore。 |
| Qwen3EmbeddingProvider | Qwen3ProviderPackingPolicyIntegrationForbidden | True | 未通过 provider comparison freeze 的 provider 不得接入 PackingPolicy 或 package output。 |

## Failed Conditions
- (empty)
