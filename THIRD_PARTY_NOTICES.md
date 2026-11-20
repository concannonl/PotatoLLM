# Third-party notices

PotatoLLM's own code (the Offline Assistant app, installer, build and benchmark scripts) is licensed under the MIT licence; see [LICENSE](LICENSE). It works with the following third-party software and models, which keep their own licences. The installer copies the exact licence files for the versions it includes into the app's `licenses` folder.

## Included in the installer

### llama.cpp: MIT licence
The AI engine (`llama-server.exe` and its libraries). https://github.com/ggml-org/llama.cpp
Its own third-party components (such as cpp-httplib and nlohmann/json) are listed in the `licenses` folder of its repository; those files are included too.

### LLVM OpenMP runtime (`libomp.dll`): Apache License 2.0 with LLVM Exceptions
Shipped with the llama.cpp Windows build. https://github.com/llvm/llvm-project

### Poppler `pdftotext`: GNU General Public License v2 or later
Used to read text from PDFs. It runs as a separate program; it isn't built into Offline Assistant, so the GPL applies to Poppler and not to the app's own code.
- Project: https://poppler.freedesktop.org
- Windows build: https://github.com/oschwartz10612/poppler-windows (built from conda-forge packages)
- **Source code:** each GitHub release of Offline Assistant has the exact Poppler source archive it was built with attached. The Windows build's other libraries come from conda-forge, whose recipes and sources are listed at https://github.com/conda-forge. Their licence files are included in the installer.

## Downloaded on first run (not included in the installer)

### Gemma (Google): Gemma Terms of Use
The AI model (Gemma 3 4B or Gemma 3 1B, quantised by bartowski on Hugging Face).

> Gemma is provided under and subject to the Gemma Terms of Use found at ai.google.dev/gemma/terms

Use is also subject to the Gemma Prohibited Use Policy at ai.google.dev/gemma/prohibited_use_policy. If you pass a copy of the model to someone else, for example on a USB stick for an offline install, include the `NOTICE.txt` from the app's `models` folder with it.
