using System.Collections;
using System.Collections.Generic;
using System.Text;
using Unity.InferenceEngine;
using Unity.InferenceEngine.Tokenization;
using Unity.InferenceEngine.Tokenization.Parsers.HuggingFace;
using UnityEngine;

namespace SentisSkills.Inference
{
    /// <summary>
    /// On-device function-calling SLM = FunctionGemma-270m-it (Gemma3ForCausalLM) in Unity Sentis 2.6.
    /// Same architecture as gemma-3-270m: 18 layers, MQA (num_kv_heads=1), head_dim 256, vocab 262144,
    /// activation gelu_pytorch_tanh. Function-calling specialized (not a dialogue model) — natural fit
    /// for the Ally's command -> action mapping.
    ///
    /// Import: self-exported with attn_implementation="eager" (dynamo, opset20). On Sentis 2.6.1 NO
    /// Gelu-tanh graph surgery is needed — the importer routes ONNX Gelu(approximate='tanh') to its
    /// GeluFast (tanh) kernel (ONNXModelConverter.cs:326). Verified against ORT by FunctionGemmaUnitTest.
    /// Manual KV cache (Sentis has none): prefill zero-length past, feed 1 token + grown KV each step.
    ///
    /// Base Gemma turn format:
    ///   &lt;bos&gt;&lt;start_of_turn&gt;user\n{user}&lt;end_of_turn&gt;\n&lt;start_of_turn&gt;model\n
    /// </summary>
    public sealed class FunctionGemmaGenerator : MonoBehaviour
    {
        [Header("Model")]
        [Tooltip("Float16-quantized .sentis under StreamingAssets (relative path).")]
        [SerializeField] string streamingModelPath = "Agent/functiongemma_home_fp16.sentis";
        [Tooltip("tokenizer.json under StreamingAssets (relative path).")]
        [SerializeField] string streamingTokenizerPath = "Agent/tokenizer.json";
        [Tooltip("fg_dev_block_home.txt under StreamingAssets (relative path).")]
        [SerializeField] string streamingDevBlockPath = "Agent/fg_dev_block_home.txt";
        // Measured 2026-09 (Apple M5 Max, fp16, batchmode, 6-command set): GPUCompute ≈ 1.5-1.8s/cmd
        // vs CPU Burst ≈ 3.3-3.6s/cmd with token-identical outputs — GPUCompute is ~2x faster.
        [SerializeField] BackendType backend = BackendType.GPUCompute;

        string m_DevBlock;   // dev-block text loaded from StreamingAssets (or the fallback TextAsset)

        [Header("Decode")]
        [SerializeField] int maxNewTokens = 32;
        [Tooltip("KV prefix cache (P1): prefill the fixed devBlock ONCE in Warmup and reuse its KV every " +
                 "command, feeding only the user-turn suffix. Big TTFT win. Off = full-prompt path (parity/fallback).")]
        [SerializeField] bool usePrefixCache = true;
        [Tooltip("GPU-side ArgMax head: pick the next token on-GPU (a tiny Functional graph) instead of reading " +
                 "the whole 262k-vocab logits tensor to the CPU every step. Off = CPU argmax (parity fallback).")]
        [SerializeField] bool useGpuArgmax = true;

        // Gemma3-270m architecture (from config.json)
        const int NumLayers = 18;
        const int NumKvHeads = 1;      // MQA
        const int HeadDim = 256;
        const int Vocab = 262144;      // gemma-3-270m vocab (huge → keep argmax + logits off the CPU)
        // special tokens
        const int BOS = 2;             // <bos>
        const int EOS = 1;             // <eos>
        const int END_OF_TURN = 106;   // <end_of_turn>
        const int START_FN_RESPONSE = 50; // <start_function_response> — model emits it right after a call = stop

        Worker m_Worker;
        Worker m_Argmax;               // tiny GPU-side ArgMax head over the logits (built when useGpuArgmax)
        ITokenizer m_Tok;
        bool m_Ready;

