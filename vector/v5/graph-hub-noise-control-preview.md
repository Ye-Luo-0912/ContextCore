# Graph Hub Noise Control Preview

生成: `2026-06-18T16:28:03.2513544+00:00`

## 摘要
- PreviewPassed: `False`
- GatePassed: `False`
- Recommendation: `KeepBaselineOnly`
- HubItemCount: `3`
- AvgEnvelopeWidthRatio: `1.3472`
- AvgHubDominanceRatio: `0.0583`

## 评分对比
- baseline        : recall=0.5083 MRR=0.2275
- previous derived: recall=0.4792 MRR=0.1998
- hub-controlled  : recall=0.3750 MRR=0.1671
- hub-ctrl delta  : recall=-0.1333 MRR=-0.0604

V5.9 preview only. Graph hub noise control applied to relation envelope. No formal retrieval, package write, selected set change, packing policy mutation, runtime switch, or vector store binding change.
