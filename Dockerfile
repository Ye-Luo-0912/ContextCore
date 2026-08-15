# ContextCore Service 容器镜像。
#
# 构建：docker build -t contextcore-service:latest .
# 运行：docker run --rm -p 8080:8080 \
#         -e Storage__Provider=postgres \
#         -e Storage__PostgresConnectionString="Host=host.docker.internal;..." \
#         contextcore-service:latest
#
# 注意：默认配置启用 deepseek / pinai-openai 模型（ApiKeyRequired），启动必须设置
#       DEEPSEEK_API_KEY 与 PINAI_OPENAI_API_KEY；Deterministic 模式不实际调用模型，
#       占位值即可通过配置校验。
#
# 说明：
# - 采用多阶段构建：SDK 阶段编译 → aspnet 运行时阶段运行（镜像不含 SDK/编译器）。
# - 发布使用 linux-x64 RID + framework-dependent：产物含 ONNX 原生库的 linux-x64 负载，
#   镜像体积受控；ReadyToRun 可按部署需求加 -p:PublishReadyToRun=true（需 RID 匹配）。
# - 冷启动优化：运行时镜像层缓存稳定（仅依赖变更时重建），ReadyToRun 可进一步缩短 JIT 预热。
# - AOT/trimming 未启用：契约/DTO 广泛使用反射式 System.Text.Json 序列化与 DI ActivatorUtilities，
#   兼容矩阵明确前不开启（开启会因反射元数据裁剪导致运行时失败）。
# - 整树 COPY src/ 后 restore：.dockerignore 排除 bin/obj/tests 等，保证容器内
#   restore 资产不被宿主机产物覆盖；restore 需带 -r linux-x64 生成 RID 专属 assets。

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 复制全部源码（.dockerignore 已排除 bin/obj）并恢复依赖。
COPY src/ src/
RUN dotnet restore src/ContextCore.Service/ContextCore.Service.csproj -r linux-x64

# 发布（linux-x64 + framework-dependent）。
RUN dotnet publish src/ContextCore.Service/ContextCore.Service.csproj \
    -c Release -r linux-x64 --self-contained false \
    -p:TieredPGO=true \
    -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080
ENTRYPOINT ["dotnet", "ContextCore.Service.dll"]
