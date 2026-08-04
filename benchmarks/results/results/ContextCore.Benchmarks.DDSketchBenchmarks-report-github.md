```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26300.9032)
Unknown processor
.NET SDK 11.0.100-preview.6.26359.118
  [Host]     : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Job-NEKVUD : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

MaxIterationCount=25  MaxWarmupIterationCount=10  MinIterationCount=15  
MinWarmupIterationCount=3  

```
| Method                | Categories | ValueCount | Mean       | Error      | StdDev     | Gen0    | Gen1   | Allocated |
|---------------------- |----------- |----------- |-----------:|-----------:|-----------:|--------:|-------:|----------:|
| **AddMany**               | **Add**        | **100**        |   **1.942 μs** |  **0.1221 μs** |  **0.1630 μs** |  **0.5608** | **0.0038** |   **4.59 KB** |
| **AddMany**               | **Add**        | **1000**       |  **17.849 μs** |  **0.5125 μs** |  **0.6664 μs** |  **2.6550** | **0.1526** |  **21.86 KB** |
| **AddMany**               | **Add**        | **10000**      | **158.311 μs** |  **4.7570 μs** |  **6.1854 μs** |  **5.6152** | **0.4883** |  **47.04 KB** |
|                       |            |            |            |            |            |         |        |           |
| **AddAndGetP95**          | **Query**      | **100**        |   **3.366 μs** |  **0.1178 μs** |  **0.1310 μs** |  **0.7591** | **0.0076** |   **6.22 KB** |
| QueryP95MultipleTimes | Query      | 100        |   8.520 μs |  0.1337 μs |  0.1117 μs |  1.5411 | 0.0153 |  12.66 KB |
| **AddAndGetP95**          | **Query**      | **1000**       |  **23.394 μs** |  **0.8131 μs** |  **1.0283 μs** |  **3.2654** | **0.1526** |   **26.9 KB** |
| QueryP95MultipleTimes | Query      | 1000       |  43.274 μs |  1.8921 μs |  2.3930 μs |  5.7373 | 0.3052 |  46.96 KB |
| **AddAndGetP95**          | **Query**      | **10000**      | **169.195 μs** |  **5.3240 μs** |  **6.1311 μs** |  **6.5918** | **0.7324** |  **55.58 KB** |
| QueryP95MultipleTimes | Query      | 10000      | 217.236 μs | 11.2251 μs | 12.9268 μs | 10.7422 | 0.9766 |  89.64 KB |
