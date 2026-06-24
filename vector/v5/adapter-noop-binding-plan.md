# Adapter No-op Binding Plan

生成: `2026-06-20T00:37:01.5667657+00:00`

## 计划摘要
- PlanPassed: `True`
- Recommendation: `ReadyForAdapterNoOpBindingPlanFreeze`
- PlanVersion: `1.0`
- FormalRetrievalAllowed: `False`
- RuntimeSwitchAllowed: `False`

## DI 接入点
- `IContextRetrievalAdapter (scoped DI registration via ServiceCollectionExtensions)`

## 空操作适配器接口
`IShadowRetrievalAdapter { Task<ShadowAdapterResult> ExecuteAsync(QueryContext, CancellationToken) }`

## 影子追踪路径
`vector/trace/shadow-adapter-trace-{query}.jsonl`

## 回滚计划
Disable IShadowRetrievalAdapter DI registration; restore original IContextPackageBuilder path.

## 急停计划
Remove adapter DI binding; re-run integration-freeze-gate before re-enabling.

## 实现阶段
- `Phase 1: Define IShadowRetrievalAdapter interface + no-op implementation`
- `Phase 2: Wire DI registration behind feature flag (default off)`
- `Phase 3: Integrate shadow trace writer with existing package assembly pipeline`
- `Phase 4: Controlled canary rollout with observation window`
- `Phase 5: Full formal retrieval binding (separate approval gate required)`

## 阻塞原因
- (empty)

空操作绑定计划。不修改正式 DI binding、不启用 formal retrieval、不写 formal package、不改 selected set、不改 PackingPolicy/package output。
