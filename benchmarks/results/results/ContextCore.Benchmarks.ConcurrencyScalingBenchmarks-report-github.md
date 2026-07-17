```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26300.8772)
Unknown processor
.NET SDK 10.0.301
  [Host]     : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Job-BZDVRE : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

IterationCount=5  WarmupCount=3  

```
| Method           | ConcurrencyLevel | ItemCount | Mean     | Error    | StdDev  | Gen0      | Gen1      | Gen2     | Allocated   |
|----------------- |----------------- |---------- |---------:|---------:|--------:|----------:|----------:|---------:|------------:|
| **Build_Concurrent** | **1**                | **50**        | **185.3 ms** |  **4.39 ms** | **1.14 ms** |         **-** |         **-** |        **-** |   **701.79 KB** |
| **Build_Concurrent** | **4**                | **50**        | **185.7 ms** |  **1.43 ms** | **0.22 ms** |  **333.3333** |         **-** |        **-** |  **2802.64 KB** |
| **Build_Concurrent** | **16**               | **50**        | **184.9 ms** | **10.75 ms** | **2.79 ms** | **1333.3333** |  **666.6667** |        **-** | **12307.98 KB** |
| **Build_Concurrent** | **64**               | **50**        | **188.7 ms** | **34.52 ms** | **8.96 ms** | **5333.3333** | **1000.0000** | **666.6667** |    **45190 KB** |
