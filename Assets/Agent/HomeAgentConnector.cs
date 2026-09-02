using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NeedleTest;                 // FgCallParser, ToolCallValidator, SmartHomeTools, AgentInfoApis, AgentCloudClient
using SentisSkills.Inference;     // FunctionGemmaGenerator
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Model → scene connector. Turns a natural-language command into on-device tool
/// calls (FunctionGemmaGenerator), validates them against the schema gate, and
/// drives <see cref="HomeDeviceController"/> (Tiger walks to the room, then the
/// device acts). Info tools hit live APIs; anything the model can't handle is
/// escalated to the cloud (Ollama) instead of dead-ending on a refusal.
///
/// This is the ONLY layer that knows about the model — swap the .onnx (and dev
/// block) on the generator and this connector is unchanged. During the interim
/// (current model still has set_temperature and no computer) both are handled:
/// temperature reports "unsupported in this scene", computer waits for the new model.
/// </summary>
public class HomeAgentConnector : MonoBehaviour
{
    [Header("Modules")]
    [SerializeField] private FunctionGemmaGenerator _agent;
    [SerializeField] private HomeDeviceController _home;

    [Header("UI")]
    [SerializeField] private InputField _input;
    [SerializeField] private Button _sendButton;
    [SerializeField] private Text _status;                 // bottom-left status
    // response shows as a world-space speech bubble above Tiger's head (TigerBubble).
    // The bubble draws with ZTest Always + a high renderQueue, so it is always on top.
    [SerializeField] private TigerSpeechBubble _bubble;

    [Header("Options")]
    [SerializeField] private bool _useCloudFallback = true;

    [Header("Cloud fallback (OpenAI-compatible chat completions)")]
    [SerializeField] private string _cloudEndpoint = "http://localhost:11434/v1/chat/completions";
    [SerializeField] private string _cloudModel = "gpt-oss:120b-cloud";

    // Persona pinned: gpt-oss otherwise introduces itself as "ChatGPT by OpenAI"
    // when asked who it is, which breaks the demo fiction.
    const string CloudSystemPrompt =
        "You are Tiger, the friendly smart-home assistant tiger of this house. " +
        "Answer concisely and accurately, ALWAYS in the same language the user used " +
        "(Korean question -> Korean answer, English -> English). Never mention OpenAI, " +
        "ChatGPT, or what model you run on — you are simply Tiger. You CANNOT operate " +
        "devices yourself: if asked to control something (windows, aircon, volume, doors...), " +
        "say you can't do that here — NEVER claim you did it. One or two sentences.";
    static readonly string s_SessionLog =
        "/Users/sky.kim/Desktop/Playground/mobile-agent-unity/needle-test/miniagent_session.log";
    // structured, machine-readable log of every turn (user + parsed calls) so real test
    // interactions can later be harvested back into training data (see harvest_logs.py).
    static readonly string s_HarvestLog =
        "/Users/sky.kim/Desktop/Playground/mobile-agent-unity/needle-test/miniagent_harvest.jsonl";

    ToolCallValidator m_Validator;
    bool m_Busy, m_Ready;
    // network fetch time accumulated across info/web tools this turn, shown separately
    // from the on-device generation time: "name{..} call {gen}ms + fetch {net}ms".
    long m_FetchMs;
    int m_NetCalls;
    // cloud-LLM synthesis time for web_search (retrieve-then-answer), shown as "+ llm {ms}".
    long m_LlmMs;

    public bool IsReady => m_Ready;
    public bool IsBusy => m_Busy;

    public void Submit(string text) { if (_input != null) { _input.text = text; ClampCaret(); } OnSend(); }

    // After we assign _input.text programmatically (or keep it after sending), the
    // InputField's cached caret can point past the new text length; the next keystroke
    // then throws in InputField.Append (Substring out of range). Keep the caret valid.
    void ClampCaret()
    {
        if (_input == null) return;
        int end = _input.text != null ? _input.text.Length : 0;
        _input.caretPosition = end;
        _input.selectionAnchorPosition = end;
        _input.selectionFocusPosition = end;
    }

    Font m_Font;

    void Awake()
    {
        // no OS Korean font -> keep the authored fonts (don't downgrade to the built-in)
        m_Font = UiRuntimeAssets.KoreanOsFont(24);
        if (m_Font != null)
            foreach (var t in FindObjectsByType<Text>(FindObjectsInactive.Exclude)) t.font = m_Font;
    }

