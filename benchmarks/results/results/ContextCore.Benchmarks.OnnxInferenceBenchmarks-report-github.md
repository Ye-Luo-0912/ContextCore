```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26300.9032)
Unknown processor
.NET SDK 11.0.100-preview.6.26359.118
  [Host]     : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Job-NEKVUD : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

MaxIterationCount=25  MaxWarmupIterationCount=10  MinIterationCount=15  
MinWarmupIterationCount=3  

```
| Method                              | Categories   | BatchSize | Mean         | Error        | StdDev       | Median       | Ratio | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|------------------------------------ |------------- |---------- |-------------:|-------------:|-------------:|-------------:|------:|--------:|-------:|-------:|----------:|------------:|
| **InferAsync_DictionaryPath**           | **Dictionary**   | **1**         |    **324.17 ns** |    **12.974 ns** |    **17.320 ns** |    **314.83 ns** |  **1.00** |    **0.07** | **0.0305** |      **-** |     **256 B** |        **1.00** |
|                                     |              |           |              |              |              |              |       |         |        |        |           |             |
| **InferAsync_DictionaryPath**           | **Dictionary**   | **8**         |  **2,518.15 ns** |   **100.272 ns** |   **133.861 ns** |  **2,470.53 ns** |  **1.00** |    **0.07** | **0.1831** |      **-** |    **1544 B** |        **1.00** |
|                                     |              |           |              |              |              |              |       |         |        |        |           |             |
| **InferAsync_DictionaryPath**           | **Dictionary**   | **32**        |  **9,881.61 ns** |   **456.669 ns** |   **609.641 ns** |  **9,855.28 ns** |  **1.00** |    **0.09** | **0.7019** |      **-** |    **5960 B** |        **1.00** |
|                                     |              |           |              |              |              |              |       |         |        |        |           |             |
| **InferAsync_DictionaryPath**           | **Dictionary**   | **128**       | **38,059.86 ns** |   **734.766 ns** |   **980.891 ns** | **37,611.95 ns** |  **1.00** |    **0.04** | **2.8076** | **0.0610** |   **23624 B** |        **1.00** |
|                                     |              |           |              |              |              |              |       |         |        |        |           |             |
| **InferBatchAsync_ContinuousMemory**    | **FeatureBatch** | **1**         |     **87.02 ns** |     **1.819 ns** |     **2.301 ns** |     **87.19 ns** |     **?** |       **?** | **0.0143** |      **-** |     **120 B** |           **?** |
|                                     |              |           |              |              |              |              |       |         |        |        |           |             |
| **InferBatchAsync_ContinuousMemory**    | **FeatureBatch** | **8**         |    **360.66 ns** |    **22.196 ns** |    **29.631 ns** |    **346.51 ns** |     **?** |       **?** | **0.0544** |      **-** |     **456 B** |           **?** |
|                                     |              |           |              |              |              |              |       |         |        |        |           |             |
| **InferBatchAsync_ContinuousMemory**    | **FeatureBatch** | **32**        |  **1,018.03 ns** |     **6.993 ns** |     **6.541 ns** |  **1,017.35 ns** |     **?** |       **?** | **0.1907** |      **-** |    **1608 B** |           **?** |
|                                     |              |           |              |              |              |              |       |         |        |        |           |             |
| **InferBatchAsync_ContinuousMemory**    | **FeatureBatch** | **128**       |  **4,064.02 ns** |    **69.486 ns** |    **71.357 ns** |  **4,043.12 ns** |     **?** |       **?** | **0.7401** | **0.0153** |    **6216 B** |           **?** |
|                                     |              |           |              |              |              |              |       |         |        |        |           |             |
| **InferBatchAsync_LargeBatchSplitting** | **Splitting**    | **1**         | **26,650.37 ns** |   **325.679 ns** |   **288.706 ns** | **26,773.18 ns** |     **?** |       **?** | **2.6855** | **0.1221** |   **21903 B** |           **?** |
|                                     |              |           |              |              |              |              |       |         |        |        |           |             |
| **InferBatchAsync_LargeBatchSplitting** | **Splitting**    | **8**         | **27,126.45 ns** |   **397.838 ns** |   **352.673 ns** | **27,159.74 ns** |     **?** |       **?** | **2.6855** | **0.1221** |   **21907 B** |           **?** |
|                                     |              |           |              |              |              |              |       |         |        |        |           |             |
| **InferBatchAsync_LargeBatchSplitting** | **Splitting**    | **32**        | **22,907.19 ns** |   **336.859 ns** |   **315.098 ns** | **22,849.02 ns** |     **?** |       **?** | **2.6855** | **0.1221** |   **21898 B** |           **?** |
|                                     |              |           |              |              |              |              |       |         |        |        |           |             |
| **InferBatchAsync_LargeBatchSplitting** | **Splitting**    | **128**       | **25,267.45 ns** | **1,650.048 ns** | **2,202.767 ns** | **26,585.87 ns** |     **?** |       **?** | **2.6855** | **0.1221** |   **21899 B** |           **?** |
