# Pocket AI Milestone 1 — runtime setup

The project pins concrete runtime/model artifacts so a Windows machine can prepare the complete portable build reproducibly.

## Pinned assets

- llama.cpp: `b10941` / commit `4a89937354190cef5a97baf8eeb17336105eb72d`
- Windows x64 CPU runtime
- Windows x64 CUDA 12.4 runtime + packaged CUDA runtime DLLs
- Qwen `Qwen3-0.6B-GGUF`, `Q8_0`, stored locally as `models/chat/model.gguf`

SHA-256 values are stored in `assets.lock.json` and enforced by `scripts/fetch-assets.ps1`.

## One-command preparation on Windows

Open PowerShell in the project directory:

```powershell
.\scripts\prepare-win-release.ps1
```

That command:

1. Downloads the pinned llama.cpp CPU/CUDA assets.
2. Downloads the pinned GGUF model.
3. Verifies SHA-256 before extracting/copying anything.
4. Runs a direct CPU llama-server smoke-test (`/health` + `/v1/chat/completions`).
5. Publishes a self-contained `PocketAI.exe` for `win-x64`.
6. Runs the actual `PocketAI.exe --self-test --force-cpu` headlessly.
7. Creates `dist\PocketAI-Milestone1-win-x64.zip`.

## Manual steps

```powershell
.\scripts\fetch-assets.ps1
.\scripts\verify-layout.ps1 -Strict
.\scripts\smoke-test-runtime.ps1
.\scripts\publish-win-x64.ps1
.\scripts\smoke-test-pocketai.ps1
```

The final portable directory is `dist\PocketAI`.