    void Start()
    {
        m_Validator = SmartHomeTools.Validator;
        if (_home == null) _home = FindAnyObjectByType<HomeDeviceController>();
        if (_agent == null) _agent = FindAnyObjectByType<FunctionGemmaGenerator>();
        // response shows as a world-space bubble above Tiger's head (always on top)
        if (_bubble == null) _bubble = FindAnyObjectByType<TigerSpeechBubble>(FindObjectsInactive.Include);
        if (_bubble != null) _bubble.gameObject.SetActive(true);
        if (_sendButton != null) _sendButton.onClick.AddListener(OnSend);
        if (_input != null) _input.onSubmit.AddListener(_ => OnSend());
        SetStatus("Loading model...");
        StyleUI();
        SessionLog($"===== session start {System.DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
        StartCoroutine(Warmup());
    }

    IEnumerator Warmup()
    {
        yield return null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _agent.Warmup();
        sw.Stop();
        m_Ready = true;
        SetStatus($"Warmup {sw.ElapsedMilliseconds}ms");
        ShowResponse("Hi, there!", 4f);
    }

    void OnSend()
    {
        var text = _input != null ? _input.text?.Trim() : null;
        if (string.IsNullOrEmpty(text) || m_Busy) return;
        // keep the typed text in the field after sending (do NOT clear it), but
        // RELEASE focus so gameplay keys (WASD / Space-to-jump) work again — a focused
        // InputField would otherwise swallow Space as a typed character.
        if (_input != null)
        {
            ClampCaret();
            _input.DeactivateInputField();
        }
        if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
        SessionLog($"USER  {text}");
        StartCoroutine(Handle(text));
    }

    IEnumerator Handle(string user)
    {
        m_Busy = true;
        // Drive the turn's (possibly nested) enumerators OURSELVES instead of handing
        // nesting to Unity: an exception anywhere in the turn — a bad API payload, a
        // missing scene object — must never leave m_Busy stuck true, which would
        // silently ignore every later command. Unity's own nesting kills the whole
        // chain on a nested throw without resuming this frame, so a try/finally here
        // wouldn't be guaranteed to run; flattening the stack makes every MoveNext
        // happen inside our try. Real yields (null / WaitForSeconds / web requests)
        // still pass through to Unity untouched.
        var stack = new Stack<IEnumerator>();
        stack.Push(HandleTurn(user));
        while (stack.Count > 0)
        {
            object cur;
            try
            {
                if (!stack.Peek().MoveNext()) { stack.Pop(); continue; }
                cur = stack.Peek().Current;
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
                SetStatus("error (see console)");
                SessionLog($"ERROR {e.GetType().Name}: {e.Message}");
                break;
            }
            if (cur is IEnumerator nested) { stack.Push(nested); continue; }
            yield return cur;
        }
        m_Busy = false;
    }

    IEnumerator HandleTurn(string user)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        string raw = null;
        yield return _agent.GenerateCallRoutine(user, r => raw = r);
        long agentMs = sw.ElapsedMilliseconds;

        var calls = new List<ParsedCall>();
        var rejected = new List<string>();
        foreach (var pc in FgCallParser.ParseAll(raw ?? ""))
        {
            var res = m_Validator.Validate(pc);
            if (res.Ok) calls.Add(pc); else rejected.Add(res.Reason);
        }

        // Reflect the tool call in the status UI IMMEDIATELY after generation — before
        // Tiger walks over and the device acts — so the feedback isn't delayed by the walk.
        string callLabel = calls.Count > 0 ? string.Join(" | ", calls.ConvertAll(FormatCall)) : "";
        if (calls.Count > 0)
            SetStatus($"{callLabel} {agentMs}ms");

        m_FetchMs = 0; m_NetCalls = 0; m_LlmMs = 0;
        var responses = new List<string>();
        foreach (var call in calls)
        {
            yield return ExecuteCall(call, responses);
        }

        // Info/web tools hit the network: show the on-device generation time, the fetch
        // time, and (for web_search's grounded synthesis) the cloud-LLM time separately.
        if (calls.Count > 0 && m_NetCalls > 0)
        {
            string t = $"{callLabel} call {agentMs}ms + fetch {m_FetchMs}ms";
            if (m_LlmMs > 0) t += $" + llm {m_LlmMs}ms";
            SetStatus(t);
        }

        bool usedCloud = false; long cloudMs = 0;
        if (calls.Count == 0)
        {
            if (_useCloudFallback)
            {
                SetStatus("Asking the cloud...");
                string ans = null;
                yield return AgentCloudClient.Complete(CloudSystemPrompt, user, _cloudEndpoint, _cloudModel,
                                                       (a, ms) => { ans = a; cloudMs = ms; });
                if (!string.IsNullOrEmpty(ans) && !ans.StartsWith("(cloud")) { usedCloud = true; responses.Add(ans); }
                else responses.Add("Sorry, I can't handle that request.");
            }
            else responses.Add("Sorry, I can't handle that request.");
        }

        sw.Stop();
        var answer = string.Join(" ", responses);
        ShowResponse(answer);
        SessionLog($"RAW   {(raw ?? "").Replace('\n', ' ')}");
        if (calls.Count > 0) SessionLog($"CALLS {string.Join(" | ", calls.ConvertAll(c => c.Raw))}");
        SessionLog(usedCloud ? $"CLOUD {answer} (route={cloudMs}ms)" : $"AGENT {answer}");
        if (rejected.Count > 0) SessionLog($"GATE-REJECT {string.Join("; ", rejected)}");
        SessionLog($"TIME  agent={agentMs}ms total={sw.ElapsedMilliseconds}ms" + (usedCloud ? $" cloud={cloudMs}ms" : ""));
        HarvestLog(user, calls, raw, usedCloud, rejected.Count > 0);
        // tool-call status was already shown right after generation; only cloud / no-tool
        // outcomes are known this late, so update the status just for those.
        if (usedCloud) SetStatus($"cloud llm {cloudMs}ms");
        else if (calls.Count == 0) SetStatus($"no tool {agentMs}ms");
    }

