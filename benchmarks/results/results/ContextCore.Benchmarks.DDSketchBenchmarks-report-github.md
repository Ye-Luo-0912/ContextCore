```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26300.8935)
Unknown processor
.NET SDK 11.0.100-preview.6.26359.118
  [Host]     : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Job-NIMFAK : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

IterationCount=1  LaunchCount=1  WarmupCount=1  
Categories=Add  

```
| Method  | ValueCount | Mean       | Error | Gen0   | Gen1   | Allocated |
|-------- |----------- |-----------:|------:|-------:|-------:|----------:|
| **AddMany** | **100**        |   **2.012 μs** |    **NA** | **0.5569** | **0.0038** |   **4.58 KB** |
| **AddMany** | **1000**       |  **16.918 μs** |    **NA** | **2.6550** | **0.0916** |  **21.85 KB** |
| **AddMany** | **10000**      | **146.940 μs** |    **NA** | **5.6152** | **0.4883** |  **47.03 KB** |