        // KV prefix cache (P1): the fixed devBlock's KV, prefilled once and reused per command.
        Tensor<float>[] m_PrefixKey, m_PrefixVal;
        int m_PrefixLen;
        bool m_PrefixReady;

        public bool IsReady => m_Ready;

        // Lazy: load only on first use so an unused component costs ZERO at Play start.
        public bool EnsureReady()
        {
            if (m_Ready) return true;

            // model / tokenizer / dev block all load from StreamingAssets
            var saPath = System.IO.Path.Combine(Application.streamingAssetsPath, streamingModelPath ?? "");
            if (string.IsNullOrEmpty(streamingModelPath) || !System.IO.File.Exists(saPath))
            {
                Debug.LogError($"{nameof(FunctionGemmaGenerator)}: model not found at StreamingAssets '{saPath}'.");
                return false;
            }
            m_Worker = new Worker(ModelLoader.Load(saPath), backend);

            string tokJson = ReadStreaming(streamingTokenizerPath);
            if (tokJson != null) m_Tok = HuggingFaceParser.GetDefault().Parse(tokJson);

            m_DevBlock = ReadStreaming(streamingDevBlockPath);
            if (useGpuArgmax)
                m_Argmax = BuildArgmaxHead(backend);
            m_Ready = true;
            return true;
        }

        // Read a StreamingAssets text file by relative path (desktop/editor direct read; null if absent).
        static string ReadStreaming(string relative)
        {
            if (string.IsNullOrEmpty(relative)) return null;
            var p = System.IO.Path.Combine(Application.streamingAssetsPath, relative);
            return System.IO.File.Exists(p) ? System.IO.File.ReadAllText(p) : null;
        }

        // A tiny standalone graph: logits (1, seq, Vocab) -> ArgMax over the vocab dim -> ids (1, seq).
        // Running this on the SAME backend keeps the 262k-wide logits on the GPU; we then read back only
        // 'seq' ints instead of cloning the whole (1, seq, 262144) logits tensor to the CPU every token.
        static Worker BuildArgmaxHead(BackendType backend)
        {
            var g = new FunctionalGraph();
            var logits = g.AddInput<float>(new DynamicTensorShape(1, -1, Vocab));
            var ids = Functional.ArgMax(logits, 2, false);   // (1, seq), int
            return new Worker(g.Compile(ids), backend);
        }

        // Next-token pick. GPU path: run the ArgMax head on the logits view, read back just the ids row
        // and take the last. CPU fallback: the old full-logits readback + scan (parity/off-switch).
        int PickNext(Tensor<float> logits)
        {
            if (m_Argmax == null) return ArgmaxLast(logits);
            m_Argmax.Schedule(logits);
            using var ids = (m_Argmax.PeekOutput() as Tensor<int>).ReadbackAndClone();
            int seq = ids.shape[1];
            return ids[0, seq - 1];
        }

        /// <summary>Compile the graph + allocate buffers up front so the first real call isn't cold.</summary>
        public void Warmup()
        {
            if (!EnsureReady()) return;
            // Prefill the devBlock prefix once (compiles the graph AND caches its KV). Fall back to a bare
            // 1-token pass (compile only) if the prefix can't be built (no tokenizer/devBlock, or toggled off).
            if (!usePrefixCache || !TryPrefillPrefix())
                GenerateFromIds(new[] { BOS }, 1);
        }

        static string BuildPrompt(string user)
        {
            var sb = new StringBuilder();
            sb.Append("<start_of_turn>user\n").Append(user).Append("<end_of_turn>\n");
            sb.Append("<start_of_turn>model\n");
            return sb.ToString();   // tokenizer adds <bos> when addSpecialTokens=true
        }

        /// <summary>
        /// Function-calling prompt = the exact developer block (system instruction + the 5 Ally
        /// function declarations, from fg_dev_block.txt, byte-identical to the training render) +
        /// the user turn + the model generation prompt. Must match chat_template.jinja exactly or the
        /// fine-tuned model degrades.
        /// </summary>
        string BuildFcPrompt(string user)
        {
            var sb = new StringBuilder();
            sb.Append(m_DevBlock);               // <start_of_turn>developer\n...<end_of_turn>\n
            sb.Append("<start_of_turn>user\n").Append(user).Append("<end_of_turn>\n");
            sb.Append("<start_of_turn>model\n");
            return sb.ToString();                    // tokenizer's TemplateProcessing prepends <bos>=2
        }

