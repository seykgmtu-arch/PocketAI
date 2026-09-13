# Release output

The Windows build workflow produces exactly:

`dist/PocketAI-win-x64.zip`

The archive contains `PocketAI.exe`, the self-contained .NET runtime files, the pinned Windows llama.cpp CPU/CUDA runtime, `Qwen3-0.6B-Q8_0.gguf`, `pocketai.json`, and smoke-test output.

The workflow fails before artifact upload if runtime hash verification, llama.cpp smoke test, .NET build/publish, or `PocketAI.exe --self-test` fails.
