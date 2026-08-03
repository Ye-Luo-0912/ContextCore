using ContextCore.Abstractions;
using ContextCore.Core.Services.ModelExecution;
using ContextCore.Inference.Onnx;
using Microsoft.Extensions.DependencyInjection;

namespace ContextCore.Tests;

// ===========================================================================
// ModelActivationManager 单元测试
//
// 覆盖范围：
// 代理行为：未激活时委托给 fallback，激活后委托给 OnnxInferenceEngine
// 激活成功路径：descriptor → 校准验证 → schema 验证 → session 创建 → 引擎切换
// 激活失败：descriptor 未找到
// 激活失败：校准验证不通过
// 激活失败：schema 未注册
// 激活失败：ONNX session 创建失败
// ActivateLatestAsync：通过 GetLatestAsync 解析
// DI 注册：AddModelActivationManager 正确注册所有接口
//
// 设计：
// 使用 InMemoryModelArtifactRegistry（测试辅助）+ MockOnnxInferenceSession 隔离真实 ONNX 加载。
// 真实 ONNX 文件 E2E 测试由 P0_6_RealOnnxFileE2ETests 承担。
// ===========================================================================

[TestClass]
[TestCategory("P0-7")]
[TestCategory("P0-8")]
public sealed class P0_7_ModelActivationManagerTests
{
    private const string SchemaVersion = "p0-7-schema-v1";
    private const string ModelArtifactId = "p0-7-model-v1";
    private const string ModelName = "p0-7-test-model";
    private const string CalibrationVersion = "p0-7-cal-v1";

    // ===========================================================================
    // 代理行为
    // ===========================================================================

    [TestMethod]
    public async Task BeforeActivation_DelegatesToFallback()
    {
        var manager = BuildManager();
        var request = new BatchInferenceRequest
        {
            Inputs = new[]
            {
                new FeatureVector
                {
                    SchemaVersion = SchemaVersion,
                    Values = new Dictionary<string, object> { ["lexical_score"] = 0.5 }
                }
            }
        };

        var result = await manager.InferAsync(request);

        // fallback = DeterministicBatchInferenceEngine，应返回确定性结果
        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(InferenceEngineKind.DeterministicReplay, manager.Kind);
        Assert.IsNull(manager.ActiveEngine);
        Assert.IsNull(manager.ActiveDescriptor);
    }

    [TestMethod]
    public async Task BeforeActivation_DelegatesFallbackBatchInference()
    {
        var manager = BuildManager();
        var batch = new FeatureBatch
        {
            SchemaVersion = SchemaVersion,
            Values = new float[] { 0.5f, 0.7f },
            RowCount = 1,
            FeatureCount = 2,
            FeatureNames = new[] { "a", "b" }
        };

        var result = await manager.InferBatchAsync(batch);

        Assert.IsTrue(result.Succeeded);
    }

    [TestMethod]
    public async Task AfterActivation_DelegatesToOnnxEngine()
    {
        var manager = BuildManagerWithMockSession(out var mockSession);
        var options = BuildOptions();

        var activateResult = await manager.ActivateAsync(ModelArtifactId, options);
        Assert.IsTrue(activateResult.Success, $"激活应成功：{activateResult.Error}");

        // 验证激活后元数据切换到 RealModel
        Assert.AreEqual(InferenceEngineKind.RealModel, manager.Kind);
        Assert.IsNotNull(manager.ActiveEngine);
        Assert.IsNotNull(manager.ActiveDescriptor);
        Assert.AreEqual(ModelArtifactId, manager.ActiveDescriptor!.ModelArtifactId);

        // 推理委托给 OnnxInferenceEngine（mockSession 返回固定输出）
        var batch = new FeatureBatch
        {
            SchemaVersion = SchemaVersion,
            Values = new float[] { 0.5f, 0.7f },
            RowCount = 1,
            FeatureCount = 2,
            FeatureNames = new[] { "a", "b" }
        };

        var result = await manager.InferBatchAsync(batch);
        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(1, result.Outputs.Count);
        Assert.AreEqual(0.5, result.Outputs[0].Score, 1e-6);
        Assert.AreEqual(0.9, result.Outputs[0].Confidence, 1e-6);
    }

