# Adapter No-op Binding Gate

生成: `2026-06-20T06:51:57.5961191+00:00`

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
- `request: op=smoke-test-d23476619928416d99831c4f582299d2 ws=smoke-ws col=smoke-col query=smoke query noop binding test baselineCount=3`
- `no-op applied=False added=0 removed=0`
- `shadow applied=False added=0 removed=0 tracePath=vector/trace\shadow-adapter\trace-smoke-test-d23476619928416d99831c4f582299d2.jsonl`

## 阻塞原因
- (empty)

空操作适配器绑定烟雾测试。所有不变量保持在 false/0。
