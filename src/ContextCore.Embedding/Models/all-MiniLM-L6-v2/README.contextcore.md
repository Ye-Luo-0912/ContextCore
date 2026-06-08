# all-MiniLM-L6-v2 本地 Embedding 模型

此目录保存 ContextCore 可选的英文 embedding 模型文件，所有文件均放在项目内专用目录，避免写入用户目录或系统目录。

## 来源

- 上游仓库：`https://huggingface.co/Xenova/all-MiniLM-L6-v2`
- 原始模型：`sentence-transformers/all-MiniLM-L6-v2`
- 当前权重：`onnx/model_quantized.onnx`
- 模型卡：见本目录 `MODEL_CARD.md`
- 许可证：上游模型卡标注为 Apache-2.0

## 当前用途

- 用于英文语义相似度和英文上下文场景。
- 可通过 `EmbeddingOptions.ModelPath` 与 `EmbeddingOptions.VocabularyPath` 手动切换使用。
- 输出维度为 384。

## 文件说明

- `onnx/model_quantized.onnx`：量化 ONNX 推理权重。
- `vocab.txt`：BERT WordPiece tokenizer 词表。
- `config.json`：模型结构元数据。
- `tokenizer_config.json`：tokenizer 配置。
- `special_tokens_map.json`：特殊 token 配置。
- `MODEL_CARD.md`：上游模型卡快照。

## 注意事项

该模型主要面向英文语义相似度任务。ContextCore 当前中文默认模型为 `bge-small-zh-v1.5`。
