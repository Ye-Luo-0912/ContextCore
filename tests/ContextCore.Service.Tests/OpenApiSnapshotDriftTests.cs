using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;

namespace ContextCore.Service.Tests;

/// <summary>
/// OpenAPI drift 检测测试。
/// 启动服务，拉取 /openapi/v1.json，与签入的 service/openapi/service-api.openapi.json 快照对比。
/// 检测 paths、operationIds、schemas 名称集合的差异，任何新增/删除都会失败，提示更新快照或回滚变更。
/// 这替代了外部 CI 脚本，将 OpenAPI 合约稳定性纳入测试套件。
/// </summary>
[TestClass]
[TestCategory("Contract")]
public sealed class OpenApiSnapshotDriftTests
{
    private const string SnapshotRelativePath = "..\\..\\..\\..\\..\\service\\openapi\\service-api.openapi.json";

    private static readonly string SnapshotFullPath = ResolveSnapshotPath();

    /// <summary>
    /// 当前服务生成的 OpenAPI 与签入快照的 paths 集合应完全一致。
    /// </summary>
    [TestMethod]
    public async Task OpenApi_PathsMatchSnapshot()
    {
        var (actualPaths, actualOperationIds, actualSchemas) = await FetchCurrentOpenApiSnapshotAsync();
        var (expectedPaths, expectedOperationIds, expectedSchemas) = LoadSignedSnapshot();

        var added = actualPaths.Except(expectedPaths).OrderBy(s => s, StringComparer.Ordinal).ToList();
        var removed = expectedPaths.Except(actualPaths).OrderBy(s => s, StringComparer.Ordinal).ToList();

        if (added.Count == 0 && removed.Count == 0)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("OpenAPI paths drift detected.");
        sb.AppendLine("Snapshot: " + SnapshotFullPath);
        sb.AppendLine();
        if (added.Count > 0)
        {
            sb.AppendLine("Added paths (new endpoints, update snapshot if intentional):");
            foreach (var p in added) sb.AppendLine("  + " + p);
            sb.AppendLine();
        }
        if (removed.Count > 0)
        {
            sb.AppendLine("Removed paths (breaking change, restore endpoint or update snapshot):");
            foreach (var p in removed) sb.AppendLine("  - " + p);
            sb.AppendLine();
        }
        Assert.Fail(sb.ToString());
    }

    /// <summary>
    /// 当前服务生成的 OpenAPI 与签入快照的 operationIds 集合应完全一致。
    /// </summary>
    [TestMethod]
    public async Task OpenApi_OperationIdsMatchSnapshot()
    {
        var (actualPaths, actualOperationIds, actualSchemas) = await FetchCurrentOpenApiSnapshotAsync();
        var (expectedPaths, expectedOperationIds, expectedSchemas) = LoadSignedSnapshot();

        var added = actualOperationIds.Except(expectedOperationIds).OrderBy(s => s, StringComparer.Ordinal).ToList();
        var removed = expectedOperationIds.Except(actualOperationIds).OrderBy(s => s, StringComparer.Ordinal).ToList();

        if (added.Count == 0 && removed.Count == 0)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("OpenAPI operationIds drift detected.");
        sb.AppendLine("Snapshot: " + SnapshotFullPath);
        sb.AppendLine();
        if (added.Count > 0)
        {
            sb.AppendLine("Added operationIds:");
            foreach (var op in added) sb.AppendLine("  + " + op);
            sb.AppendLine();
        }
        if (removed.Count > 0)
        {
            sb.AppendLine("Removed operationIds (breaking change):");
            foreach (var op in removed) sb.AppendLine("  - " + op);
            sb.AppendLine();
        }
        Assert.Fail(sb.ToString());
    }

    /// <summary>
    /// 当前服务生成的 OpenAPI 与签入快照的 schemas 名称集合应完全一致。
    /// </summary>
    [TestMethod]
    public async Task OpenApi_SchemasMatchSnapshot()
    {
        var (actualPaths, actualOperationIds, actualSchemas) = await FetchCurrentOpenApiSnapshotAsync();
        var (expectedPaths, expectedOperationIds, expectedSchemas) = LoadSignedSnapshot();

        var added = actualSchemas.Except(expectedSchemas).OrderBy(s => s, StringComparer.Ordinal).ToList();
        var removed = expectedSchemas.Except(actualSchemas).OrderBy(s => s, StringComparer.Ordinal).ToList();

        if (added.Count == 0 && removed.Count == 0)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("OpenAPI schemas drift detected.");
        sb.AppendLine("Snapshot: " + SnapshotFullPath);
        sb.AppendLine();
        if (added.Count > 0)
        {
            sb.AppendLine("Added schemas (new DTOs, update snapshot if intentional):");
            foreach (var s in added) sb.AppendLine("  + " + s);
            sb.AppendLine();
        }
        if (removed.Count > 0)
        {
            sb.AppendLine("Removed schemas (breaking change, restore DTO or update snapshot):");
            foreach (var s in removed) sb.AppendLine("  - " + s);
            sb.AppendLine();
        }
        Assert.Fail(sb.ToString());
    }

