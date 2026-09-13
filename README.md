# Pocket AI — Milestone 1

Portable Windows MVP: hardware detection, local `llama.cpp` process management, streaming chat and clean shutdown.

## What this milestone contains

- WPF desktop app (`PocketAI.exe`) on .NET 10.
- CPU/RAM/GPU detection.
- Automatic CUDA-first / CPU fallback runtime selection.
- `llama-server` bound only to `127.0.0.1` with a random port and random API key.
- OpenAI-compatible streaming chat through `/v1/chat/completions`.
- Windows Job Object cleanup plus process-tree fallback shutdown.
- Reproducible third-party asset lock file.
- Automated download + SHA-256 verification of Windows llama.cpp CPU/CUDA runtimes.
- Automated download + SHA-256 verification of a small official Qwen GGUF model.
- Runtime smoke-test and actual `PocketAI.exe` headless smoke-test.
- Windows GitHub Actions pipeline that builds, tests and emits the portable ZIP.

## Pinned smoke-test stack

- **llama.cpp:** `b10941`, commit `4a89937354190cef5a97baf8eeb17336105eb72d`
- **CPU runtime:** Windows x64
- **CUDA runtime:** Windows x64 CUDA 12.4 + matching packaged CUDA DLLs
- **Model:** `Qwen/Qwen3-0.6B-GGUF`, `Q8_0`, stored locally as `models/chat/model.gguf`

See `assets.lock.json` for exact SHA-256 hashes.

## Fastest path on a Windows 10/11 x64 machine

Install the .NET 10 SDK, open PowerShell in the repository, then run:

```powershell
.\scripts\prepare-win-release.ps1
```

It will:

1. fetch and verify all pinned AI assets;
2. test the CPU llama-server directly;
3. publish self-contained `PocketAI.exe`;
4. run `PocketAI.exe --self-test --force-cpu`;
5. produce `dist\PocketAI-Milestone1-win-x64.zip`.

The target PC that runs the resulting portable build does **not** need a separately installed .NET Runtime.

## Run individual stages

```powershell
.\scripts\fetch-assets.ps1
.\scripts\verify-layout.ps1 -Strict
.\scripts\smoke-test-runtime.ps1
.\scripts\publish-win-x64.ps1
.\scripts\smoke-test-pocketai.ps1
```

## Normal user launch

After successful publish:

```text
dist\PocketAI\PocketAI.exe
```

The app detects the hardware, tries the CUDA build when NVIDIA hardware is detected, and falls back to the CPU build if CUDA startup fails.

## Headless integration self-test

```powershell
.\dist\PocketAI\PocketAI.exe `
  --self-test `
  --force-cpu `
  --self-test-output .\dist\PocketAI\smoke-test-pocketai.json
```

A successful run exits with code `0` and writes `passed: true` to the JSON report.

## Privacy behavior in Milestone 1

- inference server listens only on loopback;
- no account;
- no telemetry SDK;
- no cloud inference API;
- `llama-server` receives `--offline`;
- the app intentionally ignores hidden reasoning fields and displays final content only.

## Current scope

This is still Milestone 1. It does **not** yet include RAG, document parsing, persistent chats, encryption, benchmark-based model selection or VRAM-aware autotuning.

See `RUNTIME_SETUP.md`, `BUILD_NOTES.md` and `THIRD_PARTY.md` for implementation details.
