using ContextCore.Abstractions.Models;
using ContextCore.Client;

namespace ContextCore.Tests;

/// <summary>
/// QueryBuilder 逐字节查询字符串输出校验；覆盖空、单参数、顺序、跳过空值、枚举、转义与 AddRaw 字面量。
/// </summary>
[TestClass]
[TestCategory("Client")]
public sealed class QueryBuilderTests
{
    [TestMethod]
    public void Empty_ReturnsEmptyString()
    {
        var qb = new QueryBuilder();
        Assert.AreEqual(string.Empty, qb.ToString());
    }

    [TestMethod]
    public void SingleAdd_ReturnsQueryStringWithoutTrailingSeparator()
    {
        var qb = new QueryBuilder().Add("workspaceId", "ws1");
        Assert.AreEqual("?workspaceId=ws1", qb.ToString());
    }

    [TestMethod]
    public void MultipleAdds_PreserveInsertionOrder()
    {
        var qb = new QueryBuilder()
            .Add("workspaceId", "ws1")
            .Add("take", 5)
            .Add("collectionId", "col1");
        Assert.AreEqual("?workspaceId=ws1&take=5&collectionId=col1", qb.ToString());
    }

    [TestMethod]
    public void AddString_NullEmptyWhitespace_AreSkipped()
    {
        var qb = new QueryBuilder()
            .Add("a", (string?)null)
            .Add("b", string.Empty)
            .Add("c", "   ")
            .Add("d", "valid");
        Assert.AreEqual("?d=valid", qb.ToString());
    }

    [TestMethod]
    public void AddEnum_PresentValue_RendersEnumName_NullValueSkipped()
    {
        var present = new QueryBuilder().AddEnum("status", (FeedbackReviewStatus?)FeedbackReviewStatus.PendingReview);
        Assert.AreEqual("?status=PendingReview", present.ToString());

        var absent = new QueryBuilder().AddEnum("status", (FeedbackReviewStatus?)null);
        Assert.AreEqual(string.Empty, absent.ToString());
    }

    [TestMethod]
    public void AddString_AppliesUriEscapeDataString()
    {
        var qb = new QueryBuilder().Add("q", "a b&c");
        Assert.AreEqual("?q=a%20b%26c", qb.ToString());
    }

    [TestMethod]
    public void AddRaw_AlwaysAppendedLiterallyWithoutEscaping()
    {
        var qb = new QueryBuilder().AddRaw("runtimeFeedback", "true");
        Assert.AreEqual("?runtimeFeedback=true", qb.ToString());
    }
}
