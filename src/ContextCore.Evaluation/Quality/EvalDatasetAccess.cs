using System.Text.Json;

namespace ContextCore.Evaluation.Quality;

/// <summary>
/// 数据集读取访问策略（测试隔离门）。
/// <para>
/// 测试集不得被调参或训练直接读取：训练/调参入口只能通过
/// <see cref="LoadTrainDevAsync"/> 打开 train/dev；读取 test 必须显式
/// <see cref="LoadSplitAsync"/> 且 allowTest=true，且只有评测执行器使用该开关。
/// </para>
/// </summary>
public static class EvalDatasetAccess
{
    /// <summary>
    /// 加载指定划分。split 为 test 时 requireTest 必须为 true，否则抛异常。
    /// </summary>
    public static async Task<IReadOnlyList<VersionedEvalSample>> LoadSplitAsync(
        string versionDir,
        string split,
        bool allowTest = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(split);

        if (string.Equals(split, EvalDatasetBuilder.Test, StringComparison.Ordinal) && !allowTest)
        {
            throw new InvalidOperationException(
                "测试集隔离门：调参/训练入口不得直接读取 test 划分；评测执行器需显式 allowTest=true。");
        }

        var path = Path.Combine(versionDir, $"{split}.jsonl");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"划分文件不存在：{path}", path);
        }

        var samples = new List<VersionedEvalSample>();
        foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            var sample = JsonSerializer.Deserialize<VersionedEvalSample>(line);
            if (sample is not null)
            {
                samples.Add(sample);
            }
        }
        return samples;
    }

    /// <summary>训练/调参入口的唯一天然入口：只返回 train 与 dev。</summary>
    public static async Task<IReadOnlyList<VersionedEvalSample>> LoadTrainDevAsync(
        string versionDir,
        CancellationToken cancellationToken = default)
    {
        var train = await LoadSplitAsync(versionDir, EvalDatasetBuilder.Train, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var dev = await LoadSplitAsync(versionDir, EvalDatasetBuilder.Dev, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return train.Concat(dev).ToArray();
    }
}
