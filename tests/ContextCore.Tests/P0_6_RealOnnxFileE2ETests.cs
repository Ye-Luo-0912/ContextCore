using System.Security.Cryptography;
using ContextCore.Abstractions;
using ContextCore.Core.Services.ModelExecution;
using ContextCore.Inference.Onnx;

namespace ContextCore.Tests;

// ===========================================================================
// Real Model E2E — 加载实际 ONNX 文件的端到端测试
//
// 目标：
//   验证 ModelActivationManager 框架（P0-7/P0-8 搭建）能加载真实 ONNX 模型文件，
//   而非仅依赖 MockOnnxInferenceSession。通过 OnnxRuntimeInferenceSessionFactory
//   读取真实模型元数据（InputMetadata/OutputMetadata），验证：
//     1. 工厂成功加载真实 ONNX 文件并创建 session（张量名校验通过）
//     2. 工厂拒绝错误张量名（证明真实读取了模型 metadata，而非 mock）
//     3. ModelActivationManager 完整激活流程：registry → 校准验证 → schema 验证
//        → session 创建 → 引擎切换（Kind 从 DeterministicReplay → RealModel）
//     4. 真实模型推理调用 ONNX Runtime（embedding 模型期望 int64 input_ids，
//        发送 float 特征会触发 OnnxRuntimeException，证明真实 session.Run 被调用）
//     5. ActivateLatestAsync 解析最新版本
//     6. 真实文件 SHA-256 ContentHash 流经 descriptor → engine
//
// 设计：
//   - 使用项目内置 embedding 模型（bge-small-zh-v1.5 / all-MiniLM-L6-v2），
//     它们是 BERT-based，具有标准张量名 input_ids（int64）与 last_hidden_state（float）。
//   - 当 ONNX 文件不存在（CI 未下载模型）时，Assert.Inconclusive 跳过。
//   - 复用 InMemoryModelArtifactRegistry（定义于 P0_7 测试文件，同 assembly internal 可见）。
//   - 推理测试利用输入类型不匹配（float vs int64）触发 OnnxRuntime 错误，
//     优雅失败（Succeeded=false）证明真实 session 被调用，而非 mock 返回固定值。
// ===========================================================================

[TestClass]
[TestCategory("P0-6")]
public sealed class P0_6_RealOnnxFileE2ETests
{
    private const string SchemaVersion = "p0-6-embedding-schema-v1";
    private const string CalibrationVersion = "p0-6-cal-v1";
    private const string ModelArtifactId = "p0-6-real-onnx-v1";
    private const string ModelName = "p0-6-real-onnx-model";

    // BERT-based embedding 模型的标准张量名
    private const string EmbeddingInputTensor = "input_ids";
    private const string EmbeddingOutputTensor = "last_hidden_state";

    private static readonly string RepoRoot = FindRepoRoot();

    // 真实 ONNX 模型文件路径（BERT-based，具有标准 input_ids / last_hidden_state 张量名）
    private static readonly string BgeSmallModelPath = Path.Combine(
        RepoRoot, "src", "ContextCore.Embedding", "Models",
        "bge-small-zh-v1.5", "onnx", "model_quantized.onnx");

    private static readonly string AllMiniLmModelPath = Path.Combine(
        RepoRoot, "src", "ContextCore.Embedding", "Models",
        "all-MiniLM-L6-v2", "onnx", "model_quantized.onnx");

    private static bool ModelsAvailable =>
        File.Exists(BgeSmallModelPath) || File.Exists(AllMiniLmModelPath);

    // ===========================================================================
    // 工厂加载真实 ONNX 文件
    // ===========================================================================

    [TestMethod]
    public async Task Factory_LoadsRealOnnxFile_CreatesSessionWithRealMetadata()
    {
        if (!ModelsAvailable)
        {
            Assert.Inconclusive("未找到真实 ONNX 模型文件，跳过 E2E 测试。");
        }

        var modelPath = PickAvailableModel();
        var options = BuildEmbeddingOptions(modelPath);
        var factory = new OnnxRuntimeInferenceSessionFactory();

        var session = await factory.CreateAsync(options);

        Assert.IsNotNull(session);
        // 元数据来自 options fallback（无 descriptor 时）
        Assert.AreEqual(options.ModelArtifactId, session.ModelArtifactId);
        Assert.AreEqual(options.ModelVersion, session.ModelVersion);
        Assert.AreEqual(ComputeSha256(modelPath), session.ContentHash);

        await session.DisposeAsync();
    }

