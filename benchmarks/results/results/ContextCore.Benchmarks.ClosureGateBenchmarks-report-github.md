```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26300.8935)
Unknown processor
.NET SDK 11.0.100-preview.6.26359.118
  [Host]     : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Job-KAVDII : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

IterationCount=1  LaunchCount=1  WarmupCount=1  

```
| Method                         | ItemCount | Mean       | Error | Ratio | StoreQueryCalls | StoreGetCalls | RouterCalls | ProviderCalls | Gen0     | Gen1    | Allocated  | Alloc Ratio |
|------------------------------- |---------- |-----------:|------:|------:|----------------:|--------------:|------------:|--------------:|---------:|--------:|-----------:|------------:|
| **LegacyRetrieval**                | **10**        |   **309.7 μs** |    **NA** |  **1.00** |               **-** |             **-** |           **-** |             **-** |  **28.3203** |  **3.9063** |  **239.28 KB** |        **1.00** |
| LegacyPackageBuild             | 10        | 1,672.2 μs |    NA |  5.40 |               - |             - |           - |             - |  54.6875 |  7.8125 |  476.34 KB |        1.99 |
| V2Retrieval_100Percent         | 10        |   443.9 μs |    NA |  1.43 |               2 |             0 |           1 |             6 |  18.5547 |  1.9531 |  154.37 KB |        0.65 |
| V2PackageBuild_100Percent      | 10        |   491.8 μs |    NA |  1.59 |               2 |             0 |           1 |             7 |  38.0859 |  6.8359 |  316.79 KB |        1.32 |
| SampledShadowRetrieval_Rate0   | 10        |   450.5 μs |    NA |  1.45 |               2 |             0 |           1 |             6 |  18.5547 |  0.9766 |  154.37 KB |        0.65 |
| SampledShadowRetrieval_Rate100 | 10        | 1,452.5 μs |    NA |  4.69 |               2 |             0 |           1 |             6 |  56.6406 |  5.8594 |  466.06 KB |        1.95 |
|                                |           |            |       |       |                 |               |             |               |          |         |            |             |
| **LegacyRetrieval**                | **50**        | **1,232.5 μs** |    **NA** |  **1.00** |               **-** |             **-** |           **-** |             **-** | **119.1406** | **64.4531** |  **987.51 KB** |        **1.00** |
| LegacyPackageBuild             | 50        | 2,409.7 μs |    NA |  1.96 |               - |             - |           - |             - | 105.4688 | 27.3438 |  862.37 KB |        0.87 |
| V2Retrieval_100Percent         | 50        |   605.0 μs |    NA |  0.49 |               2 |             0 |           1 |             6 |  25.3906 |  1.9531 |  220.81 KB |        0.22 |
| V2PackageBuild_100Percent      | 50        |   929.3 μs |    NA |  0.75 |               2 |             0 |           1 |             7 |  62.5000 | 13.6719 |  522.75 KB |        0.53 |
| SampledShadowRetrieval_Rate0   | 50        |   457.1 μs |    NA |  0.37 |               2 |             0 |           1 |             6 |  26.3672 |  1.9531 |   220.1 KB |        0.22 |
| SampledShadowRetrieval_Rate100 | 50        | 3,579.6 μs |    NA |  2.90 |               2 |             0 |           1 |             6 | 179.6875 | 15.6250 | 1488.27 KB |        1.51 |
|                                |           |            |       |       |                 |               |             |               |          |         |            |             |
| **LegacyRetrieval**                | **200**       | **2,182.0 μs** |    **NA** |  **1.00** |               **-** |             **-** |           **-** |             **-** | **199.2188** | **89.8438** | **1640.88 KB** |        **1.00** |
| LegacyPackageBuild             | 200       | 4,966.1 μs |    NA |  2.28 |               - |             - |           - |             - | 179.6875 | 39.0625 | 1498.51 KB |        0.91 |
| V2Retrieval_100Percent         | 200       |   877.5 μs |    NA |  0.40 |               2 |             0 |           1 |             6 |  54.6875 |  3.9063 |  448.75 KB |        0.27 |
| V2PackageBuild_100Percent      | 200       | 1,786.1 μs |    NA |  0.82 |               2 |             0 |           1 |             7 |  93.7500 | 19.5313 |  791.49 KB |        0.48 |
| SampledShadowRetrieval_Rate0   | 200       |   886.1 μs |    NA |  0.41 |               2 |             0 |           1 |             6 |  54.6875 |  3.9063 |   448.9 KB |        0.27 |
| SampledShadowRetrieval_Rate100 | 200       | 4,846.5 μs |    NA |  2.22 |               2 |             0 |           1 |             6 | 289.0625 | 31.2500 | 2391.17 KB |        1.46 |
