# retrieval-orchestration-baseline-v1

更新时间：2026-05-30

## 1. 基线目标

冻结当前 Retrieval 编排边界，作为后续改造的防退化对照。

冻结范围：

- `HybridContextRetriever` 只做 orchestration。
- scoring 公式不变。
- lifecycle policy 不变。
- relation expansion 语义不变。
- packing policy 不变。
- `TopK` 默认值不变。
- 不扩展 eval 样本。

## 2. 当前结构

1. `RetrievalChannelContext`
2. `IRetrievalChannelExecutor`
3. `ContextRecallChannelExecutor`
4. `MemoryRecallChannelExecutor`
5. `VectorRecallChannelExecutor`
6. `RelationRecallChannelExecutor`
7. `RetrievalCandidateAccumulator`
8. `RetrievalPackingPolicy`
9. `RetrievalTraceAssembler`
10. `RetrievalResultAssembler`

## 3. 当前指标

- `dotnet build ContextCore.sln -p:UseSharedCompilation=false`
  - 0 error
  - 4 warnings（最新串行增量 build）
- `dotnet test ContextCore.sln -p:UseSharedCompilation=false --no-build`
  - 222 passed
  - 0 failed
- 113 条扩展评测
  - PassRate: 86.73%
  - Recall@10: 95.87%
  - Failed: 15
- 50 条 A3 baseline
  - PassRate: 100.00%
  - Recall@10: 100.00%
  - Failed: 0

## 4. 防退化规则

- build 不能新增 error。
- test 不能新增失败。
- 50 条 A3 baseline 必须保持 0 failed。
- 113 条扩展评测 Recall@10 不得明显低于 95.87%。
- 若出现波动，必须在执行报告中说明样本级原因。

## 5. 后续允许改动

- 继续瘦身 orchestration。
- 增强 trace 可观测性。
- 输入层治理和幂等接入。
- 结果组装和诊断展示优化。
- Service 契约层收口可以继续推进，但不得改 retrieval 语义和评测样本。
