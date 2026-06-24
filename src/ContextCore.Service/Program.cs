using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Service;
using ContextCore.Service.Endpoints;
using ContextCore.Service.Extensions;
using ContextCore.Service.Hosting;
using ContextCore.Service.Infrastructure;
using ContextCore.Core.Services;
using ContextCore.Storage.FileSystem;
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
var retrievalAttentionRerankOptions = builder.Configuration
	.GetSection("Retrieval:AttentionRerank")
	.Get<RetrievalAttentionRerankOptions>() ?? new RetrievalAttentionRerankOptions();
var retrievalPlanningOptions = builder.Configuration
	.GetSection("Retrieval:Planning")
	.Get<RetrievalPlanningOptions>() ?? new RetrievalPlanningOptions();
var learningRankerShadowOptions = builder.Configuration
	.GetSection("Learning:RankerShadow")
	.Get<LearningRankerShadowOptions>() ?? new LearningRankerShadowOptions();
var learningRouterShadowOptions = builder.Configuration
	.GetSection("Learning:RouterShadow")
	.Get<LearningRouterShadowOptions>() ?? new LearningRouterShadowOptions();
var graphExpansionShadowSection = builder.Configuration.GetSection("Graph:ExpansionShadow");
var learningGraphExpansionShadowOptions = graphExpansionShadowSection.Exists()
	? graphExpansionShadowSection.Get<LearningGraphExpansionShadowOptions>() ?? new LearningGraphExpansionShadowOptions()
	: builder.Configuration.GetSection("Learning:GraphExpansionShadow").Get<LearningGraphExpansionShadowOptions>()
		?? new LearningGraphExpansionShadowOptions();
var graphExpansionApplySection = builder.Configuration.GetSection("Graph:ExpansionApply");
var graphExpansionApplyServiceOptions = graphExpansionApplySection.Exists()
	? graphExpansionApplySection.Get<GraphExpansionApplyServiceOptions>() ?? new GraphExpansionApplyServiceOptions()
	: builder.Configuration.GetSection("Learning:GraphExpansionApply").Get<GraphExpansionApplyServiceOptions>()
		?? new GraphExpansionApplyServiceOptions();
var lifecycleAwareRankerShadowOptions = new LifecycleAwareRankerShadowOptions
{
	Enabled = learningRankerShadowOptions.Enabled,
	DebugEndpointEnabled = learningRankerShadowOptions.DebugEndpointEnabled,
	TraceCollectionEnabled = learningRankerShadowOptions.TraceCollectionEnabled,
	MaxCandidatesPerTrace = learningRankerShadowOptions.MaxCandidatesPerTrace,
	Profile = string.IsNullOrWhiteSpace(learningRankerShadowOptions.Profile)
		? "lifecycle-aware-v1"
		: learningRankerShadowOptions.Profile
};
var graphExpansionShadowOptions = new GraphExpansionShadowOptions
{
	Enabled = learningGraphExpansionShadowOptions.Enabled,
	TraceCollectionEnabled = learningGraphExpansionShadowOptions.TraceCollectionEnabled,
	Profiles = learningGraphExpansionShadowOptions.Profiles.Count == 0
		? ["audit-v1", "conflict-v1"]
		: learningGraphExpansionShadowOptions.Profiles
			.Where(static item => !string.IsNullOrWhiteSpace(item))
			.Select(static item => item.Trim())
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray(),
	MaxRelationsPerTrace = learningGraphExpansionShadowOptions.MaxRelationsPerTrace > 0
		? learningGraphExpansionShadowOptions.MaxRelationsPerTrace
		: 50
};
var graphExpansionApplyOptions = new GraphExpansionApplyOptions
{
	Mode = string.IsNullOrWhiteSpace(graphExpansionApplyServiceOptions.Mode)
		? GraphExpansionApplyOptions.OffMode
		: graphExpansionApplyServiceOptions.Mode,
	ApplyMode = string.IsNullOrWhiteSpace(graphExpansionApplyServiceOptions.ApplyMode)
		? GraphExpansionApplyOptions.ProfileScopedApplyMode
		: graphExpansionApplyServiceOptions.ApplyMode,
	OptInProfiles = graphExpansionApplyServiceOptions.OptInProfiles
		.Where(static item => !string.IsNullOrWhiteSpace(item))
		.Select(static item => item.Trim())
		.Distinct(StringComparer.OrdinalIgnoreCase)
		.ToArray(),
	AllowedTargetSections = graphExpansionApplyServiceOptions.AllowedTargetSections.Count == 0
		?
		[
			GraphExpansionTargetSection.AuditContext,
			GraphExpansionTargetSection.ConflictEvidence,
			GraphExpansionTargetSection.HistoricalContext,
			GraphExpansionTargetSection.DiagnosticsOnly
		]
		: graphExpansionApplyServiceOptions.AllowedTargetSections
			.Where(static item => !string.IsNullOrWhiteSpace(item))
			.Select(static item => item.Trim())
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray(),
	DisallowNormalContextInjection = graphExpansionApplyServiceOptions.DisallowNormalContextInjection,
	FallbackOnRisk = graphExpansionApplyServiceOptions.FallbackOnRisk,
	MaxAddedItemsPerPackage = graphExpansionApplyServiceOptions.MaxAddedItemsPerPackage > 0
		? graphExpansionApplyServiceOptions.MaxAddedItemsPerPackage
		: 20,
	EmitComparisonTrace = graphExpansionApplyServiceOptions.EmitComparisonTrace
};
var relationGovernanceProviderSwitchSection = builder.Configuration.GetSection("Storage:RelationGovernanceProviderSwitch");
var relationGovernanceProviderSwitchOptions = relationGovernanceProviderSwitchSection.Exists()
	? relationGovernanceProviderSwitchSection.Get<RelationGovernanceProviderSwitchOptions>() ?? new RelationGovernanceProviderSwitchOptions()
	: builder.Configuration.GetSection("RelationGovernance:ProviderSwitch").Get<RelationGovernanceProviderSwitchOptions>()
		?? new RelationGovernanceProviderSwitchOptions();
