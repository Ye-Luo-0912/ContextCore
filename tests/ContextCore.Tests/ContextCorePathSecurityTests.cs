using ContextCore.Abstractions.Models;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;

namespace ContextCore.Tests;

/// <summary>
/// 路径边界安全测试。
/// 验证 FilePathResolver 对 rooted path、../、Windows 驱动器、UNC、超长 ID、Unicode 的防护。
/// 不能只在 API 层拦截——所有 Store 使用的路径方法都必须经过 SanitizeSegment + EnsureInsideRoot。
/// </summary>
[TestClass]
[TestCategory("Security")]
public sealed class ContextCorePathSecurityTests
{
    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "contextcore-sec-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    // ── SanitizeSegment ──────────────────────────────────────────────────────

    [TestMethod]
    public void SanitizeSegment_ShouldRejectRootedPathSlash()
    {
        var resolver = new FilePathResolver(new FileStorageOptions { RootPath = CreateTempRoot() });
        // "/etc" 中的 / 被替换为 -，不会产生 rooted path
        var sanitized = resolver.SanitizeSegment("/etc/passwd");
        Assert.IsFalse(Path.IsPathRooted(sanitized));
        Assert.IsFalse(sanitized.Contains('/'));
    }

    [TestMethod]
    public void SanitizeSegment_ShouldRejectRootedPathBackslash()
    {
        var resolver = new FilePathResolver(new FileStorageOptions { RootPath = CreateTempRoot() });
        var sanitized = resolver.SanitizeSegment("\\windows\\system32");
        Assert.IsFalse(Path.IsPathRooted(sanitized));
        Assert.IsFalse(sanitized.Contains('\\'));
    }

    [TestMethod]
    public void SanitizeSegment_ShouldRejectWindowsDrivePath()
    {
        var resolver = new FilePathResolver(new FileStorageOptions { RootPath = CreateTempRoot() });
        var sanitized = resolver.SanitizeSegment("C:\\windows\\system32");
        // 冒号和反斜杠都被替换
        Assert.IsFalse(Path.IsPathRooted(sanitized));
        Assert.IsFalse(sanitized.Contains(':'));
        Assert.IsFalse(sanitized.Contains('\\'));
    }

    [TestMethod]
    public void SanitizeSegment_ShouldRejectUNCPath()
    {
        var resolver = new FilePathResolver(new FileStorageOptions { RootPath = CreateTempRoot() });
        var sanitized = resolver.SanitizeSegment("\\\\server\\share");
        Assert.IsFalse(Path.IsPathRooted(sanitized));
        Assert.IsFalse(sanitized.Contains('\\'));
    }

    [TestMethod]
    public void SanitizeSegment_ShouldRejectDirectoryTraversal()
    {
        var resolver = new FilePathResolver(new FileStorageOptions { RootPath = CreateTempRoot() });
        // ".." trim 后为空，回退为 "default"
        Assert.AreEqual("default", resolver.SanitizeSegment(".."));
        Assert.AreEqual("default", resolver.SanitizeSegment("../.."));
        Assert.AreEqual("default", resolver.SanitizeSegment("..\\.."));
    }

    [TestMethod]
    public void SanitizeSegment_ShouldTruncateSuperLongId()
    {
        var resolver = new FilePathResolver(new FileStorageOptions { RootPath = CreateTempRoot() });
        var longId = new string('a', 500);
        var sanitized = resolver.SanitizeSegment(longId);
        Assert.IsTrue(sanitized.Length <= 96);
    }

    [TestMethod]
    public void SanitizeSegment_ShouldHandleUnicodeCharacters()
    {
        var resolver = new FilePathResolver(new FileStorageOptions { RootPath = CreateTempRoot() });
        // Unicode 字符应被保留（仅排除路径非法字符和控制字符）
        var sanitized = resolver.SanitizeSegment("中文工作空间");
        Assert.AreEqual("中文工作空间", sanitized);
    }

    [TestMethod]
    public void SanitizeSegment_ShouldReplacePathSeparatorsInId()
    {
        var resolver = new FilePathResolver(new FileStorageOptions { RootPath = CreateTempRoot() });
        var sanitized = resolver.SanitizeSegment("workspace/escape");
        Assert.AreEqual("workspace-escape", sanitized);
    }

    // ── EnsureInsideRoot ─────────────────────────────────────────────────────

