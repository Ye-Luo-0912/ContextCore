using ContextCore.Abstractions;

namespace ContextCore.Core.Services.ModelExecution;

// ===========================================================================
// R28-F P3-2：Default Inference Result Validator
//
// 目标：在 Scorer 把模型分数应用到 Allocator 排序键之前，对 BatchInferenceResult
// 执行严格验证，防止异常模型输出污染排序。
//
// 验证项：
//   1. Succeeded=true（推理本身成功）
//   2. Outputs.Count == 输入行数（Count 一致）
//   3. 每条 output 的 Score 不是 NaN / Infinity
//   4. 每条 output 的 Confidence 不是 NaN / Infinity
//   5. Confidence 在 [0, 1]
//   6. SchemaVersion 与输入一致（仅 FeatureBatch 重载可校验）
//   7. timeout 真实执行（Duration > 0 当 TimeoutMs > 0）
//
// 设计原则：
//   - 不抛异常：返回结构化 ValidationResult，由调用方决定降级策略。
//   - 完整违规清单：聚合所有违规（不止第一条），便于诊断。
//   - 验证顺序无关：每条独立检查。
// ===========================================================================

/// <summary>
/// R28-F P3-2：默认推理输出验证器。
/// </summary>
public sealed class DefaultInferenceResultValidator : IInferenceResultValidator
{
    private const double Epsilon = 1e-9;

    /// <inheritdoc />
    public InferenceValidationResult Validate(BatchInferenceRequest request, BatchInferenceResult result)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);

        var violations = new List<string>();

        // 1. 推理本身必须成功
        if (!result.Succeeded)
        {
            violations.Add($"推理未成功：{result.Error ?? "(no error)"}");
            // 失败时无需继续校验输出
            return Build(false, violations);
        }

        // 2. Outputs.Count == Inputs.Count
        var expectedCount = request.Inputs?.Count ?? 0;
        if (result.Outputs.Count != expectedCount)
        {
            violations.Add(
                $"Outputs.Count({result.Outputs.Count}) != Inputs.Count({expectedCount})");
        }

        ValidateOutputsInternal(result.Outputs, violations);

        // 7. timeout 真实执行（Duration > 0 当 TimeoutMs > 0）
        if (request.TimeoutMs > 0 && result.Duration <= TimeSpan.Zero)
        {
            violations.Add(
                $"TimeoutMs={request.TimeoutMs} 但 Duration={result.Duration}；疑似未真实执行");
        }

        return Build(violations.Count == 0, violations);
    }

    /// <inheritdoc />
    public InferenceValidationResult Validate(FeatureBatch batch, BatchInferenceResult result)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(result);

        var violations = new List<string>();

        if (!result.Succeeded)
        {
            violations.Add($"推理未成功：{result.Error ?? "(no error)"}");
            return Build(false, violations);
        }

        // 2. Outputs.Count == RowCount
        if (result.Outputs.Count != batch.RowCount)
        {
            violations.Add(
                $"Outputs.Count({result.Outputs.Count}) != batch.RowCount({batch.RowCount})");
        }

        ValidateOutputsInternal(result.Outputs, violations);

        // 6. SchemaVersion 一致性
        // 注意：BatchInferenceResult 不携带 SchemaVersion；通过 Error 字段约定携带不一致时由引擎写入。
        // 这里无法直接校验（result 不含 schema version），留给调用方在 Scorer 层比对 batch.SchemaVersion。
        // （若引擎在 Error 中报告了 schema 不匹配，已在 #1 路径捕获。）

        return Build(violations.Count == 0, violations);
    }

    /// <summary>
    /// R28-F P3-2：ScoreWeights 验证（w_d / w_m 非负且和为预期值）。
    /// 调用方在 Scorer 中应用 FinalScore = w_d * Det + w_m * Model 前调用此方法。
    /// </summary>
    /// <param name="deterministicWeight">w_d（DeterministicWeight）。</param>
    /// <param name="modelWeight">w_m（ModelWeight）。</param>
    /// <param name="expectedSum">预期和（默认 1.0）；w_d + w_m 应在 expectedSum ± Epsilon 内。</param>
    /// <returns>验证结果。</returns>
    /// <remarks>
    /// 当 w_d + w_m ≠ 1.0 时，FinalScore 会被错误地放大或缩小，导致 Allocator 排序失真。
    /// 此方法独立于 Validate(request, result)，可单独调用。
    /// </remarks>
    public InferenceValidationResult ValidateScoreWeights(
        double deterministicWeight,
        double modelWeight,
        double expectedSum = 1.0)
    {
        var violations = new List<string>();

        if (double.IsNaN(deterministicWeight) || double.IsInfinity(deterministicWeight))
        {
            violations.Add($"DeterministicWeight={deterministicWeight} 不是有限值");
        }
        else if (deterministicWeight < 0.0)
        {
            violations.Add($"DeterministicWeight={deterministicWeight} 为负数");
        }

        if (double.IsNaN(modelWeight) || double.IsInfinity(modelWeight))
        {
            violations.Add($"ModelWeight={modelWeight} 不是有限值");
        }
        else if (modelWeight < 0.0)
        {
            violations.Add($"ModelWeight={modelWeight} 为负数");
        }

        if (violations.Count == 0)
        {
            var actualSum = deterministicWeight + modelWeight;
            if (Math.Abs(actualSum - expectedSum) > Epsilon)
            {
                violations.Add(
                    $"w_d + w_m = {actualSum}（预期 {expectedSum}）；FinalScore 会被错误缩放");
            }
        }

        return Build(violations.Count == 0, violations);
    }

    private static void ValidateOutputsInternal(IReadOnlyList<InferenceOutput> outputs, List<string> violations)
    {
        for (var i = 0; i < outputs.Count; i++)
        {
            var output = outputs[i];

            // 3. Score 不是 NaN / Infinity
            if (double.IsNaN(output.Score) || double.IsInfinity(output.Score))
            {
                violations.Add($"Outputs[{i}].Score={output.Score} 不是有限值");
            }

            // 4. Confidence 不是 NaN / Infinity
            if (double.IsNaN(output.Confidence) || double.IsInfinity(output.Confidence))
            {
                violations.Add($"Outputs[{i}].Confidence={output.Confidence} 不是有限值");
                continue; // 范围检查无意义
            }

            // 5. Confidence 在 [0, 1]
            if (output.Confidence < 0.0 || output.Confidence > 1.0)
            {
                violations.Add(
                    $"Outputs[{i}].Confidence={output.Confidence} 超出 [0,1] 范围");
            }
        }
    }

    private static InferenceValidationResult Build(bool isValid, List<string> violations)
    {
        return new InferenceValidationResult
        {
            IsValid = isValid,
            Error = isValid ? null : string.Join("; ", violations),
            Violations = violations
        };
    }
}
