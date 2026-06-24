# Vector Embedding Provider Comparison Freeze

更新时间：2026-06-15

## 结论

V3.10 已完成 `qwen3-embedding-0.6b-onnx` provider comparison（见 `docs/vector-preview-shadow-freeze.md` 的 V3.10 节）。本轮 V3.10.F 只做 embedding provider comparison freeze，对 promotion 结论做冻结记录。

冻结结论：

- Qwen3 provider 不晋升（`PromotionStatus=DoNotPromote`）。
- Provider configuration sanity audit 已通过，provider comparison 为 `Conclusive`。
- 当前 preview provider 保持现状（`KeepCurrentPreviewProvider`），不切换。
- `FormalRetrievalAllowed=false`。
- `VectorV4RecheckAllowed=false`。
- VectorRetrieval 仍为 `PreviewOnly / BlockedByA3Recall`。
- 不改变 `PackingPolicy`、package output、scoring。
- `FormalOutputChanged=0`。

## Current Provider Baseline

当前 preview provider 保持现状（沿用 V3.x 已冻结的 ONNX local provider）。本轮只记录其作为 baseline，不切换：

- `CurrentEmbeddingProvider` readiness status = `KeepCurrentPreviewProvider`
- 作为对比基线参与 V3.10 provider comparison，不进入 promotion 流程。
- 正式检索仍由 V4 readiness gate 阻断，与 provider 选择无关。

## Qwen3 Provider Result

`qwen3-embedding-0.6b-onnx`（dimension `1024`）provider comparison 与 readiness gate 结果：

- provider smoke：通过
- indexed entries：`158/158`
- A3 recall after policy：`54.55%`
- Extended recall after policy：`76.88%`
- A3 risk after policy：`1`（非 0）
- Extended risk after policy：`1`（非 0）
- pgvector query preview projection parity：未通过
- pgvector shadow eval projection parity：未通过
- readiness gate：`Passed=false`
- readiness recommendation：`BlockedByRisk`
- `FormalOutputChanged=0`（未改变正式输出）

## Provider Configuration Sanity Audit

V3.10.F 在 freeze gate 前新增 `VectorProviderConfigurationSanityAuditRunner`。该审计只读取 qwen3 相关报告，不改变 runtime 行为。

强制校验项：

- `ProviderType=OnnxLocal`
- `ProviderId=qwen3-embedding-0.6b-onnx`
- `ModelId=qwen3-embedding-0.6b`
- `ModelPath` / `TokenizerPath` 包含 `qwen3-embedding-0.6b-onnx`
- `Dimension=1024`
- `UseForRuntime=false`

覆盖报告：

- `embedding-provider-smoke`
- `vector-provider-comparison`
- `vector-qwen3-shadow-eval-a3`
- `vector-qwen3-shadow-eval-extended`
- `vector-query-profile-sweep-a3`
- `vector-query-profile-sweep-extended`
- `vector-qwen3-readiness-gate`
- `postgres-vector-query-preview`
- `postgres-vector-shadow-eval-summary`

若任一报告 provider metadata 不匹配，freeze gate 必须输出 `ProviderComparison=Inconclusive`、`Recommendation=BlockedByProviderConfigurationMismatch`、`PromotionStatus=Inconclusive`。这种情况下不得输出 `DoNotPromote`，也不得触发 V4 recheck。

## Freeze Gate 规则

`EmbeddingProviderComparisonFreezeRunner` 冻结规则：

- provider configuration sanity audit 未通过 => `Inconclusive / BlockedByProviderConfigurationMismatch`
- Qwen3 readiness gate 未通过 => `DoNotPromote`
- `RiskAfterPolicy > 0` => block V4 recheck
- `A3RecallAfterPolicy < 80%` => block V4 recheck
- `ExtendedRecallAfterPolicy < 80%` => block V4 recheck
- `FormalOutputChanged != 0` => block
- P15 gate 未通过 => block

本轮冻结产出：

- `Passed=false`
- `ProviderComparison=Conclusive`
- `ProviderConfigurationSanityPassed=true`
- `ReadinessGatePassed=false`
- `PromotionStatus=DoNotPromote`
- `VectorV4RecheckAllowed=false`
- `FormalRetrievalAllowed=false`
- `VectorRetrievalStatus=PreviewOnly`
- `Recommendation=BlockedByRisk`（risk 优先于 recall）

