# Build notes — Milestone 1

## What is now automated

The repository contains a reproducible Windows pipeline that pins and verifies:

- `llama.cpp b10941` Windows CPU x64;
- `llama.cpp b10941` Windows CUDA 12.4 x64;
- the matching packaged CUDA 12.4 runtime DLL archive;
- official `Qwen/Qwen3-0.6B-GGUF` Q8_0 model.

`assets.lock.json` records the expected SHA-256 values. `scripts/fetch-assets.ps1` refuses files whose hash does not match.

## Smoke tests

Two separate tests are included:

1. `smoke-test-runtime.ps1` starts the CPU `llama-server.exe`, waits for `/health`, then calls `/v1/chat/completions` and requires non-empty content.
2. `smoke-test-pocketai.ps1` launches the published `PocketAI.exe` in a headless self-test mode. This exercises Pocket AI's own hardware probe, `LlamaServerManager`, API client, chat generation and shutdown behavior.

The Windows GitHub Actions workflow runs both tests before publishing the artifact.

## Current execution-environment limitation

The ChatGPT container used to prepare this source tree is Linux and does not contain the Windows/.NET SDK toolchain. Direct binary downloads from GitHub/Hugging Face are also restricted in this environment. Therefore no claim is made that the Windows executable was run inside this container.

The repository is instead prepared so the same operation is deterministic and testable on any Windows 10/11 x64 machine or the included `windows-latest` GitHub Actions job.
