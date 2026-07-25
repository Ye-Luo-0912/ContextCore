```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26300.8935)
Unknown processor
.NET SDK 11.0.100-preview.6.26359.118
  [Host]     : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Job-DKSFTA : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

IterationCount=1  LaunchCount=1  WarmupCount=1  

```
| Method                 | EntryCount | Mean        | Error | Gen0    | Gen1   | Allocated |
|----------------------- |----------- |------------:|------:|--------:|-------:|----------:|
| **StateMachine_FullCycle** | **1**          |    **333.0 ns** |    **NA** |  **0.2246** | **0.0010** |   **1.84 KB** |
| **StateMachine_FullCycle** | **10**         |  **2,879.7 ns** |    **NA** |  **1.1253** | **0.0114** |   **9.22 KB** |
| **StateMachine_FullCycle** | **100**        | **27,236.7 ns** |    **NA** | **12.4207** | **1.2512** | **101.49 KB** |
