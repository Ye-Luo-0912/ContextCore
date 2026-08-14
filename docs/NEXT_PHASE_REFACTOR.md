# ContextCore 下一阶段重构路线

> 审查基线：2026-08-14 当前工作树。本文是下一阶段唯一执行清单；现行行为以 [`LIVE_PATH.md`](LIVE_PATH.md) 和源码为准。

## 0. 目标与完成标准

目标是在**功能、公开契约、存储一致性和默认运行路径不变**的前提下减少生产代码、消除重复抽象，并用基准数据决定性能优化，不以拆文件或改名制造“重构完成”。

整阶段完成时必须同时满足：

- 多轮找回、held ID、工作集投影和观察词行为不回退；
- HTTP/OpenAPI、持久化格式、租约 fencing 与 exactly-once 语义无意外变化；
- 每个精简包都报告生产代码净增减，新增抽象必须带来净删除；
- 性能结论有同环境前后数据，不凭文件大小或直觉判断；
- 一个 Agent 一次只执行一个工作包，提交边界可独立回滚。

## 1. Gate 0：先锁定当前基线

当前工作树包含尚未提交的多轮召回改动。RF-1、RF-2 开始前，先确保相关定向测试通过并记录命令、通过数和耗时。若失败，先修复或明确归属，不能把基线失败带入重构。

最低测试集：

```powershell
dotnet test tests/ContextCore.Tests/ContextCore.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~AgentTurnSearchQueryTests|FullyQualifiedName~LexicalQueryTextsTests|FullyQualifiedName~SemanticQueryTextsTests|FullyQualifiedName~HeldIdLexicalSkipTests|FullyQualifiedName~ModelProjectionSkippedMaterialTests|FullyQualifiedName~MultiTurnForgetAndRecallTests"
```

每个工作包结束后跑包内定向测试；全部包完成后再按 [`../AGENTS.md`](../AGENTS.md) 的规则跑全量测试。不要用旧的失败数量替代本次实测。

审查时基线（2026-08-14）：上述筛选命令 **24/24 通过，0 跳过**；总命令约 60 秒，测试执行 561 ms。构建使用 .NET 11 preview，并报告 `TrainingDataExporter.cs` 两处既有 CS8619 nullable 警告。后续 Agent 应重新执行并与此结果比较，不能直接沿用数字。

## 2. 硬边界

- 不打开 Adaptive Active、Learning 训练、默认 embedding 或原型 `materialize`。
- 不修改历史 Postgres migration；需要 schema 变化时只能追加迁移，但本阶段原则上不需要。
- 不做 DTO 全量合并。只有调用方与语义完全一致、且能净删映射代码时才合并局部 DTO。
- 不建立万能 repository、万能 outbox、万能结果 DTO 或 `Dictionary<string, object>`。
- 不并行修改 `CandidateProviders.cs`、`AgentRunActor.cs` 和 Abstractions 公共契约。
- 不把“一个大类拆成多个文件”计入代码精简；以生产代码净行数、分支数、I/O 和分配为准。

## 3. 本次审查结论

### 已完成，不要重做

上一轮建议中的文件存储事务/锁收敛、ControlRoom 透传删除、Scope/Snapshot 和 source generator decorator 已经落地。继续围绕这些方向抽象化，收益低且容易重新增加层级。

### 当前真正值得做的点

| 顺序 | 工作包 | 性质 | 预期收益 | 风险 |
| --- | --- | --- | --- | --- |
| 1 | RF-1 向量排除下推 | 正确性 + 性能 | 避免 TopK 后过滤欠召回，减少无效候选 | 中 |
| 2 | RF-2 租约层收敛 | 正确性 + 精简 | 删除一套没有形成多消费者复用的通用租约层 | 中 |
| 3 | RF-3 删除旧工厂 | 纯精简 | 删除无生产/测试引用代码 | 低 |
| 4 | RF-4 Outbox 局部去重 | 精简 + 维护性 | 合并高度相同的 SQL/映射骨架 | 中高 |
| 5 | RF-5 重建性能基线 | 性能决策 | 找到多问句召回真实瓶颈 | 低 |
| 6 | RF-6 Actor 终态模板 | 条件精简 | 减少重复状态提交代码 | 高 |
| 7 | RF-7 HTTP 错误映射 | 条件精简 | 减少端点重复分支 | 中 |

## 4. RF-1：向量检索排除 held ID

### 问题

`SemanticCandidateProvider` 目前先按 `TopK` 从向量存储取结果，再在内存中过滤已持有 ID。若 TopK 大部分都已在工作集中，会返回少于预算的新候选。Lexical 路径则在存储查询前传入 held/excluded ID，两条通道语义不一致。

涉及位置：

