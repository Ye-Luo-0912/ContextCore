using System.IO;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextCore.Tests;

// ===========================================================================
// Benchmark CI 验收测试
//
// 验证 Benchmark CI 基线的两个硬门控：
//   1. 已提交的 benchmark JSON 基线每个 case 的样本数 N >= 15
//      （BenchmarkOutputConfig.MinIterationCount=15 的下游证据）
//      — N < 15 时 StdErr 未收敛，benchmark-compare.sh 的置信区间检查不可靠，
//        且 MIN_SAMPLE_COUNT=5 会跳过 N<5 的 case，导致回归检测被静默跳过。
//   2. benchmark-selftest.yml 注入回归自检工作流结构完整
//      （注入 15% latency + 10% alloc 回归 → exit 1 + regression_found=true，
//        并覆盖四层假阳性抑制参数：NOISE_FLOOR_PCT / MIN_SAMPLE_COUNT /
//        CONFIDENCE_SIGMA / IO_BOUND_THRESHOLD_PCT）
//
// 设计原则：
//   - 纯文件结构验证，不实际运行 benchmark / CI
//   - 基线 JSON 缺失或解析失败时 Assert.Inconclusive（不阻塞构建）
//   - 复用 FindRepoRoot() 模式定位 repo root（参考 R29_FinalClosureAcceptanceTests）
// ===========================================================================

