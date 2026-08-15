using Testcontainers.PostgreSql;

namespace ContextCore.Tests;

/// <summary>
/// 共享的 Testcontainers Postgres 辅助。
/// 统一 pgvector:pg17 镜像、凭据与 60 秒启动超时；启动失败返回 null，
/// 调用方以 Assert.Inconclusive 声明环境不可用。避免各测试类重复维护同一段启动逻辑。
/// </summary>
internal static class PostgresTestHost
{
    internal static async Task<PostgreSqlContainer?> TryStartPostgresAsync(string context)
    {
        const string pgVectorImage = "pgvector/pgvector:pg17";
        try
        {
            var container = new PostgreSqlBuilder(pgVectorImage)
                .WithDatabase("cctest")
                .WithUsername("cctest")
                .WithPassword("cctest")
                .Build();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await container.StartAsync(cts.Token);
            return container;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{context}] Docker/Postgres 不可用：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
