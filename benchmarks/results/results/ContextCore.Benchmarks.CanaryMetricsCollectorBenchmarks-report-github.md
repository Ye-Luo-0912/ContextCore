```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26300.9032)
Unknown processor
.NET SDK 11.0.100-preview.6.26359.118
  [Host]     : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Job-NEKVUD : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

MaxIterationCount=25  MaxWarmupIterationCount=10  MinIterationCount=15  
MinWarmupIterationCount=3  

```
| Method                             | Categories | ObservationCount | Mean        | Error     | StdDev     | Gen0     | Gen1     | Allocated  |
|----------------------------------- |----------- |----------------- |------------:|----------:|-----------:|---------:|---------:|-----------:|
| **GetAggregatedMetrics**               | **Aggregate**  | **100**              |    **13.88 μs** |  **0.743 μs** |   **0.966 μs** |   **5.0049** |   **0.5493** |   **40.94 KB** |
| GetAggregatedMetrics_AfterEviction | Aggregate  | 100              |    13.06 μs |  0.455 μs |   0.542 μs |   5.0049 |   0.5493 |   40.94 KB |
| **GetAggregatedMetrics**               | **Aggregate**  | **1000**             |   **123.61 μs** | **10.147 μs** |  **13.546 μs** |  **34.1797** |  **16.4795** |     **280 KB** |
| GetAggregatedMetrics_AfterEviction | Aggregate  | 1000             |   105.20 μs |  1.602 μs |   1.338 μs |  34.1797 |  16.4795 |     280 KB |
| **GetAggregatedMetrics**               | **Aggregate**  | **10000**            | **1,446.75 μs** | **97.911 μs** | **130.708 μs** | **326.1719** | **107.4219** | **2670.63 KB** |
| GetAggregatedMetrics_AfterEviction | Aggregate  | 10000            | 1,438.13 μs | 64.974 μs |  86.739 μs | 326.1719 | 107.4219 | 2670.63 KB |
|                                    |            |                  |             |           |            |          |          |            |
| **RecordObservations**                 | **Record**     | **100**              |    **14.62 μs** |  **1.672 μs** |   **2.232 μs** |   **4.6387** |   **0.5035** |   **37.91 KB** |
| **RecordObservations**                 | **Record**     | **1000**             |   **120.28 μs** |  **5.032 μs** |   **6.717 μs** |  **33.8135** |  **16.8457** |  **276.97 KB** |
| **RecordObservations**                 | **Record**     | **10000**            | **1,348.17 μs** | **75.258 μs** |  **95.177 μs** | **326.1719** | **107.4219** | **2667.59 KB** |
