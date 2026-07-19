```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26300.8772)
Unknown processor
.NET SDK 11.0.100-preview.6.26359.118
  [Host]     : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Job-ADWXJE : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

InvocationCount=1  IterationCount=5  UnrollFactor=1  
WarmupCount=3  

```
| Method               | Capacity | Mean         | Error        | StdDev      | Gen0      | Allocated   |
|--------------------- |--------- |-------------:|-------------:|------------:|----------:|------------:|
| **WriteWithLruEviction** | **1000**     |  **33,444.1 μs** |  **15,221.3 μs** |  **3,952.9 μs** |         **-** |  **1434.23 KB** |
| InvalidateByScope    | 1000     |     781.4 μs |     609.4 μs |    158.3 μs |         - |    79.45 KB |
| MixedReadWrite       | 1000     |  13,023.1 μs |  10,562.5 μs |  2,743.0 μs |         - |   852.89 KB |
| **WriteWithLruEviction** | **10000**    | **367,630.7 μs** | **127,240.2 μs** | **33,043.9 μs** | **1000.0000** | **12783.13 KB** |
| InvalidateByScope    | 10000    |   2,937.8 μs |   3,958.6 μs |    612.6 μs |         - |   711.46 KB |
| MixedReadWrite       | 10000    |   9,439.2 μs |   1,722.3 μs |    266.5 μs |         - |   523.42 KB |
