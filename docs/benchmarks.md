# Benchmarks

## Lenovo X250: i5-5300U (2 cores / 4 threads), 8 GB DDR3L-1600 single channel, Windows 11 Home

The tests used llama.cpp build 12521 (`6f311d7ad`) with the CPU backend (`ggml-cpu-haswell.dll`), run with `llama-bench -p 128 -n 64 -r 3`, on mains power.

| Model | Size | Threads | Prompt (tok/s) | Reply (tok/s) |
|---|---|---|---|---|
| Llama-3.2-1B Q4_0 | 0.77 GB | 2 | 36.5 ±9.9 | 12.7 |
| Llama-3.2-1B Q4_0 | 0.77 GB | 4 | 52.3 | 14.2 |
| Llama-3.2-1B Q4_K_M | 0.80 GB | 2 | 68.1 | 13.9 |
| Llama-3.2-1B Q4_K_M | 0.80 GB | 4 | 66.4 | 13.5 |
| Qwen2.5-1.5B Q4_K_M | 1.11 GB | 2 | 45.4 | 11.1 |
| Qwen2.5-1.5B Q4_K_M | 1.11 GB | 4 | 47.1 | 10.7 |
| Llama-3.2-3B Q4_K_M | 2.01 GB | 2 | 21.7 | 5.4 |
| Llama-3.2-3B Q4_K_M | 2.01 GB | 4 | 22.1 | 5.2 |

### Findings

- **Memory bandwidth is the limit.** Model size × reply speed comes to about 11 GB/s for every model, which is around 85% of the 12.8 GB/s theoretical maximum of a single DDR3L-1600 channel.
- **Threads:** the reply speed is the same with 2 or 4 threads. Prompt reading is equal or slightly better with 4. The app therefore uses one thread per physical core for replies (`-t`) and all logical threads for prompts (`-tb`).
- **Q4_K_M beats Q4_0.** It gives the same reply speed, faster prompt reading and better quality.
- **Prompt reading is the UX bottleneck.** On the 3B model, about 22 tok/s means a 1,000-token paste waits roughly 45 seconds before the reply starts.

## Lenovo X240, 4 GB

Not tested yet.
