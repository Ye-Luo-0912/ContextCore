# bge-small-zh-v1.5 中文 Embedding 模型

此目录保存 ContextCore 当前默认的中文本地 embedding 模型文件，所有文件均放在项目内专用目录，避免写入用户目录或系统目录。

## 来源

- ONNX 仓库：`https://huggingface.co/Xenova/bge-small-zh-v1.5`
- 原始模型：`https://huggingface.co/BAAI/bge-small-zh-v1.5`
- 当前权重：`onnx/model_quantized.onnx`
- 模型卡：见本目录 `MODEL_CARD.md`
- 许可证：上游模型卡标注为 MIT

## 当前用途

- 作为 ContextCore 中文上下文管理的默认本地 embedding 模型。
- 用于后续 P3 Hybrid Retrieval 的中文语义召回基础能力。
- 默认输出维度为 512。
- 默认使用 CLS pooling，并在 provider 层单位化。

## 文件说明

- `onnx/model_quantized.onnx`：量化 ONNX 推理权重。
- `vocab.txt`：BERT WordPiece tokenizer 词表。
- `config.json`：模型结构元数据。
- `tokenizer_config.json`：tokenizer 配置。
- `special_tokens_map.json`：特殊 token 配置。
- `MODEL_CARD.md`：上游模型卡快照。

## 注意事项

BGE 检索模型通常需要区分 query 与 document 的文本前缀。当前 P3-2 只完成基础 embedding 生成；后续 Hybrid Retriever 阶段应在查询向量生成时增加可配置 query instruction。
