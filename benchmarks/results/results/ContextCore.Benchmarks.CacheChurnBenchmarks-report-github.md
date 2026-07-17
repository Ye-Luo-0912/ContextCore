```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26300.8772)
Unknown processor
.NET SDK 10.0.301
  [Host]     : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Job-WJFGVL : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

InvocationCount=1  IterationCount=5  UnrollFactor=1  
WarmupCount=3  

```
| Method               | Capacity | Mean         | Error       | StdDev      | Gen0      | Allocated   |
|--------------------- |--------- |-------------:|------------:|------------:|----------:|------------:|
| **WriteWithLruEviction** | **1000**     |  **31,693.0 μs** | **19,483.2 μs** |  **3,015.1 μs** |         **-** |  **1425.59 KB** |
| InvalidateByScope    | 1000     |     724.8 μs |    556.6 μs |    144.6 μs |         - |     76.3 KB |
| MixedReadWrite       | 1000     |  11,147.0 μs |    643.8 μs |    167.2 μs |         - |   836.16 KB |
| **WriteWithLruEviction** | **10000**    | **408,192.4 μs** | **83,293.0 μs** | **21,630.9 μs** | **1000.0000** | **12470.11 KB** |
| InvalidateByScope    | 10000    |   3,333.8 μs |  2,910.4 μs |    755.8 μs |         - |   710.61 KB |
| MixedReadWrite       | 10000    |   9,717.5 μs |  4,605.6 μs |    712.7 μs |         - |    506.7 KB |
