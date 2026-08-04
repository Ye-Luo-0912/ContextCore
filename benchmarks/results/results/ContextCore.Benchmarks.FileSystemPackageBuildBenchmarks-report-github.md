```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26300.9032)
Unknown processor
.NET SDK 11.0.100-preview.6.26359.118
  [Host]     : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Job-NEKVUD : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

MaxIterationCount=25  MaxWarmupIterationCount=10  MinIterationCount=15  
MinWarmupIterationCount=3  

```
| Method                                  | ItemCount | Mean          | Error         | StdDev        | Ratio | RatioSD | Gen0     | Gen1   | Allocated  | Alloc Ratio |
|---------------------------------------- |---------- |--------------:|--------------:|--------------:|------:|--------:|---------:|-------:|-----------:|------------:|
| **FileSystem_AppCacheMiss_OsFileCacheWarm** | **10**        |  **4,691.039 μs** |    **81.2639 μs** |    **72.0383 μs** | **1.000** |    **0.02** |  **78.1250** | **7.8125** |  **663.96 KB** |        **1.00** |
| FileSystem_CacheHit                     | 10        |      5.091 μs |     0.2306 μs |     0.3078 μs | 0.001 |    0.00 |   1.4801 |      - |    12.2 KB |        0.02 |
|                                         |           |               |               |               |       |         |          |        |            |             |
| **FileSystem_AppCacheMiss_OsFileCacheWarm** | **50**        | **16,064.370 μs** |   **384.2480 μs** |   **512.9601 μs** | **1.001** |    **0.04** | **156.2500** |      **-** | **1399.61 KB** |       **1.000** |
| FileSystem_CacheHit                     | 50        |      5.051 μs |     0.0944 μs |     0.0883 μs | 0.000 |    0.00 |   1.4877 | 0.0076 |    12.2 KB |       0.009 |
|                                         |           |               |               |               |       |         |          |        |            |             |
| **FileSystem_AppCacheMiss_OsFileCacheWarm** | **200**       | **55,248.450 μs** | **1,442.1336 μs** | **1,925.2071 μs** | **1.001** |    **0.05** | **444.4444** |      **-** | **4134.85 KB** |       **1.000** |
| FileSystem_CacheHit                     | 200       |      5.696 μs |     0.4099 μs |     0.5472 μs | 0.000 |    0.00 |   1.4801 |      - |    12.2 KB |       0.003 |
