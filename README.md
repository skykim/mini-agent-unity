# mini-agent-unity

An **on-device smart-home agent** built in Unity. A tiger lives in an isometric 3D home (kitchen, living room, bedroom) and you drive the house with **natural-language commands** in Korean or English. Every command is parsed **fully on-device** by a fine-tuned **FunctionGemma-270M** model running through **Unity Sentis**. When a request isn't a home action, the agent can fall back to a cloud LLM for a conversational reply.

[![mini-agent-unity demo](https://img.youtube.com/vi/mLzsQLpqRhk/0.jpg)](https://www.youtube.com/watch?v=mLzsQLpqRhk)

## ✨ Highlights
* **On-device function calling:** a 270M model turns a sentence into a validated tool call locally.
* **Bilingual & multi-intent:** Korean and English, including compound commands like `거실 불 켜고 거실 티비 켜줘`.
* **Embodied:** the tiger walks to the target device, *then* acts; replies appear in a world-space speech bubble over its head.
* **Hybrid:** a schema gate validates every call; anything that isn't a valid home action defers to an optional cloud LLM.

## 🗣 What you can say
16 tools span home control and information retrieval:

| Category | Tools | Example |
|---|---|---|
| Lights | `turn_on_light` (room, brightness 0–100), `turn_off_light`, `set_light_color` (red/orange/yellow/green/blue/purple/pink/white/warm/cool) | `turn on the bedroom light at 30%`, `set the kitchen light to blue` |
| TV / Computer | `turn_on_tv` / `turn_off_tv`, `turn_on_computer` / `turn_off_computer` | `turn off the living room TV` |
| Music | `play_music` (room, volume, genre: rock/jazz/lofi), `stop_music`, `set_volume` (0–100), `get_volume` | `play some jazz`, `set the volume to 60` |
| Cleaning | `start_vacuum` (room) | `start the vacuum` |
| Info | `get_weather` (city), `get_time` (city), `get_location`, `web_search` (query) | `what's the weather in Seoul?`, `what time is it?` |

Rooms: **kitchen**, **living room**, **bedroom** (room-less commands resolve to a sensible default). Room and device names accept synonyms in both languages.

## 🧩 How it works

```
  user text  ("Play jazz music")
     │
     ▼
  [ FunctionGemmaGenerator ]      on-device · Unity Sentis (GPUCompute) · offline
    Gemma3-270M, greedy decode    KV cache + prefix cache + GPU argmax head
     │
     │  raw text:  call:play_music{genre:<esc>jazz<esc>}
     ▼
  [ FgCallParser ]  ──►  [ ToolCallValidation ]      gate: name? types? ranges? enums?
     │                          │
     │ valid call(s)            │ no valid call
     ▼                          ▼
  [ EXECUTE ]              [ Cloud LLM ]  (optional, OpenAI-compatible endpoint)
     │                          └──►  conversational reply     e.g. "How are you feeling today?"
     │
     ├─ local device tools ───────────►  HomeDeviceController        (instant, offline)
     │    turn_on_light / turn_off_light / set_light_color
     │    turn_on_tv / turn_off_tv / turn_on_computer / turn_off_computer
     │    play_music / stop_music / set_volume / get_volume
     │    start_vacuum
     │
     └─ info tools (network) ──────────►  AgentInfoApis
          get_time / get_location / get_weather
          web_search ─► fetch results ─► Cloud LLM (grounded synthesis) ─► answer
```

Two paths reach the cloud LLM: **(1)** when no valid tool call is produced, the whole turn falls back to it for a conversational reply; **(2)** `web_search` fetches results on-device and then asks the cloud LLM to synthesize a grounded answer. Everything else (all device control) stays fully on-device and offline.

**1. On-device inference: `FunctionGemmaGenerator.cs`**
The model is FunctionGemma-270M-it (Gemma3 architecture: 18 layers, multi-query attention with a single KV head, head-dim 256, 262 144-token vocab, `gelu_pytorch_tanh`, 512-token sliding-attention window). It runs in Unity Sentis (`com.unity.ai.inference`) on the `GPUCompute` backend (measured ~2× faster than CPU Burst, token-identical). Because Sentis has no built-in KV cache, the cache is managed manually as plain graph I/O:

* **Manual KV cache:** past keys/values are fed back each step and kept **device-resident** (`CopyOutput`, GPU→GPU, no per-token CPU sync), double-buffered so a worker never reads and writes the same tensor.
* **Prefix-KV cache:** the fixed developer block (system instruction + all tool declarations) is prefilled **once** at warmup and its KV reused for every command, so each turn only tokenizes and feeds the user turn.
* **GPU ArgMax head:** a tiny `Functional` graph picks the next token on-GPU, so the 262k-wide logits never get read back to the CPU; greedy decoding, stopping on `<eos>` / `<end_of_turn>` / `<start_function_response>`.
* **Explicit masks:** both a full-causal and a sliding-window additive mask are supplied every step (the sliding layers require it once the prompt exceeds the 512-token window).
* **Non-blocking:** generation yields **one token per frame**, so movement/animation keep running while the model thinks.

The prompt is assembled to be byte-identical to the fine-tuning chat template; the model emits a flat call format `call:NAME{key:<escape>value<escape>}`.

**2. Parse & validate: `FgCallParser` + `ToolCallValidation.cs`**
`FgCallParser.ParseAll` extracts one or more calls from the raw text (handling the escape spans and multiple calls). Each parsed call passes through a schema gate that checks the tool exists and every argument is well-typed and in range (enums like light colors, numeric bounds like brightness/volume). Invalid calls are rejected and logged, never executed.

**3. Execute: `HomeDeviceController.cs` + `TigerController.cs`**
Valid calls mutate device state (lights, TV, computer, speaker + volume/genre, vacuum). The tiger (a `NavMeshAgent`) navigates to the relevant device before the state changes, on a NavMesh baked at startup by `RuntimeNavMeshBake`. Responses render in a world-space bubble (`TigerSpeechBubble`). Info tools (`get_weather`, `get_time`, `get_location`, `web_search`) call live web APIs in `AgentInfoApis.cs`; `web_search` additionally grounds a synthesized answer through the cloud LLM.

**4. Cloud fallback: `AgentCloudClient.cs` (optional)**
When no valid tool call comes out (e.g. `오늘 기분 어때?`), the turn defers to a cloud LLM over an **OpenAI-compatible** chat-completions endpoint, with a persona system prompt. Turn the toggle off to run purely on-device; home control still works.

The turn orchestration, status UI, and per-turn timing/harvest logging live in `HomeAgentConnector.cs`.

## 🧠 On-Device Model (required)
The model weights (~436 MB) are **not bundled** in this repo (they exceed GitHub's file-size limit), so download them once:

1. Download **`functiongemma_home_uint8.sentis`** from
   👉 https://huggingface.co/Sky-Kim/functiongemma-270m-finetune/tree/main/sentis
2. Place it in this exact path:
   ```
   Assets/StreamingAssets/Agent/functiongemma_home_uint8.sentis
   ```

The tokenizer (`tokenizer.json`) and developer block (`fg_dev_block_home.txt`) are already in the repo, and the scene's `FunctionGemmaGenerator` is preconfigured for this path; no further setup.

> Without the model file the scene still opens and the tiger is controllable with `WASD`; only on-device inference is disabled (a "model not found" message is logged) until the file is present.

The training, quantization, and export pipeline that produced this model is in the companion repo **[Sky-Kim/functiongemma-270m-finetune](https://huggingface.co/Sky-Kim/functiongemma-270m-finetune)**.

## ☁️ Cloud Fallback (optional)
Configured on the `HomeAgentConnector` component:

* **Endpoint:** `http://localhost:11434/v1/chat/completions` (default: a local [Ollama](https://ollama.com) server)
* **Model:** `gpt-oss:120b-cloud`
* **Toggle:** `Use Cloud Fallback` (on by default)

Any OpenAI-compatible endpoint works; point it wherever you like, or disable it entirely.

## 🗂 Project structure
```
Assets/
├─ Agent/
│  ├─ FunctionGemmaGenerator.cs   # Sentis inference: KV cache, prefix cache, GPU argmax, masks
│  ├─ HomeAgentConnector.cs       # turn orchestration, UI, timing/harvest logging, cloud fallback
│  ├─ ToolCallValidation.cs       # tool schemas + validating gate
│  ├─ AgentCloudClient.cs         # OpenAI-compatible chat-completions client
│  ├─ AgentInfoApis.cs            # weather / time / location / web_search backends
│  ├─ RuntimeNavMeshBake.cs       # bakes the NavMesh at startup
│  ├─ TigerSpeechBubble.cs        # world-space response bubble
│  └─ MiniAgentTestRunner.cs      # optional scripted auto-run of a command list
├─ Scripts/
│  ├─ HomeDeviceController.cs     # device state (lights, TV, speaker, vacuum)
│  ├─ TigerController.cs          # WASD + NavMesh movement
│  └─ RoomTransparencyManager.cs  # fades away walls of the room in view
├─ Resources/Music/              # jazz.wav / lofi.wav / rock.wav (synthesized loops)
└─ StreamingAssets/Agent/        # tokenizer.json, fg_dev_block_home.txt, (+ the downloaded .sentis)
```

## ▶️ Running
1. Open the project in **Unity 6000.5.5f1** (URP).
2. Put the model file in place (see above).
3. Open `Assets/Scenes/MiniAgentScene.unity` and press **Play**.
4. Type a command in the on-screen input box and send it, or move the tiger with `W` `A` `S` `D`.

A `MiniAgentTestRunner` component can auto-run a scripted list of commands a few seconds after the scene starts (all configurable in the Inspector).

## 🛠 Environment
* **Unity:** 6000.5.5f1
* **Render Pipeline:** URP
* **Inference:** Unity Sentis (`com.unity.ai.inference`), `GPUCompute` backend
* **Navigation:** Unity AI Navigation (`com.unity.ai.navigation`)

## 📦 Assets & Credits
Environment and character from Kenney.nl:
* [Furniture Kit](https://kenney.nl/assets/furniture-kit): environment and furniture
* [Cube Pets](https://kenney.nl/assets/cube-pets): tiger character
