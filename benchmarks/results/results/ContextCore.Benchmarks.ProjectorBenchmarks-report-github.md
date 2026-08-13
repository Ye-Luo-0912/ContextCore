```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26300.9032)
Unknown processor
.NET SDK 11.0.100-preview.6.26359.118
  [Host]     : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Job-NEKVUD : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

MaxIterationCount=25  MaxWarmupIterationCount=10  MinIterationCount=15  
MinWarmupIterationCount=3  

```
| Method            | Categories | CandidateCount | Mean         | Error       | StdDev      | Ratio | RatioSD | Gen0    | Gen1    | Allocated | Alloc Ratio |
|------------------ |----------- |--------------- |-------------:|------------:|------------:|------:|--------:|--------:|--------:|----------:|------------:|
| **Project_Package**   | **Package**    | **10**             |   **2,615.4 ns** |   **129.60 ns** |   **168.52 ns** |     **?** |       **?** |  **1.2589** |  **0.0191** |  **10.31 KB** |           **?** |
|                   |            |                |              |             |             |       |         |         |         |           |             |
| **Project_Package**   | **Package**    | **100**            |  **13,722.2 ns** |   **258.28 ns** |   **287.08 ns** |     **?** |       **?** |  **6.8054** |  **0.7019** |  **55.59 KB** |           **?** |
|                   |            |                |              |             |             |       |         |         |         |           |             |
| **Project_Package**   | **Package**    | **1000**           | **153,664.9 ns** | **4,867.32 ns** | **6,328.89 ns** |     **?** |       **?** | **59.8145** | **20.0195** | **490.38 KB** |           **?** |
|                   |            |                |              |             |             |       |         |         |         |           |             |
| **Project_Retrieval** | **Retrieval**  | **10**             |     **789.3 ns** |    **65.72 ns** |    **87.73 ns** |  **1.01** |    **0.15** |  **0.4377** |  **0.0029** |   **3.58 KB** |        **1.00** |
|                   |            |                |              |             |             |       |         |         |         |           |             |
| **Project_Retrieval** | **Retrieval**  | **100**            |   **6,975.3 ns** |   **465.88 ns** |   **621.93 ns** |  **1.01** |    **0.12** |  **2.9297** |  **0.1907** |  **23.97 KB** |        **1.00** |
|                   |            |                |              |             |             |       |         |         |         |           |             |
| **Project_Retrieval** | **Retrieval**  | **1000**           |  **89,410.2 ns** | **1,759.67 ns** | **2,349.11 ns** |  **1.00** |    **0.04** | **27.8320** |  **7.8125** | **227.88 KB** |        **1.00** |
