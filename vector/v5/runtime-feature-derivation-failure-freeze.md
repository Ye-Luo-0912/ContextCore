# Runtime Feature Derivation Failure Freeze

生成时间: `2026-06-23T08:01:47.9927467+00:00`
操作标识: `runtime-feature-derivation-failure-freeze-1d6ebbebc3d24509a28a082b0c961061`

## 冻结状态

- FreezePassed: `True`
- Recommendation: `ReadyForGraphHubNoiseControlPreview`
- FrozenStatus: `BlockedByHubRelationNoise`
- FormalRetrievalAllowed: `False`
- RuntimeSwitchAllowed: `False`

## 组件状态

- CanonicalAnchorResolverReusable: `True`
- RuntimeRelationIntentDeriverReady: `False`
- CombinedRepairEvalUpperBoundOnly: `True`

## 失败摘要

- V5.7 推导召回率/MRR: `0.5083/0.2275`
- V5.8 训练集 baseline/derived 召回率: `0.5104/0.4792`
- V5.8 训练集 baseline/derived MRR: `0.2299/0.1998`
- V5.8 留出集 baseline/derived 召回率: `0.5000/0.4583`
- V5.8 留出集 baseline/derived MRR: `0.2181/0.2076`
- 规范关系覆盖率: `0.1333`
- 规范证据覆盖率: `0.4750`
- 规范来源覆盖率: `0.4750`

## 失败原因
- `DerivedMrrNotImproved`
- `DerivedRecallNotImproved`
- `HoldoutMrrRegression`
- `HoldoutRecallRegression`
- `LowRelationCoverage`

## 已禁用能力
- `relation boost promotion: hub-expanded relation envelope causes uniform multiplier across all candidates`
- `combined-repair formal scoring: relation boost degrades recall on hub-and-spoke relation graphs`
- `hub-expanded relation envelope scoring: hub items flood envelope with non-discriminative relations`

## 推荐后续阶段
- `RuntimeRetrievalFeatureDerivationFreeze: graph hub / relation noise control preview`
- `RuntimeRetrievalFeatureDerivationFreeze: input evidence / provenance contract enforcement`

## 已冻结产物路径
- `vector/v5/runtime-feature-derivation-preview.json/.md`
- `vector/v5/runtime-feature-derivation-gate.json/.md`
- `vector/v5/runtime-feature-derivation-repair.json/.md`
- `vector/v5/runtime-feature-derivation-repair-gate.json/.md`
- `vector/v5/runtime-feature-derivation-failure-freeze.json/.md`

失败已冻结。在解决图枢纽/关系噪声控制问题前，不做 formal retrieval、package write、PackingPolicy mutation、runtime switch、vector store binding change。
