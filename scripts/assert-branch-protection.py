#!/usr/bin/env python3
"""校验 .github/settings.yml 的 main 分支保护配置（WP-R30.1-F）。

行级解析（stdlib-only，无第三方 YAML 依赖，与仓库 CI 工具链一致）：
  1. settings.yml 必须存在（缺失 → exit 2，证据不可判定）；
  2. 必须声明 branch_protection，且包含 main 分支条目（- branch: main）；
  3. main 条目必须启用 required_status_checks（strict: true），
     并把 evidence 设为必查 context（与 ci.yml evidence job 对应，
     唯一必查 check = 聚合门禁）；
  4. main 条目必须启用 enforce_admins，且显式禁止 force push 与删除分支
     （allow_force_pushes: false / allow_deletions: false）。

任何违反 → exit 1，并打印违规项（供 CI / 人工定位）。无输出且 exit 0 表示合规。

用法：
  python3 scripts/assert-branch-protection.py [settings.yml 路径]
默认路径为 <脚本目录>/../.github/settings.yml。
"""

import os
import sys


def _indent(line: str) -> int:
    return len(line) - len(line.lstrip(" "))


def _load_lines(path: str):
    with open(path, "r", encoding="utf-8") as fh:
        return fh.read().splitlines()


def _find_key(lines, key: str, start: int = 0):
    """返回第一个匹配 key 的行的下标（跳过注释/空行），未找到返回 None。"""
    for i in range(start, len(lines)):
        stripped = lines[i].strip()
        if not stripped or stripped.startswith("#"):
            continue
        if stripped == key or stripped.startswith(key + " "):
            return i
    return None


def _block_indices(lines, start: int):
    """返回 start 行之后、缩进严格大于 start 行的连续行下标列表（子块）。"""
    parent = _indent(lines[start])
    result = []
    for i in range(start + 1, len(lines)):
        line = lines[i]
        if not line.strip() or line.strip().startswith("#"):
            continue
        if _indent(line) <= parent:
            break
        result.append(i)
    return result


def _has_key(lines, indices, key: str) -> bool:
    for i in indices:
        stripped = lines[i].strip()
        if stripped == key or stripped.startswith(key + " "):
            return True
    return False


def _has_value(lines, indices, key: str, value: str) -> bool:
    for i in indices:
        stripped = lines[i].strip()
        if stripped == f"{key} {value}":
            return True
    return False


def main() -> int:
    script_dir = os.path.dirname(os.path.abspath(__file__))
    path = sys.argv[1] if len(sys.argv) > 1 else os.path.join(script_dir, "..", ".github", "settings.yml")

    if not os.path.isfile(path):
        print(f"ERROR: 未找到 {path}（exit 2）。", file=sys.stderr)
        return 2

    lines = _load_lines(path)
    violations = []

    def fail(message: str) -> None:
        violations.append(message)
        print(f"违规：{message}", file=sys.stderr)

    # 1. branch_protection 声明
    bp_index = _find_key(lines, "branch_protection:")
    if bp_index is None:
        fail("缺少 branch_protection 声明")
        return 1

    # 2. main 分支条目
    main_entry = None
    for i in _block_indices(lines, bp_index):
        if lines[i].strip().startswith("- branch: main"):
            main_entry = i
            break
    if main_entry is None:
        fail("branch_protection 缺少 main 分支条目（- branch: main）")
        return 1

    entry_block = _block_indices(lines, main_entry)

    # 3. required_status_checks：strict: true + evidence 必查 context
    rsc_index = None
    for i in entry_block:
        if _has_key(lines, [i], "required_status_checks:"):
            rsc_index = i
            break
    if rsc_index is None:
        fail("main 条目缺少 required_status_checks")
    else:
        rsc_block = _block_indices(lines, rsc_index)
        if not _has_value(lines, rsc_block, "strict:", "true"):
            fail("required_status_checks 未启用 strict: true（分支必须与最新 main 同步）")
        if not _has_value(lines, rsc_block, "-", "evidence"):
            fail("required_status_checks 缺少 evidence 必查 context（- evidence）")

    # 4. enforce_admins + 禁止破坏性操作
    if not _has_value(lines, entry_block, "enforce_admins:", "true"):
        fail("main 条目未启用 enforce_admins: true")
    for key in ("allow_force_pushes:", "allow_deletions:"):
        if _has_value(lines, entry_block, key, "true"):
            fail(f"{key} 不允许为 true（禁止 force push / 删除分支）")
        if not _has_value(lines, entry_block, key, "false"):
            fail(f"main 条目缺少 {key} false（未显式禁用）")

    if violations:
        return 1
    print(f"OK: {os.path.normpath(path)} 分支保护合规"
          "（evidence 必查、strict、enforce_admins、禁 force push / 删除）。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
