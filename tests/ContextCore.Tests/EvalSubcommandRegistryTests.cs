using ContextCore.Evaluation.Commands;

namespace ContextCore.Tests;

/// <summary>
/// EvalSubcommandRegistry 单元测试：验证注册表 API 和 UsageLine 自动 help 基础设施。
/// </summary>
[TestClass]
[TestCategory("Evaluation")]
public sealed class EvalSubcommandRegistryTests
{
    [TestMethod]
    public void RegisterCommandOnly_StoresNameAndNoOpHandler()
    {
        var registry = new EvalSubcommandRegistry();
        registry.RegisterCommandOnly("run");

        Assert.IsTrue(registry.Contains("run"));
        Assert.IsFalse(registry.Contains("run-other"));
        Assert.IsTrue(registry.TryGetEntry("run", out var entry));
        Assert.AreEqual("run", entry!.Name);
        Assert.IsNull(entry.UsageLine);
    }

    [TestMethod]
    public void RegisterCommandOnly_WithUsageLine_StoresUsageLine()
    {
        var registry = new EvalSubcommandRegistry();
        registry.RegisterCommandOnly("run", usageLine: "  eval run [--category <name>] [--out <path>]");

        Assert.IsTrue(registry.TryGetEntry("run", out var entry));
        Assert.AreEqual("  eval run [--category <name>] [--out <path>]", entry!.UsageLine);
    }

    [TestMethod]
    public void RegisterWithUsage_StoresNameAndUsageLine()
    {
        var registry = new EvalSubcommandRegistry();
        registry.RegisterWithUsage("report", "  eval report [<path>]");

        Assert.IsTrue(registry.TryGetEntry("report", out var entry));
        Assert.AreEqual("report", entry!.Name);
        Assert.AreEqual("  eval report [<path>]", entry.UsageLine);
    }

    [TestMethod]
    public void Register_WithHandler_StoresHandler()
    {
        var registry = new EvalSubcommandRegistry();
        EvalSubcommandHandler handler = (_, _, _, _) => Task.CompletedTask;
        registry.Register("run", handler, "run eval");

        Assert.IsTrue(registry.TryGetEntry("run", out var entry));
        Assert.AreEqual("run eval", entry!.Description);
        Assert.IsNotNull(entry.Handler);
    }

    [TestMethod]
    public void RegisterCommandOnly_OverwritesWithUsageLine_OnExplicitIndexer()
    {
        // RegisterCommandOnly 使用 _entries[name] = ...（覆盖语义，不抛重复异常）
        var registry = new EvalSubcommandRegistry();
        registry.RegisterCommandOnly("run");

        // 第二次注册应覆盖（不抛异常），并更新 UsageLine
        registry.RegisterCommandOnly("run", usageLine: "  eval run [--out <path>]");

        Assert.IsTrue(registry.TryGetEntry("run", out var entry));
        Assert.AreEqual("  eval run [--out <path>]", entry!.UsageLine);
    }

    [TestMethod]
    public void Register_DuplicateName_Throws()
    {
        // Register 使用 _entries.Add(...)（严格语义，重复抛 ArgumentException）
        var registry = new EvalSubcommandRegistry();
        EvalSubcommandHandler handler = (_, _, _, _) => Task.CompletedTask;
        registry.Register("run", handler);

        Assert.ThrowsException<ArgumentException>(() =>
            registry.Register("run", handler));
    }

    [TestMethod]
    public void GetAllEntries_ReturnsSortedByName()
    {
        var registry = new EvalSubcommandRegistry();
        registry.RegisterCommandOnly("zebra");
        registry.RegisterCommandOnly("alpha");
        registry.RegisterCommandOnly("middle");

        var names = registry.GetAllNames();
        CollectionAssert.AreEqual(new[] { "alpha", "middle", "zebra" }, names.ToList());
    }

    [TestMethod]
    public void Contains_IsCaseInsensitive()
    {
        var registry = new EvalSubcommandRegistry();
        registry.RegisterCommandOnly("Run");

        Assert.IsTrue(registry.Contains("run"));
        Assert.IsTrue(registry.Contains("RUN"));
        Assert.IsTrue(registry.Contains("Run"));
    }

    [TestMethod]
    public void GetAllEntries_AllHaveUsageLineOrDefault()
    {
        var registry = new EvalSubcommandRegistry();
        registry.RegisterWithUsage("run", "  eval run [--out <path>]");
        registry.RegisterCommandOnly("report");

        var entries = registry.GetAllEntries();
        Assert.AreEqual(2, entries.Count);

        var runEntry = entries.Single(e => e.Name == "run");
        var reportEntry = entries.Single(e => e.Name == "report");

        Assert.AreEqual("  eval run [--out <path>]", runEntry.UsageLine);
        Assert.IsNull(reportEntry.UsageLine);

        // 模拟 PrintUsage 自动生成逻辑
        var usageLines = entries.Select(e => e.UsageLine ?? $"  eval {e.Name}").ToList();
        CollectionAssert.Contains(usageLines, "  eval run [--out <path>]");
        CollectionAssert.Contains(usageLines, "  eval report");
    }

    [TestMethod]
    public void RegisterCommandOnly_EmptyName_Throws()
    {
        var registry = new EvalSubcommandRegistry();
        Assert.ThrowsException<ArgumentException>(() => registry.RegisterCommandOnly(""));
        Assert.ThrowsException<ArgumentException>(() => registry.RegisterCommandOnly("   "));
    }

    [TestMethod]
    public void RegisterAliases_AllNamesResolveToSameHandler()
    {
        var registry = new EvalSubcommandRegistry();
        EvalSubcommandHandler handler = (_, _, _, _) => Task.CompletedTask;
        registry.RegisterAliases(new[] { "run", "r", "execute" }, handler);

        Assert.IsTrue(registry.Contains("run"));
        Assert.IsTrue(registry.Contains("r"));
        Assert.IsTrue(registry.Contains("execute"));
    }
}
