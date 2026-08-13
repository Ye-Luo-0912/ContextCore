```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26300.9032)
Unknown processor
.NET SDK 11.0.100-preview.6.26359.118
  [Host]     : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Job-NEKVUD : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

MaxIterationCount=25  MaxWarmupIterationCount=10  MinIterationCount=15  
MinWarmupIterationCount=3  

```
| Method | CandidateCount | Mean       | Error      | StdDev     | Gen0    | Gen1    | Allocated |
|------- |--------------- |-----------:|-----------:|-----------:|--------:|--------:|----------:|
| **Merge**  | **10**             |   **2.184 μs** |  **0.2016 μs** |  **0.2691 μs** |  **0.7782** |  **0.0076** |   **6.36 KB** |
| **Merge**  | **100**            |  **26.709 μs** |  **2.0151 μs** |  **2.6901 μs** | **11.0779** |  **1.7395** |   **90.6 KB** |
| **Merge**  | **1000**           | **288.716 μs** | **10.6393 μs** | **13.4553 μs** | **87.8906** | **32.7148** | **718.54 KB** |
