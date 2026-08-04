```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26300.9032)
Unknown processor
.NET SDK 11.0.100-preview.6.26359.118
  [Host]     : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Job-NEKVUD : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

MaxIterationCount=25  MaxWarmupIterationCount=10  MinIterationCount=15  
MinWarmupIterationCount=3  

```
| Method           | ConcurrencyLevel | ItemCount | Mean     | Error   | StdDev  | Gen0      | Gen1      | Gen2     | Allocated   |
|----------------- |----------------- |---------- |---------:|--------:|--------:|----------:|----------:|---------:|------------:|
| **Build_Concurrent** | **1**                | **50**        | **186.4 ms** | **0.99 ms** | **0.93 ms** |         **-** |         **-** |        **-** |   **750.29 KB** |
| **Build_Concurrent** | **4**                | **50**        | **186.5 ms** | **0.93 ms** | **0.87 ms** |  **333.3333** |         **-** |        **-** |   **2745.3 KB** |
| **Build_Concurrent** | **16**               | **50**        | **185.7 ms** | **2.10 ms** | **1.96 ms** | **1333.3333** |  **666.6667** |        **-** | **11993.66 KB** |
| **Build_Concurrent** | **64**               | **50**        | **190.8 ms** | **3.82 ms** | **4.83 ms** | **5666.6667** | **1000.0000** | **666.6667** |  **47917.8 KB** |
