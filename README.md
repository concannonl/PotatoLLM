# PotatoLLM

A private AI chat assistant that runs entirely on cheap, low-powered laptops, such as an ex-corporate Lenovo ThinkPad X250. It needs no internet once installed, has no accounts and has no settings to understand.

## Install (Windows 10/11, 64-bit)

1. Download **OfflineAssistant-Setup.exe** from the [latest release](https://github.com/concannonl/PotatoLLM/releases/latest).
2. Run it and click through the installer. No admin rights are needed.
3. The first time it opens, the assistant downloads the right AI model for the computer (about 1–3 GB). After that it works offline.

Windows may warn that the installer is from an unknown publisher, because it isn't code-signed yet. Click **More info**, then **Run anyway**.

**Installing without internet** (for example from a USB stick): put the model file next to `OfflineAssistant-Setup.exe` before running it. Use [Gemma 3 4B](https://huggingface.co/bartowski/google_gemma-3-4b-it-GGUF/resolve/main/google_gemma-3-4b-it-Q4_K_M.gguf) for laptops with 8 GB of memory or more, or [Gemma 3 1B](https://huggingface.co/bartowski/google_gemma-3-1b-it-GGUF/resolve/main/google_gemma-3-1b-it-Q4_K_M.gguf) for 4 GB. The installer copies it instead of downloading.

If you pass the model file on to someone else, Gemma's terms require a notice to go with it: include `NOTICE.txt` from the app's `models` folder (it says *"Gemma is provided under and subject to the Gemma Terms of Use found at ai.google.dev/gemma/terms"*).

**Uninstalling:** use Windows Settings → Apps. It asks before deleting your saved chats.

### For developers

Every push to `main` builds the installer on GitHub Actions (`.github/workflows/build.yml` runs `build/build.ps1`). Pushing a tag such as `v0.2.0` also publishes a release. `install.ps1` is the older script that builds from source on the target machine.

## What it does

- Checks the computer and picks the right model automatically. With 8 GB of memory or more it uses **Gemma 3 4B** with a 32k-token chat memory; otherwise it uses **Gemma 3 1B** with a 16k-token chat memory. Chat memory is stored in compact 8-bit form. See [docs/benchmarks.md](docs/benchmarks.md) for why.
- Chooses the best engine settings for the CPU. The user never sees them.
- Provides a simple native chat window with saved chats, a Stop button and New chat.
- Lets you **add documents** (Word .docx, PDF, .txt, .rtf) with a button or by dragging them onto the window. The assistant reads the text; long documents show how long reading will take first. PDF support uses Xpdf's `pdftotext`, which is included in the installer.
- Shows a **chat memory** bar: how much of the model's context window the chat fills. Its tooltip explains it in plain language.
- Keeps chats only on the computer, in `%LOCALAPPDATA%\OfflineAssistant\chats`.

## How it works

| Part | What it is |
|---|---|
| `build/build.ps1`, `installer/` | Compiles the app and packages it with [llama.cpp](https://github.com/ggml-org/llama.cpp) (Windows CPU build) and Xpdf's `pdftotext` into an Inno Setup installer |
| `src/OfflineAssistant.cs` | The chat window. It is C# 5 / WinForms, so it compiles on the user's machine with the `csc.exe` built into Windows, with nothing extra to install |
| `benchmark/` | A one-click speed test for llama.cpp across several small models and thread counts |

The app starts `llama-server.exe` in the background on a random local port and talks to it through the OpenAI-style chat API, streaming the reply. A Windows job object makes sure the engine closes whenever the app closes. Older messages are dropped in large chunks, so the engine's prompt cache stays valid between replies.

Everything is installed to `%LOCALAPPDATA%\OfflineAssistant`. The logs are in its `logs` folder. The app works out its own settings from the computer's memory and processor; an optional `config.ini` can override them.

## Performance

See [docs/benchmarks.md](docs/benchmarks.md). In short, these laptops are limited by memory bandwidth, so reply speed is roughly **11 GB/s ÷ model file size**. llama.cpp already gets close to that limit, so the gains come from choosing the right model and settings rather than from new maths code.

## Licence

PotatoLLM's own code is MIT licensed; see [LICENSE](LICENSE). It bundles llama.cpp (MIT) and Xpdf's pdftotext (GPL), and downloads Google's Gemma model under the Gemma Terms of Use. See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for the details. Users accept short [terms of use](installer/terms.txt) when installing.
