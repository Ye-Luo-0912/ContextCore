# FileSystem Layout Registry

FS1 引入统一的 artifact routing 层，用来约束 report、trace、eval 和治理数据的文件落点。

## 目标

- 所有新 artifact 通过 `ArtifactDescriptor` 描述，而不是在业务代码里散落拼路径。
- `ContextCoreDataLayout` 负责 workspace / collection id 清洗、root escape prevention 和稳定路径解析。
- `FileArtifactStore` 负责原子写、JSONL 追加、自动建目录和 manifest upsert。
- 旧路径继续保留兼容，不做破坏性迁移，不删除旧文件。

## 标准目录

```text
context-core-data/
  system/
    artifact-manifest.jsonl
  workspaces/{workspaceId}/collections/{collectionId}/
    memory/
      short/
      candidate/
      stable/
    relations/
    constraints/
    vector/
    learning/
      feedback/
      router/
      ranker/
      graph/
    traces/
    eval/
    reports/
    jobs/
  eval/
  reports/
  traces/
  jobs/
  temp/
  backups/
```

## Artifact Kind

`ArtifactKind` 覆盖 memory、relation、constraint、vector、learning feedback、router、ranker、graph、eval、traces、jobs 和 reports。

## 兼容策略

第一批 report writer 会保留原输出路径，同时镜像写入标准 artifact 路径，并更新 `system/artifact-manifest.jsonl`。

第一批覆盖范围：

- `learning/feedback/*`
- `learning/readiness/*`
- `learning/router/*`
- `learning/ranker/*`
- `vector/reindex/*`
- `eval/vector*`
- `eval/graph*`
- `eval/relation-expansion*`
- `eval/eval-report-p15*`
- `eval/extended-failure-triage-report*`

## 写入规则

- 覆盖写必须使用原子写。
- JSONL 追加必须保留已有内容。
- descriptor 相同的 artifact manifest 记录必须 upsert，不重复追加。
- workspaceId / collectionId 只能作为路径 segment 使用，必须先 sanitize。
- 任何解析出的路径不得逃逸 data root。

## ControlRoom

Service Admin / Runtime 页面展示 File Layout Status：data root、artifact category count、manifest count、report count、resolved path samples 和 diagnostics。

## 约束

FS1 不改变 retrieval、planning、scoring、PackingPolicy 或 package output。
后续新增 report writer 应优先使用 `ArtifactDescriptor` / `IArtifactStore`，避免继续散落硬编码路径。

## FS2 Memory Layer Routing

FS2 将 memory layer 的第一批 store 路径迁入 `ContextCoreDataLayout`，并预留 temporal memory 灰度区目录。该阶段只治理文件落点，不改变 memory 业务逻辑、retrieval、planning、scoring、`PackingPolicy` 或 package output。

新增 memory artifact kind：

- `MemoryShortTermRawEvent`
- `MemoryShortTermWorkingItem`
- `MemoryShortTermArchive`
- `MemoryShortTermCompactionRun`
- `MemoryTemporalItem`
- `MemoryTemporalArchive`
- `MemoryTemporalDiagnostics`
- `MemoryCandidateItem`
- `MemoryCandidateReview`
- `MemoryCandidateDiagnostics`
- `MemoryCandidateEvidence`
- `MemoryStableItem`
- `MemoryStableLifecycleReview`
- `MemoryStableReplacementChain`
- `MemoryStableProvenance`
- `MemoryStableDiagnostics`

标准 memory 目录：

```text
workspaces/{workspaceId}/collections/{collectionId}/memory/
  short-term/
    raw-events/
    working-items/
    archive/
    compaction/
  temporal/
    items/
    archive/
    diagnostics/
  candidate/
    items/
    reviews/
    diagnostics/
    evidence/
  stable/
    items/
    lifecycle-reviews/
    replacement-chains/
    provenance/
    diagnostics/
```

兼容策略：

- 新写入走 layout resolver 解析出的标准路径。
- 旧版短期 raw / working / archive / compaction 文件继续作为只读 fallback。
- 旧版 CandidateMemory review 与 StableLifecycleReview 文件继续作为只读 fallback。
- 旧版 stable memory 文件继续参与读取，避免稳定记忆在路径治理期间不可见。
- 不删除旧文件，不做破坏性迁移。

ControlRoom 会展示 Memory Layout Status：memory layer path、artifact count、legacy fallback count、temporal placeholder readiness 和 diagnostics。

## FS3 Trace / Tool-call Routing

FS3 将 trace / tool-call artifact 纳入标准 layout。该阶段只治理 trace 文件落点和读取兼容，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

新增 trace artifact kind：

- `TraceRetrieval`
- `TracePlanning`
- `TraceToolCall`
- `TraceRouterShadow`
- `TraceRankerShadow`
- `TraceVectorShadow`
- `TraceGraphShadow`
- `TracePackageBuild`
- `TraceModelCall`
- `TraceError`

标准 trace 目录：