    // ===========================================================================
    // 激活成功路径
    // ===========================================================================

    [TestMethod]
    public async Task ActivateAsync_ValidDescriptor_ActivatesEngine()
    {
        var manager = BuildManagerWithMockSession(out _);
        var options = BuildOptions();

        var result = await manager.ActivateAsync(ModelArtifactId, options);

        Assert.IsTrue(result.Success);
        Assert.IsNotNull(result.Descriptor);
        Assert.AreEqual(ModelArtifactId, result.Descriptor!.ModelArtifactId);
        Assert.IsNotNull(result.Engine);
        Assert.AreEqual(CalibrationVersion, result.Engine!.CalibrationVersion);
    }

    [TestMethod]
    public async Task ActivateAsync_WithCalibrationService_ValidatesCalibration()
    {
        // 校准服务注入后，激活时验证校准参数
        var calibration = new PlattCalibrationService();
        calibration.RegisterPlattParameters(a: 1.0, b: 0.0, modelName: ModelArtifactId, version: CalibrationVersion);

        var manager = BuildManagerWithMockSession(out _, calibrationService: calibration);
        var options = BuildOptions();

        var result = await manager.ActivateAsync(ModelArtifactId, options);

        Assert.IsTrue(result.Success);
        Assert.IsNotNull(result.CalibrationValidation);
        Assert.IsTrue(result.CalibrationValidation!.IsValid);
    }

    // ===========================================================================
    // 激活失败：descriptor 未找到
    // ===========================================================================

