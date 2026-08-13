```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26300.9032)
Unknown processor
.NET SDK 11.0.100-preview.6.26359.118
  [Host]     : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Job-NEKVUD : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

MaxIterationCount=25  MaxWarmupIterationCount=10  MinIterationCount=15  
MinWarmupIterationCount=3  Categories=Store  

```
| Method                     | CheckpointCount | Mean       | Error    | StdDev   | Gen0   | Gen1   | Allocated |
|--------------------------- |---------------- |-----------:|---------:|---------:|-------:|-------:|----------:|
| **CheckpointStore_SaveAndGet** | **1**               |   **180.4 ns** | **17.35 ns** | **23.16 ns** | **0.1519** | **0.0005** |   **1.24 KB** |
| **CheckpointStore_SaveAndGet** | **10**              |   **494.8 ns** | **13.17 ns** | **17.58 ns** | **0.2632** | **0.0019** |   **2.16 KB** |
| **CheckpointStore_SaveAndGet** | **50**              | **2,621.4 ns** | **50.21 ns** | **51.56 ns** | **1.0338** | **0.0267** |   **8.45 KB** |