    // Compact "name{val1,val2}" for the status line, e.g. turn_on_tv{거실}, set_volume{거실,30}.
    static string FormatCall(ParsedCall c)
    {
        var vals = new List<string>();
        foreach (var kv in c.Args) vals.Add(kv.Value);
        return $"{c.Name}{{{string.Join(",", vals)}}}";
    }

    IEnumerator ExecuteCall(ParsedCall call, List<string> responses)
    {
        string roomStr = Arg(call, "room");
        HomeDeviceController.Room room = default;
        bool haveRoom = _home != null && roomStr != null && _home.TryResolveRoom(roomStr, out room);
        string rn = haveRoom ? HomeDeviceController.RoomName(room) : roomStr;

        switch (call.Name)
        {
            case "turn_on_light":
            {
                if (!haveRoom) { responses.Add(NoRoom(roomStr)); break; }
                var b = Arg(call, "brightness");
                int? bi = b != null && int.TryParse(b, out var bv) ? bv : (int?)null;
                yield return _home.WalkTo(room);
                _home.SetLight(room, true, bi);
                responses.Add(bi.HasValue ? $"Turned on the {rn} light at {bi}% brightness." : $"Turned on the {rn} light.");
                break;
            }
            case "turn_off_light":
                if (!haveRoom) { responses.Add(NoRoom(roomStr)); break; }
                yield return _home.WalkTo(room); _home.SetLight(room, false);
                responses.Add($"Turned off the {rn} light.");
                break;
            case "set_light_color":
            {
                if (!haveRoom) { responses.Add(NoRoom(roomStr)); break; }
                var col = Arg(call, "color");
                yield return _home.WalkTo(room); _home.SetLightColor(room, col);
                responses.Add($"Changed the {rn} light to {col}.");
                break;
            }
            // TV is a single device (lives in the Living Room) — no room arg.
            case "turn_on_tv":
            case "turn_off_tv":
            {
                bool on = call.Name == "turn_on_tv";
                var tvRoom = HomeDeviceController.Room.LivingRoom;
                yield return _home.WalkTo(tvRoom, HomeDeviceController.Device.Tv);
                _home.SetTv(tvRoom, on);
                responses.Add($"Turned {(on ? "on" : "off")} the TV.");
                break;
            }
            // Computer is a single device (lives in the Bedroom) — no room arg.
            case "turn_on_computer":
            case "turn_off_computer":
            {
                bool on = call.Name == "turn_on_computer";
                var pcRoom = HomeDeviceController.Room.Bedroom;
                yield return _home.WalkTo(pcRoom, HomeDeviceController.Device.Computer);
                _home.SetComputer(pcRoom, on);
                responses.Add($"Turned {(on ? "on" : "off")} the computer.");
                break;
            }
            // music/volume have no room — they target the single speaker (Living Room).
            case "play_music":
            {
                var sroom = HomeDeviceController.Room.LivingRoom;
                // genre in {rock,jazz,lofi}; no genre given => default jazz.
                var genre = Arg(call, "genre");
                if (string.IsNullOrEmpty(genre)) genre = "jazz";
                yield return _home.WalkTo(sroom, HomeDeviceController.Device.Speaker);
                if (_home.SetMusic(sroom, true, null, genre))
                    responses.Add($"Playing {genre} music.");
                else responses.Add("There's no speaker.");
                break;
            }
            case "stop_music":
            {
                var sroom = HomeDeviceController.Room.LivingRoom;
                yield return _home.WalkTo(sroom, HomeDeviceController.Device.Speaker);
                responses.Add(_home.SetMusic(sroom, false) ? "Stopped the music." : "There's no speaker.");
                break;
            }
            case "set_volume":
            {
                var sroom = HomeDeviceController.Room.LivingRoom;
                var v = Arg(call, "volume"); int vi = int.TryParse(v, out var vv) ? vv : 50;
                yield return _home.WalkTo(sroom, HomeDeviceController.Device.Speaker);
                responses.Add(_home.SetVolume(sroom, vi) ? $"Set the volume to {vi}%." : "There's no speaker.");
                break;
            }
            case "get_volume":
            {
                int cur = _home.GetVolume(HomeDeviceController.Room.LivingRoom);
                responses.Add(cur >= 0 ? $"The volume is {cur}%." : "There's no speaker.");
                break;
            }
            // Vacuum is a single device (lives in the Bedroom) — no room arg. Walk up to
            // the robot first, then start it (same as walking to the computer).
            case "start_vacuum":
            {
                var vacRoom = HomeDeviceController.Room.Bedroom;
                yield return _home.WalkTo(vacRoom, HomeDeviceController.Device.Vacuum);
                _home.StartVacuum(null);
                responses.Add("The robot vacuum started cleaning.");
                break;
            }
            case "set_temperature":   // interim: no thermostat object in this scene
                responses.Add("Temperature control isn't supported in this scene.");
                break;
            case "get_weather":
            {
                var fsw = System.Diagnostics.Stopwatch.StartNew(); string wx = null;
                yield return AgentInfoApis.GetWeather(Arg(call, "city") ?? "", a => wx = a);
                fsw.Stop(); m_FetchMs += fsw.ElapsedMilliseconds; m_NetCalls++;
                responses.Add(wx); break;
            }
            case "get_time":
            {
                var fsw = System.Diagnostics.Stopwatch.StartNew(); string tm = null;
                yield return AgentInfoApis.GetTime(Arg(call, "city"), a => tm = a);
                fsw.Stop(); m_FetchMs += fsw.ElapsedMilliseconds; m_NetCalls++;
                responses.Add(tm); break;
            }
            case "get_location":
            {
                var fsw = System.Diagnostics.Stopwatch.StartNew(); string loc = null;
                yield return AgentInfoApis.GetLocation(a => loc = a);
                fsw.Stop(); m_FetchMs += fsw.ElapsedMilliseconds; m_NetCalls++;
                responses.Add(loc); break;
            }
            // web_search = RETRIEVE (DuckDuckGo) -> SYNTHESIZE (cloud LLM over the results).
            // A real search-then-answer chain: the search grounds the cloud answer, so it
            // works even when there's no clean instant-answer. Distinct from the chit-chat
            // cloud fallback, which answers directly with no retrieval.
            case "web_search":
            {
                string query = Arg(call, "query") ?? "";
                // 1) retrieve context from the web
                var fsw = System.Diagnostics.Stopwatch.StartNew(); string context = null;
                yield return AgentInfoApis.WebSearchRetrieve(query, c => context = c);
                fsw.Stop(); m_FetchMs += fsw.ElapsedMilliseconds; m_NetCalls++;
                // 2) synthesize a grounded answer with the cloud LLM
                string answer = null;
                if (_useCloudFallback)
                {
                    SetStatus($"web_search{{{query}}} synthesizing...");
                    const string sys =
                        "You answer the user's question using the web search results provided. " +
                        "Ground your answer in them; if they are insufficient, answer from your own " +
                        "knowledge and note that briefly. Reply in EXACTLY the language the question " +
                        "is written in (Korean question -> Korean answer, English -> English; never " +
                        "any other language). One or two sentences.";
                    string prompt = string.IsNullOrEmpty(context)
                        ? $"Question: {query}\n\n(No search results were found.)"
                        : $"Question: {query}\n\nWeb search results:\n{context}";
                    var lsw = System.Diagnostics.Stopwatch.StartNew(); string ans = null;
                    yield return AgentCloudClient.Complete(sys, prompt, _cloudEndpoint, _cloudModel, (a, _) => ans = a);
                    lsw.Stop(); m_LlmMs += lsw.ElapsedMilliseconds;
                    if (!string.IsNullOrEmpty(ans) && !ans.StartsWith("(cloud")) answer = ans;
                }
                // 3) fallback: raw retrieved snippet, or a not-found note
                if (string.IsNullOrEmpty(answer))
                    answer = !string.IsNullOrEmpty(context) ? context : $"I couldn't find an answer for \"{query}\".";
                responses.Add(answer);
                break;
            }
            default:
                responses.Add($"Can't handle '{call.Name}'.");
                break;
        }
    }

