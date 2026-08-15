namespace ContextCore.Tests;

/// <summary>
/// 生产代码规模预算守卫。统计 <c>src/</c> 下全部 <c>.cs</c> 源文件行数（排除 bin/obj），
/// 防止生产代码无界增长（ROADMAP 长期目标：不删正式质量能力时生产源码净减 ≥20%）。
/// 预算 = 实测基线 + 容差：新增代码需以删除等量代码为代价，否则失败。
/// </summary>
[TestClass]
[TestCategory("Architecture")]
[TestCategory("Budget")]
public sealed class ProductionLocBudgetTests
{
    // 预算上限 = 实测基线 + 5% 容差。更新方式：确认生产代码变更属有意收敛后，按实测值重设。
    // 当前基线（2026-08 实测，排除 bin/obj）：840 文件 / 243,325 行。
    private const int ProductionLocBudget = 256000;

    [TestMethod]
    public void ProductionSourceLines_WithinBudget()
    {
        var repoRoot = FindRepoRoot();
        var srcDir = Path.Combine(repoRoot, "src");
        Assert.IsTrue(Directory.Exists(srcDir), $"src 目录不存在：{srcDir}");

        long total = 0;
        var fileCount = 0;
        foreach (var file in Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories))
        {
            if (IsExcludedPath(file))
            {
                continue;
            }
            fileCount++;
            total += CountLines(file);
        }

        Console.WriteLine($"[ProductionLocBudget] src .cs files = {fileCount}, lines = {total}");
        Assert.IsTrue(
            total <= ProductionLocBudget,
            $"生产源码行数超预算：{total} > {ProductionLocBudget}（基线 243,325 + 5% 容差）。" +
            "新增生产代码需以删除等量代码为代价（净减目标 ≥20%）。");
    }

    private static bool IsExcludedPath(string fullPath)
    {
        var normalized = fullPath.Replace('\\', '/');
        return normalized.Contains("/bin/", StringComparison.Ordinal)
            || normalized.Contains("/obj/", StringComparison.Ordinal);
    }

    private static long CountLines(string path)
    {
        long count = 0;
        using var reader = new StreamReader(path);
        while (reader.ReadLine() is not null)
        {
            count++;
        }
        return count;
    }

    private static string FindRepoRoot()
    {
        // 测试运行目录通常为 tests/ContextCore.Tests/bin/Release/net10.0/
        // 向上查找直到找到包含 src/ 和 tests/ 的目录
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
}
