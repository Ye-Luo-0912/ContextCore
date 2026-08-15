using ContextCore.Evaluation.Quality;

namespace ContextCore.Tests;

/// <summary>
/// 分层评测集契约测试。
/// <para>
/// 验证目标：
/// 1. 划分确定性：同一声明重复构建得到完全相同的划分与计数
/// 2. 划分完备性：每条样本恰好落一个划分，无重复
/// 3. 覆盖门：缺少任一固定维度时构建失败，完整时通过
/// 4. 版本不可变：同版本已存在时拒绝覆盖，--force 才允许重建
/// 5. 测试集隔离：无 allowTest 时读取 test 抛异常；train/dev 无需开关
/// 6. 可追溯性：每条样本携带来源、标注理由、版本
/// 7. 校验：VerifyAsync 能识别损坏；仓库内 v1 数据集通过校验
/// 8. 声明校验：重复 ID / 空证据 / 未知维度拒绝
/// </summary>
[TestClass]
[TestCategory("LR1A")]
public sealed class EvalDatasetContractTests
{
    private const string Version = "v1";

    // =========================================================================
    // 1. 划分确定性
    // =========================================================================

    [TestMethod]
    public async Task Build_SameDeclarations_IdenticalSplitAndCounts()
    {
        using var temp = new TempDir();
        var declarations = BuildStarterDeclarations();

        var first = await EvalDatasetBuilder.BuildAsync(declarations, Version, temp.Path);
        var second = await EvalDatasetBuilder.BuildAsync(declarations, Version, temp.Path, force: true);

        CollectionAssert.AreEqual(
            first.SplitCounts.OrderBy(kv => kv.Key).ToArray(),
            second.SplitCounts.OrderBy(kv => kv.Key).ToArray(),
            "同一声明重复构建应得到相同划分计数。");

        var firstSamples = await LoadAllAsync(VersionDir(temp));
        var secondSamples = await LoadAllAsync(VersionDir(temp));
        Assert.AreEqual(
            string.Join(",", firstSamples.Select(s => $"{s.SampleId}:{s.Split}").OrderBy(x => x)),
            string.Join(",", secondSamples.Select(s => $"{s.SampleId}:{s.Split}").OrderBy(x => x)),
            "每条样本的划分必须逐位一致。");
    }

    // =========================================================================
    // 2. 划分完备性
    // =========================================================================

    [TestMethod]
    public async Task Build_EverySampleInExactlyOneSplit()
    {
        using var temp = new TempDir();
        var declarations = BuildStarterDeclarations();

        await EvalDatasetBuilder.BuildAsync(declarations, Version, temp.Path);
        var all = await LoadAllAsync(VersionDir(temp));

        Assert.AreEqual(declarations.Count, all.Count, "样本总数与声明一致。");
        Assert.AreEqual(
            declarations.Count,
            all.Select(s => s.SampleId).Distinct(StringComparer.Ordinal).Count(),
            "样本 ID 不应重复。");
        Assert.IsTrue(all.All(s => s.Split is EvalDatasetBuilder.Train or EvalDatasetBuilder.Dev or EvalDatasetBuilder.Test),
            "每条样本必须落在合法划分。");
    }

    // =========================================================================
    // 3. 覆盖门
    // =========================================================================

