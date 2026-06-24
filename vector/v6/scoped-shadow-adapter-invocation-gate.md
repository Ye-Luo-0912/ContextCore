# Scoped Shadow Adapter Invocation Gate

生成: `2026-06-20T07:45:12.7193701+00:00`

## 调用摘要
- InvocationPassed: `True`
- AdapterType: `ScopedShadow`
- Allowlisted invocations: `1`
- Non-allowlisted invocations: `1`
- Allowlisted result: `False/0/0`
- Non-allowlisted result: `False/0/0`
- FormalSelectedSetChanged: `False`
- FormalOutputChanged: `0`
- FormalPackageWritten: `False`
- RuntimeMutated: `False`

## 诊断信息
- `allowlisted: adapter=ScopedShadow applied=False added=0 removed=0 trace=vector/trace\shadow-adapter\trace-v61-allowlisted-3c9002da33bf44c3939976cb8458e785.jsonl`
- `non-allowlisted: applied=False added=0 removed=0`

## 阻塞原因
- (empty)

范围影子适配器调用验证。Allowlisted 路径使用 ScopedShadow，non-allowlisted 保持 NoOp。所有不变量保持在 false/0。
