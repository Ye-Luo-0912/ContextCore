using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Service;
using ContextCore.Service.Endpoints;
using ContextCore.Service.Extensions;
using ContextCore.Service.Hosting;
using ContextCore.Service.Infrastructure;
using ContextCore.Service.Security;
using ContextCore.Core.Services;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Timeouts;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;

var normalizedArgs = ServiceCommandLine.Normalize(args);
var builder = WebApplication.CreateBuilder(normalizedArgs);
var privateConfiguration = UserPrivateConfiguration.Load(builder.Configuration);
if (normalizedArgs.Length > 0)
{
	// 显式命令行参数优先级最高，用于临时覆盖用户目录中的私有配置。
	builder.Configuration.AddCommandLine(normalizedArgs);
}

// ── 配置绑定 ─────────────────────────────────────────────────────────
var storageOptions = builder.Configuration
	.GetSection("Storage")
	.Get<StorageOptions>() ?? new StorageOptions();
var compressionOptions = builder.Configuration
	.GetSection("Compression")
	.Get<CompressionProviderOptions>() ?? new CompressionProviderOptions();
var securityOptions = builder.Configuration
	.GetSection("Security")
	.Get<SecurityOptions>() ?? new SecurityOptions();
var relationGovernanceProviderSwitchSection = builder.Configuration.GetSection("Storage:RelationGovernanceProviderSwitch");
var relationGovernanceProviderSwitchOptions = relationGovernanceProviderSwitchSection.Exists()
	? relationGovernanceProviderSwitchSection.Get<RelationGovernanceProviderSwitchOptions>() ?? new RelationGovernanceProviderSwitchOptions()
	: builder.Configuration.GetSection("RelationGovernance:ProviderSwitch").Get<RelationGovernanceProviderSwitchOptions>()
		?? new RelationGovernanceProviderSwitchOptions();
var embeddingProviderOptions = builder.Configuration
	.GetSection("EmbeddingProvider")
	.Get<EmbeddingProviderOptions>() ?? new EmbeddingProviderOptions();
builder.Services.AddSingleton(storageOptions);
builder.Services.AddSingleton(compressionOptions);
builder.Services.AddSingleton(securityOptions);
builder.Services.AddSingleton(relationGovernanceProviderSwitchOptions);
builder.Services.AddSingleton(embeddingProviderOptions);
builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(embeddingProviderOptions));
builder.Services.Configure<JobWorkerOptions>(builder.Configuration.GetSection("JobWorker"));
builder.Services.Configure<ShortTermMaintenanceOptions>(builder.Configuration.GetSection("ShortTermMaintenance"));
// RelationReconciliation worker 配置。默认 Enabled=false；启用前需确认 Storage:Provider=postgres
// （IRelationOutboxStore 仅 Postgres provider 注册；其他 provider 时 worker 启动后即退出 no-op）。
builder.Services.Configure<RelationReconciliationOptions>(builder.Configuration.GetSection("RelationReconciliation"));
// Package Template Cache Canary 配置。默认关闭（Enabled=false）；
// 启用时仅缓存 AllowedWorkspaces 列出的工作空间，并通过 ContextStateCacheAccessor.canaryGate 控制按工作空间粒度缓存。
builder.Services.Configure<PackageTemplateCacheOptions>(builder.Configuration.GetSection("PackageTemplateCache"));
builder.Services.AddHostedService<ContextJobWorker>();
builder.Services.AddHostedService<ShortTermMemoryMaintenanceWorker>();
// RelationReconciliation worker。默认 Enabled=false；启用时周期性调度 outbox pending 记录。
// FileSystem/InMemory provider 时 worker 内部检测 IRelationOutboxStore=null 后立即退出（no-op）。
builder.Services.AddHostedService<RelationReconciliationWorker>();
// LearningMaterializationWorker 由 AddContextCoreRuntime 统一注册（按 Profile 分发）。
// 配置绑定 LearningMaterializationOptions 也由 AddContextCoreRuntime 完成。
builder.Services.AddSingleton<ContextCoreMetrics>();
builder.Services.AddRequestTimeouts(options =>
{
	options.DefaultPolicy = new RequestTimeoutPolicy
	{
		Timeout = TimeSpan.FromSeconds(15),
		TimeoutStatusCode = StatusCodes.Status503ServiceUnavailable
	};
});

