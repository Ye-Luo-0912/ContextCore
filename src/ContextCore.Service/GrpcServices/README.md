# GrpcServices — gRPC 迁移预留层

> 当前传输层为 **HTTP Minimal API**，保留此目录用于后期 gRPC 迁移。

## 迁移步骤（后期执行）

1. 在 `ContextCore.Service.csproj` 中添加：
   ```xml
   <PackageReference Include="Grpc.AspNetCore" Version="*" />
   ```

2. 在 `src/Protos/` 下创建 `.proto` 定义文件：
   - `context.proto`
   - `memory.proto`
   - `package.proto`

3. 在本目录下创建对应服务实现：
   - `ContextGrpcService.cs`（替代 `Endpoints/ContextEndpoints.cs`）
   - `MemoryGrpcService.cs`（替代 `Endpoints/MemoryEndpoints.cs`）
   - `PackageGrpcService.cs`（替代 `Endpoints/PackageEndpoints.cs`）

4. 在 `Program.cs` 的 `// TODO-GRPC` 注释处替换为：
   ```csharp
   builder.Services.AddGrpc();
   // ...
   app.MapGrpcService<ContextGrpcService>();
   app.MapGrpcService<MemoryGrpcService>();
   app.MapGrpcService<PackageGrpcService>();
   ```

5. 删除 `Endpoints/` 目录下的 HTTP 端点文件。
