```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26300.9032)
Unknown processor
.NET SDK 11.0.100-preview.6.26359.118
  [Host]     : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Job-NEKVUD : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

MaxIterationCount=25  MaxWarmupIterationCount=10  MinIterationCount=15  
MinWarmupIterationCount=3  

```
| Method                                 | ItemCount | QueryDelayMs | Mean         | Error       | StdDev      | Ratio  | RatioSD | Gen0     | Gen1   | Allocated  | Alloc Ratio |
|--------------------------------------- |---------- |------------- |-------------:|------------:|------------:|-------:|--------:|---------:|-------:|-----------:|------------:|
| **NoDelay_ParallelPrefetch**               | **50**        | **0**            |     **815.2 μs** |    **42.37 μs** |    **50.43 μs** |   **1.00** |    **0.08** |  **82.0313** | **7.8125** |  **672.51 KB** |        **1.00** |
| WithDelay_ParallelPrefetch             | 50        | 0            |     868.1 μs |    54.29 μs |    70.60 μs |   1.07 |    0.10 |  89.8438 | 1.9531 |  742.46 KB |        1.10 |
| WithDelay_Concurrent4_ParallelPrefetch | 50        | 0            |   3,313.1 μs |   187.45 μs |   230.20 μs |   4.08 |    0.36 | 363.2813 | 7.8125 | 2970.05 KB |        4.42 |
|                                        |           |              |              |             |             |        |         |          |        |            |             |
| **NoDelay_ParallelPrefetch**               | **50**        | **1**            |     **778.0 μs** |    **27.86 μs** |    **35.24 μs** |   **1.00** |    **0.06** |  **82.0313** | **7.8125** |  **672.51 KB** |        **1.00** |
| WithDelay_ParallelPrefetch             | 50        | 1            | 185,478.6 μs | 2,411.54 μs | 2,255.75 μs | 238.88 |   10.82 |        - |      - |  680.65 KB |        1.01 |
| WithDelay_Concurrent4_ParallelPrefetch | 50        | 1            | 186,478.4 μs |   586.78 μs |   548.88 μs | 240.17 |   10.53 | 333.3333 |      - | 2742.88 KB |        4.08 |
|                                        |           |              |              |             |             |        |         |          |        |            |             |
| **NoDelay_ParallelPrefetch**               | **50**        | **5**            |     **966.0 μs** |    **38.41 μs** |    **51.28 μs** |   **1.00** |    **0.07** |  **82.0313** | **7.8125** |   **672.5 KB** |        **1.00** |
| WithDelay_ParallelPrefetch             | 50        | 5            | 186,525.7 μs | 1,064.02 μs |   995.28 μs | 193.60 |    9.76 |        - |      - |  680.65 KB |        1.01 |
| WithDelay_Concurrent4_ParallelPrefetch | 50        | 5            | 186,597.6 μs |   724.49 μs |   677.69 μs | 193.67 |    9.73 | 333.3333 |      - | 2717.27 KB |        4.04 |
