# P0 冻结 Benchmark Baseline

采集时间：2026-07-20
HEAD：P0 冻结 commit（`fix(P0): freeze`，R14-PG 收口 `3dbc1db` 之后；见 `git log --grep`）
环境：Windows 11, .NET 10.0.9, BenchmarkDotNet v0.14.0, IterationCount=5 WarmupCount=3

## 摘要

37 个 benchmark，总耗时 8:14。完整报告：`results/` 子目录下的 `*-report-github.md` / `*-report-full.json` / `*-report.csv`。

## P0-4 Package cold/cache benchmark

### PackageBuildBenchmarks（InMemory，Package 构建路径）

| Method | ItemCount | Mean | StdDev | Allocated | Alloc Ratio |
|---|---:|---:|---:|---:|---:|
| BuildDetailed_Cold | 10 | 1,222.1 μs | 109.0 μs | 477.93 KB | 1.00 |
| BuildDetailed_CacheHit | 10 | 6.3 μs | 0.06 μs | 12.42 KB | 0.03 |
| BuildDetailed_Cold | 50 | 2,329.0 μs | 191.7 μs | 819.14 KB | 1.00 |
| BuildDetailed_CacheHit | 50 | 6.6 μs | 0.19 μs | 12.56 KB | 0.02 |
| BuildDetailed_Cold | 200 | 4,336.9 μs | 281.3 μs | 1498.31 KB | 1.00 |
| BuildDetailed_CacheHit | 200 | 6.5 μs | 0.20 μs | 12.7 KB | 0.008 |

CacheHit/Cold 延迟比 ~0.003（~360x 加速），分配比 ~0.01（~75x 节省）。
并发 8 线程：Cold 50 项 19.7ms / 7102KB；CacheHit 50 项 50.5μs / 100.73KB。

### FileSystemPackageBuildBenchmarks（真实文件 I/O）

| Method | ItemCount | Mean | Allocated |
|---|---:|---:|---:|
| FileSystem_AppCacheMiss_OsFileCacheWarm | 10 | 5,497.6 μs | 649.44 KB |
| FileSystem_CacheHit | 10 | 5.9 μs | 12.19 KB |
| FileSystem_AppCacheMiss_OsFileCacheWarm | 50 | 20,507.1 μs | 1385.58 KB |
| FileSystem_CacheHit | 50 | 6.0 μs | 12.19 KB |
| FileSystem_AppCacheMiss_OsFileCacheWarm | 200 | 76,396.2 μs | 4135.29 KB |
| FileSystem_CacheHit | 200 | 6.1 μs | 12.19 KB |

OS 文件缓存预热后 CacheHit 仍 ~1000x 快于 Cold。

### ConcurrencyScalingBenchmarks（并发扩展，DelayedStore 1ms/query）

| ConcurrencyLevel | ItemCount | Mean | Allocated |
|---:|---:|---:|---:|
| 1 | 50 | 186.2 ms | 669.04 KB |
| 4 | 50 | 186.5 ms | 2692.14 KB |
| 16 | 50 | 185.3 ms | 11766.14 KB |
| 64 | 50 | 189.0 ms | 47016.72 KB |

1ms/query 延迟下并发 1→64 总耗时基本持平（并发已充分吸收 I/O 等待），分配随并发线性增长。

### ParallelPrefetchBenchmarks（并行预取）

| Method | QueryDelayMs | Mean | Allocated |
|---|---:|---:|---:|
| NoDelay_ParallelPrefetch | 0 | 988.5 μs | 726.8 KB |
| WithDelay_ParallelPrefetch | 1 | 186,246.7 μs | 737.55 KB |
| WithDelay_Concurrent4_ParallelPrefetch | 1 | 186,154.1 μs | 2689.18 KB |

### CacheChurnBenchmarks（InMemoryContextStateCache）

| Method | Capacity | Mean | Allocated |
|---|---:|---:|---:|
| WriteWithLruEviction | 1000 | 33,444.1 μs | 1434.23 KB |
| InvalidateByScope | 1000 | 781.4 μs | 79.45 KB |
| MixedReadWrite | 1000 | 13,023.1 μs | 852.89 KB |
| WriteWithLruEviction | 10000 | 367,630.7 μs | 12783.13 KB |
| InvalidateByScope | 10000 | 2,937.8 μs | 711.46 KB |
| MixedReadWrite | 10000 | 9,439.2 μs | 523.42 KB |

## P0-5 FileSystem vs PostgreSQL store call / bytes / allocation

### PostgreSQL 性能（Testcontainers 集成测试，PostgresPerformanceTests.cs）