    [TestMethod]
    public async Task ActivateAsync_UnknownArtifactId_ReturnsFailure()
    {
        var manager = BuildManagerWithMockSession(out _);
        var options = BuildOptions();

        var result = await manager.ActivateAsync("nonexistent-model-id", options);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Error!.Contains("nonexistent-model-id"));
        Assert.IsNull(result.Descriptor);
        Assert.IsNull(result.Engine);
    }

    // ===========================================================================
    // 激活失败：校准验证不通过
    // ===========================================================================

    [TestMethod]
    public async Task ActivateAsync_InvalidCalibration_RejectsActivation()
    {
        // 校准参数非法（Platt A=0）→ 激活被拒绝
        var calibration = new PlattCalibrationService();
        calibration.RegisterPlattParameters(a: 0.0, b: 0.0, modelName: ModelArtifactId, version: CalibrationVersion); // A=0 → Error

        var manager = BuildManagerWithMockSession(out _, calibrationService: calibration);
        var options = BuildOptions();

        var result = await manager.ActivateAsync(ModelArtifactId, options);

        Assert.IsFalse(result.Success);
        Assert.IsNotNull(result.CalibrationValidation);
        Assert.IsFalse(result.CalibrationValidation!.IsValid);
        Assert.IsTrue(result.Error!.Contains("校准验证失败"));
        Assert.IsNull(result.Engine, "校准失败时不应创建引擎");

        // 验证管理器仍使用 fallback
        Assert.IsNull(manager.ActiveEngine);
        Assert.AreEqual(InferenceEngineKind.DeterministicReplay, manager.Kind);
    }

    // ===========================================================================
    // 激活失败：schema 未注册
    // ===========================================================================

    [TestMethod]
    public async Task ActivateAsync_SchemaNotRegistered_RejectsActivation()
    {
        // descriptor 引用了未注册的 schema 版本 → 激活被拒绝
        var registry = new InMemoryModelArtifactRegistry();
        registry.RegisterAsync(new ModelArtifactDescriptor
        {
            ModelArtifactId = "bad-schema-model",
            ModelName = "bad-schema-model",
            ModelVersion = "1.0.0",
            FeatureSchemaVersion = "unregistered-schema-v999", // 未注册
            CalibrationVersion = CalibrationVersion,
            EngineKind = InferenceEngineKind.RealModel,
            ContentHash = "sha256:test",
            ArtifactPath = "/path/to/model.onnx",
            RegisteredAt = DateTimeOffset.UtcNow
        }).GetAwaiter().GetResult();

        var featureRegistry = new DefaultFeatureRegistry();
        // 故意不注册 "unregistered-schema-v999"

        var mockSession = new MockOnnxInferenceSession("id", "1.0.0", "hash", Array.Empty<InferenceOutput>());
        var factory = new MockSessionFactory(mockSession);
        var fallback = new DeterministicBatchInferenceEngine();
        var calValidator = new DefaultCalibrationValidator();

        // fail-closed：提供有效校准参数以便流程越过校准检查到达 schema 验证步骤
        var manager = new ModelActivationManager(
            registry, calValidator, featureRegistry, factory, fallback, BuildValidCalibration(modelName: "bad-schema-model"));

        var options = BuildOptions();
        var result = await manager.ActivateAsync("bad-schema-model", options);

        Assert.IsFalse(result.Success);
        Assert.IsNotNull(result.SchemaValidationError);
        Assert.IsTrue(result.SchemaValidationError!.Contains("unregistered-schema-v999"));
        Assert.IsNull(result.Engine);

        // 验证管理器仍使用 fallback
        Assert.IsNull(manager.ActiveEngine);
    }

    // ===========================================================================
    // 激活失败：ONNX session 创建失败
    // ===========================================================================

    [TestMethod]
    public async Task ActivateAsync_SessionCreationFails_ReturnsFailure()
    {
        var manager = BuildManagerWithFailingFactory();
        var options = BuildOptions();

        var result = await manager.ActivateAsync(ModelArtifactId, options);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Error!.Contains("ONNX") || result.Error!.Contains("session"));
        Assert.IsNull(result.Engine);
        Assert.IsNull(manager.ActiveEngine);
    }

    // ===========================================================================
    // ActivateLatestAsync
    // ===========================================================================

    [TestMethod]
    public async Task ActivateLatestAsync_ResolvesLatestVersion()
    {
        var registry = new InMemoryModelArtifactRegistry();
        // 注册两个版本，latest 应为 v2
        registry.RegisterAsync(MakeDescriptor("latest-model-v1", ModelName, "1.0.0", DateTimeOffset.UtcNow.AddSeconds(-10))).GetAwaiter().GetResult();
        registry.RegisterAsync(MakeDescriptor("latest-model-v2", ModelName, "2.0.0", DateTimeOffset.UtcNow)).GetAwaiter().GetResult();

        var mockSession = new MockOnnxInferenceSession("latest-model-v2", "2.0.0", "sha256:v2", Array.Empty<InferenceOutput>());
        var factory = new MockSessionFactory(mockSession);
        var featureRegistry = BuildFeatureRegistry();
        var fallback = new DeterministicBatchInferenceEngine();
        var calValidator = new DefaultCalibrationValidator();

        // fail-closed：注册与 ModelName 匹配的校准参数（descriptor 用 MakeDescriptor，
        // 其 ModelArtifactId 各不相同但 ModelName 统一为同一测试模型）
        var manager = new ModelActivationManager(registry, calValidator, featureRegistry, factory, fallback, BuildValidCalibration(modelName: ModelName));
        var options = BuildOptions();

        var result = await manager.ActivateLatestAsync(ModelName, options);

        Assert.IsTrue(result.Success);
        Assert.AreEqual("latest-model-v2", result.Descriptor!.ModelArtifactId);
        Assert.AreEqual("2.0.0", result.Descriptor.ModelVersion);
    }

    [TestMethod]
    public async Task ActivateLatestAsync_UnknownModelName_ReturnsFailure()
    {
        var manager = BuildManagerWithMockSession(out _);
        var options = BuildOptions();

        var result = await manager.ActivateLatestAsync("unknown-model-name", options);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Error!.Contains("unknown-model-name"));
    }

    // ===========================================================================
    // DI 注册
    // ===========================================================================

    [TestMethod]
    public void AddModelActivationManager_RegistersAllInterfaces()
    {
        var services = new ServiceCollection();
        var fallback = new DeterministicBatchInferenceEngine();

        services.AddModelActivationManager(fallback);
        // 补充依赖
        services.AddSingleton<ICalibrationValidator, DefaultCalibrationValidator>();
        services.AddSingleton<IFeatureRegistry>(BuildFeatureRegistry());
        services.AddSingleton<IModelArtifactRegistry>(new InMemoryModelArtifactRegistry());

        var provider = services.BuildServiceProvider();

        var manager = provider.GetRequiredService<ModelActivationManager>();
        var iface = provider.GetRequiredService<IModelActivationManager>();
        var engine = provider.GetRequiredService<IBatchInferenceEngine>();

        Assert.IsNotNull(manager);
        Assert.AreSame(manager, iface);
        Assert.AreSame(manager, engine);
    }

    // ===========================================================================
    // 辅助方法
    // ===========================================================================

    private static ModelActivationManager BuildManager()
    {
        var registry = BuildRegistry();
        var featureRegistry = BuildFeatureRegistry();
        var factory = new MockSessionFactory(new MockOnnxInferenceSession("id", "1.0.0", "hash", Array.Empty<InferenceOutput>()));
        var fallback = new DeterministicBatchInferenceEngine();
        var calValidator = new DefaultCalibrationValidator();

        return new ModelActivationManager(registry, calValidator, featureRegistry, factory, fallback);
    }

    private static ModelActivationManager BuildManagerWithMockSession(
        out MockOnnxInferenceSession mockSession,
        ICalibrationService? calibrationService = null)
    {
        mockSession = new MockOnnxInferenceSession(
            ModelArtifactId, "1.0.0", "sha256:test",
            new[] { new InferenceOutput { Score = 0.5, Confidence = 0.9 } });

        var registry = BuildRegistry();
        var featureRegistry = BuildFeatureRegistry();
        var factory = new MockSessionFactory(mockSession);
        var fallback = new DeterministicBatchInferenceEngine();
        var calValidator = new DefaultCalibrationValidator();

        // fail-closed 要求 calibrationService 非空且版本精确匹配；
        // 未显式传入时使用与 descriptor.CalibrationVersion 一致的有效参数。
        var cal = calibrationService ?? BuildValidCalibration();

        return new ModelActivationManager(
            registry, calValidator, featureRegistry, factory, fallback,
            cal);
    }

    private static ModelActivationManager BuildManagerWithFailingFactory()
    {
        var registry = BuildRegistry();
        var featureRegistry = BuildFeatureRegistry();
        var factory = new FailingSessionFactory();
        var fallback = new DeterministicBatchInferenceEngine();
        var calValidator = new DefaultCalibrationValidator();

        // fail-closed：提供有效校准参数以便流程越过校准检查到达 session 创建步骤
        return new ModelActivationManager(registry, calValidator, featureRegistry, factory, fallback, BuildValidCalibration());
    }

    /// <summary>
    /// 构造与 descriptor.CalibrationVersion 精确匹配的有效 Platt 校准参数（A=1.0, B=0.0）。
    /// fail-closed 要求 ICalibrationService 非空且版本精确匹配才能通过校准验证。
    /// </summary>
    /// <param name="modelName">注册校准参数的目标模型名（默认 <see cref="ModelArtifactId"/>）。</param>
    private static PlattCalibrationService BuildValidCalibration(string? modelName = null)
    {
        var cal = new PlattCalibrationService();
        cal.RegisterPlattParameters(a: 1.0, b: 0.0, modelName: modelName ?? ModelArtifactId, version: CalibrationVersion);
        return cal;
    }

    private static InMemoryModelArtifactRegistry BuildRegistry()
    {
        var registry = new InMemoryModelArtifactRegistry();
        registry.RegisterAsync(MakeDescriptor(ModelArtifactId, ModelName, "1.0.0", DateTimeOffset.UtcNow)).GetAwaiter().GetResult();
        return registry;
    }

    private static DefaultFeatureRegistry BuildFeatureRegistry()
    {
        var registry = new DefaultFeatureRegistry();
        registry.Register(new FeatureSchema
        {
            Version = SchemaVersion,
            CreatedAt = DateTimeOffset.UtcNow,
            Features = new[]
            {
                new FeatureDefinition { Name = "lexical_score", Type = FeatureType.Numeric, IsRequired = false, DefaultValue = "0" },
                new FeatureDefinition { Name = "semantic_score", Type = FeatureType.Numeric, IsRequired = false, DefaultValue = "0" }
            }
        });
        return registry;
    }

    private static OnnxInferenceEngineOptions BuildOptions() => new()
    {
        InputTensorName = "input",
        ScoreOutputName = "logits"
    };

    private static ModelArtifactDescriptor MakeDescriptor(
        string artifactId, string modelName, string version, DateTimeOffset registeredAt) => new()
    {
        ModelArtifactId = artifactId,
        ModelName = modelName,
        ModelVersion = version,
        FeatureSchemaVersion = SchemaVersion,
        CalibrationVersion = CalibrationVersion,
        EngineKind = InferenceEngineKind.RealModel,
        ContentHash = "sha256:" + artifactId,
        ArtifactPath = "/path/to/" + artifactId + ".onnx",
        RegisteredAt = registeredAt
    };
}