    [TestMethod]
    public void EnsureInsideRoot_ShouldAllowPathWithinRoot()
    {
        var root = CreateTempRoot();
        var resolver = new FilePathResolver(new FileStorageOptions { RootPath = root });
        var safePath = Path.Combine(root, "workspaces", "ws1", "collections");
        var result = resolver.EnsureInsideRoot(safePath);
        Assert.IsTrue(result.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void EnsureInsideRoot_ShouldThrowOnPathOutsideRoot()
    {
        var root = CreateTempRoot();
        var resolver = new FilePathResolver(new FileStorageOptions { RootPath = root });
        var escapedPath = Path.Combine(Path.GetTempPath(), "outside-root", "secret.json");
        Assert.ThrowsException<InvalidOperationException>(() => resolver.EnsureInsideRoot(escapedPath));
    }

    // ── FilePathResolver 路径方法安全验证 ────────────────────────────────────

    [TestMethod]
    public void GetCollectionDirectory_ShouldNotEscapeRootOnTraversal()
    {
        var root = CreateTempRoot();
        var resolver = new FilePathResolver(new FileStorageOptions { RootPath = root });
        var path = resolver.GetCollectionDirectory("../outside", "..\\escape");
        Assert.IsTrue(path.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(path.Contains("..", StringComparison.Ordinal));
    }

    [TestMethod]
    public void GetCollectionDirectory_ShouldNotEscapeRootOnRootedPath()
    {
        var root = CreateTempRoot();
        var resolver = new FilePathResolver(new FileStorageOptions { RootPath = root });
        // "/etc" 被 sanitize 为 "etc"，不会逃逸
        var path = resolver.GetCollectionDirectory("/etc", "passwd");
        Assert.IsTrue(path.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void GetCollectionDirectory_ShouldNotEscapeRootOnWindowsDrive()
    {
        var root = CreateTempRoot();
        var resolver = new FilePathResolver(new FileStorageOptions { RootPath = root });
        var path = resolver.GetCollectionDirectory("C:\\windows", "system32");
        Assert.IsTrue(path.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(path.Contains("windows\\system32", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void GetCollectionDirectory_ShouldNotEscapeRootOnUNC()
    {
        var root = CreateTempRoot();
        var resolver = new FilePathResolver(new FileStorageOptions { RootPath = root });
        var path = resolver.GetCollectionDirectory("\\\\server\\share", "secret");
        Assert.IsTrue(path.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void GetCollectionDirectory_ShouldHandleSuperLongId()
    {
        var root = CreateTempRoot();
        var resolver = new FilePathResolver(new FileStorageOptions { RootPath = root });
        var longId = new string('a', 500);
        var path = resolver.GetCollectionDirectory(longId, "col1");
        Assert.IsTrue(path.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void GetCollectionDirectory_ShouldHandleUnicodeId()
    {
        var root = CreateTempRoot();
        var resolver = new FilePathResolver(new FileStorageOptions { RootPath = root });
        var path = resolver.GetCollectionDirectory("中文工作空间", "中文集合");
        Assert.IsTrue(path.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(path.Contains("中文工作空间"));
        Assert.IsTrue(path.Contains("中文集合"));
    }

    // ── GetRawContentPath itemId 安全验证 ───────────────────────────────────

    [TestMethod]
    public void GetRawContentPath_ShouldSanitizeItemIdTraversal()
    {
        var root = CreateTempRoot();
        var resolver = new FilePathResolver(new FileStorageOptions { RootPath = root });
        var path = resolver.GetRawContentPath("ws1", "col1", "../../../etc/passwd", ContextContentFormat.PlainText);
        Assert.IsTrue(path.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(path.Contains("..", StringComparison.Ordinal));
    }

    [TestMethod]
    public void GetRawContentPath_ShouldSanitizeItemIdRootedPath()
    {
        var root = CreateTempRoot();
        var resolver = new FilePathResolver(new FileStorageOptions { RootPath = root });
        var path = resolver.GetRawContentPath("ws1", "col1", "/etc/passwd", ContextContentFormat.PlainText);
        Assert.IsTrue(path.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void GetRawContentPath_ShouldSanitizeItemIdWindowsDrive()
    {
        var root = CreateTempRoot();
        var resolver = new FilePathResolver(new FileStorageOptions { RootPath = root });
        var path = resolver.GetRawContentPath("ws1", "col1", "C:\\windows\\system32", ContextContentFormat.Json);
        Assert.IsTrue(path.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase));
        // 路径不包含 .. 遍历
        Assert.IsFalse(path.Contains("..", StringComparison.Ordinal));
    }

    // ── 全局路径安全验证 ─────────────────────────────────────────────────────

    [TestMethod]
    public void GetGlobalConstraintsJsonlPath_ShouldNotEscapeRootOnTraversal()
    {
        var root = CreateTempRoot();
        var resolver = new FilePathResolver(new FileStorageOptions { RootPath = root });
        var path = resolver.GetGlobalConstraintsJsonlPath("../../etc");
        Assert.IsTrue(path.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(path.Contains("..", StringComparison.Ordinal));
    }

    [TestMethod]
    public void GetGlobalContextJsonlPath_ShouldNotEscapeRootOnRootedPath()
    {
        var root = CreateTempRoot();
        var resolver = new FilePathResolver(new FileStorageOptions { RootPath = root });
        var path = resolver.GetGlobalContextJsonlPath("/etc/passwd");
        Assert.IsTrue(path.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase));
    }

    // ── Store 级路径安全验证（不能只在 API 层拦截）─────────────────────────

    [TestMethod]
    public async Task FileContextStore_ShouldNotEscapeRootOnTraversalWorkspace()
    {
        var root = CreateTempRoot();
        var options = new FileStorageOptions { RootPath = root };
        var resolver = new FilePathResolver(options);
        var store = new FileContextStore(options);

        // 传入恶意 workspaceId，存储不应逃逸根目录
        var item = new ContextItem
        {
            Id = "safe-item",
            WorkspaceId = "../outside",
            CollectionId = "col1",
            Type = "note",
            Content = "test",
            ContentFormat = ContextContentFormat.PlainText,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await store.SaveAsync(item);
        // 验证文件确实写在 root 内
        var allFiles = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
        Assert.IsTrue(allFiles.Length > 0);
        Assert.IsTrue(allFiles.All(f => f.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task FileContextStore_ShouldNotEscapeRootOnRootedWorkspace()
    {
        var root = CreateTempRoot();
        var options = new FileStorageOptions { RootPath = root };
        var resolver = new FilePathResolver(options);
        var store = new FileContextStore(options);

        var item = new ContextItem
        {
            Id = "safe-item",
            WorkspaceId = "/etc/passwd",
            CollectionId = "col1",
            Type = "note",
            Content = "test",
            ContentFormat = ContextContentFormat.PlainText,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await store.SaveAsync(item);
        var allFiles = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
        Assert.IsTrue(allFiles.All(f => f.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)));
    }
}