// ── 服务注册 ─────────────────────────────────────────────────────────
builder.Services
	.AddOpenApi(options =>
	{
		options.AddDocumentTransformer((doc, _, _) =>
		{
			doc.Info.Title = "ContextCore Service API";
			doc.Info.Version = "v1";
			doc.Info.Description = "上下文管理服务：摄取、记忆、打包与索引。";
			return Task.CompletedTask;
		});
	})
	.AddContextStorage(storageOptions)
	// 统一入口 AddContextCoreRuntime（唯一运行时配置入口，替代旧双入口）。
	// 该方法内部按 ContextCoreRuntime:ModelMode 选择 ModelExecutionOptions 调用 AddContextCore，
	// 再按 ContextCoreRuntime:Profile 分发 HostedService / Run Lease / Canary 注册。
	// 必须在 AddContextStorage 之后调用（依赖其注册的 IModelArtifactRegistry / IAgentRunStore 等）。
	.AddContextCoreRuntime(builder.Configuration)
	.AddContextModelGateway(builder.Configuration)
	.AddEmbeddingProviders(embeddingProviderOptions)
	// 安全框架注册：WorkspaceContext / RBAC / API Key Store（含轮换）/ Tool Authorizer /
	// Workspace Quota / Audit Retention。默认实现为进程内版本，生产应替换为持久化实现。
	// 默认所有开关关闭（向后兼容），通过 appsettings.json Security:Rbac:Enforce /
	// Security:RateLimit:Enabled / Security:ApiKeyRotation:EnableStaticKeyRotation 等显式启用。
	.AddContextCoreSecurity(securityOptions)
	.AddContextCoreRateLimiter(securityOptions)
	.AddContextCoreApiKeyPurgeWorker(securityOptions);

// ── 可观测性（OpenTelemetry，按 Observability:Enabled 条件启用）─────────
var otlpEndpoint = builder.Configuration["Observability:OtlpEndpoint"];
var otelEnabled = builder.Configuration.GetValue<bool>("Observability:Enabled");
if (otelEnabled && !string.IsNullOrWhiteSpace(otlpEndpoint))
{
	builder.Services.AddOpenTelemetry()
		.WithMetrics(m => m
			.AddAspNetCoreInstrumentation()
			.AddMeter("ContextCore.*")
			.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint)))
		.WithTracing(t => t
			.AddSource("ContextCore.*")
			.AddAspNetCoreInstrumentation()
			.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint)));
}

// TODO-GRPC: 后期迁移至 gRPC 时，在此处注册 builder.Services.AddGrpc() 并映射 GrpcServices/ 下的服务

// ── CORS ─────────────────────────────────────────────────────────────
// 空列表：不注册践源策略（默认拒绝所有践源请求）。
// "*"：允许所有来源（不建议生产）。
// 具体来源：只允许指定地址（推荐）。
const string CorsPolicyName = "ContextCoreCors";
if (securityOptions.AllowedOrigins.Count > 0)
{
	builder.Services.AddCors(options =>
	{
		options.AddPolicy(CorsPolicyName, policy =>
		{
			if (securityOptions.AllowedOrigins.Contains("*", StringComparer.Ordinal))
			{
				policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
			}
			else
			{
				policy
					.WithOrigins([.. securityOptions.AllowedOrigins])
					.AllowAnyMethod()
					.AllowAnyHeader();
			}
		});
	});
}

// ── 构建应用 ─────────────────────────────────────────────────────────
var app = builder.Build();

// 中间件顺序（重要）：
//   RateLimiter → RequestTimeouts → CORS → ApiKey → WorkspaceContext → AuditLog → Endpoint
// RateLimiter 最先：在认证前拒绝超限请求（节省下游资源）。
// ApiKey 在 WorkspaceContext 之前：WorkspaceContext 依赖 ApiKey 已写入的 ApiKeyId。
// AuditLog 在 WorkspaceContext 之后：审计日志需要 workspace_id（由 WorkspaceContext 填充）。
if (securityOptions.RateLimit.Enabled)
{
        app.UseRateLimiter();
}
app.UseRequestTimeouts();
if (securityOptions.AllowedOrigins.Count > 0)
{
        app.UseCors(CorsPolicyName);
}
app.UseMiddleware<ApiKeyMiddleware>();
app.UseMiddleware<WorkspaceContextMiddleware>();
app.UseMiddleware<AuditLogMiddleware>();
// 请求阶段生产准入门（ProductionHA）：运行时实时探针失败时对业务请求返回 503。
// 位于 AuditLog 之后——被拒绝的请求仍经过认证与审计；健康/诊断路径豁免。
app.UseMiddleware<ProductionAdmissionMiddleware>();
// 请求阶段 workspace 配额门禁（Security:Quota:Enabled=true 时生效）：配额已耗尽的
// workspace 创建 Agent Run 返回 429。同样位于 AuditLog 之后（被拒请求仍留审计轨迹）。
app.UseMiddleware<WorkspaceQuotaMiddleware>();

