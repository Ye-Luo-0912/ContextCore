# Allowlisted Mainline Shadow Adapter Observation Gate

生成: `2026-06-22T01:52:30.0522453+00:00`

## 观察摘要
- ObservationPassed: `True`
- AllowlistedKey: `:`
- MainlineInvocations: `120`
- AllowlistedCount: `60` NonAllowlistedCount: `60`
- ShadowAdds: `0` ShadowRemoves: `0`
- TraceCompleteness: `100.00%`
- P50/P95: `0/0 ms`
- FormalSelectedSetChanged: `False`

## 诊断
- `allowlistKey=: (frequency=120)`
- `samples=120 mainline=120 allowlisted=60 nonAllowlisted=60`
- `shadowAdds=0 shadowRemoves=0`
- `traceCompleteness=100.00% p50=0ms p95=0ms`
- `allowlistedShadowDelta: generated (May be empty on optimal baseline)`

## 阻塞
- (empty)

V6.3 allowlisted mainline shadow observation。adapter 输出始终丢弃。
