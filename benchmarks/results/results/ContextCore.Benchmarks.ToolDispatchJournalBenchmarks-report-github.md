```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26300.9032)
Unknown processor
.NET SDK 11.0.100-preview.6.26359.118
  [Host]     : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Job-NEKVUD : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

MaxIterationCount=25  MaxWarmupIterationCount=10  MinIterationCount=15  
MinWarmupIterationCount=3  

```
| Method                       | EntryCount | Mean        | Error       | StdDev      | Gen0    | Gen1   | Allocated |
|----------------------------- |----------- |------------:|------------:|------------:|--------:|-------:|----------:|
| **StateMachine_FullCycle**       | **1**          |    **445.9 ns** |    **13.90 ns** |    **18.08 ns** |  **0.3920** | **0.0033** |    **3.2 KB** |
| StateMachine_PrepareAndQuery | 1          |    242.2 ns |    11.98 ns |    15.99 ns |  0.2770 | 0.0019 |   2.27 KB |
| **StateMachine_FullCycle**       | **10**         |  **2,872.8 ns** |    **84.98 ns** |   **107.47 ns** |  **1.6975** | **0.0229** |  **13.89 KB** |
| StateMachine_PrepareAndQuery | 10         |    623.1 ns |    17.50 ns |    21.49 ns |  0.5522 | 0.0048 |   4.52 KB |
| **StateMachine_FullCycle**       | **100**        | **37,993.8 ns** | **3,596.43 ns** | **4,801.14 ns** | **17.0288** | **1.8616** | **139.21 KB** |
| StateMachine_PrepareAndQuery | 100        |  8,832.7 ns |   270.54 ns |   351.78 ns |  5.4779 | 0.3815 |  44.76 KB |