// ── OpenAPI / Scalar UI ──────────────────────────────────────────────
// MapOpenApi 提供 /openapi/v1.json 规范文档；Scalar 将其渲染为交互式 UI
app.MapOpenApi();
app.MapScalarApiReference(options =>
{
	options.Title = "ContextCore Service";
	options.Theme = ScalarTheme.DeepSpace;
	options.DefaultHttpClient = new(ScalarTarget.Http, ScalarClient.Http11);
});

// 访问根路径直接跳转到 Scalar UI，避免浏览器看到 404
app.MapGet("/", () => Results.Redirect("/scalar/v1"))
	.ExcludeFromDescription();

// ── 路由注册 ─────────────────────────────────────────────────────────
app
	.MapHealthEndpoints()
	.MapStatusEndpoints()
	.MapAdminEndpoints()
	.MapContextEndpoints()
	.MapRetrievalEndpoints()
	.MapMemoryEndpoints()
	.MapPackageEndpoints()
	.MapCompressionEndpoints()
	.MapJobEndpoints()
	.MapRelationEndpoints()
	.MapConstraintEndpoints()
	.MapLearningEndpoints()
	.MapProvenanceEndpoints()
	.MapVectorEndpoints()
	.MapModelEndpoints()
	.MapModelControlPlaneEndpoints()
	.MapUtilityLedgerEndpoints()
	.MapProductionRuntimeEndpoints()
	.MapAgentExecutionEndpoints()
	.MapCanaryEmergencyEndpoints()
	.MapAdaptiveRetrievalEndpoints();

app.MapGet("/health", () => Results.Ok(new { status = "ok", utc = DateTimeOffset.UtcNow }))
	.WithTags("Health")
	.WithName("HealthCheck")
	.WithSummary("服务健康检查（已小成本保留，推荐使用 /api/health/live）");

// ── 启动日志 ─────────────────────────────────────────────────────────
var logger = app.Services.GetRequiredService<ILogger<Program>>();
// ResolvedRootPath 已展开环境变量并转为绝对路径，便于直观确认数据写入位置
var rootPath = storageOptions.ResolvedRootPath;
var server = app.Services.GetRequiredService<IServer>();

logger.LogInformation("ContextCore.Service 启动");
logger.LogInformation(
	"用户私有配置目录: {DirectoryPath}",
	privateConfiguration.DirectoryPath);
logger.LogInformation(
	"用户私有 JSON 配置: {JsonPath} ({Status}, privateApiKeys={Count})",
	privateConfiguration.JsonPath,
	privateConfiguration.JsonExists ? "已加载" : "未找到",
	privateConfiguration.LoadedJsonApiKeyCount);
logger.LogInformation(
	"用户私有 env 文件: {EnvPath} ({Status}, loaded={Count})",
	privateConfiguration.EnvPath,
	privateConfiguration.EnvExists ? "已读取" : "未找到",
	privateConfiguration.LoadedEnvironmentVariableCount);
logger.LogInformation("存储提供商: {Provider}", storageOptions.Provider);
logger.LogInformation("压缩提供商: {Provider}", compressionOptions.Provider);
if (securityOptions.RequireApiKey)
{
	logger.LogInformation(
		"API Key 认证: 已启用（头名称: {Header}，Key 已配置: {Configured}）",
		securityOptions.ApiKeyHeaderName,
		!string.IsNullOrWhiteSpace(securityOptions.ApiKey));
}
else
{
	logger.LogWarning("API Key 认证: 已禁用（RequireApiKey=false），仅限受信任内网或本地开发使用。");
}
if (securityOptions.AllowedOrigins.Count == 0)
{
	logger.LogInformation("CORS: 未配置 AllowedOrigins，跨源请求将被拒绝。");
}
else if (securityOptions.AllowedOrigins.Contains("*", StringComparer.Ordinal))
{
	logger.LogWarning("CORS: AllowedOrigins=[\"*\"]，允许所有来源，仅限开发/内网场景。");
}
else
{
	logger.LogInformation("CORS: 已启用，允许来源: {Origins}", string.Join(", ", securityOptions.AllowedOrigins));
}
if (storageOptions.IsFileSystem)
{
	logger.LogInformation("存储根目录: {RootPath}", rootPath);
}
else if (storageOptions.IsPostgres)
{
	logger.LogInformation("PostgreSQL 连接字符串已配置（env 变量展开后长度={Len}）。",
		storageOptions.ResolvedPostgresConnectionString.Length);
}
else
{
	logger.LogInformation("存储根目录: {RootPath} (内存存储不使用，仅显示配置解析结果)", rootPath);
	logger.LogWarning(
		"当前使用内存存储（--storage memory），进程重启后数据将全部丢失，仅用于测试。");
}