var routerShadowOptions = new RouterShadowOptions
{
	Enabled = learningRouterShadowOptions.Enabled,
	TraceCollectionEnabled = learningRouterShadowOptions.TraceCollectionEnabled,
	ShadowClassifier = string.IsNullOrWhiteSpace(learningRouterShadowOptions.ShadowClassifier)
		? RouterIntentClassifierBaselineNames.TokenCentroidRouterBaseline
		: learningRouterShadowOptions.ShadowClassifier,
	RecordAgreements = learningRouterShadowOptions.RecordAgreements,
	RecordDisagreements = learningRouterShadowOptions.RecordDisagreements
};
builder.Services.AddSingleton(storageOptions);
builder.Services.AddSingleton(compressionOptions);
builder.Services.AddSingleton(securityOptions);
builder.Services.AddSingleton(retrievalAttentionRerankOptions);
builder.Services.AddSingleton(retrievalPlanningOptions);
builder.Services.AddSingleton(learningRankerShadowOptions);
builder.Services.AddSingleton(learningRouterShadowOptions);
builder.Services.AddSingleton(routerShadowOptions);
builder.Services.AddSingleton(lifecycleAwareRankerShadowOptions);
builder.Services.AddSingleton(learningGraphExpansionShadowOptions);
builder.Services.AddSingleton(graphExpansionShadowOptions);
builder.Services.AddSingleton(graphExpansionApplyServiceOptions);
builder.Services.AddSingleton(graphExpansionApplyOptions);
builder.Services.AddSingleton(relationGovernanceProviderSwitchOptions);
builder.Services.Configure<JobWorkerOptions>(builder.Configuration.GetSection("JobWorker"));
builder.Services.Configure<ShortTermMaintenanceOptions>(builder.Configuration.GetSection("ShortTermMaintenance"));
builder.Services.AddHostedService<ContextJobWorker>();
builder.Services.AddHostedService<ShortTermMemoryMaintenanceWorker>();
builder.Services.AddSingleton<ContextCoreMetrics>();
builder.Services.AddSingleton(new FoundationStatusService(Directory.GetCurrentDirectory()));
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
	.AddContextCore()
	.AddContextModelGateway(builder.Configuration);

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

app.UseRequestTimeouts();
if (securityOptions.AllowedOrigins.Count > 0)
{
	app.UseCors(CorsPolicyName);
}
app.UseMiddleware<ApiKeyMiddleware>();
app.UseMiddleware<AuditLogMiddleware>();

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
	.MapModelEndpoints();

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

// ── PostgreSQL 启动连接验证（fail-fast）────────────────────────────────
// 在 app.Run() 前执行一次 SELECT 1，确保 Postgres 可达；失败则 LogCritical 并中止进程。
// 这是 B1 §9.2 fail-fast 保护，避免 Postgres 不可达时服务静默启动但存储全部报错。
if (storageOptions.IsPostgres)
{
	try
	{
		using var startupCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
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
		logger.LogInformation("PostgreSQL 连接验证成功（SELECT 1 通过）。");
	}
	catch (Exception ex)
	{
		logger.LogCritical(ex,
			"[FATAL] PostgreSQL 连接验证异常。服务将中止。");
		await app.StopAsync();
		return;
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