// ===========================================================================
// 测试辅助：InMemoryModelArtifactRegistry
// ===========================================================================

internal sealed class InMemoryModelArtifactRegistry : IModelArtifactRegistry
{
    private readonly Dictionary<string, ModelArtifactDescriptor> _byId = new(StringComparer.Ordinal);
    private readonly List<ModelArtifactDescriptor> _all = new();
    private readonly object _lock = new();

    public ValueTask RegisterAsync(ModelArtifactDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        lock (_lock)
        {
            if (_byId.ContainsKey(descriptor.ModelArtifactId))
            {
                throw new InvalidOperationException($"ModelArtifactId '{descriptor.ModelArtifactId}' 已注册。");
            }
            _byId[descriptor.ModelArtifactId] = descriptor;
            _all.Add(descriptor);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<ModelArtifactDescriptor?> GetAsync(string modelArtifactId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelArtifactId);
        lock (_lock)
        {
            _byId.TryGetValue(modelArtifactId, out var descriptor);
            return ValueTask.FromResult(descriptor);
        }
    }

    public ValueTask<ModelArtifactDescriptor?> GetLatestAsync(string modelName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        lock (_lock)
        {
            var latest = _all
                .Where(d => string.Equals(d.ModelName, modelName, StringComparison.Ordinal))
                .OrderByDescending(d => d.RegisteredAt)
                .FirstOrDefault();
            return ValueTask.FromResult(latest);
        }
    }

    public ValueTask<IReadOnlyList<ModelArtifactDescriptor>> ListByVersionAsync(string modelName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        lock (_lock)
        {
            var versions = _all
                .Where(d => string.Equals(d.ModelName, modelName, StringComparison.Ordinal))
                .OrderBy(d => d.RegisteredAt)
                .ToList();
            return ValueTask.FromResult<IReadOnlyList<ModelArtifactDescriptor>>(versions);
        }
    }

    public ValueTask<IReadOnlyList<ModelArtifactDescriptor>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var all = _all.OrderBy(d => d.RegisteredAt).ToList();
            return ValueTask.FromResult<IReadOnlyList<ModelArtifactDescriptor>>(all);
        }
    }
}

// ===========================================================================
// 测试辅助：FailingSessionFactory（session 创建总是失败）
// ===========================================================================

internal sealed class FailingSessionFactory : IOnnxInferenceSessionFactory
{
    public ValueTask<IOnnxInferenceSession> CreateAsync(
        OnnxInferenceEngineOptions options,
        ModelArtifactDescriptor? descriptor = null,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("模拟 session 创建失败。");
    }
}
