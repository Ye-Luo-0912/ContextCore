# ContextCore 当前路线

> 更新：2026-08-15。这里只保留当前入口与状态；长期安排见路线文档，已完成细节从 Git 查询。

## 当前状态

路线图 LR-0A..LR-7E 全部工作包已完成并验证：全量测试 4222 通过 / 6 跳过 / 0 失败；容器构建与冒烟通过；已推送 origin/main。当前无进行中工作包。

- 现行行为与调用链：见 [`docs/LIVE_PATH.md`](docs/LIVE_PATH.md)
- 唯一活动路线与下一轮候选方向：见 [`docs/ROADMAP.md`](docs/ROADMAP.md)
- 当前性能基线：[`benchmarks/results/MULTIQUERY_RECALL_BASELINE.md`](benchmarks/results/MULTIQUERY_RECALL_BASELINE.md)

下一轮工作包从 ROADMAP §2 / §1.2 / §1.3 缺口中指定：质量基线落地、真实模型打分与 Learning 四道门、长期精简目标。未达门槛前不打开 Adaptive Active / Learning 训练，不接原型 materialize。