    static string NoRoom(string r) => $"There's no '{r}' room in this house. (Kitchen / Living Room / Bedroom)";

    static string Arg(ParsedCall c, string key)
    {
        foreach (var kv in c.Args) if (kv.Key == key) return kv.Value;
        return null;
    }

    void SetStatus(string s) { if (_status != null) _status.text = s; }

    // ---- response shows in the world-space bubble above Tiger's head ----
    void ShowResponse(string text, float seconds = 6f)
    {
        if (_bubble == null || string.IsNullOrEmpty(text)) return;
        _bubble.Say(text, seconds);
    }

    // ---- UI polish: rounded corners on the button/input/status + English placeholder ----
    void StyleUI()
    {
        var round = RoundedSprite();
        RoundImage(_sendButton != null ? _sendButton.GetComponent<Image>() : null, round);
        RoundImage(_input != null ? _input.GetComponent<Image>() : null, round);
        if (_status != null) RoundImage(_status.GetComponentInParent<Image>(), round);
        if (_input != null && _input.placeholder is Text ph)
            ph.text = "Type a command (e.g., turn on the living room light)";
        if (_sendButton != null)
        {
            var label = _sendButton.GetComponentInChildren<Text>();
            if (label != null) label.text = "Send";
        }
    }

    static void RoundImage(Image img, Sprite s)
    {
        if (img == null) return;
        img.sprite = s;
        img.type = Image.Type.Sliced;
    }

