using ContextCore.Abstractions;

namespace ContextCore.Core.Services.ModelExecution;

// ===========================================================================
// R29 WP-A-4：Default Feature Schema Validator
//
// 目标：
//   在推理前对输入特征与 FeatureSchema 执行严格匹配验证：
//     1. SchemaVersion 一致性：input.SchemaVersion == schema.Version
//     2. 必填特征存在：IsRequired=true 时输入必须包含该特征
//     3. 无未知特征（严格模式）：输入特征名必须全部出现在 schema.Features 中
//     4. 类型可转换：输入值必须可转换为目标 FeatureType
//     5. 默认值可解析：当必填特征缺失且 schema 提供 DefaultValue 时，
//        默认值字符串必须可解析为目标类型（Warning，否则 Error）
//
// 设计原则：
//   1. 不抛异常：所有非法情形转为 Error 级 FeatureSchemaViolation。
//   2. 完整违规清单：聚合所有违规（Error + Warning + Info），便于诊断。
//   3. 严格模式默认开启：调用方可在 options 中关闭"未知特征"检查（向后兼容）。
//   4. 与 IInferenceResultValidator 互补：前者关心"输入 vs schema"，
//      后者关心"输出 vs 输入约束"。
// ===========================================================================

/// <summary>
/// R29 WP-A-4：默认特征 schema 验证器。
/// </summary>
public sealed class DefaultFeatureSchemaValidator : IFeatureSchemaValidator
{
    private readonly FeatureSchemaValidatorOptions _options;

    /// <summary>
    /// 默认构造：使用默认 options（严格模式开启）。
    /// </summary>
    public DefaultFeatureSchemaValidator()
        : this(FeatureSchemaValidatorOptions.Default)
    {
    }

    /// <summary>
    /// 带 options 构造：允许调用方关闭严格模式或调整默认值处理策略。
    /// </summary>
    /// <param name="options">验证器配置。</param>
    public DefaultFeatureSchemaValidator(FeatureSchemaValidatorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc />
    public FeatureSchemaValidationResult Validate(FeatureSchema schema, FeatureVector input)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(input);

        var violations = new List<FeatureSchemaViolation>();
        ValidateSchemaVersion(schema, input.SchemaVersion, rowIndex: null, violations);
        ValidateFeatureNames(schema, input.Values, rowIndex: null, violations);
        return BuildResult(violations);
    }

    /// <inheritdoc />
    public FeatureSchemaValidationResult Validate(FeatureSchema schema, FeatureBatch batch)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(batch);

        var violations = new List<FeatureSchemaViolation>();

        // 1. SchemaVersion 一致
        if (!string.Equals(schema.Version, batch.SchemaVersion, StringComparison.Ordinal))
        {
            violations.Add(Error(
                "schema.version_mismatch",
                $"FeatureBatch.SchemaVersion='{batch.SchemaVersion}' 与 schema.Version='{schema.Version}' 不一致。",
                FeatureName: null,
                RowIndex: null));
        }

        // 2. FeatureCount 一致
        if (batch.FeatureCount != schema.Features.Count)
        {
            violations.Add(Error(
                "schema.feature_count_mismatch",
                $"FeatureBatch.FeatureCount={batch.FeatureCount} 与 schema.Features.Count={schema.Features.Count} 不一致。",
                FeatureName: null,
                RowIndex: null));
        }

        // 3. FeatureNames 顺序对齐
        if (batch.FeatureNames.Count != schema.Features.Count)
        {
            violations.Add(Error(
                "schema.feature_names_count_mismatch",
                $"FeatureBatch.FeatureNames.Count={batch.FeatureNames.Count} 与 schema.Features.Count={schema.Features.Count} 不一致。",
                FeatureName: null,
                RowIndex: null));
        }
        else
        {
            for (var i = 0; i < schema.Features.Count; i++)
            {
                var expectedName = schema.Features[i].Name;
                var actualName = batch.FeatureNames[i];
                if (!string.Equals(expectedName, actualName, StringComparison.Ordinal))
                {
                    violations.Add(Error(
                        "schema.feature_name_mismatch",
                        $"FeatureNames[{i}]='{actualName}' 与 schema.Features[{i}].Name='{expectedName}' 不一致（顺序敏感）。",
                        FeatureName: expectedName,
                        RowIndex: null));
                }
            }
        }

        // 4. Values.Length == RowCount × FeatureCount（防止内存越界）
        if (batch.Values.Length != batch.RowCount * batch.FeatureCount)
        {
            violations.Add(Error(
                "batch.values_length_mismatch",
                $"FeatureBatch.Values.Length={batch.Values.Length} != RowCount({batch.RowCount}) × FeatureCount({batch.FeatureCount})。",
                FeatureName: null,
                RowIndex: null));
        }