```text
workspaces/{workspaceId}/collections/{collectionId}/traces/
  retrieval/{date}/
  planning/{date}/
  tool-calls/{date}/{operationId}/
  router-shadow/{date}/
  ranker-shadow/{date}/
  vector-shadow/{date}/
  graph-shadow/{date}/
  package-build/{date}/
  model-calls/{date}/
  errors/{date}/
```

新增 `TraceArtifactDescriptorFactory` 与 `TraceArtifactWriter`：

- `WriteTraceAsync`
- `AppendTraceJsonLineAsync`
- `WriteToolCallRequestAsync`
- `WriteToolCallResponseAsync`
- `WriteToolCallErrorAsync`
- `WriteTraceManifestAsync`

第一批迁移：

- retrieval trace 新写入到 `traces/retrieval/{date}`。
- package build trace 新写入到 `traces/package-build/{date}`。
- router shadow trace 新写入到 `traces/router-shadow/{date}`。
- ranker / graph shadow trace 继续作为 retrieval trace 的 shadow block 读取，因此随 retrieval trace routing 迁移。
- vector shadow trace 预留标准 artifact kind 和目录，后续独立 trace store 可直接接入。

兼容策略：

- 新 trace 走 layout resolver。
- 查询会枚举标准日期分片并合并 legacy trace 文件。
- legacy trace 只读 fallback，不删除、不迁移。
- 合并读取时按 trace id / request id 去重，避免重复噪声。

## FS4 Eval / Report Artifact Migration

FS4 将 eval / report / learning / vector / graph / readiness 报告纳入标准 artifact layout，并增强 manifest。该阶段只治理报告写出和可观测状态，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

`ArtifactManifestEntry` 增强字段：

- `artifactId`
- `artifactKind`
- `workspaceId`
- `collectionId`
- `relativePath`
- `legacyPath`
- `contentType`
- `extension`
- `reportId`
- `capabilityId`
- `providerId`
- `policyVersion`
- `schemaVersion`
- `createdAt`
- `updatedAt`
- `sizeBytes`
- `contentHash`
- `isLatest`
- `isSnapshot`
- `sourceCommand`

新增 `ReportArtifactRegistry` / `ReportArtifactDescriptorFactory` / `ReportArtifactMirrorWriter`：

- 按 legacy report 路径分类 report capability。
- 旧路径继续写出，标准 layout 同步 mirror。
- manifest 同时记录 legacy path、standard relative path、content hash 和 size。
- 每个被 mirror 的 report 同步维护 `ReportId=latest` 的 latest alias。
- 不删除旧文件，不做破坏性迁移。

第一批 mirror 范围：

- `eval/p15/*`
- `eval/vector*`
- `eval/graph*`
- `eval/relation-expansion*`
- `learning/feedback/*`
- `learning/readiness/*`
- `learning/router/*`
- `learning/ranker/*`
- `vector/reindex/*`

ControlRoom Admin / Runtime 页面增加 Report Layout Status：

- report categories
- latest report count
- legacy mirror count
- missing standard / legacy artifact count
- duplicate content hash count
- largest reports
- sample resolved paths

## DB0 Artifact Plane / Operational Store Boundary

FS1-FS4 完成 artifact routing 后，DB0 明确哪些数据不应继续强化为文件系统主存储。本阶段只生成 storage boundary report，不迁移数据库、不改 retrieval、planning、scoring、`PackingPolicy` 或 package output。

分类原则：

- Eval / learning / readiness report：`ArtifactOnly` / `FileSystemPreferred`
- Retrieval / tool-call / shadow trace：`TraceOnly` / `FileSystemPreferred`
- Feedback export：`ExportOnly` / `FileSystemPreferred`
- ContextItem / MemoryItem / RelationItem / ConstraintState / JobRecord：`OperationalState` / `DatabaseRecommended`
- VectorIndexEntry：`IndexState` / `DatabaseRecommended`，后续可评估 pgvector

运行 `eval storage-boundary-report` 输出：

- `storage/storage-boundary-report.json`
- `storage/storage-boundary-report.md`

该报告只用于 migration candidate 标记和后续 DB phase 排序，不触发迁移、不删除旧文件。

## DB2.3 Relation Dual-write Trace Artifact

DB2.3 增加 relation governance dual-write trace：

- `ArtifactKind.TraceRelationDualWrite`
- 标准目录：`traces/relation-dual-write/{date}`
- 兼容 legacy 输出：`storage/postgres/postgres-relation-dual-write-traces.jsonl`

该 trace 记录 FileSystem 写入、Postgres 写入、mismatch、fallback 和耗时。它只用于 dual-write smoke / quality report，不参与 runtime read，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

## DB2.4 Relation Shadow-read Trace Artifact

DB2.4 增加 relation governance shadow-read trace：

- `ArtifactKind.TraceRelationShadowRead`
- 标准目录：`traces/relation-shadow-read/{date}`
- 兼容 legacy 输出：`storage/postgres/postgres-relation-shadow-read-traces.jsonl`

该 trace 记录 FileSystem / Postgres 读取是否成功、结果 hash、mismatch、fallback 和两侧 latency。它只用于 shadow-read smoke / quality report，不切换 runtime provider，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。
