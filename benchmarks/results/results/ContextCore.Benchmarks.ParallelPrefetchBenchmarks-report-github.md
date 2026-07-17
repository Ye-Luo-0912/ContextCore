```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26300.8772)
Unknown processor
.NET SDK 10.0.301
  [Host]     : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Job-BZDVRE : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

IterationCount=5  WarmupCount=3  

```
| Method                                 | ItemCount | QueryDelayMs | Mean       | Error     | StdDev    | Ratio  | RatioSD | Gen0     | Gen1    | Allocated  | Alloc Ratio |
|--------------------------------------- |---------- |------------- |-----------:|----------:|----------:|-------:|--------:|---------:|--------:|-----------:|------------:|
| **NoDelay_ParallelPrefetch**               | **50**        | **0**            |   **1.138 ms** | **0.0798 ms** | **0.0207 ms** |   **1.00** |    **0.02** |  **95.7031** |  **9.7656** |  **795.82 KB** |        **1.00** |
| WithDelay_ParallelPrefetch             | 50        | 0            |   1.129 ms | 0.0939 ms | 0.0244 ms |   0.99 |    0.03 |  83.9844 |  7.8125 |  699.96 KB |        0.88 |
| WithDelay_Concurrent4_ParallelPrefetch | 50        | 0            |   4.835 ms | 0.1163 ms | 0.0302 ms |   4.25 |    0.07 | 359.3750 | 23.4375 | 2962.02 KB |        3.72 |
|                                        |           |              |            |           |           |        |         |          |         |            |             |
| **NoDelay_ParallelPrefetch**               | **50**        | **1**            |   **1.301 ms** | **0.6844 ms** | **0.1777 ms** |   **1.02** |    **0.18** |  **82.0313** |  **7.8125** |  **692.67 KB** |        **1.00** |
| WithDelay_ParallelPrefetch             | 50        | 1            | 186.981 ms | 4.2055 ms | 1.0921 ms | 146.04 |   19.10 |        - |       - |  700.93 KB |        1.01 |
| WithDelay_Concurrent4_ParallelPrefetch | 50        | 1            | 186.293 ms | 3.7302 ms | 0.5773 ms | 145.51 |   19.12 | 333.3333 |       - | 2798.72 KB |        4.04 |
|                                        |           |              |            |           |           |        |         |          |         |            |             |
| **NoDelay_ParallelPrefetch**               | **50**        | **5**            |   **1.392 ms** | **0.6292 ms** | **0.1634 ms** |   **1.01** |    **0.16** |  **89.8438** |  **7.8125** |  **739.27 KB** |        **1.00** |
| WithDelay_ParallelPrefetch             | 50        | 5            | 186.141 ms | 3.4425 ms | 0.8940 ms | 135.20 |   14.85 |        - |       - |  701.02 KB |        0.95 |
| WithDelay_Concurrent4_ParallelPrefetch | 50        | 5            | 185.905 ms | 4.7682 ms | 1.2383 ms | 135.03 |   14.84 | 333.3333 |       - | 2799.08 KB |        3.79 |
