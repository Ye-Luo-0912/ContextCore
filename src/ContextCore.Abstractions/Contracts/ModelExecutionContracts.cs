namespace ContextCore.Abstractions;

// ===========================================================================
// R28-D：Model Execution Runtime 契约
//
// 目标：
//   把分散在 ModelGateway 基础设施之上的特征管理、批量推理、模型校准能力
//   显式契约化，补齐以下四个抽象：
//     1. IFeatureRegistry       —— Feature Registry：管理特征 schema 版本
//     2. IBatchInferenceEngine  —— Batch Inference Engine：批量推理
//     3. ICalibrationService    —— Calibration Service：模型分数校准
//     4. Deterministic fallback —— 由 DeterministicBatchInferenceEngine 提供，
//        在真实模型不可用时使用 feature hash 产出确定性分数
//
// 设计原则：
//   1. 契约层不引入存储 I/O：所有抽象为进程内接口，实现层可注入持久化 store。
//   2. FeatureSchema 全局不可变：版本号一旦注册不可修改，新版本通过新 schema 注册实现。
//   3. BatchInference 必须可降级：当真实模型不可用时回退到 Deterministic 实现，
//      保证主链始终能产出非空结果（fail-safe 而非 fail-fast）。
//   4. Calibration 默认 identity（A=1, B=0），未配置参数时等价于恒等变换。
// ===========================================================================

/// <summary>R28-D：Feature Registry — 管理特征 schema 版本。</summary>
public interface IFeatureRegistry
{
    /// <summary>注册特征 schema。</summary>
    void Register(FeatureSchema schema);

    /// <summary>获取指定版本的特征 schema。</summary>
    FeatureSchema? Get(string schemaVersion);

    /// <summary>获取最新版本。</summary>
    FeatureSchema? GetLatest();

    /// <summary>列出所有已注册版本。</summary>
    IReadOnlyList<FeatureSchema> ListAll();
}

/// <summary>R28-D：特征 schema 定义。</summary>
public sealed record FeatureSchema
{
    /// <summary>Schema 版本号（语义化版本字符串，如 "1.0.0"）。</summary>
    public required string Version { get; init; }

    /// <summary>该 schema 包含的特征定义列表。</summary>
    public required IReadOnlyList<FeatureDefinition> Features { get; init; }

    /// <summary>Schema 创建时间戳。</summary>
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>R28-D：特征定义。</summary>
public sealed record FeatureDefinition
{
    /// <summary>特征名（在 schema 内唯一）。</summary>
    public required string Name { get; init; }

    /// <summary>特征类型。</summary>
    public required FeatureType Type { get; init; }

    /// <summary>是否必填（缺失时是否阻止推理）。</summary>
    public required bool IsRequired { get; init; }

    /// <summary>默认值（字符串形式，由消费方按 Type 解析）；可空表示无默认值。</summary>
    public string? DefaultValue { get; init; }
}

/// <summary>R28-D：特征类型枚举。</summary>
public enum FeatureType : byte
{
    /// <summary>数值型特征。</summary>
    Numeric = 0,

    /// <summary>类别型特征。</summary>
    Categorical = 1,

    /// <summary>布尔型特征。</summary>
    Boolean = 2,

    /// <summary>文本型特征。</summary>
    Text = 3
}

/// <summary>R28-D：Batch Inference Engine — 批量推理。</summary>
public interface IBatchInferenceEngine
{
    /// <summary>批量推理。</summary>
    ValueTask<BatchInferenceResult> InferAsync(BatchInferenceRequest request, CancellationToken ct = default);

    /// <summary>模型版本。</summary>
    string ModelVersion { get; }
}

/// <summary>R28-D：批量推理请求。</summary>
public sealed record BatchInferenceRequest
{
    /// <summary>批量输入特征向量列表。</summary>
    public required IReadOnlyList<FeatureVector> Inputs { get; init; }

    /// <summary>目标模型名（可选；为空时使用引擎默认模型）。</summary>
    public string? ModelName { get; init; }

    /// <summary>超时时间（毫秒）。</summary>
    public int TimeoutMs { get; init; } = 5000;
}

/// <summary>R28-D：特征向量。</summary>
public sealed record FeatureVector
{
    /// <summary>关联的 schema 版本。</summary>
    public required string SchemaVersion { get; init; }

    /// <summary>特征值字典（key = 特征名，value = 原始值）。</summary>
    public required IReadOnlyDictionary<string, object> Values { get; init; }
}

/// <summary>R28-D：批量推理结果。</summary>
public sealed record BatchInferenceResult
{
    /// <summary>每条输入对应的推理输出（顺序与输入一致）。</summary>
    public required IReadOnlyList<InferenceOutput> Outputs { get; init; }

    /// <summary>整体是否成功（false 时 Error 字段填充失败原因）。</summary>
    public required bool Succeeded { get; init; }

    /// <summary>失败原因（Succeeded=true 时为 null）。</summary>
    public string? Error { get; init; }

    /// <summary>本次推理耗时。</summary>
    public required TimeSpan Duration { get; init; }
}

/// <summary>R28-D：单条推理输出。</summary>
public sealed record InferenceOutput
{
    /// <summary>主分数（语义由模型决定，校准前可能不在 [0,1]）。</summary>
    public required double Score { get; init; }

    /// <summary>置信度（[0,1]）。</summary>
    public required double Confidence { get; init; }

    /// <summary>多分类场景下每个类别的分数（key = 类别标签，value = 分数）；二分类可空。</summary>
    public IReadOnlyDictionary<string, double>? PerClassScores { get; init; }
}

/// <summary>R28-D：Calibration Service — 模型校准。</summary>
public interface ICalibrationService
{
    /// <summary>校准分数。</summary>
    double Calibrate(double rawScore, string? modelName = null);

    /// <summary>批量校准。</summary>
    IReadOnlyList<double> CalibrateBatch(IReadOnlyList<double> rawScores, string? modelName = null);

    /// <summary>获取校准参数。</summary>
    CalibrationParameters? GetParameters(string? modelName = null);
}

/// <summary>R28-D：校准参数。</summary>
public sealed record CalibrationParameters
{
    /// <summary>校准方法名（"isotonic" / "platt" / "temperature"）。</summary>
    public required string Method { get; init; }

    /// <summary>校准参数（Platt: A；Temperature: T；Isotonic: 忽略）。</summary>
    public required double Parameter { get; init; }

    /// <summary>参数拟合时间戳。</summary>
    public required DateTimeOffset FittedAt { get; init; }
}
