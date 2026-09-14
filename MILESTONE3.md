# PocketAI Milestone 3

Milestone 3 adds:
- PDF import via PdfPig;
- optional dedicated local llama.cpp embedding server (`models/embeddings/model.gguf`);
- persisted vector index (`knowledge/vectors.json`) with cosine search and lexical fallback;
- explicit `WEB ENABLED` switch, DuckDuckGo HTML search and HTTP/HTTPS page reader;
- local image generation client compatible with stable-diffusion.cpp `POST /v1/images/generations`;
- diagnostic ZIP fields for vector/web/image state.

## Privacy defaults
Web and image features are disabled by default. Chat inference remains loopback-only. External page text is treated as untrusted context. Diagnostic ZIP omits prompts, raw knowledge contents, keys and GGUF files.

## Embeddings
Place a dedicated embedding GGUF at `models/embeddings/model.gguf`. PocketAI starts a second loopback llama-server in `--embedding --pooling mean` mode. If absent, RAG continues with lexical search.

## Images
Start a local stable-diffusion.cpp-compatible server and set `images.enabled=true`. Default endpoint: `http://127.0.0.1:7860/v1/images/generations`.
