# Demo

`demo.mp4` is 75 seconds, 1920×1080. The customer chats on the left, and the clinic's panel shows the result live on
the right. It was recorded on 23 Oct 2026 against Claude Haiku 5.5, as is: the assistant's words are the model's.

| File | Shows |
|---|---|
| `demo.mp4` | The whole flow: a request in plain Spanish, the times on offer, a held slot that appears in the panel at once, the customer's Confirmar, the steps behind the booking, sign-in with the emailed code, and a cancellation that frees the slot live |
| `poster.jpg` | The video's poster: the panel at the end |
| `01-chat.png` | The assistant offering free times for "el lunes en la mañana" |
| `02-tarjeta.png` | The held slot as a card, with its countdown and the Confirmar button |
| `03-panel-apartada.png` | The panel with the held booking, marked IA, before it is confirmed |
| `04-traza.png` | The "why" of the booking: the conversation, every model and tool call, and the cost (USD 0.0024) |
| `05-mis-citas.png` | The customer signed in with the emailed code, before cancelling |

## Recording it again

```bash
cd web
pnpm e2e:deps                     # Postgres (5434) and Mailpit (8026) for the run
DEMO_SCRIPTED=1 pnpm demo         # rehearsal with a script: free
pnpm demo                         # the real take: ANTHROPIC_API_KEY from the environment or .env, ~USD 0.0024
```

The script (`web/e2e/demo/demo.spec.ts`) drives two browser pages and records each one with Chromium's screencast.
ffmpeg, in a pinned container image with no network, lays them side by side into the MP4, so nothing is installed
on the host. Each run uses its own database, so every slot is free. If the assistant asks "¿te lo aparto?" instead
of holding the slot at once, the scripted customer answers yes.