app.Lifetime.ApplicationStarted.Register(() =>
{
	var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
	var serviceUrls = addresses is { Count: > 0 }
		? string.Join(", ", addresses)
		: string.Join(", ", app.Urls);
	if (string.IsNullOrWhiteSpace(serviceUrls))
	{
		serviceUrls = "(未配置)";
	}

	logger.LogInformation("服务地址: {ServiceUrls}", serviceUrls);

	// 检测是否绑定到非 localhost 地址且未启用 API Key 校验
	var isExternalBinding = addresses?.Any(a =>
		!a.Contains("localhost", StringComparison.OrdinalIgnoreCase)
		&& !a.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase)
		&& !a.Contains("::1", StringComparison.OrdinalIgnoreCase)) ?? false;

	if (isExternalBinding && !securityOptions.RequireApiKey)
	{
		logger.LogWarning(
			"[安全警告] 服务绑定到外部地址 {Urls}，但 API Key 认证未启用。" +
			"请在生产环境中将 Security:RequireApiKey 设为 true 并配置 Security:ApiKey。",
			serviceUrls);
	}
});

// ── Production Admission 校验（ProductionHA 强制项从 warning 升为 error）────────
// 生产准入门：ProductionHA 下，任一强制项不满足（API Key / 显式 Workspace / RBAC /
// Approval 与高风险 Tool 覆盖 / Tool Schema / 原生 Tool Calling 路由 / Model Slot /
// Hydration 管道 / Worker 集群）时记录 Critical 并中止进程，防止半配置环境静默上线。
// 在 ApplicationStarted 后执行，确保所有 Worker 已启动（worker-fleet-started 可判定）。
if (app.Services.GetRequiredService<ContextCoreRuntimeOptions>().Profile == RuntimeProfile.ProductionHA)
{
	app.Lifetime.ApplicationStarted.Register(() =>
	{
		_ = RunProductionAdmissionAsync(app, logger);
	});
}

// 生产准入校验执行器：任一强制项不满足时记录 Critical 并停止应用。
static async Task RunProductionAdmissionAsync(WebApplication app, ILogger<Program> logger)
{
	try
	{
		var validator = app.Services.GetRequiredService<ProductionAdmissionValidator>();
		var report = await validator.ValidateAsync(CancellationToken.None).ConfigureAwait(false);
		var failed = report.Checks
			.Where(c => c.Status == ProductionAdmissionCheckStatus.Fail)
			.ToList();
		foreach (var check in report.Checks)
		{
			if (check.Status == ProductionAdmissionCheckStatus.Fail)
			{
				logger.LogCritical("[ADMISSION] {CheckName}: {Message}", check.Name, check.Message);
			}
			else
			{
				logger.LogInformation("[ADMISSION] {CheckName}: {Message}", check.Name, check.Message);
			}
		}

		if (!report.AllPassed)
		{
			logger.LogCritical(
				"[FATAL] Production Admission 未通过（{FailedCount}/{Total}）。服务将中止。",
				failed.Count, report.Checks.Count);
			await app.StopAsync().ConfigureAwait(false);
		}
		else
		{
			logger.LogInformation(
				"Production Admission 通过：{Total} 项强制项全部满足。",
				report.Checks.Count);
		}
	}
	catch (Exception ex)
	{
		logger.LogCritical(ex, "[FATAL] Production Admission 校验异常。服务将中止。");
		await app.StopAsync().ConfigureAwait(false);
	}
}

