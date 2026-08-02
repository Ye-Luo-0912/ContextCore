using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;

namespace ContextCore.Tests;

/// <summary>
/// PostgresOptions HA 友好配置与连接字符串关键字解析单元测试。
/// 不依赖 Docker，验证配置正确传递给 NpgsqlConnectionStringBuilder。
/// </summary>
[TestClass]
[TestCategory("Storage")]
public sealed class PostgresHAOptionsTests
{
    /// <summary>
    /// 验证 PostgresOptions 默认值符合 HA 运行时要求。
    /// CommandTimeoutSeconds 默认 30 秒与 Npgsql 一致，避免长查询无限制占用连接池。
    /// AutoMigrate/EnablePgVectorExtension 默认开启，避免首次启动时缺失表或扩展。
    /// </summary>
    [TestMethod]
    public void PostgresOptions_Defaults_AreHAFriendly()
    {
        var options = new PostgresOptions();
        Assert.AreEqual(30, options.CommandTimeoutSeconds, "CommandTimeoutSeconds 默认应为 30 秒（HA 友好）");
        Assert.IsTrue(options.AutoMigrate, "AutoMigrate 默认应开启，避免首次启动时缺失表");
        Assert.IsTrue(options.EnablePgVectorExtension, "EnablePgVectorExtension 默认应开启");
        Assert.AreEqual("cc_", options.TablePrefix, "TablePrefix 默认应避免与业务表冲突");
    }

    /// <summary>
    /// 连接字符串中的连接池设置应被 NpgsqlConnectionStringBuilder 正确解析。
    /// 覆盖 MaxPoolSize/MinPoolSize/ConnectionIdleLifetime/Timeout——这四个关键字直接决定
    /// HA 场景下连接池在故障/恢复时的行为。
    /// </summary>
    [TestMethod]
    public void PostgresOptions_ConnectionString_PreservesPoolSettings()
    {
        var connectionString = "Host=localhost;Database=test;Username=test;Password=test;MaxPoolSize=50;MinPoolSize=5;ConnectionIdleLifetime=30;Timeout=10";
        var builder = new NpgsqlConnectionStringBuilder(connectionString);

        Assert.AreEqual(50, builder.MaxPoolSize);
        Assert.AreEqual(5, builder.MinPoolSize);
        Assert.AreEqual(30, builder.ConnectionIdleLifetime);
        Assert.AreEqual(10, builder.Timeout);
    }

    /// <summary>
    /// 多 host 连接字符串支持 failover。
    /// Npgsql 10 中 Host 接受逗号分隔多 host；TargetSessionAttributes 属性对应的连接字符串关键字
    /// 是 "Target Session Attributes"（含空格）；HostRecheckSeconds 关键字是 "Host Recheck Seconds"；
    /// LoadBalanceHosts 关键字是 "Load Balance Hosts"（bool，启用 round-robin 负载均衡）。
    /// 注意：Npgsql 10 中没有 LoadBalanceMode 枚举（旧版/外部文档说法），LoadBalanceHosts 是 bool。
    /// </summary>
    [TestMethod]
    public void PostgresOptions_ConnectionString_SupportsMultiHostFailover()
    {
        // Npgsql 10 的关键字含空格；通过属性构造可避免关键字拼写误差，再用 ToString() round-trip 验证。
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = "primary,standby",
            Database = "test",
            Username = "test",
            Password = "test",
            TargetSessionAttributes = "primary",
            HostRecheckSeconds = 5,
            LoadBalanceHosts = true
        };

        Assert.AreEqual("primary,standby", builder.Host, "多 host 应被原样保留以支持 failover");
        Assert.AreEqual("primary", builder.TargetSessionAttributes, "TargetSessionAttributes 控制目标会话类型（primary/standby/any）");
        Assert.AreEqual(5, builder.HostRecheckSeconds, "HostRecheckSeconds 控制 failover 时 host 状态重检查间隔");
        Assert.IsTrue(builder.LoadBalanceHosts, "LoadBalanceHosts=true 启用 round-robin 负载均衡");

