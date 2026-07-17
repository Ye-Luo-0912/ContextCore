# R12-F.1 Benchmark Baseline

采集时间：2026-07-17
HEAD：dbf963f（R12.4A Post-Refactor Correctness Closure 完成后）
环境：Windows 11, .NET 10.0.9, BenchmarkDotNet v0.14.0, IterationCount=5 WarmupCount=3

## 摘要

37 个 benchmark，总耗时 4:55。完整报告：`results/` 子目录下的 `*-report-github.md` / `*-report-full.json` / `*-report.csv`。

## 关键指标

### PackageBuildBenchmarks（InMemory，Package 构建路径）

| Method | ItemCount | Mean | StdDev | Allocated | Alloc Ratio |
|---|---:|---:|---:|---:|---:|
| BuildDetailed_Cold | 10 | 1,422.8 μs | 149.0 μs | 488.3 KB | 1.00 |
| BuildDetailed_CacheHit | 10 | 7.1 μs | 0.1 μs | 12.23 KB | 0.03 |
| BuildDetailed_Cold | 50 | 2,846.6 μs | 94.6 μs | 924.54 KB | 1.00 |
| BuildDetailed_CacheHit | 50 | 7.5 μs | 0.2 μs | 12.38 KB | 0.01 |
| BuildDetailed_Cold | 200 | 5,823.6 μs | 448.9 μs | 1605.76 KB | 1.00 |
| BuildDetailed_CacheHit | 200 | 7.4 μs | 0.1 μs | 12.51 KB | 0.008 |

CacheHit/Cold 延迟比 ~0.003（~380x 加速），分配比 ~0.01（~75x 节省）。
并发 8 线程：Cold 50 项 20.4ms / 7396KB；CacheHit 50 项 60.3μs / 99.23KB。

### FileSystemPackageBuildBenchmarks（真实文件 I/O）

| Method | ItemCount | Mean | Allocated |
|---|---:|---:|---:|
| FileSystem_AppCacheMiss_OsFileCacheWarm | 10 | 6,258.3 μs | 796.91 KB |
| FileSystem_CacheHit | 10 | 6.4 μs | 12 KB |
| FileSystem_AppCacheMiss_OsFileCacheWarm | 50 | 19,015.1 μs | 1538.63 KB |
| FileSystem_CacheHit | 50 | 6.3 μs | 12 KB |
| FileSystem_AppCacheMiss_OsFileCacheWarm | 200 | 72,199.4 μs | 4272.09 KB |
| FileSystem_CacheHit | 200 | 7.2 μs | 12 KB |

OS 文件缓存预热后 CacheHit 仍 ~1000x 快于 Cold。

### ConcurrencyScalingBenchmarks（并发扩展，DelayedStore 1ms/query）

| ConcurrencyLevel | ItemCount | Mean | Allocated |
|---:|---:|---:|---:|
| 1 | 50 | 185.3 ms | 701.79 KB |
| 4 | 50 | 185.7 ms | 2802.64 KB |
| 16 | 50 | 184.9 ms | 12307.98 KB |
| 64 | 50 | 188.7 ms | 45190 KB |

1ms/query 延迟下并发 1→64 总耗时基本持平（并发已充分吸收 I/O 等待），分配随并发线性增长。

### ParallelPrefetchBenchmarks（并行预取）

| Method | QueryDelayMs | Mean | Allocated |
|---|---:|---:|---:|
| NoDelay_ParallelPrefetch | 0 | 1.14 ms | 795.82 KB |
| WithDelay_ParallelPrefetch | 1 | 187.0 ms | 700.93 KB |
| WithDelay_Concurrent4_ParallelPrefetch | 1 | 186.3 ms | 2798.72 KB |

### CacheChurnBenchmarks（InMemoryContextStateCache）

| Method | Capacity | Mean | Allocated |
|---|---:|---:|---:|
| WriteWithLruEviction | 1000 | 31.7 ms | 1425.59 KB |
| InvalidateByScope | 1000 | 0.72 ms | 76.3 KB |
| MixedReadWrite | 1000 | 11.1 ms | 836.16 KB |
| WriteWithLruEviction | 10000 | 408.2 ms | 12470.11 KB |
| InvalidateByScope | 10000 | 3.33 ms | 710.61 KB |
| MixedReadWrite | 10000 | 9.72 ms | 506.7 KB |

## baseline / current 对比工作流

1. 变更前运行：`dotnet run -c Release --project benchmarks/ContextCore.Benchmarks -- --filter *`
2. 复制 `results/results/*-report-full.json` 为 `baseline.json`
3. 变更后运行同上，复制为 `current.json`
4. 用 BenchmarkDotNet `--diff baseline.json` 生成对比

## 待补充域指标

BenchmarkOutputConfig 注释中标记为待补充（需自定义 EventCounter）：
- File I/O bytes
- DB query count
- Cache hit/miss
- trace write amplification
