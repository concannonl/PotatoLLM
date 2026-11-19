# PotatoLLM

A private AI chat assistant that runs entirely on cheap, low-powered laptops, such as an ex-corporate Lenovo ThinkPad X250. It needs no internet once installed, has no accounts and has no settings to understand.

## Install (Windows 10/11, 64-bit)

1. On this page, click **Code → Download ZIP** and unzip it.
2. Double-click **Install Offline Assistant.bat**.
3. Wait for the download to finish (about 1–2 GB). The assistant then opens by itself.

After that, use the **Offline Assistant** icon on the desktop. No admin rights are needed.

## What it does

- Checks the computer and picks the right model automatically. With 8 GB of memory or more it uses **Gemma 3 4B** with a 16k-token chat memory; otherwise it uses a 1B model (still under evaluation). See [docs/benchmarks.md](docs/benchmarks.md) for why.
- Chooses the best engine settings for the CPU. The user never sees them.
- Provides a simple native chat window with saved chats, a Stop button and New chat.
- Shows a **chat memory** bar: how much of the model's context window the chat fills. Its tooltip explains it in plain language.
- Keeps chats only on the computer, in `%LOCALAPPDATA%\OfflineAssistant\chats`.

## How it works

| Part | What it is |
|---|---|
| `install.ps1` | Downloads [llama.cpp](https://github.com/ggml-org/llama.cpp) (Windows CPU build) and the model, writes `config.ini`, builds the app, creates shortcuts |
| `src/OfflineAssistant.cs` | The chat window. It is C# 5 / WinForms, so it compiles on the user's machine with the `csc.exe` built into Windows, with nothing extra to install |
| `benchmark/` | A one-click speed test for llama.cpp across several small models and thread counts |

The app starts `llama-server.exe` in the background on a random local port and talks to it through the OpenAI-style chat API, streaming the reply. A Windows job object makes sure the engine closes whenever the app closes. Older messages are dropped in large chunks, so the engine's prompt cache stays valid between replies.

Everything is installed to `%LOCALAPPDATA%\OfflineAssistant`. The logs are in its `logs` folder.

## Performance

See [docs/benchmarks.md](docs/benchmarks.md). In short, these laptops are limited by memory bandwidth, so reply speed is roughly **11 GB/s ÷ model file size**. llama.cpp already gets close to that limit, so the gains come from choosing the right model and settings rather than from new maths code.
