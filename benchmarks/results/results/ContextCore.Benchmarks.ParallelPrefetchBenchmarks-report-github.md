```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26300.8772)
Unknown processor
.NET SDK 11.0.100-preview.6.26359.118
  [Host]     : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Job-XZCWAQ : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

IterationCount=5  WarmupCount=3  

```
| Method                                 | ItemCount | QueryDelayMs | Mean         | Error      | StdDev      | Ratio  | RatioSD | Gen0     | Gen1    | Allocated  | Alloc Ratio |
|--------------------------------------- |---------- |------------- |-------------:|-----------:|------------:|-------:|--------:|---------:|--------:|-----------:|------------:|
| **NoDelay_ParallelPrefetch**               | **50**        | **0**            |     **988.5 μs** |   **391.4 μs** |   **101.64 μs** |   **1.01** |    **0.13** |  **87.8906** |  **7.8125** |   **726.8 KB** |        **1.00** |
| WithDelay_ParallelPrefetch             | 50        | 0            |     925.0 μs |   408.6 μs |   106.10 μs |   0.94 |    0.13 |  93.7500 |  7.8125 |  768.51 KB |        1.06 |
| WithDelay_Concurrent4_ParallelPrefetch | 50        | 0            |   3,902.4 μs |   307.4 μs |    79.83 μs |   3.98 |    0.36 | 355.4688 | 35.1563 | 2912.18 KB |        4.01 |
|                                        |           |              |              |            |             |        |         |          |         |            |             |
| **NoDelay_ParallelPrefetch**               | **50**        | **1**            |     **976.6 μs** |   **279.7 μs** |    **72.63 μs** |   **1.00** |    **0.10** |  **80.0781** |  **7.8125** |  **658.03 KB** |        **1.00** |
| WithDelay_ParallelPrefetch             | 50        | 1            | 186,246.7 μs | 1,091.7 μs |   168.93 μs | 191.54 |   12.88 |        - |       - |  737.55 KB |        1.12 |
| WithDelay_Concurrent4_ParallelPrefetch | 50        | 1            | 186,154.1 μs | 4,253.8 μs | 1,104.71 μs | 191.45 |   12.84 |        - |       - | 2689.18 KB |        4.09 |
|                                        |           |              |              |            |             |        |         |          |         |            |             |
| **NoDelay_ParallelPrefetch**               | **50**        | **5**            |     **952.3 μs** |   **168.9 μs** |    **26.13 μs** |   **1.00** |    **0.03** |  **82.0313** |  **7.8125** |  **670.23 KB** |        **1.00** |
| WithDelay_ParallelPrefetch             | 50        | 5            | 186,711.9 μs | 3,554.2 μs |   923.02 μs | 196.18 |    4.77 |        - |       - |  669.13 KB |        1.00 |
| WithDelay_Concurrent4_ParallelPrefetch | 50        | 5            | 185,663.1 μs | 4,524.1 μs |   700.11 μs | 195.07 |    4.73 |        - |       - | 2665.29 KB |        3.98 |
