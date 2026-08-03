using System.IO;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextCore.Tests;

// ===========================================================================
// CI 生产证据固化验收测试
//
// 验证 CI 证据设施的硬门控（纯文件结构验证，不实际运行 CI）：
//   1. ci.yml evidence job 结构完整（manifest 门禁 + HEAD 记录）：
//      a. assert-required-jobs.py 硬断言所有必需上游 job 均为 success
//         （堵住"上游失败、Evidence 单独绿"的假绿路径）；
//      b. 下载必需测试结果工件（required-artifacts.json）不再 continue-on-error；
//      c. gate-evidence.py 门禁：0 Failed / 0 Inconclusive / 0 未声明跳过 +
//         每个必测类别至少 minExecuted 条真实执行；
//      d. 记录当前 HEAD（commit sha + run id + 各 job 结果 + policy manifest 快照）。
//   2. ci-manifests/*.json 存在且结构合法（required-jobs / required-artifacts /
//      required-test-categories 与 ci.yml needs 一致）。
//   3. scripts/gate-evidence.py 语义正确（Failed/Inconclusive/NotExecuted → exit 1；
//      无 TRX → exit 2；白名单机制）。
//   4. benchmark-main.yml 将基准结果与当前 HEAD 绑定（head-commit.json）。
//   5. docs/WP-S6-Evidence.md 证据文档存在。
//
// 设计原则：
//   - 纯文件结构验证，不实际运行 benchmark / CI。
//   - 复用 FindRepoRoot() 模式定位 repo root。
// ===========================================================================

[TestClass]
[TestCategory("R29-Hard-Gate")]
[TestCategory("Benchmark-CI")]
public sealed class R29H_CiEvidenceAcceptanceTests
{
    private static readonly string[] RequiredJobNames =
    {
        "build", "unit", "integration-postgres", "architecture", "public-api", "openapi-snapshot"
    };

    // =======================================================================
    // 测试 1：ci.yml evidence job 结构完整（manifest 门禁 + HEAD 记录）
    // =======================================================================
    [TestMethod]
    public void Ci_EvidenceJob_GatesOnManifests_AndRecordsHead()
    {
        var repoRoot = FindRepoRoot();
        var workflowPath = Path.Combine(repoRoot, ".github", "workflows", "ci.yml");

        if (!File.Exists(workflowPath))
        {
            Assert.Inconclusive("未找到 .github/workflows/ci.yml，跳过 CI 证据验证。");
            return;
        }

        var content = File.ReadAllText(workflowPath);

        // evidence job 必须存在
        Assert.IsTrue(content.Contains("evidence:", StringComparison.Ordinal),
            "ci.yml 必须包含 evidence job。");

        // evidence job 必须依赖全部测试 job（聚合完整证据）
        Assert.IsTrue(content.Contains("needs: [build, unit, integration-postgres, architecture, public-api, openapi-snapshot]", StringComparison.Ordinal),
            "evidence job 必须 needs 全部测试 job（build/unit/integration-postgres/architecture/public-api/openapi-snapshot）。");

        // 必需上游 job 断言：必须引用 assert-required-jobs.py（堵住 Evidence 单独绿）
        Assert.IsTrue(content.Contains("assert-required-jobs.py", StringComparison.Ordinal),
            "evidence job 必须运行 assert-required-jobs.py 断言所有必需上游 job 均为 success。");

        // manifest 门禁：必须引用 gate-evidence.py + required-test-categories.json
        Assert.IsTrue(content.Contains("gate-evidence.py", StringComparison.Ordinal),
            "evidence job 必须运行 gate-evidence.py 门禁脚本（0 Failed/Inconclusive/未声明跳过 + 每类必测）。");
        Assert.IsTrue(content.Contains("ci-manifests", StringComparison.Ordinal),
            "evidence job 必须引用 ci-manifests 目录（required-*.json）。");

        // HEAD 可追溯性：必须记录 commit sha + run id（经 write-head-evidence.py 生成 head-evidence.json）
        Assert.IsTrue(content.Contains("write-head-evidence.py", StringComparison.Ordinal),
            "evidence job 必须运行 write-head-evidence.py 生成 head-evidence.json。");
        Assert.IsTrue(content.Contains("github.sha", StringComparison.Ordinal),
            "evidence job 必须引用 GITHUB_SHA 表达式。");
        Assert.IsTrue(content.Contains("github.run_id", StringComparison.Ordinal),
            "evidence job 必须记录 run id（可追溯到具体 CI 运行）。");

        // 证据工件上传（保留 90 天）
        Assert.IsTrue(content.Contains("evidence-${{ github.run_id }}", StringComparison.Ordinal),
            "evidence job 必须上传 evidence 工件（按 run id 命名）。");
        Assert.IsTrue(content.Contains("retention-days: 90", StringComparison.Ordinal),
            "evidence 工件必须保留 90 天。");
    }