        // round-trip 验证：ToString() 输出应能被 NpgsqlConnectionStringBuilder 再次解析为相同关键字
        var roundTripped = new NpgsqlConnectionStringBuilder(builder.ToString());
        Assert.AreEqual("primary,standby", roundTripped.Host);
        Assert.AreEqual("primary", roundTripped.TargetSessionAttributes);
        Assert.AreEqual(5, roundTripped.HostRecheckSeconds);
        Assert.IsTrue(roundTripped.LoadBalanceHosts);

        // 直接用关键字构造连接字符串也应被接受（关键字含空格，是 Npgsql 10 的命名约定）
        var keywordCs = "Host=primary,standby;Database=test;Username=test;Password=test;Target Session Attributes=primary;Host Recheck Seconds=5;Load Balance Hosts=True";
        var fromKeyword = new NpgsqlConnectionStringBuilder(keywordCs);
        Assert.AreEqual("primary,standby", fromKeyword.Host);
        Assert.AreEqual("primary", fromKeyword.TargetSessionAttributes);
        Assert.AreEqual(5, fromKeyword.HostRecheckSeconds);
        Assert.IsTrue(fromKeyword.LoadBalanceHosts);
    }

    /// <summary>
    /// 连接不可达时 PingAsync 应返回 (false, errorMessage) 而非抛异常。
    /// 这是 HA 健康检查的核心要求：上层调用方依赖 PingAsync 的失败返回值而非异常来判定后端可用性。
    /// 使用不可达端口 9（discard 协议端口，不会有 PostgreSQL 监听）；Timeout=2 避免测试卡住。
    /// </summary>
    [TestMethod]
    public async Task PostgresConnectionFactory_PingAsync_ReturnsFalseOnUnreachableHost()
    {
        var options = new PostgresOptions
        {
            ConnectionString = "Host=localhost;Port=9;Database=test;Username=test;Password=test;Timeout=2",
            AutoMigrate = false,
            EnablePgVectorExtension = false
        };
        await using var factory = new PostgresConnectionFactory(options);
        var (success, error) = await factory.PingAsync();
        Assert.IsFalse(success, "不可达主机应返回 false");
        Assert.IsFalse(string.IsNullOrEmpty(error), "应提供错误信息");
        Assert.IsTrue(
            error.Contains("Npgsql") || error.Contains("connect") || error.Contains("Failed") || error.Contains("refused") || error.Contains("timeout"),
            $"错误信息应包含连接失败原因，实际：{error}");
    }

    /// <summary>
    /// PostgresOptions.CommandTimeoutSeconds 与 Npgsql 连接字符串的 CommandTimeout 解耦。
    /// 各 Postgres store 在每个 command 上显式赋值 command.CommandTimeout = Options.CommandTimeoutSeconds，
    /// 覆盖 Npgsql 默认值——这种显式赋值确保超时行为可预测，不依赖连接字符串配置。
    /// 此测试验证：连接字符串不含 CommandTimeout 时 NpgsqlCommand.CommandTimeout 默认值是 30，
    /// 而 PostgresOptions.CommandTimeoutSeconds 可独立配置（如 60）。
    /// </summary>
    [TestMethod]
    public void PostgresOptions_CommandTimeoutSeconds_DoesNotAffectNpgsqlCommandTimeoutByDefault()
    {
        var options = new PostgresOptions
        {
            ConnectionString = "Host=localhost;Database=test;Username=test;Password=test",
            CommandTimeoutSeconds = 60
        };
        var builder = new NpgsqlConnectionStringBuilder(options.ConnectionString);
        Assert.AreEqual(30, builder.CommandTimeout, "Npgsql 默认 CommandTimeout 是 30 秒");
        Assert.AreEqual(60, options.CommandTimeoutSeconds, "PostgresOptions 可独立配置 CommandTimeoutSeconds=60");
        // 各 Postgres store 在每个 command 上赋值 command.CommandTimeout = Options.CommandTimeoutSeconds，
        // 覆盖 Npgsql 默认值。这种显式赋值确保超时行为可预测，不依赖连接字符串。
    }
}
