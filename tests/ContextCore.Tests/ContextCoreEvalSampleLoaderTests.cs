using ContextCore.Abstractions.Models;
using ContextCore.Core;

namespace ContextCore.Tests;

/// <summary>覆盖 A3 评测样本读取器。</summary>
[TestClass]
public sealed class ContextCoreEvalSampleLoaderTests
{
    [TestMethod]
    public async Task ContextEvalSampleLoader_ShouldLoadSeedFilesAndCountModes()
    {
        var root = Path.Combine(Path.GetTempPath(), "contextcore-eval-loader-" + Guid.NewGuid().ToString("N"));
        try
        {
            var chatDir = Path.Combine(root, "chat");
            var projectDir = Path.Combine(root, "project");
            Directory.CreateDirectory(chatDir);
            Directory.CreateDirectory(projectDir);
            await File.WriteAllTextAsync(
                Path.Combine(chatDir, "seed_samples.json"),
                """
                [
                  {
                    "id": "chat-1",
                    "query": "继续中文偏好设置。",
                    "mode": "ChatMode",
                    "mustHit": ["pref:zh"],
                    "mustNotHit": [],
                    "expectedScopes": ["session"],
                    "expectedEntities": ["中文偏好"],
                    "expectedConstraints": ["保持中文"],
                    "expectedUncertainties": [],
                    "goldenNotes": "应命中中文偏好。"
                  }
                ]
                """);
            await File.WriteAllTextAsync(
                Path.Combine(projectDir, "seed_samples.json"),
                """
                [
                  {
                    "id": "project-1",
                    "query": "检查 A3 评测集进度。",
                    "mode": "ProjectMode",
                    "mustHit": ["todo:A3"],
                    "mustNotHit": ["todo:P0"],
                    "expectedScopes": ["collection"],
                    "expectedEntities": ["A3"],
                    "expectedConstraints": ["报告中文"],
                    "expectedUncertainties": ["样本数量待确认"],
                    "goldenNotes": "应聚焦 A3。"
                  }
                ]
                """);

            var result = await new ContextEvalSampleLoader().LoadAsync(root);

            Assert.AreEqual(2, result.Samples.Count);
            Assert.AreEqual(2, result.Files.Count);
            Assert.AreEqual(1, result.ModeCounts["ChatMode"]);
            Assert.AreEqual(1, result.ModeCounts["ProjectMode"]);
            Assert.IsTrue(result.Samples.All(sample => sample.Metadata.ContainsKey("sourceFile")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task ContextEvalSampleLoader_ShouldLoadProjectEvalSeedSamples()
    {
        var repoRoot = FindRepoRoot();
        var evalRoot = Path.Combine(repoRoot, "eval", "contexts");
        var result = await new ContextEvalSampleLoader().LoadAsync(evalRoot);

        // A3 阶段的最低验收线：五类真实中文场景都要达到可回归的基础数量。
        AssertModeAtLeast(result, "ChatMode", 30);
        AssertModeAtLeast(result, "ProjectMode", 30);
        AssertModeAtLeast(result, "NovelMode", 30);
        AssertModeAtLeast(result, "AutomationMode", 20);
        AssertModeAtLeast(result, "CodingMode", 20);
    }

    private static void AssertModeAtLeast(ContextEvalSampleLoadResult result, string mode, int minimum)
    {
        Assert.IsTrue(
            result.ModeCounts.TryGetValue(mode, out var count),
            $"评测样本缺少模式：{mode}");
        Assert.IsTrue(
            count >= minimum,
            $"{mode} 样本数量不足，当前 {count}，最低要求 {minimum}。");
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ContextCore.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        Assert.Fail("未找到 ContextCore.sln，无法定位仓库根目录。");
        return "";
    }
}