// ── PostgreSQL 启动连接、schema bootstrap 与 version 验证（fail-fast）────────────
// 在 app.Run() 前先执行 SELECT 1 确认 Postgres 可达；然后（若 AutoBootstrap=true）应用幂等 baseline migration
// 打破“缺 schema → 服务退出 → 无法访问迁移 HTTP 接口”自锁；最后校验 schema version 是否与代码期望一致。
// 任一环节失败则 LogCritical 并中止进程。这是 B1 §9.2 fail-fast 保护，避免数据库不可达或 schema 过期时
// 服务静默启动但存储全部报错。超时设为 30 秒（schema 校验与迁移比 SELECT 1 慢）。
if (storageOptions.IsPostgres)
{
	try
	{
		using var startupCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var pgFactory = app.Services.GetRequiredService<PostgresConnectionFactory>();
		var (pgOk, pgError) = await pgFactory.PingAsync(startupCts.Token);
		if (!pgOk)
		{
			logger.LogCritical(
				"[FATAL] PostgreSQL 连接验证失败：{Error}。" +
				"请确认 Storage:PostgresConnectionString 配置正确且数据库服务可达。服务将中止。",
				pgError);
			await app.StopAsync();
			return;
		}

		var migrationRunner = app.Services.GetRequiredService<PostgresMigrationRunner>();

		// AutoBootstrap=true（默认）时先应用幂等 baseline migration。
		// 新数据库 schema 缺失会被自动创建，打破缺 schema → 服务退出 → 无法访问迁移接口的自锁。
		// 已存在 schema 的数据库上 MigrateAsync 是 no-op（CREATE TABLE IF NOT EXISTS / ADD COLUMN IF NOT EXISTS）。
		// 设 AutoBootstrap=false 回退到原 fail-fast 行为，DBA 严格管控 schema 时使用。
		if (storageOptions.AutoBootstrap)
		{
			logger.LogInformation(
				"AutoBootstrap 启用，开始应用 PostgreSQL baseline migration（幂等：已存在 schema 视为 no-op）。");
			await migrationRunner.MigrateAsync(startupCts.Token);
			logger.LogInformation("PostgreSQL baseline migration 应用完成。");
		}

		// 连接可达 + bootstrap 完成后，进一步校验 schema version，确保表结构与代码期望一致
		var schemaReport = await migrationRunner.VerifySchemaAsync(startupCts.Token);
		var schemaOutOfDate = schemaReport.MissingRequiredTableCount > 0
			|| schemaReport.MissingIndexCount > 0
			|| schemaReport.CurrentSchemaVersion != PostgresMigrationRunner.SchemaVersion;
		if (schemaOutOfDate)
		{
			logger.LogCritical(
				"[FATAL] PostgreSQL schema 验证失败。服务将中止。" +
				"当前版本：{CurrentVersion}，期望版本：{ExpectedVersion}。" +
				"缺失必需表数量：{MissingTableCount}，缺失索引数量：{MissingIndexCount}。" +
				"缺失必需表：{MissingTables}。" +
				"诊断信息：{Diagnostics}。" +
				"AutoBootstrap={AutoBootstrap} 时已尝试应用 baseline migration 但 schema 仍不匹配——" +
				"可能为代码版本与已存在 schema 不兼容（如降级运行），或 AutoBootstrap=false 且 schema 未手工初始化。" +
				"处理建议：1) 升级到与已存在 schema 兼容的代码版本；2) 确认 Storage:AutoBootstrap=true（默认）；" +
				"3) 通过独立工具或容器入口脚本执行 schema 迁移后再启动服务。",
				schemaReport.CurrentSchemaVersion,
				PostgresMigrationRunner.SchemaVersion,
				schemaReport.MissingRequiredTableCount,
				schemaReport.MissingIndexCount,
				string.Join(", ", schemaReport.MissingRequiredTables),
				string.Join(", ", schemaReport.Diagnostics),
				storageOptions.AutoBootstrap);
			await app.StopAsync();
			return;
		}
		logger.LogInformation(
			"PostgreSQL 启动验证成功：连接可达，schema version={CurrentVersion}。",
			schemaReport.CurrentSchemaVersion);
	}
	catch (Exception ex)
	{
		logger.LogCritical(ex,
			"[FATAL] PostgreSQL 启动验证异常。服务将中止。");
		await app.StopAsync();
		return;
	}
}