| Method | Mean | 预算 | 结果 |
|---|---:|---:|---|
| ColdBuild_SingleRequest | 323 ms | 2,000 ms | 通过 |
| ConcurrentBuild_4Way | 337 ms | 8,000 ms (4×) | 通过 |
| ConcurrentBuild_16Way | 561 ms | 20,000 ms (10×) | 通过 |

### 冷路径分配 Gate（PackageColdPathPerformanceGateTests.cs）

| Method | Provider | Gate | 结果 |
|---|---|---:|---|
| ColdPath_InMemory_50Items_AllocationUnderGate | InMemory | 2 MB | 通过 |
| ColdPath_InMemory_200Items_AllocationUnderGate | InMemory | 3.2 MB | 通过 |
| ColdPath_FileSystem_50Items_AllocationUnderGate | FileSystem | 3 MB | 通过 |
| ColdPath_InMemory_50Items_P95LatencyUnderGate | InMemory | 50 ms | 通过 |

### 差距记录

以下指标当前未实现直接对比基准（BenchmarkOutputConfig 注释中标记为待补充）：
- **File I/O bytes**：未实现字节计数 EventCounter
- **DB query count**：未实现查询计数 EventCounter
- **Store call count 对比**：PackageReadPlanTests 仅覆盖 InMemory 的 StoreCallCounts 字典；未对比 FileSystem vs PostgreSQL
- **Allocation 对比**：分别有 InMemory/FileSystem 的 gate，但无 PostgreSQL allocation gate，无跨 provider 断言

## P0-6 Cache hit / stale retry / version mismatch

95 个测试全部通过（ContextStateCacheTests.cs + MultiInstanceCacheInvalidationTests.cs + BoundedChannelContextEventSinkTests.cs）。

| 子领域 | 测试数 | 关键测试 |
|---|---:|---|
| Cache hit | ~20 | `GetOrAddAsync_CacheHit_DoesNotInvokeFactory`, `SetAndGet_SingleScope_ReturnsCachedValue` |
| Stale retry | ~5 | `Race_WriteDuringBuild_VersionMismatch_TriggersSingleRetry_ReturnsFreshValue_Cached` |
| Version mismatch | ~15 | `Get_VersionMismatch_RemovesEntryAndCountsMismatch`, `MultiInstance_SharedVersionStore_CrossInstanceBumpCausesMissOnOtherInstance` |
| TTL / eviction | ~10 | `Ttl_EntryExpiresAfterTtl_ReturnsNullAfterExpiry` |
| Canary gate | ~5 | `CanaryGate_AllowedWorkspace_UsesCachePath_FactoryCalledOnce` |
| Bounded channel | ~14 | `BoundedChannel_BestEffort_DropsEventsWhenFull`, `BoundedChannel_DisposeAsync_DrainsPendingEvents` |

## P0-7 Trace queue drop / flush latency

### Trace queue drop（已覆盖）

`BoundedChannelContextEventSinkTests.cs` 中 14 个测试全部通过：
- `BoundedChannel_BestEffort_DropsEventsWhenFull`：capacity=4，写 9 条 → DroppedCount=5，PendingCount=4
- `BoundedChannel_RecordsOtelCounters_ForDropErrorBatchEmit`：验证 OTel counters
- `BoundedChannel_WithRequiredAuditSink_NeverDropsAuditEventsUnderPressure`：Required sink 0 drop
- `BoundedChannel_BestEffort_EmitsInBatchesToInner`：batch=8 → 至少 2 批

### Trace flush latency（差距记录）

当前 `BoundedChannel_DisposeAsync_DrainsPendingEvents` 验证 drain 正确性（PendingCount=0），但无时间预算断言。
flush latency 时间门（类似 `ColdPath_InMemory_50Items_P95LatencyUnderGate`）尚未实现，列为后续补充。

## baseline / current 对比工作流

1. 变更前运行：`dotnet run -c Release --project benchmarks/ContextCore.Benchmarks -- --filter *`
2. 复制 `results/results/*-report-full.json` 为 `baseline.json`
3. 变更后运行同上，复制为 `current.json`
4. 用 BenchmarkDotNet `--diff baseline.json` 生成对比

## 待补充域指标

BenchmarkOutputConfig 注释中标记为待补充（需自定义 EventCounter）：
- File I/O bytes
- DB query count
- Cache hit/miss（测试已覆盖，benchmark 未埋点）
- Trace flush latency（测试已覆盖正确性，未覆盖延迟预算）
- trace write amplification
