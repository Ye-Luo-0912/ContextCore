using System.Runtime.CompilerServices;

// R13.1 #4：暴露 PendingPurge 给测试，让 retention fire-and-forget Task 可被等待断言。
[assembly: InternalsVisibleTo("ContextCore.Tests")]