        /// <summary>
        /// Non-blocking (P2) command → generation: decodes ONE token per frame (yields between steps) so
        /// vision/motion/enemy-patrol keep ticking while the SLM thinks — the Ally never freezes mid-command.
        /// Delivers the RAW generated text (special tokens kept so call:/&lt;escape&gt; survive) via onDone;
        /// parsing/validation is the caller's job (FgCallParser handles multi-call outputs and escape spans).
        /// Prefix KV is reused (Warmup prefills it, so no per-command prefill hitch). Drive with StartCoroutine.
        /// </summary>
        public IEnumerator GenerateCallRoutine(string user, System.Action<string> onDone, int maxNew = 0)
        {
            if (!EnsureReady() || m_Tok == null || string.IsNullOrEmpty(m_DevBlock)) { onDone?.Invoke(""); yield break; }

            int budget = maxNew > 0 ? maxNew : maxNewTokens;
            var generated = new List<int>();
            if (usePrefixCache && TryPrefillPrefix())
            {
                // Prefix-cache path: only the user turn is tokenized/fed; the devBlock KV is reused.
                // Boundary verified 5/5: Encode(dev) + Encode(userTurn, BOS stripped) == Encode(dev+userTurn).
                var suf = new List<int>(m_Tok.Encode(BuildPrompt(user)).GetIds());
                if (suf.Count > 0 && suf[0] == BOS) suf.RemoveAt(0);   // prefix already carries <bos>
                yield return GenerateFromIdsRoutine(suf.ToArray(), budget, m_PrefixKey, m_PrefixVal, m_PrefixLen, generated);
            }
            else
            {
                var ids = new List<int>(m_Tok.Encode(BuildFcPrompt(user)).GetIds());
                if (ids.Count == 0 || ids[0] != BOS) ids.Insert(0, BOS);   // ensure <bos> regardless of tokenizer post-proc
                yield return GenerateFromIdsRoutine(ids.ToArray(), budget, null, null, 0, generated);
            }
            onDone?.Invoke(m_Tok.Decode(generated));
        }

        /// <summary>Greedy generation from text (requires tokenizerJson). Returns decoded model text.</summary>
        public string Generate(string user, int maxNew = 0)
        {
            if (!EnsureReady()) return "";
            if (m_Tok == null) { Debug.LogError($"{nameof(FunctionGemmaGenerator)}: tokenizerJson not assigned."); return ""; }
            var ids = new List<int>(m_Tok.Encode(BuildPrompt(user)).GetIds());
            var gen = GenerateFromIds(ids.ToArray(), maxNew > 0 ? maxNew : maxNewTokens);
            return m_Tok.Decode(gen).Trim();
        }

        /// <summary>
        /// Greedy decode from pre-tokenized ids. Tokenizer-independent — used by the unit test to
        /// compare Sentis greedy ids against an ORT reference (model-graph parity, incl. GeluFast).
        /// Returns the generated token ids (stops on EOS/end_of_turn, NOT included).
        /// </summary>
        public List<int> GenerateFromIds(int[] promptIds, int maxNew)
            => GenerateFromIds(promptIds, maxNew, null, null, 0);

