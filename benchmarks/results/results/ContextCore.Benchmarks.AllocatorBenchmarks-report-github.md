```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26300.8935)
Unknown processor
.NET SDK 11.0.100-preview.6.26359.118
  [Host]     : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Job-OHFTIC : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

IterationCount=1  LaunchCount=1  WarmupCount=1  
Categories=MMR  

```
| Method     | CandidateCount | Mean         | Error | Gen0   | Allocated |
|----------- |--------------- |-------------:|------:|-------:|----------:|
| **Mmr_Rerank** | **10**             |     **207.4 ns** |    **NA** | **0.0486** |     **408 B** |
| **Mmr_Rerank** | **100**            |  **17,717.1 ns** |    **NA** | **0.3357** |    **2928 B** |
| **Mmr_Rerank** | **1000**           | **131,278.2 ns** |    **NA** | **0.9766** |    **9400 B** |
