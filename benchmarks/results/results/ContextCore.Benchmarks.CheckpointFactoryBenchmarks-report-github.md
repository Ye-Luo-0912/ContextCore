```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26300.9032)
Unknown processor
.NET SDK 11.0.100-preview.6.26359.118
  [Host]     : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Job-NEKVUD : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

MaxIterationCount=25  MaxWarmupIterationCount=10  MinIterationCount=15  
MinWarmupIterationCount=3  

```
| Method                 | Categories | CommittedResultCount | Mean      | Error     | StdDev    | Ratio | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|----------------------- |----------- |--------------------- |----------:|----------:|----------:|------:|--------:|-------:|-------:|----------:|------------:|
| **CreateCheckpoint_Delta** | **Delta**      | **1**                    |  **1.962 μs** | **0.0551 μs** | **0.0717 μs** |     **?** |       **?** | **0.4730** |      **-** |   **3.88 KB** |           **?** |
|                        |            |                      |           |           |           |       |         |        |        |           |             |
| **CreateCheckpoint_Delta** | **Delta**      | **10**                   |  **2.349 μs** | **0.0803 μs** | **0.1044 μs** |     **?** |       **?** | **0.4654** |      **-** |   **3.84 KB** |           **?** |
|                        |            |                      |           |           |           |       |         |        |        |           |             |
| **CreateCheckpoint_Delta** | **Delta**      | **100**                  |  **4.188 μs** | **0.1547 μs** | **0.2065 μs** |     **?** |       **?** | **0.4578** |      **-** |   **3.87 KB** |           **?** |
|                        |            |                      |           |           |           |       |         |        |        |           |             |
| **CreateCheckpoint_Full**  | **Full**       | **1**                    |  **2.372 μs** | **0.0622 μs** | **0.0830 μs** |  **1.00** |    **0.05** | **0.5226** |      **-** |   **4.29 KB** |        **1.00** |
|                        |            |                      |           |           |           |       |         |        |        |           |             |
| **CreateCheckpoint_Full**  | **Full**       | **10**                   |  **6.011 μs** | **0.1553 μs** | **0.2019 μs** |  **1.00** |    **0.05** | **1.1749** | **0.0076** |   **9.61 KB** |        **1.00** |
|                        |            |                      |           |           |           |       |         |        |        |           |             |
| **CreateCheckpoint_Full**  | **Full**       | **100**                  | **42.211 μs** | **2.3364 μs** | **3.0380 μs** |  **1.01** |    **0.10** | **7.6904** | **0.7324** |  **63.13 KB** |        **1.00** |
