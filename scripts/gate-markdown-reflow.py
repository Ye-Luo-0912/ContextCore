#!/usr/bin/env python3
"""阻止报告 Markdown 回流到版本控制目录。

检查仓库里每个 .md 文件是否落在活动文档白名单内：

允许：
- 根 README.md / AGENTS.md / TODO.md；
- docs/ 顶层与 docs/runbooks/（活动文档区；docs/archive/、docs/api/ 等
  已归档子目录不允许出现 .md）；
- eval/contexts/**/README.md（评测语料元数据）；
- benchmarks/results/MULTIQUERY_RECALL_BASELINE.md（当前活动性能基线报告）；
- src/ContextCore.Embedding/Models/**/*.md（模型卡）。

禁止：eval/（除 contexts/）、learning/、foundation/、vector/、storage/、
service/、benchmarks/results/results/、docs/archive/、docs/api/ 等机器证据
目录出现任何 .md；未跟踪的新 .md 只要不在白名单内同样判定为回流。

用法：python3 scripts/gate-markdown-reflow.py [--repo-root <path>]
退出码：0 通过；1 发现回流。
"""

import argparse
import os
import sys

SKIP_DIRS = {
    ".git",
    "bin",
    "obj",
    "artifacts",
    "BenchmarkDotNet.Artifacts",
    "TestResults",
    "test-results",
    ".appdata",
    ".vs",
    ".idea",
    ".claude",
    ".msbuild",
    ".nuget",
    ".userprofile",
    ".dotnet_home",
    ".localappdata",
    "node_modules",
    "context-core-data",
    "context-core-relation-smoke",
}

ROOT_ALLOWED = {"README.md", "AGENTS.md", "TODO.md"}
BENCHMARK_ALLOWED = {"benchmarks/results/MULTIQUERY_RECALL_BASELINE.md"}


def is_allowed(rel: str) -> bool:
    """判断相对路径是否属于活动文档白名单。rel 使用 / 分隔。"""
    if rel in ROOT_ALLOWED:
        return True
    if rel == BENCHMARK_ALLOWED or rel in BENCHMARK_ALLOWED:
        return True
    if rel.startswith("docs/"):
        parts = rel.split("/")
        # 归档/API 等已删除子目录不允许 Markdown 回流
        if parts[1] in ("archive", "api"):
            return False
        # 活动文档：docs/ 顶层与 docs/runbooks/
        if len(parts) == 2:
            return True
        if len(parts) == 3 and parts[1] == "runbooks":
            return True
        return False
    if rel.startswith("eval/contexts/") and rel.endswith("/README.md"):
        return True
    if rel.startswith("src/ContextCore.Embedding/Models/") and rel.endswith(".md"):
        return True
    return False


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo-root", default=os.getcwd(), help="仓库根目录")
    args = parser.parse_args()

    repo_root = os.path.abspath(args.repo_root)
    violations = []

    for dirpath, dirnames, filenames in os.walk(repo_root):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
        for name in filenames:
            if not name.endswith(".md"):
                continue
            full = os.path.join(dirpath, name)
            rel = os.path.relpath(full, repo_root).replace(os.sep, "/")
            if not is_allowed(rel):
                violations.append(rel)

    if violations:
        print("Markdown reflow detected (outside activity-docs whitelist):")
        for rel in sorted(violations):
            print(f"  {rel}")
        print("Move generated reports to ignored artifacts/ or commit them as activity docs.")
        return 1

    print("No Markdown reflow: all .md files are within the activity-docs whitelist.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