        // 5. float 值本身校验：NaN / Infinity（防止上游脏数据）
        //    仅在长度一致时校验，避免双重违规刷屏。
        if (batch.Values.Length == batch.RowCount * batch.FeatureCount
            && batch.RowCount > 0
            && batch.FeatureCount > 0)
        {
            var values = batch.Values.Span;
            for (var row = 0; row < batch.RowCount; row++)
            {
                var offset = row * batch.FeatureCount;
                for (var col = 0; col < batch.FeatureCount; col++)
                {
                    var v = values[offset + col];
                    if (float.IsNaN(v) || float.IsInfinity(v))
                    {
                        violations.Add(Error(
                            "batch.value_not_finite",
                            $"FeatureBatch[{row},{col}]={v} 不是有限数值（NaN/Infinity）。",
                            FeatureName: col < batch.FeatureNames.Count ? batch.FeatureNames[col] : null,
                            RowIndex: row));
                    }
                }
            }
        }

        return BuildResult(violations);
    }

    /// <inheritdoc />
    public FeatureSchemaValidationResult ValidateBatch(FeatureSchema schema, IReadOnlyList<FeatureVector> inputs)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(inputs);

        var allViolations = new List<FeatureSchemaViolation>();
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            if (input is null)
            {
                allViolations.Add(Error(
                    "input.null_vector",
                    $"inputs[{i}] 为 null。",
                    FeatureName: null,
                    RowIndex: i));
                continue;
            }

            var perRow = new List<FeatureSchemaViolation>();
            ValidateSchemaVersion(schema, input.SchemaVersion, rowIndex: i, perRow);
            ValidateFeatureNames(schema, input.Values, rowIndex: i, perRow);
            allViolations.AddRange(perRow);
        }

        return BuildResult(allViolations);
    }

    // -----------------------------------------------------------------------
    // 内部：SchemaVersion 校验
    // -----------------------------------------------------------------------

    private void ValidateSchemaVersion(FeatureSchema schema, string inputSchemaVersion, int? rowIndex, List<FeatureSchemaViolation> violations)
    {
        if (string.IsNullOrWhiteSpace(inputSchemaVersion))
        {
            violations.Add(Error(
                "schema.version_missing",
                $"输入 SchemaVersion 为空；期望 '{schema.Version}'。",
                FeatureName: null,
                RowIndex: rowIndex));
            return;
        }

        if (!string.Equals(schema.Version, inputSchemaVersion, StringComparison.Ordinal))
        {
            violations.Add(Error(
                "schema.version_mismatch",
                $"输入 SchemaVersion='{inputSchemaVersion}' 与 schema.Version='{schema.Version}' 不一致。",
                FeatureName: null,
                RowIndex: rowIndex));
        }
    }

    // -----------------------------------------------------------------------
    // 内部：特征名称 + 必填 + 类型 + 默认值校验
    // -----------------------------------------------------------------------

    private void ValidateFeatureNames(
        FeatureSchema schema,
        IReadOnlyDictionary<string, object> values,
        int? rowIndex,
        List<FeatureSchemaViolation> violations)
    {
        ArgumentNullException.ThrowIfNull(values);

        // 1. 未知特征检查（严格模式）
        if (_options.StrictUnknownFeatures)
        {
            var knownNames = new HashSet<string>(
                schema.Features.Select(f => f.Name),
                StringComparer.Ordinal);

            foreach (var kv in values)
            {
                if (!knownNames.Contains(kv.Key))
                {
                    violations.Add(Error(
                        "feature.unknown",
                        $"输入包含 schema 中未定义的特征 '{kv.Key}'。",
                        FeatureName: kv.Key,
                        RowIndex: rowIndex));
                }
            }
        }

        // 2. 必填 + 类型 + 默认值检查（按 schema 顺序）
        foreach (var feature in schema.Features)
        {
            if (values.TryGetValue(feature.Name, out var value))
            {
                // 值存在：检查 null 与类型可转换性
                if (value is null)
                {
                    if (feature.IsRequired)
                    {
                        // 必填特征值为 null：尝试默认值回退
                        TryDefaultValueFallback(feature, rowIndex, violations);
                    }
                    else
                    {
                        // 可选特征值为 null：合法，跳过类型检查
                        violations.Add(Info(
                            "feature.optional_null",
                            $"可选特征 '{feature.Name}' 值为 null；将以默认值替代。",
                            FeatureName: feature.Name,
                            RowIndex: rowIndex));
                    }
                    continue;
                }

                if (!IsConvertibleTo(value, feature.Type))
                {
                    violations.Add(Error(
                        "feature.type_mismatch",
                        $"特征 '{feature.Name}' 值 '{value}' (类型 {value.GetType().Name}) 无法转换为 {feature.Type}。",
                        FeatureName: feature.Name,
                        RowIndex: rowIndex));
                }
            }
            else
            {
                // 值缺失
                if (feature.IsRequired)
                {
                    // 必填缺失：尝试默认值回退
                    TryDefaultValueFallback(feature, rowIndex, violations);
                }
                else
                {
                    // 可选缺失：合法，记录 Info
                    violations.Add(Info(
                        "feature.optional_missing",
                        $"可选特征 '{feature.Name}' 缺失；将使用默认值或忽略。",
                        FeatureName: feature.Name,
                        RowIndex: rowIndex));
                }
            }
        }
    }

    private void TryDefaultValueFallback(
        FeatureDefinition feature,
        int? rowIndex,
        List<FeatureSchemaViolation> violations)
    {
        if (string.IsNullOrEmpty(feature.DefaultValue))
        {
            // 无默认值：必填缺失 → Error
            violations.Add(Error(
                "feature.missing_required",
                $"必填特征 '{feature.Name}' 缺失且 schema 未提供 DefaultValue。",
                FeatureName: feature.Name,
                RowIndex: rowIndex));
            return;
        }

        // 默认值字符串必须可解析为目标类型
        if (!IsConvertibleTo(feature.DefaultValue, feature.Type))
        {
            violations.Add(Error(
                "feature.default_value_unparseable",
                $"必填特征 '{feature.Name}' 缺失，且 DefaultValue='{feature.DefaultValue}' 无法解析为 {feature.Type}。",
                FeatureName: feature.Name,
                RowIndex: rowIndex));
            return;
        }

        // 默认值可解析：Warning（提示上游应直接提供该特征）
        violations.Add(Warning(
            "feature.applied_default",
            $"必填特征 '{feature.Name}' 缺失，已应用 DefaultValue='{feature.DefaultValue}'；上游应直接提供该特征。",
            FeatureName: feature.Name,
            RowIndex: rowIndex));
    }

    // -----------------------------------------------------------------------
    // 内部：类型可转换性
    // -----------------------------------------------------------------------

    private static bool IsConvertibleTo(object value, FeatureType type)
    {
        return type switch
        {
            FeatureType.Numeric => IsNumeric(value),
            FeatureType.Categorical => value is string || value is char,
            FeatureType.Boolean => value is bool,
            FeatureType.Text => value is string || value is char,
            _ => false
        };
    }

    private static bool IsConvertibleTo(string stringValue, FeatureType type)
    {
        return type switch
        {
            FeatureType.Numeric => double.TryParse(stringValue, out _),
            FeatureType.Categorical => stringValue.Length > 0,
            FeatureType.Boolean => bool.TryParse(stringValue, out _),
            FeatureType.Text => stringValue.Length > 0,
            _ => false
        };
    }

    private static bool IsNumeric(object value)
    {
        return value switch
        {
            float or double or decimal => true,
            int or long or short or sbyte => true,
            uint or ulong or ushort or byte => true,
            string s when double.TryParse(s, out _) => true,
            _ => false
        };
    }

    // -----------------------------------------------------------------------
    // 辅助：违规构造
    // -----------------------------------------------------------------------

    private static FeatureSchemaViolation Error(string code, string message, string? FeatureName, int? RowIndex)
        => new()
        {
            Severity = FeatureSchemaViolationSeverity.Error,
            Code = code,
            Message = message,
            FeatureName = FeatureName,
            RowIndex = RowIndex
        };

    private static FeatureSchemaViolation Warning(string code, string message, string? FeatureName, int? RowIndex)
        => new()
        {
            Severity = FeatureSchemaViolationSeverity.Warning,
            Code = code,
            Message = message,
            FeatureName = FeatureName,
            RowIndex = RowIndex
        };

    private static FeatureSchemaViolation Info(string code, string message, string? FeatureName, int? RowIndex)
        => new()
        {
            Severity = FeatureSchemaViolationSeverity.Info,
            Code = code,
            Message = message,
            FeatureName = FeatureName,
            RowIndex = RowIndex
        };

    private static FeatureSchemaValidationResult BuildResult(IReadOnlyList<FeatureSchemaViolation> violations)
    {
        var errorCount = violations.Count(v => v.Severity == FeatureSchemaViolationSeverity.Error);
        var isValid = errorCount == 0;

        string? error = null;
        if (!isValid)
        {
            var errorMessages = violations
                .Where(v => v.Severity == FeatureSchemaViolationSeverity.Error)
                .Select(v => v.RowIndex.HasValue
                    ? $"[row={v.RowIndex}, {v.Code}] {v.Message}"
                    : $"[{v.Code}] {v.Message}");
            error = string.Join("; ", errorMessages);
        }

        return new FeatureSchemaValidationResult
        {
            IsValid = isValid,
            Error = error,
            Violations = violations
        };
    }
}

/// <summary>
/// R29 WP-A-4：FeatureSchemaValidator 配置选项。
/// </summary>
public sealed class FeatureSchemaValidatorOptions
{
    /// <summary>
    /// 默认配置：严格模式开启（拒绝未知特征）。
    /// </summary>
    public static FeatureSchemaValidatorOptions Default { get; } = new();

    /// <summary>
    /// 是否启用严格未知特征检查（默认 true）。
    /// 关闭后输入可包含 schema 未定义的特征（向后兼容旧 producer）。
    /// </summary>
    public bool StrictUnknownFeatures { get; init; } = true;
}