    // =======================================================================
    // 测试 1b：evidence job 的下载步骤不允许 continue-on-error（缺失即失败）
    // =======================================================================
    [TestMethod]
    public void Ci_EvidenceJob_DoesNotSwallowArtifactDownloadFailures()
    {
        var repoRoot = FindRepoRoot();
        var workflowPath = Path.Combine(repoRoot, ".github", "workflows", "ci.yml");

        if (!File.Exists(workflowPath))
        {
            Assert.Inconclusive("未找到 .github/workflows/ci.yml，跳过 CI 证据验证。");
            return;
        }

        var content = File.ReadAllText(workflowPath);
        var evidenceIndex = content.IndexOf("  evidence:", StringComparison.Ordinal);
        Assert.IsTrue(evidenceIndex >= 0, "ci.yml 必须包含 evidence job。");

        // evidence 是 ci.yml 的最后一个 job，取其块到文件末尾
        var evidenceBlock = content.Substring(evidenceIndex);

        Assert.IsFalse(evidenceBlock.Contains("continue-on-error: true", StringComparison.Ordinal),
            "evidence job 的工件下载步骤不允许 continue-on-error: true——缺失工件必须使 Evidence 失败，"
            + "否则上游 job 未上传 TRX 时 Evidence 仍可能假绿。");

        // 且必须包含 5 个必需工件下载步骤（required-artifacts.json 的 dir 全集）
        foreach (var dir in new[] { "unit", "integration", "architecture", "public-api", "openapi-snapshot" })
        {
            Assert.IsTrue(evidenceBlock.Contains($"evidence/trx/{dir}", StringComparison.Ordinal),
                $"evidence job 必须下载 {dir} 测试结果到 evidence/trx/{dir}。");
        }
    }

    // =======================================================================
    // 测试 1c：ci-manifests/*.json 存在且结构合法，且与 ci.yml needs 一致
    // =======================================================================
    [TestMethod]
    public void Ci_EvidenceManifests_DefineCompletePolicy()
    {
        var repoRoot = FindRepoRoot();
        var manifestDir = Path.Combine(repoRoot, "ci-manifests");

        // 1. required-jobs.json：job 名单 == ci.yml evidence needs
        var jobsManifest = ReadJson(Path.Combine(manifestDir, "required-jobs.json"), "required-jobs.json");
        var jobs = GetStringArray(jobsManifest, "jobs", "required-jobs.json");
        CollectionAssert.AreEqual(RequiredJobNames, jobs,
            "required-jobs.json 的 jobs 必须与 ci.yml evidence job 的 needs 完全一致。");

        // 2. required-artifacts.json：5 个必需工件，dir 集合 == 必测类别 dir 集合
        var artifactsManifest = ReadJson(Path.Combine(manifestDir, "required-artifacts.json"), "required-artifacts.json");
        var artifacts = artifactsManifest.RootElement.GetProperty("artifacts").EnumerateArray().ToList();
        Assert.AreEqual(5, artifacts.Count, "required-artifacts.json 必须定义 5 个测试结果工件。");
        var artifactDirs = artifacts
            .Select(a => a.GetProperty("dir").GetString())
            .Where(d => d is not null)
            .Select(d => d!)
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToArray();

        // 3. required-test-categories.json：5 个必测类别，dir 与工件一致，minExecuted >= 1
        var categoriesManifest = ReadJson(Path.Combine(manifestDir, "required-test-categories.json"), "required-test-categories.json");
        var categories = categoriesManifest.RootElement.GetProperty("categories").EnumerateArray().ToList();
        Assert.AreEqual(5, categories.Count, "required-test-categories.json 必须定义 5 个必测类别。");
        var categoryDirs = categories
            .Select(c => c.GetProperty("dir").GetString())
            .Where(d => d is not null)
            .Select(d => d!)
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(artifactDirs, categoryDirs,
            "必测类别 dir 必须与必需工件 dir 完全一致（每类证据都有一份工件）。");

        foreach (var category in categories)
        {
            var name = category.GetProperty("name").GetString();
            Assert.IsFalse(string.IsNullOrEmpty(name), "必测类别必须有 name。");
            Assert.IsTrue(category.GetProperty("minExecuted").GetInt32() >= 1,
                $"必测类别 {name} 的 minExecuted 必须 >= 1（不允许 0 执行的类别）。");
            Assert.IsTrue(category.TryGetProperty("allowNotExecuted", out _),
                $"必测类别 {name} 必须声明 allowNotExecuted（可为空数组）。");
            var filter = category.GetProperty("filter").GetString();
            Assert.IsFalse(string.IsNullOrEmpty(filter), $"必测类别 {name} 必须声明 filter。");
        }

        // 4. 五个必测类别 name 必须覆盖 unit/integration/architecture/public-api/openapi-snapshot
        var names = categories.Select(c => c.GetProperty("name").GetString()).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(
            new[] { "architecture", "integration", "openapi-snapshot", "public-api", "unit" }, names,
            "必测类别 name 必须覆盖 unit/integration/architecture/public-api/openapi-snapshot。");
    }

