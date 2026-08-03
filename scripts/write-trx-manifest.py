#!/usr/bin/env python3
"""CI 证据 TRX 清单（步骤 2.5）—— 将 evidence/trx 下每个必测类别的 TRX 文件与
测试计数固化为 evidence/trx-manifest.json，供证据工件机器可读审计与
gate-evidence.py --trx-manifest 一致性校验。

用法：write-trx-manifest.py --manifest-dir ci-manifests --out evidence/trx-manifest.json TRX_ROOT...

语义（WP-R30.1-F）：
  1. 按 required-artifacts.json 的 dir 集合扫描每个类别的 TRX（与 gate-evidence.py 同目录定位规则）；
  2. 每个类别记录 trxFiles（相对 TRX_ROOT 的路径列表）+ trxCount + executed/passed/failed 计数
     （outcome ∈ {Passed, Failed} 计为真实执行，与 gate-evidence.py 口径一致）；
  3. 输出供证据工件机器可读审计，并让 gate-evidence.py 校验"被门禁的 TRX 集"与清单一致
     （堵住清单由陈旧目录生成的漂移）。

退出码：0 成功；2 配置错误/解析失败。
"""

import argparse
import datetime
import json
import os
import sys
import xml.etree.ElementTree as ET

TRX_NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
EXECUTED_OUTCOMES = {"Passed", "Failed"}


def load_json(path: str) -> dict:
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except Exception as ex:  # noqa: BLE001 - 配置错误一律视为证据不可判定
        print(f"ERROR: 读取 manifest 失败 {path}: {ex}")
        sys.exit(2)


def collect_trx_files(directory: str):
    """收集 directory 下的 .trx 文件（递归），确定性排序。"""
    files = []
    for root, _dirs, names in os.walk(directory):
        for name in names:
            if name.endswith(".trx"):
                files.append(os.path.join(root, name))
    return sorted(files)


def count_executed(path: str):
    """统计单个 TRX 的 executed/passed/failed（与 gate-evidence.py 同口径）。"""
    tree = ET.parse(path)
    root = tree.getroot()
    executed = passed = failed = 0
    for result in root.findall(".//t:UnitTestResult", TRX_NS):
        outcome = result.get("outcome", "")
        if outcome in EXECUTED_OUTCOMES:
            executed += 1
            if outcome == "Passed":
                passed += 1
            else:
                failed += 1
    return executed, passed, failed


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest-dir", default="ci-manifests",
                        help="ci-manifests 目录（含 required-*.json）。")
    parser.add_argument("--out", default="evidence/trx-manifest.json",
                        help="输出文件路径。")
    parser.add_argument("roots", nargs="+", help="TRX 根目录（如 evidence/trx）。")
    args = parser.parse_args()

    artifacts_manifest = load_json(os.path.join(args.manifest_dir, "required-artifacts.json"))
    artifacts = artifacts_manifest.get("artifacts")
    if not isinstance(artifacts, list):
        print("ERROR: required-artifacts.json 结构非法（缺少 artifacts 数组）。")
        return 2

    # 与 gate-evidence.py 相同的类别目录集合（相对第一个 TRX root）。
    category_dirs = sorted({a.get("dir") for a in artifacts if a.get("dir")})

    categories = {}
    for cat_dir in category_dirs:
        files = []
        for root in args.roots:
            candidate = os.path.join(root, cat_dir)
            if os.path.isdir(candidate):
                files.extend(collect_trx_files(candidate))
        entries = []
        for path in files:
            executed, passed, failed = count_executed(path)
            entries.append({
                "file": os.path.relpath(path, args.roots[0]).replace("\\", "/"),
                "executed": executed,
                "passed": passed,
                "failed": failed
            })
        categories[cat_dir] = {
            "trxCount": len(entries),
            "trxFiles": entries,
            "executed": sum(e["executed"] for e in entries),
            "passed": sum(e["passed"] for e in entries),
            "failed": sum(e["failed"] for e in entries)
        }

    document = {
        "manifestVersion": 1,
        "generatedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "categories": categories
    }

    out_dir = os.path.dirname(args.out)
    if out_dir:
        os.makedirs(out_dir, exist_ok=True)
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(document, f, ensure_ascii=False, indent=2)
        f.write("\n")

    total_trx = sum(c["trxCount"] for c in categories.values())
    total_executed = sum(c["executed"] for c in categories.values())
    print(f"OK: 已写入 {args.out}（{len(categories)} 个类别，{total_trx} 个 TRX，"
          f"{total_executed} 条真实执行）。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
