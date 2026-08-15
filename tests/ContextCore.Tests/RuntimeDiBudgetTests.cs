using ContextCore.Abstractions;
using ContextCore.Service.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ContextCore.Tests;

/// <summary>
/// 生产 DI 预算守卫。统计各 RuntimeProfile 组合根下的 ServiceDescriptor 总数，
/// 防止宿主注册无界膨胀（依赖收敛目标的可观测护栏）。
/// 预算以"当前实测 + 容差"为上限：新增注册必须删除等量冗余，否则失败。
/// </summary>
[TestClass]
[TestCategory("Architecture")]
[TestCategory("Budget")]
public sealed class RuntimeDiBudgetTests
{
    // 预算上限 = 实测基线 + 10% 容差。更新方式：确认注册变更属有意收敛后，按实测值重设。
    // 当前基线（2026-08 实测）：Development ≈ 351，SingleNode ≈ 356，ProductionHA ≈ 361。
    private const int DevelopmentDescriptorBudget = 390;
    private const int SingleNodeDescriptorBudget = 395;
    private const int ProductionHaDescriptorBudget = 400;

    private static ServiceCollection BuildBase(string profile, string provider)
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = provider,
            ["Storage:PostgresConnectionString"] =
                "Host=localhost;Database=fake;Username=fake;Password=fake",
            ["ContextCoreRuntime:Profile"] = profile
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddContextCore(ModelExecutionOptions.Default);
        services.AddContextCoreRuntime(configuration);
        return services;
    }

    [TestMethod]
    public void Development_DescriptorCount_WithinBudget()
    {
        var services = BuildBase("Development", "filesystem");
        var count = services.Count;
        Console.WriteLine($"[RuntimeDiBudget] Development descriptors = {count}");
        Assert.IsTrue(
            count <= DevelopmentDescriptorBudget,
            $"Development 组合根 DI descriptors 超预算：{count} > {DevelopmentDescriptorBudget}。" +
            "新增注册需删除等量冗余（LR-7B 收敛目标）。");
    }

    [TestMethod]
    public void SingleNode_DescriptorCount_WithinBudget()
    {
        var services = BuildBase("SingleNode", "postgres");
        var count = services.Count;
        Console.WriteLine($"[RuntimeDiBudget] SingleNode descriptors = {count}");
        Assert.IsTrue(
            count <= SingleNodeDescriptorBudget,
            $"SingleNode 组合根 DI descriptors 超预算：{count} > {SingleNodeDescriptorBudget}。" +
            "新增注册需删除等量冗余（LR-7B 收敛目标）。");
    }

    [TestMethod]
    public void ProductionHA_DescriptorCount_WithinBudget()
    {
        var services = BuildBase("ProductionHA", "postgres");
        var count = services.Count;
        Console.WriteLine($"[RuntimeDiBudget] ProductionHA descriptors = {count}");
        Assert.IsTrue(
            count <= ProductionHaDescriptorBudget,
            $"ProductionHA 组合根 DI descriptors 超预算：{count} > {ProductionHaDescriptorBudget}。" +
            "新增注册需删除等量冗余（LR-7B 收敛目标）。");
    }

    private static Microsoft.Extensions.Configuration.IConfiguration BuildConfiguration(
        Dictionary<string, string?> values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}
