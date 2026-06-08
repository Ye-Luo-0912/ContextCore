# ContextCore 本地 Alpha 运行手册

生成时间：2026-05-25  
适用阶段：A0 Alpha 可用边界固化

## 1. 推荐运行模式

当前推荐：

```text
ContextCore.Service + FileSystem Storage + 项目内 context-core-data + ControlRoom 调试
```

当前不推荐：

- `memory` provider：仅用于测试、Demo、临时验证，不持久化。
- `postgres` provider：仍为 Experimental / Partial，完整契约补齐前不能作为 Service 后端启动。

## 2. 启动 Service

推荐命令：

```powershell
dotnet run --project src\ContextCore.Service\ContextCore.Service.csproj -- --storage filesystem
```

指定项目内数据目录：

```powershell
dotnet run --project src\ContextCore.Service\ContextCore.Service.csproj -- --storage filesystem --root .\context-core-data
```

服务启动后可访问：

```text
GET /api/status
GET /scalar/v1
```

## 3. 数据目录

默认数据目录：

```text
.\context-core-data
```

原则：

- 默认写入项目内专用目录。
- 只有显式配置绝对路径时才写到项目外。
- 不建议把测试数据和真实上下文资产混在同一个目录。

## 4. 私有配置目录

模型 API Key 和不便提交的配置读取自用户目录：

```text
%USERPROFILE%\.contextcore\.env
%USERPROFILE%\.contextcore\secrets.json
```

推荐使用 JSON：

```json
{
  "PrivateApiKeys": {
    "DEEPSEEK_API_KEY": "replace-with-your-local-key",
    "PINAI_OPENAI_API_KEY": "replace-with-your-local-key"
  }
}
```

注意：

- 不要把明文密钥写入项目仓库。
- 不要在日志和报告中输出明文密钥。
- 命令行参数优先级高于用户目录私有配置。

## 5. ControlRoom 常用命令

查看状态：

```powershell
dotnet run --project src\ContextCore.ControlRoom\ContextCore.ControlRoom.csproj -- status --root .\context-core-data
```

查看列表：

```powershell
dotnet run --project src\ContextCore.ControlRoom\ContextCore.ControlRoom.csproj -- list --root .\context-core-data
```

查看模型状态：

```powershell
dotnet run --project src\ContextCore.ControlRoom\ContextCore.ControlRoom.csproj -- model status --root .\context-core-data
```

导出调试报告：

```powershell
dotnet run --project src\ContextCore.ControlRoom\ContextCore.ControlRoom.csproj -- report export --out .\docs\controlroom-alpha-report.md --root .\context-core-data
```

## 6. 清理测试数据

仅在确认目录内没有真实上下文资产后清理：

```powershell
Remove-Item -LiteralPath .\context-core-data -Recurse -Force
```

建议：

- 真实样本评测使用独立目录。
- 临时 smoke test 使用独立目录。
- 清理前先确认路径为项目内目录。

## 7. 导入真实样本

当前建议路径：

```text
eval/contexts/
```

推荐先导入：

- 项目报告。
- TODO Roadmap。
- 架构讨论摘要。
- 真实中文聊天片段。
- 自动化流程日志。

导入后使用 ControlRoom 检查：

- 上下文条目数量。
- 记忆层状态。
- 检索 trace。
- package preview。

## 8. 当前不支持或不建议事项

- 不建议开放到公网。
- 不建议无 API key 暴露给非本机调用方。
- 不建议使用 `memory` 保存真实数据。
- 不允许把 `postgres` 误认为完整 Service provider。
- 尚未完成真实 PostgreSQL + pgvector 集成测试。
- 尚未完成完整备份恢复流程。
- 尚未完成真实中文评测集指标闭环。

## 9. 性能与效率注意事项

- `/api/status` 的文件系统 readiness 探针带短 TTL 缓存，避免高频状态刷新造成额外磁盘写压力。
- 真实评测和批量 embedding 应使用独立数据目录，避免影响当前工作数据。
- 大规模导入前应先建立小样本回归，确认 package 构建和检索质量。
- 后续性能基线应记录模型加载耗时、embedding 吞吐、检索延迟、package 构建延迟和 token waste。
