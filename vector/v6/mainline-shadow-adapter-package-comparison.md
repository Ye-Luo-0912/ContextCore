# Mainline Shadow Adapter Package Comparison

生成: `2026-06-20T07:43:44.4187043+00:00`

## 比较摘要
- ComparisonPassed: `True`
- MainlineInvocationCount: `120`
- AllowlistedCount: `0` NonAllowlistedCount: `120`
- TotalBaselineCandidates: `600`
- ShadowAdds: `0` ShadowRemoves: `0`
- BaselineTokenTotal: `16735` ShadowTokenTotal: `16735`
- TokenDeltaMax: `0`
- TraceCompleteness: `100.00%`
- P50/P95 Latency: `0/1 ms`
- FormalSelectedSetChanged: `False`
- RuntimeMutated: `False`

## 诊断
- `samples=120 mainlineInvocations=120`
- `allowlisted=0 nonAllowlisted=120`
- `totalBaselineCandidates=600`
- `totalShadowAdds=0 totalShadowRemoves=0`
- `tracesWritten=120/120`
- `nonAllowlistedScope: confirmed NoOp behavior (no add/remove from non-allowlisted)`

## 阻塞
- (empty)

V6.2 mainline shadow adapter package comparison。适配器输出始终丢弃，baseline 继续作为正式结果。
