#!/usr/bin/env python3
"""CI 证据门禁 —— 拒绝 Inconclusive 测试结果掩盖缺失证据。

用法：gate-no-inconclusive.py <trx 文件或目录>...

语义：
  - 逐个解析 TRX（vstest 结果 XML），统计所有 UnitTestResult 的 outcome。
  - 任一 outcome == "Inconclusive" → exit 1（CI 失败）：
    Inconclusive 表示测试因环境/前置条件未满足而跳过，不能证明功能通过。
    生产证据 CI（integration-postgres 等）必须全部真实通过，不允许用
    Inconclusive 掩盖缺失的证据（例如 Docker 未就绪导致的静默跳过）。
  - 未找到任何 TRX 文件 → exit 2（证据缺失）。
  - 全部通过 → exit 0。
"""

import sys
import xml.etree.ElementTree as ET


def collect_trx_files(paths):
    """收集 paths 中的 .trx 文件（目录递归）。"""
    files = []
    for path in paths:
        if path.endswith(".trx"):
            files.append(path)
        else:
            import os
            if os.path.isdir(path):
                for root, _dirs, names in os.walk(path):
                    for name in names:
                        if name.endswith(".trx"):
                            files.append(os.path.join(root, name))
    return files


def main() -> int:
    if len(sys.argv) < 2:
        print("ERROR: 未提供 TRX 文件/目录路径。用法: gate-no-inconclusive.py <trx>...")
        return 2

    trx_files = collect_trx_files(sys.argv[1:])
    if not trx_files:
        print("ERROR: 未找到任何 TRX 文件 —— 测试结果缺失，生产证据不完整。")
        return 2

    ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
    inconclusive = []
    failed = []
    total = 0

    for path in trx_files:
        try:
            tree = ET.parse(path)
        except Exception as ex:  # noqa: BLE001 - 任何解析失败都视为证据不完整
            print(f"ERROR: 解析 TRX 失败 {path}: {ex}")
            return 2
        root = tree.getroot()
        for result in root.findall(".//t:UnitTestResult", ns):
            total += 1
            outcome = result.get("outcome", "")
            test_name = result.get("testName", "?")
            if outcome == "Inconclusive":
                inconclusive.append(f"{path}: {test_name}")
            elif outcome == "Failed":
                failed.append(f"{path}: {test_name}")

    if inconclusive:
        print(
            f"ERROR: 发现 {len(inconclusive)} 个 Inconclusive 测试结果"
            "（CI 环境应全部真实通过，不允许用 Inconclusive 掩盖缺失证据）："
        )
        for line in inconclusive:
            print("  - " + line)
        return 1

    if failed:
        print(f"ERROR: 发现 {len(failed)} 个 Failed 测试结果：")
        for line in failed[:20]:
            print("  - " + line)
        if len(failed) > 20:
            print(f"  ... 及另外 {len(failed) - 20} 个")
        return 1

    print(f"OK: {len(trx_files)} 个 TRX 共 {total} 条测试结果，0 Inconclusive / 0 Failed。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