        /// <summary>
        /// Greedy decode. When pk/pv are supplied (KV prefix cache), generation starts from that cached KV
        /// (prefixLen keys already attended) and only 'promptIds' (the user-turn suffix) is fed on the first
        /// pass — the fixed devBlock prefill is reused instead of recomputed each command. The caller's pk/pv
        /// are never mutated or disposed, so the cache survives across calls. Purely causal masking makes
        /// this exact.
        ///
        /// KV rides the backend the whole way: each step, CopyOutput copies present.*→a fresh buffer (GPU→GPU
        /// on GPU backends — no per-step ReadbackAndClone, which would be 36 GPU→CPU syncs/token for 18 layers)
        /// and feeds it as the next step's past. Double-buffered (present→new while past fed from current)
        /// because a worker can't write a KV output into the same tensor it's reading as that KV's input.
        /// (Token-exact parity with the old CPU-clone path was verified before that path was removed.)
        /// </summary>
        public List<int> GenerateFromIds(int[] promptIds, int maxNew, Tensor<float>[] pk, Tensor<float>[] pv, int prefixLen)
        {
            var generated = new List<int>();
            if (!EnsureReady()) return generated;

            // "current past" fed each step. First step = cached prefix KV (CPU, uploaded once) or empty.
            // We only OWN (and dispose) buffers we allocated — never the caller's prefix pk/pv.
            var curK = new Tensor[NumLayers];
            var curV = new Tensor[NumLayers];
            bool ownCur;
            if (pk != null) { for (int l = 0; l < NumLayers; l++) { curK[l] = pk[l]; curV[l] = pv[l]; } ownCur = false; }
            else
            {
                for (int l = 0; l < NumLayers; l++)
                {
                    curK[l] = new Tensor<float>(new TensorShape(1, NumKvHeads, 0, HeadDim));
                    curV[l] = new Tensor<float>(new TensorShape(1, NumKvHeads, 0, HeadDim));
                }
                ownCur = true;
            }

            int total = prefixLen + promptIds.Length;
            var step = promptIds;
            int pastLen = prefixLen;

            for (int t = 0; t < maxNew; t++)
            {
                int qLen = step.Length;
                using var idT = new Tensor<int>(new TensorShape(1, qLen), step);
                using var mask = new Tensor<float>(new TensorShape(1, 1, qLen, total), CausalMask(qLen, total, pastLen));
                using var maskS = new Tensor<float>(new TensorShape(1, 1, qLen, total), SlidingMask(qLen, total, pastLen));
                m_Worker.SetInput("input_ids", idT);
                m_Worker.SetInput("mask_full", mask);
                m_Worker.SetInput("mask_sliding", maskS);
                for (int l = 0; l < NumLayers; l++)
                {
                    m_Worker.SetInput($"past_key_values.{l}.key", curK[l]);
                    m_Worker.SetInput($"past_key_values.{l}.value", curV[l]);
                }
                m_Worker.Schedule();

                int next = PickNext(m_Worker.PeekOutput("logits") as Tensor<float>);

                // present KV → fresh GPU tensors (CopyOutput allocates exact-shape CloneEmpty on a null ref);
                // GPU→GPU MemCopy, NO ReadbackAndClone / CPU sync. These become the next step's past.
                var newK = new Tensor[NumLayers];
                var newV = new Tensor[NumLayers];
                for (int l = 0; l < NumLayers; l++)
                {
                    Tensor dk = null, dv = null;
                    m_Worker.CopyOutput($"present.{l}.key", ref dk);
                    m_Worker.CopyOutput($"present.{l}.value", ref dv);
                    newK[l] = dk; newV[l] = dv;
                }
                if (ownCur) for (int l = 0; l < NumLayers; l++) { curK[l].Dispose(); curV[l].Dispose(); }
                curK = newK; curV = newV; ownCur = true;

                if (next == EOS || next == END_OF_TURN || next == START_FN_RESPONSE) break;
                generated.Add(next);
                step = new[] { next };
                pastLen = total;
                total += 1;
            }

            if (ownCur) for (int l = 0; l < NumLayers; l++) { curK[l].Dispose(); curV[l].Dispose(); }
            return generated;
        }