- `src/ContextCore.Core/Services/DecisionEngine/CandidateProviders.cs`
- `src/ContextCore.Abstractions/Models/EmbeddingDtos.cs`
- 三个向量存储 provider 的 `VectorQuery` 实现

### 实施

1. 给 `VectorQuery` 增加只表达“本次不返回”的 source ID 集合，名称与现有 `ExcludedIds` 语义保持清晰区分。
2. InMemory、FileSystem、Postgres 在排序/限制前应用排除条件。
3. `SemanticCandidateProvider` 将 held ID 下推；保留末端去重作为防御，但不能依赖末端过滤满足 TopK。
4. 覆盖单问句和 `QueryTexts` 多问句；确保同一 source 不因多个 query 重复进入候选。
5. 若 `VectorQuery` 暴露到 HTTP，更新 OpenAPI 快照；否则不要扩大 API 面。

### 验收

- held ID 占满原 TopK 时，仍能补足可用的新候选；
- 排除发生在 `Take/Limit` 前；
- 三个 provider 语义一致；
- embedding/search 调用次数不增加；
- 旧调用方不传排除集合时结果不变。

### 禁止

- 不用把 TopK 任意放大后继续内存过滤的方式掩盖问题；
- 不把 held ID 写入代表“确认不存在”的领域排除集合；
- 不顺手重写整个候选提供器。

## 5. RF-2：删除通用 LeasedWork 层

### 问题

当前同一个 closed generic `PostgresLeasedWorkStore<string>` 被用于 Agent Run 和 Canary 两套配置。单服务解析只能得到其中一个注册，行为依赖注册顺序，类型系统无法区分两张表。当前 Canary 注册在后，运行时未必已经出错，但结构脆弱，也没有形成真正的双消费者复用。

同时，Agent Run 与 Canary 已各自拥有专用接口和 Postgres 实现。生产代码中通用 `ILeasedWorkStore` 的实际消费者主要是 Canary hosted service，因此通用层增加了公共契约和约 800 行实现，却没有稳定复用价值。

涉及位置：

- `src/ContextCore.Abstractions/Contracts/LeasedWorkStoreContracts.cs`
- `src/ContextCore.Storage.Postgres/Stores/PostgresLeasedWorkStore.cs`
- `src/ContextCore.Storage.Postgres/Extensions/PostgresServiceCollectionExtensions.cs`
- `src/ContextCore.Service/Hosting/CanaryLeaderHostedService.cs`
- `src/ContextCore.Abstractions/Contracts/CanaryHAAggregationContracts.cs`
- `src/ContextCore.Storage.Postgres/Stores/PostgresCanaryLeaderLease.cs`

### 实施

1. 先补 DI 组合测试，明确当前 Canary 解析的表/实现，避免把推测当事实。
2. 将 Canary hosted service 改为只依赖专用 `ICanaryLeaderLease`。
3. 对齐 TryAcquire、Renew、Release、Reap 与 fencing token 行为；不降低租约安全性。
4. 删除两处 `AddLeasedWorkStore<string>` 注册、通用实现和不再被引用的公共契约。
5. 更新 Public API baseline；在交付说明中单列删除的公开类型。若仓库兼容策略禁止删除公共 API，则先将其内部化/废弃并记录下一主版本删除点，不要保留第二套运行链。

### 验收

- Canary 只解析专用租约接口；
- Agent Run 租约路径不变；
- acquire/renew/release/reap/fencing/过期接管测试齐全；
- DI 中没有同 closed generic 多配置覆盖；
- 生产代码显著净删除，目标约 800 行以上。

## 6. RF-3：删除旧 ExecutionArtifactFactory

`UnifiedRuntimeDefaults.cs` 同时存在现行 `DefaultExecutionArtifactFactory` 和无生产/测试引用的内部静态 `ExecutionArtifactFactory`。确认 `rg` 零引用后删除旧工厂及只为它服务的辅助代码，跑 execution artifact 定向测试。

这是低风险包，应独立完成，不和 RF-4 混在一起。若删除后净减不明显，说明还有隐式反射或测试约束，需要停下调查。

## 7. RF-4：Outbox 只做局部内部去重

多个 Postgres store 都有 `FOR UPDATE SKIP LOCKED`、lease token、ack/nack/reap，但业务状态、幂等键、重试和死信语义并不相同。不要建立新的公共“万能队列”。

第一轮只比较语义最接近的：

- `PostgresLearningEventOutboxStore`
- `PostgresRelationOutboxStore`

允许提取的仅是内部、无状态、可直接测试的重复件，例如：

- 参数绑定与通用 lease 时间列映射；
- 完全相同的批量 ID/token SQL 片段生成；
- 完全相同的 reader 基础字段读取。

不允许抽取：状态机、幂等键、死信判定、业务 payload、事务边界。若辅助件新增行数大于删掉的重复，立即放弃该包。