    [TestMethod]
    public async Task Build_MissingCoverageDimension_Fails()
    {
        using var temp = new TempDir();
        var incomplete = BuildStarterDeclarations()
            .Where(d => d.CoverageDimensions.Contains(EvalCoverageDimensions.ProviderParity) is false)
            .ToArray();

        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => EvalDatasetBuilder.BuildAsync(incomplete, Version, temp.Path));
        StringAssert.Contains(ex.Message, "覆盖门未通过");
        StringAssert.Contains(ex.Message, EvalCoverageDimensions.ProviderParity);
    }

    [TestMethod]
    public async Task Build_AllDimensionsCovered_CoverageComplete()
    {
        using var temp = new TempDir();
        await EvalDatasetBuilder.BuildAsync(BuildStarterDeclarations(), Version, temp.Path);

        var manifest = await ReadManifestAsync(VersionDir(temp));
        Assert.IsTrue(manifest.CoverageComplete);
        foreach (var dim in EvalCoverageDimensions.All)
        {
            Assert.IsTrue(manifest.CoverageCounts[dim] > 0, $"维度 {dim} 应有样本。");
        }
    }

    // =========================================================================
    // 4. 版本不可变
    // =========================================================================

    [TestMethod]
    public async Task Build_ExistingVersion_RefusesOverwrite_UnlessForce()
    {
        using var temp = new TempDir();
        var declarations = BuildStarterDeclarations();

        await EvalDatasetBuilder.BuildAsync(declarations, Version, temp.Path);

        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => EvalDatasetBuilder.BuildAsync(declarations, Version, temp.Path));
        StringAssert.Contains(ex.Message, "拒绝覆盖");

        var manifest = await EvalDatasetBuilder.BuildAsync(declarations, Version, temp.Path, force: true);
        Assert.AreEqual(Version, manifest.Version, "--force 应允许重建。");
    }

    // =========================================================================
    // 5. 测试集隔离
    // =========================================================================

    [TestMethod]
    public async Task Access_TestSplit_RequiresAllowTest()
    {
        using var temp = new TempDir();
        await EvalDatasetBuilder.BuildAsync(BuildStarterDeclarations(), Version, temp.Path);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => EvalDatasetAccess.LoadSplitAsync(VersionDir(temp), EvalDatasetBuilder.Test),
            "无 allowTest 时读取 test 必须被隔离门拦截。");

        var test = await EvalDatasetAccess.LoadSplitAsync(VersionDir(temp), EvalDatasetBuilder.Test, allowTest: true);
        Assert.AreEqual((await ReadManifestAsync(VersionDir(temp))).SplitCounts[EvalDatasetBuilder.Test], test.Count,
            "allowTest=true 时应返回 test 划分全部样本。");
    }

    [TestMethod]
    public async Task Access_TrainDev_NoSwitchNeeded_ExcludesTest()
    {
        using var temp = new TempDir();
        await EvalDatasetBuilder.BuildAsync(BuildStarterDeclarations(), Version, temp.Path);

        var trainDev = await EvalDatasetAccess.LoadTrainDevAsync(VersionDir(temp));
        var manifest = await ReadManifestAsync(VersionDir(temp));
        Assert.AreEqual(
            manifest.SplitCounts[EvalDatasetBuilder.Train] + manifest.SplitCounts[EvalDatasetBuilder.Dev],
            trainDev.Count,
            "train/dev 无需开关即可读取。");
        Assert.IsFalse(trainDev.Any(s => s.Split == EvalDatasetBuilder.Test), "训练/调参入口不得包含 test 样本。");
    }

    // =========================================================================
    // 6. 可追溯性
    // =========================================================================

    [TestMethod]
    public async Task EverySample_HasProvenance()
    {
        using var temp = new TempDir();
        await EvalDatasetBuilder.BuildAsync(BuildStarterDeclarations(), Version, temp.Path);

        var all = await LoadAllAsync(VersionDir(temp));
        foreach (var sample in all)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(sample.Source), $"样本 {sample.SampleId} 应有来源。");
            Assert.IsFalse(string.IsNullOrWhiteSpace(sample.AnnotationReason), $"样本 {sample.SampleId} 应有标注理由。");
            Assert.AreEqual(Version, sample.Version, $"样本 {sample.SampleId} 应携带版本。");
            Assert.IsFalse(string.IsNullOrWhiteSpace(sample.Query));
        }
    }

    // =========================================================================
    // 7. 校验
    // =========================================================================

    [TestMethod]
    public async Task Verify_CorruptedDataset_ReportsErrors()
    {
        using var temp = new TempDir();
        await EvalDatasetBuilder.BuildAsync(BuildStarterDeclarations(), Version, temp.Path);

        var ok = await EvalDatasetBuilder.VerifyAsync(VersionDir(temp));
        Assert.IsTrue(ok.Ok, "完整数据集应通过校验。");

        File.Delete(Path.Combine(VersionDir(temp), "dev.jsonl"));
        var broken = await EvalDatasetBuilder.VerifyAsync(VersionDir(temp));
        Assert.IsFalse(broken.Ok, "缺少划分文件应校验失败。");
        Assert.IsTrue(broken.Errors.Any(e => e.Contains("dev.jsonl")), "错误信息应指出缺失文件。");
    }

    [TestMethod]
    public async Task CheckedInDataset_V1_PassesVerification()
    {
        var repoRoot = FindRepoRoot();
        var versionDir = Path.Combine(repoRoot, "eval", "contexts", "quality", "v1");

        var result = await EvalDatasetBuilder.VerifyAsync(versionDir);
        Assert.IsTrue(result.Ok, "仓库内 v1 数据集应通过校验：" + string.Join("；", result.Errors));
        Assert.IsNotNull(result.Manifest);
        Assert.AreEqual(18, result.Manifest!.SampleCount, "v1 起步集样本数固定。");
        Assert.IsTrue(result.Manifest.CoverageComplete);
    }

    [TestMethod]
    public async Task CheckedInDataset_V1_SplitAssignment_IsStable()
    {
        var repoRoot = FindRepoRoot();
        var versionDir = Path.Combine(repoRoot, "eval", "contexts", "quality", "v1");

        var test = await EvalDatasetAccess.LoadSplitAsync(versionDir, EvalDatasetBuilder.Test, allowTest: true);
        var testIds = test.Select(s => s.SampleId).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(new[] { "lr1a-gr-001", "lr1a-pp-002" }, testIds,
            "SHA-256 稳定划分下 test 划分内容固定（同输入重复执行一致结论）。");
    }

    // =========================================================================
    // 8. 声明校验
    // =========================================================================

    [TestMethod]
    public async Task Build_InvalidDeclarations_Rejected()
    {
        using var temp = new TempDir();
        var valid = BuildStarterDeclarations();

        var duplicate = valid.Concat([valid[0]]).ToArray();
        var ex1 = await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => EvalDatasetBuilder.BuildAsync(duplicate, Version, temp.Path));
        StringAssert.Contains(ex1.Message, "样本 ID 重复");

        var emptyEvidence = new[]
        {
            new DeclaredEvalSample
            {
                SampleId = "bad-1",
                Query = "q",
                Source = "test",
                AnnotationReason = "test",
                Evidence = new QualityEvidenceExpectation(),
                CoverageDimensions = [EvalCoverageDimensions.ExactKeyword]
            }
        };
        var ex2 = await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => EvalDatasetBuilder.BuildAsync(emptyEvidence, Version, temp.Path));
        StringAssert.Contains(ex2.Message, "未声明任何期望证据");

        var unknownDim = new[]
        {
            new DeclaredEvalSample
            {
                SampleId = "bad-2",
                Query = "q",
                Source = "test",
                AnnotationReason = "test",
                Evidence = new QualityEvidenceExpectation { RequiredEvidenceIds = ["a"] },
                CoverageDimensions = ["not-a-dimension"]
            }
        };
        var ex3 = await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => EvalDatasetBuilder.BuildAsync(unknownDim, Version, temp.Path));
        StringAssert.Contains(ex3.Message, "未知覆盖维度");
    }

    // =========================================================================
    // 辅助
    // =========================================================================

    private static IReadOnlyList<DeclaredEvalSample> BuildStarterDeclarations()
    {
        var declarations = new List<DeclaredEvalSample>();
        foreach (var dim in EvalCoverageDimensions.All)
        {
            declarations.Add(new DeclaredEvalSample
            {
                SampleId = $"t-{dim}-a",
                Query = $"查询 {dim} 维度样本 A",
                Source = "unit-test",
                AnnotationReason = $"覆盖维度 {dim} 的测试样本 A",
                Evidence = new QualityEvidenceExpectation { RequiredEvidenceIds = [$"q:{dim}-a"] },
                CoverageDimensions = [dim]
            });
            declarations.Add(new DeclaredEvalSample
            {
                SampleId = $"t-{dim}-b",
                Query = $"查询 {dim} 维度样本 B",
                Source = "unit-test",
                AnnotationReason = $"覆盖维度 {dim} 的测试样本 B",
                Evidence = new QualityEvidenceExpectation { RequiredEvidenceIds = [$"q:{dim}-b"] },
                CoverageDimensions = [dim]
            });
        }
        return declarations;
    }

    private static async Task<IReadOnlyList<VersionedEvalSample>> LoadAllAsync(string versionDir)
    {
        var train = await EvalDatasetAccess.LoadSplitAsync(versionDir, EvalDatasetBuilder.Train);
        var dev = await EvalDatasetAccess.LoadSplitAsync(versionDir, EvalDatasetBuilder.Dev);
        var test = await EvalDatasetAccess.LoadSplitAsync(versionDir, EvalDatasetBuilder.Test, allowTest: true);
        return train.Concat(dev).Concat(test).ToArray();
    }

    private static async Task<EvalDatasetManifest> ReadManifestAsync(string versionDir)
    {
        var result = await EvalDatasetBuilder.VerifyAsync(versionDir);
        Assert.IsTrue(result.Ok, string.Join("；", result.Errors));
        return result.Manifest!;
    }

    private static string VersionDir(TempDir temp) => System.IO.Path.Combine(temp.Path, Version);

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, "src")) && Directory.Exists(Path.Combine(dir, "tests")))
            {
                return dir;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
        return AppContext.BaseDirectory;
    }

    /// <summary>测试临时目录（释放时递归删除）。</summary>
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cc-lr1a-tests", Guid.NewGuid().ToString("N"));

        public TempDir()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // 清理失败不影响测试结论。
            }
        }
    }
}



