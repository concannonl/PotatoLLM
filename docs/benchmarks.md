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

## Long chats: reply speed vs how full the memory is (X250)

The tests used `benchmark/context-benchmark.ps1` with build 12521. They measure reply speed with empty memory and with 8,192 tokens in memory, using 16-bit and 8-bit (q8_0, flash attention on) KV cache. The long-chat runs are single repetitions, so allow about ±10% noise.

| Model | Memory | Prompt (tok/s) | Reply, empty | Reply, 8k in memory | Slowdown |
|---|---|---|---|---|---|
| Llama-3.2-1B | 16-bit | 65.4 | 9.71 | 2.44 | 75% |
| Llama-3.2-1B | 8-bit | 54.7 | 13.56 | 2.28 | 83% |
| LFM2.5-1.2B | 16-bit | 64.3 | 14.41 | 3.94 | 73% |
| LFM2.5-1.2B | 8-bit | 47.3 | 10.88 | 4.91 | 55% |
| LFM2.5-2.6B | 16-bit | 26.1 | 6.17 | 3.26 | 47% |
| LFM2.5-2.6B | 8-bit | 25.2 | 6.52 | 3.00 | 54% |
| Llama-3.2-3B | 16-bit | 22.0 | 5.40 | 1.24 | 77% |
| Llama-3.2-3B | 8-bit | 19.1 | 5.44 | 1.27 | 77% |
| Gemma-3-4B | 16-bit | 19.7 | 4.24 | 3.07 | 28% |
| Gemma-3-4B | 8-bit | 18.2 | 4.32 | 3.44 | 20% |

### Findings

- **On long chats, attention compute dominates, not memory bandwidth.** Llama 3B drops from 5.4 to 1.24 tok/s at 8k, far more than the extra KV reads explain. Time per token grows roughly linearly with chat length, which puts Llama 3B at about 2 tok/s when the current 4k window is full.
- **Architecture matters more than size.** Gemma 3, with sliding-window attention, loses only 20–28% at 8k. LFM2.5, a hybrid with convolution and attention layers, loses about 50%. Llama, with full attention on every layer, loses about 77%.
- **8-bit KV gives no consistent speed gain on this CPU.** Prompt reading is 5–25% slower with it. Its value here is RAM only.
- **LFM2.5-1.2B beats Llama-3.2-1B** in every test except prompt reading, where it is equal.

## Answer quality: 8 GB candidates (X250)

These are the results of `benchmark/quality-test.ps1`: 10 everyday questions with the app's system prompt and temperature 0.7, a 512-token reply cap and seed 42.

| | Llama-3.2-3B | LFM2.5-2.6B | Gemma-3-4B |
|---|---|---|---|
| Average time per answer | 19.7 s | 53.1 s | 21.9 s |
| Electricity bill (£67.35) | ✗ £67.53 (210 × 24.5 miscalculated) | ✓ | ✓ |
| Bat and ball (5p) | ✓ | ✓ | ✓ |
| Canberra / ~27m | ✓ (25–26m) | ✓ | ✓ |
| 2030 World Cup | ✓ honest | ✗ honest, but invents "held in Italy" | ✓ honest |
| Other problems | Pension explanation muddled; sky answer too technical for a child | Empty answer to the recipe question; uses markdown; sky answer blames "dust and water droplets" | Recipe uses markdown bullets; "bi-weekly" is ambiguous |

- **LFM2.5-2.6B thinks before answering.** Its timings imply roughly 150–500 hidden reasoning tokens per answer. On the recipe question it hit the 512-token cap while still thinking and returned nothing. Its high raw speed doesn't carry through to the user.
- **Gemma-3-4B gave correct, concise and well-pitched answers** at almost the same time per answer as Llama, and it holds up far better in long chats.

## Answer quality: 4 GB candidates (run on the X250)

| | Llama-3.2-1B | LFM2.5-1.2B | Gemma-3-1B |
|---|---|---|---|
| Reply speed | 13.4 tok/s | 15.2 tok/s | 12.4 tok/s |
| Peak engine memory | ~1.5 GB | ~1.3 GB | **~1.0 GB** |
| Hidden thinking | none | none | none |
| Electricity bill (£67.35) | ✗ 6760p | ✗ £6,735 | ✗ 5304p |
| Bat and ball (5p) | ✓ | ✓ ("5 cents") | ✓ |
| Canberra / ~27m | ✓ | ✓, but says Australia is "one of the most populous countries" | ✓ |
| 2030 World Cup | ✓ honest | ✓ honest | ✗ claims it was held in 2022 in Qatar |
| Summary | ✗ says food waste also goes fortnightly | leaves out weekly food waste | leaves out weekly food waste |
| Other | Rambling, wrong sky explanation; recipe isn't from the cupboard | Friendly text drops "another time" | Best explanations; uses markdown |

- **No 1B model can be trusted with arithmetic.** All three got the bill wrong, and two of the wrong answers look believable.
- Each model has one serious failure. Gemma-3-1B has the lowest memory use, which matters a great deal on 4 GB with Windows, and gives the clearest explanations. Its sliding-window attention should also keep long chats fast.

## Re-run with today's date in the system prompt

The system prompt now adds "Today is …" and "if you don't know something, say so instead of guessing".

**4 GB candidates**
- **Gemma-3-1B** still made up a World Cup answer, this time that the 2030 tournament "was cancelled due to climate change impacts". It also garbled the summary. It invented an answer to an unanswerable question in both runs.
- **LFM2.5-1.2B** answered the World Cup question honestly in both runs and got the summary fully right this time. The bill was close but wrong (210 × 24.5 = 5137.5p). It used no hidden thinking, peaked at about 1.3 GB and was the fastest.
- **Llama-3.2-1B** was honest about the World Cup but wrongly claimed the hosts haven't been confirmed. It got the bill wrong again.

**Decision:** keep Gemma-3-1B as the 4 GB default. It has the lowest memory use and the clearest explanations; the risk of it making things up is covered by the welcome note.

**8 GB candidates**
- **Gemma-3-4B** still got the bill, the bat and ball, and the World Cup right. However, the new line made it over-hedge: it added "I don't have the most up-to-date information on this" to the sky explanation. It also gave a stale population figure ("approximately 23 million").
- **LFM2.5-2.6B** still thinks before every answer and was cut off mid-recipe. **Llama-3.2-3B** got the bill wrong again, this time £51.98.
- **Memory:** the peak working set of the engine rose steadily during the run, from 4.2 GB to 4.6 GB for Gemma-3-4B and from 3.8 GB to 3.9 GB for Llama-3.2-3B. This needs checking in the real app, since 8 GB machines run Windows too.

## Lenovo X240, 4 GB

Not tested yet.
