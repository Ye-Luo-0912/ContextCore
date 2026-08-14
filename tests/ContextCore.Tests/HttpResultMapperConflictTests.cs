using System.Text.Json;
using ContextCore.Service.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextCore.Tests;

// ===========================================================================
// RF-7 验收：ContextCoreHttpResultMapper.Conflict
//
// 端点中重复的 CAS 并发冲突映射收敛到 mapper 的稳定 Conflict 方法。
// 本测试锁定响应状态与 JSON 体（OperationId / ErrorCode / Message），
// 确保替换内联构造后响应形状完全不变。
// ===========================================================================

/// <summary>ContextCoreHttpResultMapper.Conflict 验收测试。</summary>
[TestClass]
[TestCategory("RF")]
[TestCategory("Service")]
public sealed class HttpResultMapperConflictTests
{
    [TestMethod]
    public async Task Conflict_Returns409WithStableBody()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        // .NET 10 的 JsonHttpResult<T>.ExecuteAsync 需要从 RequestServices 解析 ILoggerFactory。
        httpContext.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();

        var result = ContextCoreHttpResultMapper.Conflict(
            "models.activate", "CAS 更新失败：集群模型槽位已被并发修改，请重试。", "CasConflict");

        await result.ExecuteAsync(httpContext);

        Assert.AreEqual(StatusCodes.Status409Conflict, httpContext.Response.StatusCode,
            "CAS 冲突必须返回 409。");

        httpContext.Response.Body.Position = 0;
        using var reader = new StreamReader(httpContext.Response.Body);
        var json = await reader.ReadToEndAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.AreEqual("CasConflict", GetString(root, "ErrorCode"), "错误码必须稳定。");
        Assert.AreEqual("models.activate", GetString(root, "OperationId"), "操作 ID 必须稳定。");
        Assert.IsTrue(
            GetString(root, "Message").Contains("CAS 更新失败", StringComparison.Ordinal),
            "消息必须保持原内联文案。");
    }

    /// <summary>忽略大小写读取 JSON 属性（不依赖序列化命名约定）。</summary>
    private static string GetString(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.GetString() ?? string.Empty;
            }
        }
        return string.Empty;
    }
}