        /// <summary>
        /// Coroutine mirror of GenerateFromIds: one decode step per frame (yields between steps),
        /// writing tokens into 'generated'. Same math as the sync path (prefix KV injection included).
        /// </summary>
        public IEnumerator GenerateFromIdsRoutine(int[] promptIds, int maxNew, Tensor<float>[] pk, Tensor<float>[] pv, int prefixLen, List<int> generated)
        {
            if (!EnsureReady()) yield break;

            // on-backend KV (mirrors GenerateFromIds): CopyOutput present→fresh tensor each step,
            // no per-step ReadbackAndClone. We own every buffer except the caller's prefix pk/pv.
            var curK = new Tensor[NumLayers];
            var curV = new Tensor[NumLayers];
            bool ownCur;
            if (pk != null) { for (int l = 0; l < NumLayers; l++) { curK[l] = pk[l]; curV[l] = pv[l]; } ownCur = false; }
            else
            {
                for (int l = 0; l < NumLayers; l++)
                {
                    curK[l] = new Tensor<float>(new TensorShape(1, NumKvHeads, 0, HeadDim));
                    curV[l] = new Tensor<float>(new TensorShape(1, NumKvHeads, 0, HeadDim));
                }
                ownCur = true;
            }

            int total = prefixLen + promptIds.Length;
            var step = promptIds;
            int pastLen = prefixLen;
            bool stop = false;

            for (int t = 0; t < maxNew && !stop; t++)
            {
                int qLen = step.Length;
                using (var idT = new Tensor<int>(new TensorShape(1, qLen), step))
                using (var mask = new Tensor<float>(new TensorShape(1, 1, qLen, total), CausalMask(qLen, total, pastLen)))
                using (var maskS = new Tensor<float>(new TensorShape(1, 1, qLen, total), SlidingMask(qLen, total, pastLen)))
                {
                    m_Worker.SetInput("input_ids", idT);
                    m_Worker.SetInput("mask_full", mask);
                    m_Worker.SetInput("mask_sliding", maskS);
                    for (int l = 0; l < NumLayers; l++)
                    {
                        m_Worker.SetInput($"past_key_values.{l}.key", curK[l]);
                        m_Worker.SetInput($"past_key_values.{l}.value", curV[l]);
                    }
                    m_Worker.Schedule();

                    int next = PickNext(m_Worker.PeekOutput("logits") as Tensor<float>);

                    var newK = new Tensor[NumLayers];
                    var newV = new Tensor[NumLayers];
                    for (int l = 0; l < NumLayers; l++)
                    {
                        Tensor dk = null, dv = null;
                        m_Worker.CopyOutput($"present.{l}.key", ref dk);
                        m_Worker.CopyOutput($"present.{l}.value", ref dv);
                        newK[l] = dk; newV[l] = dv;
                    }
                    if (ownCur) for (int l = 0; l < NumLayers; l++) { curK[l].Dispose(); curV[l].Dispose(); }
                    curK = newK; curV = newV; ownCur = true;

                    if (next == EOS || next == END_OF_TURN || next == START_FN_RESPONSE) stop = true;
                    else { generated.Add(next); step = new[] { next }; pastLen = total; total += 1; }
                }
                yield return null;   // one decode step per frame: the game keeps ticking while the SLM thinks
            }

            if (ownCur) for (int l = 0; l < NumLayers; l++) { curK[l].Dispose(); curV[l].Dispose(); }
        }

        const float MaskNeg = -1e9f;   // additive mask: 0 = attend, large-negative = masked
        const int SlidingWindow = 512; // Gemma3 sliding_attention window (config.sliding_window)
        // (1,1,qLen,kvLen) row-major: query q (absolute pos pastLen+q) attends key k iff k <= pastLen+q.
        static float[] CausalMask(int qLen, int kvLen, int pastLen)
        {
            var a = new float[qLen * kvLen];
            for (int q = 0; q < qLen; q++)
                for (int k = 0; k < kvLen; k++)
                    a[q * kvLen + k] = (k > pastLen + q) ? MaskNeg : 0f;
            return a;
        }

