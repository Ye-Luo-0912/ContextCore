```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26300.8772)
Unknown processor
.NET SDK 11.0.100-preview.6.26359.118
  [Host]     : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Job-XZCWAQ : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

IterationCount=5  WarmupCount=3  

```
| Method           | ConcurrencyLevel | ItemCount | Mean     | Error    | StdDev  | Gen0      | Gen1      | Gen2     | Allocated   |
|----------------- |----------------- |---------- |---------:|---------:|--------:|----------:|----------:|---------:|------------:|
| **Build_Concurrent** | **1**                | **50**        | **186.2 ms** |  **2.62 ms** | **0.68 ms** |         **-** |         **-** |        **-** |   **669.04 KB** |
| **Build_Concurrent** | **4**                | **50**        | **186.5 ms** |  **2.21 ms** | **0.57 ms** |         **-** |         **-** |        **-** |  **2692.14 KB** |
| **Build_Concurrent** | **16**               | **50**        | **185.3 ms** |  **8.57 ms** | **2.22 ms** | **1333.3333** |  **666.6667** |        **-** | **11766.14 KB** |
| **Build_Concurrent** | **64**               | **50**        | **189.0 ms** | **10.92 ms** | **2.84 ms** | **5666.6667** | **1000.0000** | **666.6667** | **47016.72 KB** |
