```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26300.8772)
Unknown processor
.NET SDK 10.0.301
  [Host]     : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Job-BZDVRE : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

IterationCount=5  WarmupCount=3  

```
| Method                                  | ItemCount | Mean          | Error          | StdDev        | Ratio | RatioSD | Gen0     | Gen1    | Allocated  | Alloc Ratio |
|---------------------------------------- |---------- |--------------:|---------------:|--------------:|------:|--------:|---------:|--------:|-----------:|------------:|
| **FileSystem_AppCacheMiss_OsFileCacheWarm** | **10**        |  **6,258.285 μs** |    **943.2194 μs** |   **244.9511 μs** | **1.001** |    **0.05** |  **93.7500** | **15.6250** |  **796.91 KB** |        **1.00** |
| FileSystem_CacheHit                     | 10        |      6.405 μs |      0.3309 μs |     0.0512 μs | 0.001 |    0.00 |   1.4648 |  0.0153 |      12 KB |        0.02 |
|                                         |           |               |                |               |       |         |          |         |            |             |
| **FileSystem_AppCacheMiss_OsFileCacheWarm** | **50**        | **19,015.138 μs** |  **3,303.5098 μs** |   **857.9110 μs** | **1.002** |    **0.06** | **187.5000** | **31.2500** | **1538.63 KB** |       **1.000** |
| FileSystem_CacheHit                     | 50        |      6.318 μs |      0.1468 μs |     0.0227 μs | 0.000 |    0.00 |   1.4648 |  0.0153 |      12 KB |       0.008 |
|                                         |           |               |                |               |       |         |          |         |            |             |
| **FileSystem_AppCacheMiss_OsFileCacheWarm** | **200**       | **72,199.353 μs** | **11,790.5343 μs** | **1,824.5982 μs** | **1.000** |    **0.03** | **500.0000** |       **-** | **4272.09 KB** |       **1.000** |
| FileSystem_CacheHit                     | 200       |      7.155 μs |      1.2129 μs |     0.1877 μs | 0.000 |    0.00 |   1.4648 |       - |      12 KB |       0.003 |
