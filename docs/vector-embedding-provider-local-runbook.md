# Vector Embedding Provider Local Runbook

本 runbook 用于本地验证 `OnnxLocal` embedding provider。该 provider 只用于 vector index preview / shadow eval，不接正式 retrieval、不改 scoring、不改 `PackingPolicy`，也不让 vector 进入 package 输出。

## 本地文件要求

- 不提交 ONNX 模型文件。
- 不提交 tokenizer / vocab 文件。
- `OnnxLocal` 默认关闭。
- `ModelPath` / `TokenizerPath` 必须通过本地私有配置或 CLI 参数显式传入。
- 建议将模型放到仓库外的本地目录，例如 `%USERPROFILE%\.contextcore\models\embedding\`。

可参考仓库根目录的 `appsettings.VectorEmbedding.sample.json`。该文件只提供字段示例，不代表真实模型路径。

## Smoke Test

先执行 smoke test，确认 provider 能完成 tokenizer、ONNX inference、dimension、normalization 和 batch embedding 检查：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval embedding-provider-smoke `
  --provider onnx-local `
  --model-path %USERPROFILE%\.contextcore\models\embedding\model.onnx `
  --tokenizer-path %USERPROFILE%\.contextcore\models\embedding\vocab.txt `
  --embedding-model local-onnx-embedding `
  --dimension 512
```

默认输出：

- `eval/embedding-provider-smoke-report.json`
- `eval/embedding-provider-smoke-report.md`

如果模型或 tokenizer 缺失，命令会生成失败报告和 diagnostics，但不会写 vector index。

## Provider-scoped Reindex

smoke test 通过后，才能执行 provider-scoped reindex：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval vector-reindex-apply --confirm `
  --provider onnx-local `
  --model-path %USERPROFILE%\.contextcore\models\embedding\model.onnx `
  --tokenizer-path %USERPROFILE%\.contextcore\models\embedding\vocab.txt `
  --embedding-model local-onnx-embedding `
  --dimension 512
```

如果 provider / model / dimension / normalization 与旧 index 不一致，planner 会标记 stale / requires reindex。query preview 会按 provider/model scoped search，不会静默混用 deterministic-hash 旧索引。

## Quality Re-run

reindex 完成后运行 profile sweep 和 shadow eval：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval vector-query-profile-sweep `
  --provider onnx-local `
  --model-path %USERPROFILE%\.contextcore\models\embedding\model.onnx `
  --tokenizer-path %USERPROFILE%\.contextcore\models\embedding\vocab.txt `
  --embedding-model local-onnx-embedding `
  --dimension 512

dotnet run --project src\ContextCore.ControlRoom -- eval vector-query-shadow-eval `
  --provider onnx-local `
  --model-path %USERPROFILE%\.contextcore\models\embedding\model.onnx `
  --tokenizer-path %USERPROFILE%\.contextcore\models\embedding\vocab.txt `
  --embedding-model local-onnx-embedding `
  --dimension 512
```

重点检查：

- `eval/vector-embedding-provider-comparison.md`
- `eval/vector-embedding-quality-baseline.md`
- `eval/vector-query-profile-sweep.md`
- `eval/vector-query-shadow-eval.md`

进入下一阶段前，至少需要：

- `SimilaritySeparation` 明显高于 deterministic hash baseline。
- `MustNotHitRiskAfterPolicy = 0`。
- `LifecycleRiskAfterPolicy = 0`。
- `NoCandidateCount` 不异常升高。
- provider comparison 中 `onnx-local` 不再出现 `ProviderUnavailable` / `ModelFileMissing` / `TokenizerUnavailable` / `OnnxSessionFailed`。

## 回退

如需回到 deterministic baseline：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval vector-query-profile-sweep --provider deterministic-hash
dotnet run --project src\ContextCore.ControlRoom -- eval vector-query-shadow-eval --provider deterministic-hash
```

该回退仍只影响离线 vector preview / shadow eval，不影响正式 retrieval/package。