## Readiness Registry

统一 learning readiness registry 新增两个 capability 条目：

- `Qwen3EmbeddingProvider`：`Status=PreviewOnly`，`GatePassed=false`，`Recommendation=BlockedByRisk` / `DoNotPromote`，禁止 `FormalRetrievalSwitch` / `PgVectorFormalRetrievalSwitch` / `FormalIVectorIndexStoreBinding` / `PackingPolicyIntegration` / `PackageOutputIntegration`。
- `CurrentEmbeddingProvider`：`Status=KeepCurrentPreviewProvider`，`GatePassed=true`，`Recommendation=KeepCurrentPreviewProvider`，作为 preview provider 不切换。
- `VectorV4RecheckAllowed=false`（由 freeze 报告决定，未通过前不允许任何 V4 recheck）。

## Runtime-Change Gate 硬规则

`learning-runtime-change-readiness-gate` 对未通过 provider comparison freeze 的 provider 新增 3 条硬规则（与 VectorPostgresProvider 的 V4 前置规则一致）：

- `Qwen3ProviderFormalRetrievalSwitchForbidden`：未通过 freeze 的 provider 不得触发 formal retrieval。
- `Qwen3ProviderFormalStoreBindingForbidden`：未通过 freeze 的 provider 不得绑定为正式 `IVectorIndexStore`。
- `Qwen3ProviderPackingPolicyIntegrationForbidden`：未通过 freeze 的 provider 不得接入 `PackingPolicy` / package output。

这三条只校验 readiness registry 中的 forbidden runtime mode，不修改任何运行时行为。

## 输出

冻结报告：

- `vector/providers/qwen3/vector-provider-configuration-sanity-audit.json`
- `vector/providers/qwen3/vector-provider-configuration-sanity-audit.md`
- `vector/providers/qwen3/vector-provider-comparison-freeze.json`
- `vector/providers/qwen3/vector-provider-comparison-freeze.md`

生成命令：

```
dotnet run --project src/ContextCore.ControlRoom -- eval vector-provider-comparison-freeze
```

该命令依赖前置生成的 qwen3 provider reports（位于同一 `vector/providers/qwen3/` 目录），并会先写出 provider configuration sanity audit。sanity audit 通过后，质量 gate 才能给出 `DoNotPromote` 或 promotion 结论。

## 冻结规则（持续有效）

在后续阶段解除冻结前，以下规则保持有效：

- 不允许未通过 freeze 的 provider 切换为正式 vector retrieval provider。
- 不允许未通过 freeze 的 provider 绑定正式 `IVectorIndexStore`。
- 不允许未通过 freeze 的 provider 改变 `PackingPolicy` 或 package output。
- `FormalRetrievalAllowed` 恒为 `false`，与 promotion 结论无关。
- VectorRetrieval 仍为 `PreviewOnly / BlockedByA3Recall`。
- 相关改动必须通过 `scripts/eval-gate-p15.ps1`。

## 解除冻结的条件

本轮冻结不阻塞后续实验。若未来某候选 provider（Qwen3 或其它）希望重新评估 promotion，必须**同时**满足：

- provider smoke 通过
- A3 recall after policy ≥ 80%
- Extended recall after policy ≥ 80%
- `RiskAfterPolicy=0`、`MustNotHitRiskAfterPolicy=0`、`LifecycleRiskAfterPolicy=0`
- pgvector query preview / shadow eval projection parity 通过
- `FormalOutputChanged=0`
- P15 gate 通过

满足上述全部条件后 freeze gate 才可能 `Passed=true`、`PromotionStatus=PromoteCandidate`、`VectorV4RecheckAllowed=true`。即便如此，正式 retrieval 仍需独立通过 Vector V4 readiness gate，provider comparison freeze 不会自动开启 formal retrieval。

当前 Qwen3 同时存在 recall 不足、risk 非 0、projection parity 未通过，距离解除条件较远，本轮结论为 `DoNotPromote`。
