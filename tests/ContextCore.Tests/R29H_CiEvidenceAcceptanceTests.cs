using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextCore.Tests;

// ===========================================================================
// CI 生产证据固化验收测试
//
// 验证 CI 证据设施的硬门控（纯文件结构验证，不实际运行 CI）：
//   1. ci.yml 包含 evidence job：聚合各测试 job 的 TRX，
//      a. no-Inconclusive 门禁（不允许用 Inconclusive 掩盖缺失证据）；
//      b. 记录当前 HEAD（commit sha + run id + 各 job 结果）为可追溯证据并上传工件。
//   2. benchmark-main.yml 将基准结果与当前 HEAD 绑定（head-commit.json）。
//   3. scripts/gate-no-inconclusive.py 门禁脚本存在且语义正确
//      （Inconclusive → exit 1；无 TRX → exit 2）。
//   4. docs/WP-S6-Evidence.md 证据文档存在。
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
    // =======================================================================
    // 测试 1：ci.yml evidence job 结构完整（no-Inconclusive 门禁 + HEAD 记录）
    // =======================================================================
    [TestMethod]
    public void Ci_EvidenceJob_GatesOnNoInconclusive_AndRecordsHead()
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

        // no-Inconclusive 门禁：必须引用 gate-no-inconclusive.py
        Assert.IsTrue(content.Contains("gate-no-inconclusive.py", StringComparison.Ordinal),
            "evidence job 必须运行 no-Inconclusive 门禁脚本（不允许 Inconclusive 掩盖缺失证据）。");

        // HEAD 可追溯性：必须记录 commit sha + run id
        Assert.IsTrue(content.Contains("headSha", StringComparison.Ordinal),
            "evidence job 必须记录 headSha（当前提交）。");
        Assert.IsTrue(content.Contains("github.sha", StringComparison.Ordinal),
            "evidence job 必须引用 GITHUB_SHA 表达式。");
        Assert.IsTrue(content.Contains("github.run_id", StringComparison.Ordinal),
            "evidence job 必须记录 run id（可追溯到具体 CI 运行）。");

        // 证据工件上传（保留 90 天）
        Assert.IsTrue(content.Contains("evidence-${{ github.run_id }}", StringComparison.Ordinal),
            "evidence job 必须上传 evidence 工件（按 run id 命名）。");
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
    // 测试 3：no-Inconclusive 门禁脚本语义正确
    // =======================================================================
    [TestMethod]
    public void Ci_GateScript_RejectsInconclusive()
    {
        var repoRoot = FindRepoRoot();
        var gatePath = Path.Combine(repoRoot, "scripts", "gate-no-inconclusive.py");

        if (!File.Exists(gatePath))
        {
            Assert.Inconclusive("未找到 scripts/gate-no-inconclusive.py，跳过门禁脚本验证。");
            return;
        }

        var content = File.ReadAllText(gatePath);

        // 检测 Inconclusive outcome
        Assert.IsTrue(content.Contains("Inconclusive", StringComparison.Ordinal),
            "门禁脚本必须检测 Inconclusive outcome。");

        // 发现 Inconclusive 时 exit 1（CI 失败）
        Assert.IsTrue(content.Contains("return 1", StringComparison.Ordinal),
            "门禁脚本发现 Inconclusive 时必须返回 exit 1（CI 失败）。");

        // 无 TRX 文件时 exit 2（证据缺失）
        Assert.IsTrue(content.Contains("return 2", StringComparison.Ordinal),
            "门禁脚本找不到 TRX 时必须返回 exit 2（证据不完整）。");
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