        // Sliding-attention layers additionally forbid keys older than SlidingWindow:
        // attend iff pastLen+q-SlidingWindow < k <= pastLen+q. Feeding the full-causal
        // mask here silently works only while the whole sequence fits in the window;
        // the 13-tool dev block (~989 tokens) exceeds it and generation collapses.
        static float[] SlidingMask(int qLen, int kvLen, int pastLen)
        {
            var a = new float[qLen * kvLen];
            for (int q = 0; q < qLen; q++)
            {
                int p = pastLen + q;
                for (int k = 0; k < kvLen; k++)
                    a[q * kvLen + k] = (k > p || k <= p - SlidingWindow) ? MaskNeg : 0f;
            }
            return a;
        }

        static int ArgmaxLast(Tensor<float> logits)
        {
            using var cpu = logits.ReadbackAndClone();
            var shape = cpu.shape;            // (1, seq, vocab)
            int seq = shape[1], vocab = shape[2];
            int best = 0; float bestVal = float.NegativeInfinity;
            for (int v = 0; v < vocab; v++)
            {
                float val = cpu[0, seq - 1, v];
                if (val > bestVal) { bestVal = val; best = v; }
            }
            return best;
        }

        /// <summary>
        /// Prefill the fixed devBlock once and cache its KV (P1). Idempotent; returns false if the
        /// tokenizer/devBlock aren't available so callers can fall back to the full-prompt path.
        /// </summary>
        bool TryPrefillPrefix()
        {
            if (m_PrefixReady) return true;
            if (!EnsureReady() || m_Tok == null || string.IsNullOrEmpty(m_DevBlock)) return false;

            var ids = new List<int>(m_Tok.Encode(m_DevBlock).GetIds());
            if (ids.Count == 0 || ids[0] != BOS) ids.Insert(0, BOS);
            int prefixLen = ids.Count;

            var key = new Tensor<float>[NumLayers];
            var val = new Tensor<float>[NumLayers];
            for (int l = 0; l < NumLayers; l++)
            {
                key[l] = new Tensor<float>(new TensorShape(1, NumKvHeads, 0, HeadDim));
                val[l] = new Tensor<float>(new TensorShape(1, NumKvHeads, 0, HeadDim));
            }
            using (var idT = new Tensor<int>(new TensorShape(1, prefixLen), ids.ToArray()))
            using (var mask = new Tensor<float>(new TensorShape(1, 1, prefixLen, prefixLen), CausalMask(prefixLen, prefixLen, 0)))
            using (var maskS = new Tensor<float>(new TensorShape(1, 1, prefixLen, prefixLen), SlidingMask(prefixLen, prefixLen, 0)))
            {
                m_Worker.SetInput("input_ids", idT);
                m_Worker.SetInput("mask_full", mask);
                m_Worker.SetInput("mask_sliding", maskS);
                for (int l = 0; l < NumLayers; l++)
                {
                    m_Worker.SetInput($"past_key_values.{l}.key", key[l]);
                    m_Worker.SetInput($"past_key_values.{l}.value", val[l]);
                }
                m_Worker.Schedule();

                // Keep the cached prefix KV resident on the inference device (CopyOutput clones
                // on-backend). ReadbackAndClone here would park ~35MB of KV on the CPU and
                // re-upload all of it on the FIRST step of EVERY command on GPU backends.
                m_PrefixKey = new Tensor<float>[NumLayers];
                m_PrefixVal = new Tensor<float>[NumLayers];
                for (int l = 0; l < NumLayers; l++)
                {
                    Tensor k = null, v = null;
                    m_Worker.CopyOutput($"present.{l}.key", ref k);
                    m_Worker.CopyOutput($"present.{l}.value", ref v);
                    m_PrefixKey[l] = k as Tensor<float>;
                    m_PrefixVal[l] = v as Tensor<float>;
                }
            }
            for (int l = 0; l < NumLayers; l++) { key[l].Dispose(); val[l].Dispose(); }
            m_PrefixLen = prefixLen;
            m_PrefixReady = true;
            return true;
        }

        void OnDestroy()
        {
            m_Worker?.Dispose();
            m_Argmax?.Dispose();
            if (m_PrefixKey != null) foreach (var t in m_PrefixKey) t?.Dispose();
            if (m_PrefixVal != null) foreach (var t in m_PrefixVal) t?.Dispose();
        }
    }
}
