```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26300.8772)
Unknown processor
.NET SDK 11.0.100-preview.6.26359.118
  [Host]     : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Job-XZCWAQ : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

IterationCount=5  WarmupCount=3  

```
| Method                                  | ItemCount | Mean          | Error         | StdDev        | Ratio | RatioSD | Gen0     | Gen1   | Allocated  | Alloc Ratio |
|---------------------------------------- |---------- |--------------:|--------------:|--------------:|------:|--------:|---------:|-------:|-----------:|------------:|
| **FileSystem_AppCacheMiss_OsFileCacheWarm** | **10**        |  **5,497.611 μs** |   **369.1695 μs** |    **57.1294 μs** | **1.000** |    **0.01** |  **78.1250** | **7.8125** |  **649.44 KB** |        **1.00** |
| FileSystem_CacheHit                     | 10        |      5.939 μs |     1.3140 μs |     0.3412 μs | 0.001 |    0.00 |   1.4648 |      - |   12.19 KB |        0.02 |
|                                         |           |               |               |               |       |         |          |        |            |             |
| **FileSystem_AppCacheMiss_OsFileCacheWarm** | **50**        | **20,507.053 μs** | **1,934.9307 μs** |   **299.4327 μs** | **1.000** |    **0.02** | **156.2500** |      **-** | **1385.58 KB** |       **1.000** |
| FileSystem_CacheHit                     | 50        |      6.009 μs |     0.3873 μs |     0.0599 μs | 0.000 |    0.00 |   1.4877 | 0.0153 |   12.19 KB |       0.009 |
|                                         |           |               |               |               |       |         |          |        |            |             |
| **FileSystem_AppCacheMiss_OsFileCacheWarm** | **200**       | **76,396.153 μs** | **4,109.4437 μs** | **1,067.2095 μs** | **1.000** |    **0.02** | **500.0000** |      **-** | **4135.29 KB** |       **1.000** |
| FileSystem_CacheHit                     | 200       |      6.075 μs |     1.0285 μs |     0.2671 μs | 0.000 |    0.00 |   1.4877 | 0.0153 |   12.19 KB |       0.003 |
