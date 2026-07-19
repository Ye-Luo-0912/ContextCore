using ContextCore.Abstractions.Models;
using ContextCore.Service;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ContextCore.Service.Tests;

/// <summary>
/// P0-6：验证 StorageOptions.AutoBootstrap 配置默认值、绑定行为与 schema-version endpoint 暴露。
/// 该选项控制服务启动时是否自动应用 PostgreSQL baseline migration，打破
/// “缺 schema → 服务退出 → 无法访问迁移 HTTP 接口”自锁。
/// </summary>
[TestClass]
public class StorageAutoBootstrapTests
{
    /// <summary>
    /// 默认 AutoBootstrap=true——新数据库无需手工迁移即可启动。
    /// </summary>
    [TestMethod]
    public void StorageOptions_AutoBootstrap_DefaultsToTrue()
    {
        var options = new StorageOptions();
        Assert.IsTrue(options.AutoBootstrap, "AutoBootstrap 默认应为 true，打破缺 schema 启动自锁");
    }

    /// <summary>
    /// 从配置绑定后 AutoBootstrap 反映配置值（true）。
    /// </summary>
    [TestMethod]
    public void StorageOptions_AutoBootstrap_BindsFromConfiguration_True()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:Provider"] = "postgres",
                ["Storage:AutoBootstrap"] = "true"
            })
            .Build();

        var options = config.GetSection("Storage").Get<StorageOptions>();
        Assert.IsNotNull(options);
        Assert.AreEqual("postgres", options.Provider);
        Assert.IsTrue(options.AutoBootstrap);
    }

    /// <summary>
    /// 配置显式 false 时绑定后 AutoBootstrap=false——DBA 严格管控 schema 场景。
    /// </summary>
    [TestMethod]
    public void StorageOptions_AutoBootstrap_BindsFromConfiguration_False()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:Provider"] = "postgres",
                ["Storage:AutoBootstrap"] = "false"
            })
            .Build();

        var options = config.GetSection("Storage").Get<StorageOptions>();
        Assert.IsNotNull(options);
        Assert.IsFalse(options.AutoBootstrap);
    }

    /// <summary>
    /// 未在配置中显式设置时，绑定后 AutoBootstrap 仍为默认值 true。
    /// </summary>
    [TestMethod]
    public void StorageOptions_AutoBootstrap_DefaultsToTrue_WhenNotInConfiguration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:Provider"] = "postgres"
                // 故意不设置 AutoBootstrap
            })
            .Build();

        var options = config.GetSection("Storage").Get<StorageOptions>();
        Assert.IsNotNull(options);
        Assert.IsTrue(options.AutoBootstrap, "未在配置中显式设置时应使用默认值 true");
    }

    /// <summary>
    /// schema-version 响应 DTO 应包含 AutoBootstrap 字段——保证 endpoint 暴露该值供运维查询。
    /// </summary>
    [TestMethod]
    public void ContextCoreSchemaVersionResponse_AutoBootstrap_PropertyExists()
    {
        var response = new ContextCoreSchemaVersionResponse
        {
            Provider = "postgres",
            CodeVersion = "test-version",
            AppliedVersion = "test-version",
            UpToDate = true,
            AutoMigrate = true,
            AutoBootstrap = true
        };

        Assert.IsTrue(response.AutoBootstrap, "DTO 应能设置 AutoBootstrap 值");
        Assert.IsTrue(response.AutoMigrate, "DTO 应保留 AutoMigrate 字段（向后兼容）");
    }
}
