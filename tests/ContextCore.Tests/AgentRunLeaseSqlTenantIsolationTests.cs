using System.Text.RegularExpressions;

namespace ContextCore.Tests;

/// <summary>
/// 架构守卫：所有查询 agent_run_leases 表的生产 SQL 必须同时引用 workspace_id 与 run_id。
/// Run 租约身份是 (workspace_id, run_id) 复合键——任何遗漏工作区维度的租约查询
/// （fencing / 续约 / 活跃过滤 / 丢失标记）都会导致跨工作区同 RunId 相互干扰。
/// 唯一豁免：全局过期回收（DELETE 全部过期租约，无 Run 身份维度）。
/// </summary>
[TestClass]
[TestCategory("Architecture")]
public sealed class AgentRunLeaseSqlTenantIsolationTests
{
    private static readonly Regex RawStringBlockRegex = new(
        @"\$?""""""(?<sql>.*?)""""""",
        RegexOptions.Singleline | RegexOptions.Compiled);

    [TestMethod]
    public void AllAgentRunLeaseQueries_ReferenceBothWorkspaceAndRun()
    {
        var repoRoot = FindRepoRoot();
        var srcDir = Path.Combine(repoRoot, "src", "ContextCore.Storage.Postgres");
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match match in RawStringBlockRegex.Matches(text))
            {
                var sql = match.Groups["sql"].Value;
                if (!sql.Contains("agent_run_leases", StringComparison.Ordinal))
                {
                    continue;
                }
                // 全局过期回收（DELETE 全部过期租约，无 Run 身份维度）豁免。
                if (IsGlobalCleanup(sql))
                {
                    continue;
                }
                var hasWorkspace = sql.Contains("workspace_id", StringComparison.OrdinalIgnoreCase);
                var hasRun = sql.Contains("run_id", StringComparison.OrdinalIgnoreCase);
                if (!hasWorkspace || !hasRun)
                {
                    violations.Add($"{Path.GetFileName(file)}: 缺 {(hasWorkspace ? "" : "workspace_id ")}{(hasRun ? "" : "run_id")}");
                }
            }
        }

        Assert.AreEqual(
            0, violations.Count,
            "所有查询 agent_run_leases 的生产 SQL 必须同时引用 workspace_id 与 run_id（Run 身份为租户复合键）。违规：\n"
            + string.Join("\n", violations));
    }

    private static bool IsGlobalCleanup(string sql)
        => sql.Contains("DELETE", StringComparison.OrdinalIgnoreCase)
           && !sql.Contains("run_id", StringComparison.OrdinalIgnoreCase);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ContextCore.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("无法定位仓库根目录（ContextCore.sln）。");
    }
}