    private static async Task<(HashSet<string> Paths, HashSet<string> OperationIds, HashSet<string> Schemas)> FetchCurrentOpenApiSnapshotAsync()
    {
        var rootPath = Path.Combine(
            Directory.GetCurrentDirectory(),
            "openapi-drift-test-data",
            Guid.NewGuid().ToString("N"));
        try
        {
            using var factory = new OpenApiDriftFactory(rootPath);
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/openapi/v1.json");
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStreamAsync();
            var doc = await JsonDocument.ParseAsync(json);
            return ExtractSnapshot(doc.RootElement);
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                try { Directory.Delete(rootPath, recursive: true); } catch { /* ignore */ }
            }
        }
    }

    private static (HashSet<string> Paths, HashSet<string> OperationIds, HashSet<string> Schemas) LoadSignedSnapshot()
    {
        if (!File.Exists(SnapshotFullPath))
        {
            Assert.Inconclusive("OpenAPI snapshot not found at: " + SnapshotFullPath);
        }
        using var stream = File.OpenRead(SnapshotFullPath);
        var doc = JsonDocument.Parse(stream);
        return ExtractSnapshot(doc.RootElement);
    }

    private static (HashSet<string> Paths, HashSet<string> OperationIds, HashSet<string> Schemas) ExtractSnapshot(JsonElement root)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var operationIds = new HashSet<string>(StringComparer.Ordinal);
        var schemas = new HashSet<string>(StringComparer.Ordinal);

        if (root.TryGetProperty("paths", out var pathsEl) && pathsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var pathProp in pathsEl.EnumerateObject())
            {
                paths.Add(pathProp.Name);
                if (pathProp.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var methodProp in pathProp.Value.EnumerateObject())
                    {
                        if (methodProp.Value.ValueKind == JsonValueKind.Object &&
                            methodProp.Value.TryGetProperty("operationId", out var opIdEl) &&
                            opIdEl.ValueKind == JsonValueKind.String)
                        {
                            operationIds.Add(opIdEl.GetString() ?? string.Empty);
                        }
                    }
                }
            }
        }

        if (root.TryGetProperty("components", out var componentsEl) &&
            componentsEl.TryGetProperty("schemas", out var schemasEl) &&
            schemasEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var schemaProp in schemasEl.EnumerateObject())
            {
                schemas.Add(schemaProp.Name);
            }
        }

        return (paths, operationIds, schemas);
    }

    private static string ResolveSnapshotPath()
    {
        // 测试 bin 目录: tests/ContextCore.Service.Tests/bin/<Config>/net10.0/
        // 快照目录: service/openapi/
        var assemblyLocation = typeof(OpenApiSnapshotDriftTests).Assembly.Location;
        var binDir = Path.GetDirectoryName(assemblyLocation) ?? AppContext.BaseDirectory;
        var candidate = Path.GetFullPath(Path.Combine(binDir, SnapshotRelativePath));
        if (File.Exists(candidate)) return candidate;

        // 回退：逐级向上查找直到仓库根
        var dir = binDir;
        for (var i = 0; i < 10 && dir is not null; i++)
        {
            var svcPath = Path.Combine(dir, "service", "openapi", "service-api.openapi.json");
            if (File.Exists(svcPath)) return svcPath;
            dir = Path.GetDirectoryName(dir);
        }
        return candidate;
    }

    private sealed class OpenApiDriftFactory : WebApplicationFactory<Program>
    {
        private readonly string _rootPath;

        public OpenApiDriftFactory(string rootPath) => _rootPath = rootPath;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Storage:Provider", "filesystem");
            builder.UseSetting("Storage:RootPath", _rootPath);
            builder.UseSetting("Compression:Provider", "mock");
            builder.UseSetting("JobWorker:Enabled", "false");
            builder.UseSetting("Security:RequireApiKey", "false");
        }
    }
}
