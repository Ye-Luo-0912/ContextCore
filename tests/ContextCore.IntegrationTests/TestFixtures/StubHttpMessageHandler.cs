using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ContextCore.IntegrationTests.TestFixtures;

// ===========================================================================
// Production Evidence E2E 共享 HTTP mock
//
// 目标：合并 tests 目录下 4 处重复的 StubHttpMessageHandler 实现，提供统一的
// 脚本化外部 HTTP 依赖 mock（LLM API、外部工具端点等）。
//
// 设计原则：
//   1. 支持两种模式：
//      - 单一 handler 模式：所有请求返回同一响应（最简场景）。
//      - 队列匹配模式：按入队顺序依次返回（脚本化多次调用）。
//   2. 捕获所有请求用于断言（方法、URI、请求体、请求头）。
//   3. 线程安全（ConcurrentQueue (ordered)），支持并发 HTTP 调用。
//   4. 提供 Json(...) 静态工厂简化 JSON 响应构造。
// ===========================================================================

/// <summary>
/// 脚本化 HTTP mock handler，用于 E2E 测试中模拟外部 HTTP 依赖。
/// </summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<Func<HttpRequestMessage, HttpResponseMessage>> _handlers = new();
    private readonly ConcurrentQueue<HttpRequestMessage> _capturedRequests = new();

    /// <summary>
    /// 使用单一 handler 构造：所有请求返回同一响应。
    /// </summary>
    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        _handlers.Enqueue(handler);
    }

    /// <summary>
    /// 使用响应队列构造：按入队顺序依次返回（脚本化多次调用）。
    /// 队列耗尽时返回 500 Internal Server Error。
    /// </summary>
    public StubHttpMessageHandler(IEnumerable<HttpResponseMessage> responses)
    {
        foreach (var response in responses)
        {
            var captured = response;
            _handlers.Enqueue(_ => captured);
        }
    }

    /// <summary>已捕获的请求列表（用于断言验证）。</summary>
    public IReadOnlyList<HttpRequestMessage> CapturedRequests => _capturedRequests.ToArray();

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // 捕获请求（克隆以避免 Dispose 后无法读取）
        _capturedRequests.Enqueue(CloneRequest(request));

        if (_handlers.TryDequeue(out var handler))
        {
            return Task.FromResult(handler(request));
        }

        // 队列耗尽：返回 500，避免测试挂起
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("StubHttpMessageHandler 队列已耗尽：未预期的额外 HTTP 请求。", Encoding.UTF8, "text/plain")
        });
    }

    /// <summary>创建 JSON 响应消息。</summary>
    public static HttpResponseMessage Json(object body, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var json = JsonSerializer.Serialize(body);
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    /// <summary>创建文本响应消息。</summary>
    public static HttpResponseMessage Text(string body, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain")
        };
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage original)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri);
        if (original.Content is not null)
        {
            var content = original.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            clone.Content = new ByteArrayContent(content);
            if (original.Content.Headers.ContentType is not null)
            {
                clone.Content.Headers.ContentType = new MediaTypeHeaderValue(original.Content.Headers.ContentType.MediaType);
            }
        }
        foreach (var header in original.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return clone;
    }
}