### 验收

- 两个 store 的 SQL 快照/集成行为不变；
- 并发领取无重复、Ack/Nack、过期回收、死信测试通过；
- 无新公共 API；
- 生产代码净删除，复杂度没有转移到大量泛型参数或委托。

## 8. RF-5：重建多问句召回性能基线

现有性能报告早于 `QueryTexts` 多问句链路，不能指导当前优化。完成 RF-1 后，在相同机器、Release、固定数据集上至少测：

| 维度 | 建议值 |
| --- | --- |
| QueryTexts 数量 | 1 / 4 / 8 |
| TopK | 10 / 50 / 100 |
| Held ID 数量 | 0 / 10 / 100 |
| Provider | InMemory / FileSystem / Postgres |
| 模式 | lexical-only / semantic-only / combined |

必须记录：p50/p95、吞吐、分配字节、embedding 次数、vector search 次数、存储 roundtrip、返回有效候选数和欠召回数。

决策规则：

- 如果 embedding 次数随 QueryTexts 线性增长且占主导，优先批量 embedding 或 query 去重缓存；
- 如果 Postgres roundtrip 占主导，优先单请求批量查询，不能直接并行放大连接池压力；
- 如果分配由候选合并主导，再优化集合复用/容量预估；
- p95 或分配未显著改善的优化不进入主线。

## 9. RF-6：AgentRunActor 终态提交模板（条件项）

`AgentRunActor` 的完成、失败、取消路径重复 update → transition → event → flush，但 RetryPending、工具 reconciliation、安全告警和结算时点不同。只有先用行为测试锁定每条终态路径，且能提取一个小型私有模板并净删代码时才做。

禁止把全部终态压进参数众多的万能方法；禁止改变事件顺序、flush 时点、结算 exactly-once 或 retry 语义。达不到净删除和可读性双目标就取消本包。

## 10. RF-7：Service 错误结果映射（条件项）

端点中仍有重复的 not-found/conflict/validation 映射。优先复用现有 `ContextCoreHttpResultMapper`，按稳定的错误类型增加少量映射；不要引入捕获所有异常的全局过滤器，也不要隐藏业务状态码差异。

仅当 OpenAPI 快照和端点测试证明响应状态/JSON 完全不变，且净删重复分支时交付。

## 11. DTO 处理原则

DTO 确实可能制造重复，但不是按名字批量删除：

- API request/response、持久化记录、领域命令的变化原因不同，默认保持分离；
- 只合并“字段、校验、生命周期、版本策略、所有消费者”均一致的类型；
- 优先删除一次性 wrapper、重复 mapper、纯转发 DTO；
- 不为少写几行映射而把内部字段暴露给 API；
- 每次 DTO 精简必须跑 Public API baseline、JSON round-trip 与 OpenAPI drift 测试。

下一阶段不安排 Abstractions 全目录重命名或 Domain/Api/Ports 大搬家：代码 churn 大，几乎不减少运行时成本，也会阻塞 RF-1/RF-2。

## 12. Agent 交接模板

把下面内容连同**一个**工作包编号交给执行 Agent：

```text
只执行 docs/NEXT_PHASE_REFACTOR.md 的 RF-X，不扩展到其他工作包。
先读 docs/LIVE_PATH.md、AGENTS.md 和 RF-X 涉及源码；保留当前工作树中他人的改动。
先补/运行行为基线，再修改；不得改变公开行为、存储语义、事件顺序或默认配置。
目标是生产代码净删除或有数据证明的性能改进，不以拆文件/改名计成果。
完成后报告：改动文件、行为不变量、生产代码净增减、定向测试命令与结果、性能前后数据、剩余风险。
不要自行 commit，不要顺手执行 RF-X 之外的清理。
```

## 13. 阶段完成检查

- [x] Gate 0 当前基线有可复现结果
- [x] RF-1 完成且三 provider 一致
- [x] RF-2 完成且通用租约层已删除或有明确兼容阻断记录
- [x] RF-3 完成
- [x] RF-4 经净删除门槛后完成或明确取消
- [x] RF-5 形成新性能报告与下一步数据结论（`benchmarks/results/MULTIQUERY_RECALL_BASELINE.md`，162 组合）
- [x] RF-6 / RF-7 分别完成或因不满足门槛取消
- [x] `git diff --check`、文档链接、Public API、OpenAPI 与全量测试已验证（全量 4075 通过 / 0 失败 / 7 跳过，跳过均为重新生成辅助或需真实 Postgres）
- [x] [`TODO.md`](../TODO.md)、[`README.md`](../README.md)、[`LIVE_PATH.md`](LIVE_PATH.md) 指向下一份真实执行清单（当前无新清单，指向已更新为完成态）