// ── Embedding Provider 启动 readiness 检查 ────────────────────────────
// 验证配置的 embedding provider 是否可用：维度校验、模型文件存在性检查。
// 检查失败时输出明确警告，但不阻止服务启动（embedding 为可选能力）。
{
	var embOptions = app.Services.GetRequiredService<EmbeddingProviderOptions>();
	if (embOptions.Enabled && !string.Equals(embOptions.ProviderType, EmbeddingProviderTypes.Disabled, StringComparison.OrdinalIgnoreCase))
	{
		if (embOptions.Dimension <= 0)
		{
			logger.LogWarning(
				"[Embedding] Provider={ProviderType} Dimension={Dimension} 无效，embedding 服务不可用。",
				embOptions.ProviderType, embOptions.Dimension);
		}
		else if (string.Equals(embOptions.ProviderType, EmbeddingProviderTypes.OnnxLocal, StringComparison.OrdinalIgnoreCase))
		{
			if (string.IsNullOrWhiteSpace(embOptions.ModelPath) || !File.Exists(embOptions.ModelPath))
			{
				logger.LogWarning(
					"[Embedding] OnnxLocal provider 模型文件不存在：ModelPath={ModelPath}。" +
					"向量召回通道将不可用。请通过 EmbeddingProvider:ModelPath 配置有效的 ONNX 模型路径。",
					embOptions.ModelPath ?? "(未配置)");
			}
			else if (!embOptions.IsSemanticRetrieval)
			{
				logger.LogWarning(
					"[Embedding] OnnxLocal provider 已配置但 IsSemanticRetrieval=false。" +
					"向量召回通道将不启用。如需语义检索，请设置 EmbeddingProvider:IsSemanticRetrieval=true。");
			}
			else
			{
				logger.LogInformation(
					"[Embedding] OnnxLocal provider 就绪：Model={Model}, Dimension={Dimension}, IsSemanticRetrieval=true。",
					embOptions.EmbeddingModel, embOptions.Dimension);
			}
		}
		else if (string.Equals(embOptions.ProviderType, EmbeddingProviderTypes.DeterministicHash, StringComparison.OrdinalIgnoreCase))
		{
			logger.LogInformation(
				"[Embedding] DeterministicHash provider 就绪：Dimension={Dimension}, IsSemanticRetrieval={IsSemantic}。" +
				"注意：DeterministicHash 不是语义检索，仅用于可重复基础设施测试和预览。",
				embOptions.Dimension, embOptions.IsSemanticRetrieval);
		}
	}
	else
	{
		logger.LogInformation("[Embedding] Embedding 服务已禁用（ProviderType=Disabled 或 Enabled=false）。");
	}
}

app.Run();

internal static class ServiceCommandLine
{
	public static string[] Normalize(string[] args)
	{
		var normalized = new List<string>(args.Length);

		for (var i = 0; i < args.Length; i++)
		{
			var arg = args[i];
			if (TryMapInlineOption(arg, "--root=", $"--{FileStorageOptions.RootPathConfigurationKey}", normalized)
				|| TryMapInlineOption(arg, "--storage=", "--Storage:Provider", normalized))
			{
				continue;
			}

			if (IsOption(arg, "--root") && i + 1 < args.Length)
			{
				normalized.Add($"--{FileStorageOptions.RootPathConfigurationKey}");
				normalized.Add(args[++i]);
				continue;
			}

			if (IsOption(arg, "--storage") && i + 1 < args.Length)
			{
				normalized.Add("--Storage:Provider");
				normalized.Add(args[++i]);
				continue;
			}

			// --api-key-env <VAR_NAME>：从环境变量读取 API Key，避免在命令行历史中留下密钥。
			// 这是推荐的注入方式，命令行参数中不会回显 key 值。
			if (IsOption(arg, "--api-key-env") && i + 1 < args.Length)
			{
				var envVar = args[++i];
				var apiKey = Environment.GetEnvironmentVariable(envVar);
				if (!string.IsNullOrWhiteSpace(apiKey))
				{
					normalized.Add("--Security:ApiKey");
					normalized.Add(apiKey);
				}
				// 不回显 envVar 名称到命令行参数，避免暴露环境变量命名
				continue;
			}

			// --api-key <value>：直接传递 API Key 值。不推荐使用，会在命令行历史中留下密钥。
			// SecurityOptions 的启动日志只输出布尔值，不会回显 key 值。
			if (IsOption(arg, "--api-key") && i + 1 < args.Length)
			{
				normalized.Add("--Security:ApiKey");
				normalized.Add(args[++i]);
				continue;
			}

			normalized.Add(arg);
		}

		return normalized.ToArray();
	}

	private static bool TryMapInlineOption(
		string arg,
		string prefix,
		string mappedName,
		ICollection<string> output)
	{
		if (!arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		output.Add(mappedName);
		output.Add(arg[prefix.Length..]);
		return true;
	}

	private static bool IsOption(string arg, string name)
	{
		return string.Equals(arg, name, StringComparison.OrdinalIgnoreCase);
	}
}

public partial class Program
{
}