[TestClass]
[TestCategory("R29-Hard-Gate")]
[TestCategory("Benchmark-CI")]
public sealed class R29H_BenchmarkCIAcceptanceTests
{
    // =======================================================================
    // 测试1：基线 JSON 每个 case 的样本数 N >= 15
    // =======================================================================
    [TestMethod]
    public void Benchmark_Baseline_Contains_AtLeast_15_Samples_PerCase()
    {
        // 验证：已提交的 benchmark JSON 基线中，每个 case 的 Statistics.N >= 15
        // （BenchmarkOutputConfig.MinIterationCount=15 的下游证据）
        var repoRoot = FindRepoRoot();
        var resultsDir = Path.Combine(repoRoot, "benchmarks", "results", "results");

        if (!Directory.Exists(resultsDir))
        {
            Assert.Inconclusive("未找到 benchmarks/results/results/ 目录，跳过基线样本数检查。");
            return;
        }

        var reportFiles = Directory.GetFiles(resultsDir, "*-report-full.json");
        if (reportFiles.Length == 0)
        {
            Assert.Inconclusive("未找到 *-report-full.json 基线文件，跳过基线样本数检查。");
            return;
        }

        var lowSampleCases = new List<string>();

        foreach (var reportFile in reportFiles)
        {
            JsonDocument doc;
            try
            {
                using var stream = File.OpenRead(reportFile);
                doc = JsonDocument.Parse(stream);
            }
            catch (JsonException)
            {
                // JSON 解析失败 → Inconclusive（基线文件可能正在写入或损坏）
                Assert.Inconclusive($"基线 JSON 解析失败：{reportFile}");
                return;
            }
            catch (IOException)
            {
                Assert.Inconclusive($"基线 JSON 读取失败：{reportFile}");
                return;
            }

            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("Benchmarks", out var benchmarks))
                {
                    // 缺少 Benchmarks 数组 → Inconclusive（JSON 结构不符合预期）
                    Assert.Inconclusive($"基线 JSON 缺少 Benchmarks 数组：{reportFile}");
                    return;
                }

                foreach (var benchmark in benchmarks.EnumerateArray())
                {
                    var type = benchmark.TryGetProperty("Type", out var typeEl)
                        ? typeEl.GetString() ?? "?"
                        : "?";
                    var method = benchmark.TryGetProperty("Method", out var methodEl)
                        ? methodEl.GetString() ?? "?"
                        : "?";
                    var parameters = benchmark.TryGetProperty("Parameters", out var paramEl)
                        ? paramEl.GetString() ?? ""
                        : "";
                    var fileName = Path.GetFileName(reportFile);
                    var caseKey = $"{fileName} | {type}.{method} [{parameters}]";

                    if (!benchmark.TryGetProperty("Statistics", out var stats))
                    {
                        lowSampleCases.Add($"{caseKey} — 缺少 Statistics 节点");
                        continue;
                    }

                    if (!stats.TryGetProperty("N", out var nEl) || !nEl.TryGetInt32(out int n))
                    {
                        lowSampleCases.Add($"{caseKey} — Statistics.N 缺失");
                        continue;
                    }

                    if (n < 15)
                    {
                        lowSampleCases.Add($"{caseKey} — N={n}（< 15，BenchmarkOutputConfig.MinIterationCount 未生效）");
                    }
                }
            }
        }

        if (lowSampleCases.Count > 0)
        {
            Assert.Fail(
                "以下 benchmark case 的样本数 N < 15（BenchmarkOutputConfig.MinIterationCount=15 未生效），" +
                "需以修复后的配置重新生成基线 JSON：\n  - " +
                string.Join("\n  - ", lowSampleCases));
        }
    }

    // =======================================================================
    // 测试2：benchmark-selftest.yml 注入回归自检工作流结构完整
    // =======================================================================
    [TestMethod]
    public void Benchmark_Script_InjectedRegression_FailsCI()
    {
        // 验证：benchmark-selftest.yml 注入回归自检工作流结构完整
        // — 注入 15% latency + 10% alloc 回归 → 期望 exit 1 + regression_found=true
        // — 覆盖四层假阳性抑制参数（MIN_SAMPLE_COUNT / NOISE_FLOOR_PCT /
        //   CONFIDENCE_SIGMA / IO_BOUND_THRESHOLD_PCT）
        // 纯文件结构验证，不实际运行 CI。
        var repoRoot = FindRepoRoot();
        var workflowPath = Path.Combine(repoRoot, ".github", "workflows", "benchmark-selftest.yml");

        if (!File.Exists(workflowPath))
        {
            Assert.Inconclusive("未找到 .github/workflows/benchmark-selftest.yml，跳过 self-test 工作流验证。");
            return;
        }

        var content = File.ReadAllText(workflowPath);
        var contentLower = content.ToLowerInvariant();

        // --- 注入回归关键内容 ---
        // 15% latency 回归注入（LatencyCase cur +15% median）
        Assert.IsTrue(content.Contains("15%"),
            "self-test 工作流必须注入 15% latency 回归。");
        Assert.IsTrue(contentLower.Contains("latency"),
            "self-test 工作流必须包含 latency 回归注入（LatencyCase / LATENCY 门控）。");

        // 10% alloc 回归注入（AllocCase cur +10% alloc）
        Assert.IsTrue(content.Contains("10%"),
            "self-test 工作流必须注入 10% alloc 回归。");
        Assert.IsTrue(contentLower.Contains("alloc"),
            "self-test 工作流必须包含 alloc 回归注入（AllocCase / ALLOC 门控）。");

        // 检测到回归时必须 exit 1
        Assert.IsTrue(content.Contains("exit 1"),
            "self-test 工作流必须断言 exit 1（门控检测到回归时退出码）。");

        // regression_found=true 标记必须存在
        Assert.IsTrue(content.Contains("regression_found=true"),
            "self-test 工作流必须输出 regression_found=true 标记（检测到回归）。");

        // --- 假阳性抑制参数（定义于 benchmark-compare.sh，self-test 需覆盖） ---
        Assert.IsTrue(content.Contains("MIN_SAMPLE_COUNT"),
            "self-test 工作流必须引用 MIN_SAMPLE_COUNT（样本不足跳过）。");
        Assert.IsTrue(content.Contains("NOISE_FLOOR_PCT"),
            "self-test 工作流必须引用 NOISE_FLOOR_PCT（噪声底抑制）。");
        Assert.IsTrue(content.Contains("CONFIDENCE_SIGMA"),
            "self-test 工作流必须引用 CONFIDENCE_SIGMA（置信区间检查）。");
        Assert.IsTrue(content.Contains("IO_BOUND_THRESHOLD_PCT"),
            "self-test 工作流必须引用 IO_BOUND_THRESHOLD_PCT（I/O 宽松阈值）。");
    }

    // =======================================================================
    // 辅助：定位 repo root（参考 R29_FinalClosureAcceptanceTests.FindRepoRoot）
    // =======================================================================
    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            if (Directory.Exists(Path.Combine(dir, "src")) && Directory.Exists(Path.Combine(dir, "scripts")))
            {
                return dir;
            }
            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }
        return AppContext.BaseDirectory;
    }
}
