# Adapter No-op Binding Smoke

生成: `2026-06-20T02:48:41.9531944+00:00`

## 烟雾测试摘要
- SmokePassed: `True`
- InvocationCount: `2`
- AddCount: `0`
- RemoveCount: `0`
- NoOpType: `NoOpContextRetrievalAdapter`
- ShadowType: `NoOpShadowRetrievalAdapter`
- FormalSelectedSetChanged: `False`
- FormalOutputChanged: `0`
- FormalPackageWritten: `False`
- RuntimeMutated: `False`

## 诊断信息
- `adapter name: NoOp`
- `shadow adapter name: NoOpShadow`
- `request: op=smoke-test-1a540c18fece4a3ab90879fd88b77fa8 ws=smoke-ws col=smoke-col query=smoke query noop binding test baselineCount=3`
- `no-op applied=False added=0 removed=0`
- `shadow applied=False added=0 removed=0 tracePath=vector/trace\shadow-adapter\trace-smoke-test-1a540c18fece4a3ab90879fd88b77fa8.jsonl`

## 阻塞原因
- (empty)

空操作适配器绑定烟雾测试。所有不变量保持在 false/0。