    // =======================================================================
    // 测试 2：benchmark-main.yml 将基准结果与当前 HEAD 绑定
    // =======================================================================
    [TestMethod]
    public void Ci_BenchmarkMain_RecordsHeadCommit()
    {
        var repoRoot = FindRepoRoot();
        var workflowPath = Path.Combine(repoRoot, ".github", "workflows", "benchmark-main.yml");

        if (!File.Exists(workflowPath))
        {
            Assert.Inconclusive("未找到 .github/workflows/benchmark-main.yml，跳过基准 HEAD 绑定验证。");
            return;
        }

        var content = File.ReadAllText(workflowPath);

        // 基准结果必须写入 head-commit.json（与提交绑定）
        Assert.IsTrue(content.Contains("head-commit.json", StringComparison.Ordinal),
            "benchmark-main.yml 必须将基准结果写入 head-commit.json（HEAD 可追溯）。");
        Assert.IsTrue(content.Contains("headSha", StringComparison.Ordinal),
            "head-commit.json 必须记录 headSha。");
        Assert.IsTrue(content.Contains("github.sha", StringComparison.Ordinal),
            "head-commit.json 必须引用 GITHUB_SHA 表达式。");
    }

    // =======================================================================
    // 测试 3：evidence 门禁脚本语义正确（gate-evidence.py）
    // =======================================================================
    [TestMethod]
    public void Ci_GateScript_RejectsNonExecutedOutcomes()
    {
        var repoRoot = FindRepoRoot();
        var gatePath = Path.Combine(repoRoot, "scripts", "gate-evidence.py");

        if (!File.Exists(gatePath))
        {
            Assert.Inconclusive("未找到 scripts/gate-evidence.py，跳过门禁脚本验证。");
            return;
        }

        var content = File.ReadAllText(gatePath);

        // 必须检测 Failed / Inconclusive / NotExecuted / Skipped outcome
        foreach (var outcome in new[] { "Failed", "Inconclusive", "NotExecuted", "Skipped" })
        {
            Assert.IsTrue(content.Contains(outcome, StringComparison.Ordinal),
                $"门禁脚本必须检测 {outcome} outcome。");
        }

        // 发现策略违反时 exit 1（CI 失败）
        Assert.IsTrue(content.Contains("return 1", StringComparison.Ordinal),
            "门禁脚本发现 Failed/Inconclusive/未声明跳过时必须返回 exit 1（CI 失败）。");

        // 无 TRX 文件时 exit 2（证据缺失）
        Assert.IsTrue(content.Contains("return 2", StringComparison.Ordinal),
            "门禁脚本找不到 TRX 时必须返回 exit 2（证据不完整）。");

        // 必测类别 executed >= minExecuted（环境跳过的必测项失败）
        Assert.IsTrue(content.Contains("minExecuted", StringComparison.Ordinal),
            "门禁脚本必须校验每类 executed >= minExecuted。");

        // 白名单机制：allowNotExecuted（文档化已知 [Ignore]）
        Assert.IsTrue(content.Contains("allowNotExecuted", StringComparison.Ordinal),
            "门禁脚本必须支持 allowNotExecuted 白名单（已知 [Ignore] 测试）。");

        // 必须支持 --manifest-dir 参数（读取 ci-manifests）
        Assert.IsTrue(content.Contains("--manifest-dir", StringComparison.Ordinal),
            "门禁脚本必须支持 --manifest-dir 参数。");
    }

    // =======================================================================
    // 测试 4：WP-S6 证据文档存在
    // =======================================================================
    [TestMethod]
    public void Ci_EvidenceDocument_Exists()
    {
        var repoRoot = FindRepoRoot();
        var docPath = Path.Combine(repoRoot, "docs", "WP-S6-Evidence.md");

        if (!File.Exists(docPath))
        {
            Assert.Inconclusive("未找到 docs/WP-S6-Evidence.md，跳过证据文档验证。");
            return;
        }

        var content = File.ReadAllText(docPath);
        Assert.IsTrue(content.Length > 0, "证据文档不应为空。");
        Assert.IsTrue(content.Contains("WP-S6", StringComparison.Ordinal),
            "证据文档应包含 WP-S6 标识。");
        Assert.IsTrue(content.Contains("gate-evidence.py", StringComparison.Ordinal),
            "证据文档应描述 evidence manifest 门禁（gate-evidence.py）。");
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

    private static JsonDocument ReadJson(string path, string name)
    {
        Assert.IsTrue(File.Exists(path), $"未找到 {name}（{path}）。");
        try
        {
            return JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            Assert.Fail($"{name} 不是合法 JSON：{ex.Message}");
            throw;
        }
    }

    private static string[] GetStringArray(JsonDocument doc, string property, string manifestName)
    {
        if (!doc.RootElement.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            Assert.Fail($"{manifestName} 缺少 {property} 数组。");
        }
        var values = element.EnumerateArray()
            .Select(e => e.GetString())
            .Where(v => v is not null)
            .Select(v => v!)
            .ToArray();
        Assert.AreEqual(element.GetArrayLength(), values.Length, $"{manifestName}.{property} 含 null 项。");
        return values;
    }
}