    static Sprite RoundedSprite() => UiRuntimeAssets.RoundedSprite(16f);

    static void SessionLog(string line)
    {
        try { File.AppendAllText(s_SessionLog, $"{System.DateTime.Now:HH:mm:ss}  {line}\n"); } catch { }
    }

    // ---- harvest log: one JSON object per turn, harvestable into training data ----
    static string JEsc(string s)
    {
        if (s == null) return "";
        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            if (c == '\\' || c == '"') sb.Append('\\').Append(c);
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\t') sb.Append("\\t");
            else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    void HarvestLog(string user, List<ParsedCall> calls, string raw, bool cloud, bool rejected)
    {
        var sb = new StringBuilder();
        sb.Append("{\"ts\":\"").Append(System.DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss")).Append("\",");
        sb.Append("\"user\":\"").Append(JEsc(user)).Append("\",");
        sb.Append("\"cloud\":").Append(cloud ? "true" : "false").Append(',');
        sb.Append("\"rejected\":").Append(rejected ? "true" : "false").Append(',');
        sb.Append("\"calls\":[");
        for (int i = 0; i < (calls?.Count ?? 0); i++)
        {
            var c = calls[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"name\":\"").Append(JEsc(c.Name)).Append("\",\"args\":{");
            bool f = true;
            foreach (var kv in c.Args)
            {
                if (!f) sb.Append(',');
                f = false;
                sb.Append('"').Append(JEsc(kv.Key)).Append("\":\"").Append(JEsc(kv.Value)).Append('"');
            }
            sb.Append("}}");
        }
        sb.Append("],\"raw\":\"").Append(JEsc(raw ?? "")).Append("\"}");
        try { File.AppendAllText(s_HarvestLog, sb.ToString() + "\n"); } catch { }
    }
}