    // ===========================================================================
    // 工厂拒绝错误张量名（证明真实读取了模型 metadata）
    // ===========================================================================

    [TestMethod]
    public async Task Factory_RealModel_RejectsWrongTensorNames()
    {
        if (!ModelsAvailable)
        {
            Assert.Inconclusive("未找到真实 ONNX 模型文件，跳过 E2E 测试。");
        }

        var modelPath = PickAvailableModel();
        var options = new OnnxInferenceEngineOptions
        {
            InputTensorName = "nonexistent_input_tensor",
            ScoreOutputName = EmbeddingOutputTensor,
            ModelPath = modelPath,
            ApplySigmoid = false
        };
        var factory = new OnnxRuntimeInferenceSessionFactory();

        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => factory.CreateAsync(options).AsTask());

        // 错误消息应列出真实模型的可用输入张量名（包含 input_ids）
        Assert.IsTrue(ex.Message.Contains("nonexistent_input_tensor"),
            $"错误消息应包含错误的张量名：{ex.Message}");
        Assert.IsTrue(ex.Message.Contains("input_ids"),
            $"错误消息应列出可用输入张量（证明读取了真实 metadata）：{ex.Message}");
    }

    [TestMethod]
    public async Task Factory_RealModel_RejectsWrongOutputTensorName()
    {
        if (!ModelsAvailable)
        {
            Assert.Inconclusive("未找到真实 ONNX 模型文件，跳过 E2E 测试。");
        }

        var modelPath = PickAvailableModel();
        var options = new OnnxInferenceEngineOptions
        {
            InputTensorName = EmbeddingInputTensor,
            ScoreOutputName = "nonexistent_output_tensor",
            ModelPath = modelPath,
            ApplySigmoid = false
        };
        var factory = new OnnxRuntimeInferenceSessionFactory();

        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => factory.CreateAsync(options).AsTask());

        Assert.IsTrue(ex.Message.Contains("nonexistent_output_tensor"),
            $"错误消息应包含错误的输出张量名：{ex.Message}");
        // 错误消息应列出真实模型的可用输出张量名
        Assert.IsTrue(ex.Message.Contains("可用输出"),
            $"错误消息应列出可用输出张量：{ex.Message}");
    }

    // ===========================================================================
    // ModelActivationManager 完整激活流程（真实 ONNX 文件）
    // ===========================================================================

    [TestMethod]
    public async Task ModelActivationManager_ActivatesRealOnnxFile_FullPipeline()
    {
        if (!ModelsAvailable)
        {
            Assert.Inconclusive("未找到真实 ONNX 模型文件，跳过 E2E 测试。");
        }

        var modelPath = PickAvailableModel();
        var contentHash = ComputeSha256(modelPath);
        var descriptor = BuildDescriptor(modelPath, contentHash);

        var registry = new InMemoryModelArtifactRegistry();
        await registry.RegisterAsync(descriptor);

        var featureRegistry = BuildFeatureRegistry();
        var factory = new OnnxRuntimeInferenceSessionFactory();
        var fallback = new DeterministicBatchInferenceEngine();
        var calValidator = new DefaultCalibrationValidator();
        var manager = new ModelActivationManager(
            registry, calValidator, featureRegistry, factory, fallback, BuildValidCalibration());

        // 激活前：使用 fallback（DeterministicReplay）
        Assert.AreEqual(InferenceEngineKind.DeterministicReplay, manager.Kind);
        Assert.IsNull(manager.ActiveEngine);

        var options = BuildEmbeddingOptions(modelPath);
        var result = await manager.ActivateAsync(descriptor.ModelArtifactId, options);

        // 激活成功
        Assert.IsTrue(result.Success, $"激活应成功：{result.Error}");
        Assert.IsNotNull(result.Engine);
        Assert.IsNotNull(result.Descriptor);
        Assert.AreEqual(descriptor.ModelArtifactId, result.Descriptor!.ModelArtifactId);

        // 激活后：切换到 RealModel
        Assert.AreEqual(InferenceEngineKind.RealModel, manager.Kind);
        Assert.IsNotNull(manager.ActiveEngine);
        Assert.IsNotNull(manager.ActiveDescriptor);
        Assert.AreEqual(descriptor.ModelArtifactId, manager.ActiveDescriptor!.ModelArtifactId);

        // ContentHash 来自真实文件的 SHA-256
        Assert.AreEqual(contentHash, manager.ContentHash);
        Assert.AreEqual(CalibrationVersion, manager.CalibrationVersion);
    }

    // ===========================================================================
    // 真实模型推理调用 ONNX Runtime（embedding 模型输入类型不匹配 → 优雅失败）
    // ===========================================================================

    [TestMethod]
    public async Task ModelActivationManager_RealModelInference_InvokesOnnxRuntime()
    {
        if (!ModelsAvailable)
        {
            Assert.Inconclusive("未找到真实 ONNX 模型文件，跳过 E2E 测试。");
        }

        var modelPath = PickAvailableModel();
        var descriptor = BuildDescriptor(modelPath, ComputeSha256(modelPath));

        var registry = new InMemoryModelArtifactRegistry();
        await registry.RegisterAsync(descriptor);

        var featureRegistry = BuildFeatureRegistry();
        var factory = new OnnxRuntimeInferenceSessionFactory();
        var fallback = new DeterministicBatchInferenceEngine();
        var calValidator = new DefaultCalibrationValidator();
        var manager = new ModelActivationManager(
            registry, calValidator, featureRegistry, factory, fallback, BuildValidCalibration());

        var options = BuildEmbeddingOptions(modelPath);
        var activateResult = await manager.ActivateAsync(descriptor.ModelArtifactId, options);
        Assert.IsTrue(activateResult.Success, $"激活应成功：{activateResult.Error}");

        // 发送 float 特征到期望 int64 input_ids 的 embedding 模型
        // → OnnxRuntime 抛出类型不匹配异常 → 优雅失败（Succeeded=false）
        // 这证明真实 session.Run 被调用，而非 mock 返回固定值
        var batch = new FeatureBatch
        {
            SchemaVersion = SchemaVersion,
            Values = new float[] { 0.5f, 0.7f, 0.3f, 0.9f },
            RowCount = 2,
            FeatureCount = 2,
            FeatureNames = new[] { "lexical_score", "semantic_score" }
        };

        var result = await manager.InferBatchAsync(batch);

        // 推理应失败（输入类型不匹配：float vs int64 input_ids）
        Assert.IsFalse(result.Succeeded,
            $"embedding 模型期望 int64 input_ids，发送 float 特征应触发 ONNX 错误。Error={result.Error}");
        Assert.IsNotNull(result.Error);
        // 错误来自真实 ONNX Runtime（而非 mock 的固定输出）
        Assert.IsTrue(result.Error.Contains("ONNX"),
            $"错误应来自 ONNX Runtime：{result.Error}");
    }

    // ===========================================================================
    // ActivateLatestAsync 解析最新版本（真实 ONNX 文件）
    // ===========================================================================

    [TestMethod]
    public async Task ModelActivationManager_ActivateLatest_RealOnnxFile()
    {
        if (!ModelsAvailable)
        {
            Assert.Inconclusive("未找到真实 ONNX 模型文件，跳过 E2E 测试。");
        }

        var modelPath = PickAvailableModel();
        var contentHash = ComputeSha256(modelPath);

        var registry = new InMemoryModelArtifactRegistry();
        // 注册两个版本，v2 为最新
        await registry.RegisterAsync(BuildDescriptor(
            ModelArtifactId + "-v1", ModelName, "1.0.0",
            modelPath, contentHash, DateTimeOffset.UtcNow.AddSeconds(-10)));
        await registry.RegisterAsync(BuildDescriptor(
            ModelArtifactId + "-v2", ModelName, "2.0.0",
            modelPath, contentHash, DateTimeOffset.UtcNow));

        var featureRegistry = BuildFeatureRegistry();
        var factory = new OnnxRuntimeInferenceSessionFactory();
        var fallback = new DeterministicBatchInferenceEngine();
        var calValidator = new DefaultCalibrationValidator();
        var manager = new ModelActivationManager(
            registry, calValidator, featureRegistry, factory, fallback, BuildValidCalibration());

        var options = BuildEmbeddingOptions(modelPath);
        var result = await manager.ActivateLatestAsync(ModelName, options);

        Assert.IsTrue(result.Success, $"激活应成功：{result.Error}");
        Assert.AreEqual(ModelArtifactId + "-v2", result.Descriptor!.ModelArtifactId);
        Assert.AreEqual("2.0.0", result.Descriptor.ModelVersion);
        Assert.AreEqual(InferenceEngineKind.RealModel, manager.Kind);
    }

    // ===========================================================================
    // 激活失败后回退到 fallback（真实文件路径不存在时）
    // ===========================================================================

    [TestMethod]
    public async Task ModelActivationManager_MissingRealFile_ReturnsFailureAndKeepsFallback()
    {
        var modelPath = Path.Combine(RepoRoot, "nonexistent", "model.onnx");
        var descriptor = BuildDescriptor(
            ModelArtifactId + "-missing", ModelName, "1.0.0",
            modelPath, "sha256:missing", DateTimeOffset.UtcNow);

        var registry = new InMemoryModelArtifactRegistry();
        await registry.RegisterAsync(descriptor);

        var featureRegistry = BuildFeatureRegistry();
        var factory = new OnnxRuntimeInferenceSessionFactory();
        var fallback = new DeterministicBatchInferenceEngine();
        var calValidator = new DefaultCalibrationValidator();
        var manager = new ModelActivationManager(
            registry, calValidator, featureRegistry, factory, fallback, BuildValidCalibration());

        var options = BuildEmbeddingOptions(modelPath);
        var result = await manager.ActivateAsync(descriptor.ModelArtifactId, options);

        // 文件不存在 → 激活失败
        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Error!.Contains("未找到") || result.Error!.Contains("ONNX"),
            $"错误应提示文件未找到：{result.Error}");
        Assert.IsNull(result.Engine);

        // 激活失败后仍使用 fallback（fail-safe）
        Assert.IsNull(manager.ActiveEngine);
        Assert.AreEqual(InferenceEngineKind.DeterministicReplay, manager.Kind);
    }

    // ===========================================================================
    // 辅助方法
    // ===========================================================================

    private static string PickAvailableModel()
    {
        if (File.Exists(BgeSmallModelPath)) return BgeSmallModelPath;
        if (File.Exists(AllMiniLmModelPath)) return AllMiniLmModelPath;
        return BgeSmallModelPath; // 返回默认路径（测试会因 ModelsAvailable 检查而 Inconclusive）
    }

    private static OnnxInferenceEngineOptions BuildEmbeddingOptions(string modelPath) => new()
    {
        InputTensorName = EmbeddingInputTensor,
        ScoreOutputName = EmbeddingOutputTensor,
        ModelPath = modelPath,
        ApplySigmoid = false, // embedding 输出不是 logits
        ApplySigmoidToConfidence = false,
        IntraOpNumThreads = 1,
        InferenceTimeoutMs = 30000 // 真实模型加载+推理可能较慢
    };

    private static ModelArtifactDescriptor BuildDescriptor(
        string modelPath, string contentHash) =>
        BuildDescriptor(ModelArtifactId, ModelName, "1.0.0", modelPath, contentHash, DateTimeOffset.UtcNow);

    private static ModelArtifactDescriptor BuildDescriptor(
        string artifactId, string modelName, string version,
        string modelPath, string contentHash, DateTimeOffset registeredAt) => new()
    {
        ModelArtifactId = artifactId,
        ModelName = modelName,
        ModelVersion = version,
        FeatureSchemaVersion = SchemaVersion,
        CalibrationVersion = CalibrationVersion,
        EngineKind = InferenceEngineKind.RealModel,
        ContentHash = contentHash,
        ArtifactPath = modelPath,
        RegisteredAt = registeredAt
    };

    private static DefaultFeatureRegistry BuildFeatureRegistry()
    {
        var registry = new DefaultFeatureRegistry();
        registry.Register(new FeatureSchema
        {
            Version = SchemaVersion,
            CreatedAt = DateTimeOffset.UtcNow,
            Features = new[]
            {
                new FeatureDefinition
                {
                    Name = "lexical_score",
                    Type = FeatureType.Numeric,
                    IsRequired = false,
                    DefaultValue = "0"
                },
                new FeatureDefinition
                {
                    Name = "semantic_score",
                    Type = FeatureType.Numeric,
                    IsRequired = false,
                    DefaultValue = "0"
                }
            }
        });
        return registry;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        var hashBytes = SHA256.HashData(stream);
        return "sha256:" + Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static PlattCalibrationService BuildValidCalibration()
    {
        var cal = new PlattCalibrationService();
        cal.RegisterPlattParameters(a: 1.0, b: 0.0, modelName: ModelName, version: CalibrationVersion);
        return cal;
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, "src"))
                && Directory.Exists(Path.Combine(dir, "tests")))
            {
                return dir;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
        return AppContext.BaseDirectory;
    }
}
