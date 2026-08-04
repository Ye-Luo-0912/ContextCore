```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26300.9032)
Unknown processor
.NET SDK 11.0.100-preview.6.26359.118
  [Host]     : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Job-MBJNBE : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

InvocationCount=1  MaxIterationCount=25  MaxWarmupIterationCount=10  
MinIterationCount=15  MinWarmupIterationCount=3  UnrollFactor=1  

```
| Method               | Capacity | Mean         | Error       | StdDev      | Gen0      | Allocated   |
|--------------------- |--------- |-------------:|------------:|------------:|----------:|------------:|
| **WriteWithLruEviction** | **1000**     |  **25,784.7 μs** |    **683.2 μs** |    **888.4 μs** |         **-** |  **1433.38 KB** |
| InvalidateByScope    | 1000     |     511.7 μs |    166.0 μs |    221.6 μs |         - |    76.59 KB |
| MixedReadWrite       | 1000     |   9,997.4 μs |  1,132.8 μs |  1,512.2 μs |         - |   857.41 KB |
| **WriteWithLruEviction** | **10000**    | **444,218.3 μs** | **27,807.0 μs** | **37,121.6 μs** | **1000.0000** | **12782.66 KB** |
| InvalidateByScope    | 10000    |   2,955.0 μs |    373.1 μs |    498.1 μs |         - |   710.61 KB |
| MixedReadWrite       | 10000    |   7,783.2 μs |    321.3 μs |    382.5 μs |         - |   518.14 KB |
