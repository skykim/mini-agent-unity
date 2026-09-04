using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Model-agnostic control layer for the smart-home scene. Holds references to the
/// interactive objects, knows which device lives in which room, and exposes a small
/// verb API (lights / TV / computer / speaker / vacuum) plus Tiger navigation.
///
/// Nothing here knows about the LLM — the model-connector layer (added next) parses
/// tool calls and calls into these methods. Every device method returns <c>true</c>
/// only if that device actually exists in the requested room, so the connector can
/// report "there's no such device in that room" for the asymmetric rooms
/// (TV/speaker = LivingRoom only, computer/vacuum = Bedroom only).
///
/// Scene layout (from RoomTransparencyManager): rooms are laid out along +X,
/// Kitchen (x&lt;15) | LivingRoom (15..45) | Bedroom (x&gt;=45).
/// </summary>
public class HomeDeviceController : MonoBehaviour
{
    public enum Room { Kitchen, LivingRoom, Bedroom }

    [Header("Agent avatar")]
    [Tooltip("The Tiger avatar that walks to a room before acting (auto-found by name).")]
    [SerializeField] private TigerController _tiger;

    [Header("Interactive objects (auto-found by name if left empty)")]
    [SerializeField] private Transform _lampSquareFloor;   // Kitchen light
    [SerializeField] private Transform _lampRoundFloor;    // LivingRoom light
    [SerializeField] private Transform _televisionModern;  // LivingRoom TV
    [SerializeField] private Transform _speaker;           // LivingRoom speaker
    [SerializeField] private Transform _lampSquareTable;   // Bedroom light
    [SerializeField] private Transform _robotCleaner;      // Bedroom vacuum
    [SerializeField] private Transform _computerScreen;    // Bedroom computer

    [Header("Tuning")]
    [Tooltip("How close Tiger must get to a room before an action runs.")]
    [SerializeField] private float _walkTimeout = 8f;

    // per-room device set
    private sealed class RoomDevices
    {
        public Transform light, tv, speaker, computer, vacuum, anchor;
    }
    private readonly Dictionary<Room, RoomDevices> _rooms = new();

    // runtime device state
    private readonly Dictionary<Transform, LightState> _lightState = new();
    private readonly Dictionary<Transform, ScreenState> _screenState = new();
    private SpeakerState _speakerState;
    private Coroutine _vacuumCo;
    private Vector3 _vacuumDock;   // robot's original resting spot, captured once

    private static readonly Dictionary<string, Color> s_Colors = new()
    {
        // vivid / near-primary hues (low secondary channels keep them from washing to white)
        ["red"] = new(1f, 0.08f, 0.08f),   ["orange"] = new(1f, 0.42f, 0.02f),
        ["yellow"] = new(1f, 0.82f, 0.02f),["green"] = new(0.08f, 0.9f, 0.12f),
        ["blue"] = new(0.06f, 0.28f, 1f),  ["purple"] = new(0.55f, 0.1f, 1f),
        ["pink"] = new(1f, 0.18f, 0.6f),   ["white"] = new(1f, 1f, 1f),
        ["warm"] = new(1f, 0.72f, 0.32f),  ["cool"] = new(0.45f, 0.8f, 1f),
    };

    private void Awake()
    {
        if (_tiger == null) _tiger = FindAnyObjectByType<TigerController>();
        _lampSquareFloor  = Resolve(_lampSquareFloor,  "lampSquareFloor");
        _lampRoundFloor   = Resolve(_lampRoundFloor,   "lampRoundFloor");
        _televisionModern = Resolve(_televisionModern, "televisionModern");
        _speaker          = Resolve(_speaker,          "speaker");
        _lampSquareTable  = Resolve(_lampSquareTable,  "lampSquareTable");
        _robotCleaner     = Resolve(_robotCleaner,     "robotCleaner");
        _computerScreen   = Resolve(_computerScreen,   "computerScreen");
        if (_robotCleaner != null) _vacuumDock = _robotCleaner.position;   // fixed dock

        _rooms[Room.Kitchen] = new RoomDevices
        { light = _lampSquareFloor, anchor = _lampSquareFloor };
        _rooms[Room.LivingRoom] = new RoomDevices
        { light = _lampRoundFloor, tv = _televisionModern, speaker = _speaker, anchor = _lampRoundFloor };
        _rooms[Room.Bedroom] = new RoomDevices
        { light = _lampSquareTable, computer = _computerScreen, vacuum = _robotCleaner, anchor = _lampSquareTable };
        // NOTE: room lights start OFF as authored in the scene (LampGlow lights disabled +
        // lamp emissive materials zeroed), not toggled off here at play entry.
    }

    // ---------------- room resolution ----------------

    /// <summary>Map an EN/KO room word to a Room. Returns false if unknown.</summary>
    public bool TryResolveRoom(string s, out Room room)
    {
        room = Room.LivingRoom;
        if (string.IsNullOrEmpty(s)) return false;
        // The fine-tuned model emits the room word the user said (KO or EN), so we match a
        // broad set of synonyms for each of the three real rooms. Normalize spaces so
        // "living room" / "livingroom" / "living-room" all match.
        var key = s.Trim().ToLowerInvariant().Replace("-", " ").Replace("  ", " ");
        var flat = key.Replace(" ", "");
        switch (key)
        {
            case "kitchen": case "주방": case "부엌": case "키친": case "부억":
                room = Room.Kitchen; return true;
            case "living room": case "livingroom": case "living": case "lounge": case "lounge room":
            case "거실": case "마루": case "거실방": case "리빙룸": case "리빙 룸": case "응접실":
                room = Room.LivingRoom; return true;
            case "bedroom": case "bed room": case "master bedroom":
            case "안방": case "침실": case "침실방": case "베드룸": case "안 방":
                room = Room.Bedroom; return true;
        }
        // space-insensitive fallback for the multi-word EN synonyms
        switch (flat)
        {
            case "kitchen": room = Room.Kitchen; return true;
            case "livingroom": case "living": case "lounge": case "loungeroom":
                room = Room.LivingRoom; return true;
            case "bedroom": case "masterbedroom": room = Room.Bedroom; return true;
        }
        return false;
    }

    public static string RoomName(Room r) => r switch
    {
        Room.Kitchen => "Kitchen", Room.LivingRoom => "Living Room", Room.Bedroom => "Bedroom", _ => "?"
    };

    // ---------------- Tiger navigation ----------------

    /// <summary>Device categories Tiger can walk to within a room.</summary>
    public enum Device { Light, Tv, Computer, Speaker, Vacuum }

    /// <summary>Walk Tiger to the specific device being controlled (not just the room),
    /// so it stands by the TV / computer / speaker it is about to act on.</summary>
    public IEnumerator WalkTo(Room room, Device device = Device.Light)
    {
        if (_tiger == null) yield break;
        var dev = _rooms[room];
        Transform target = device switch
        {
            Device.Tv => dev.tv,
            Device.Computer => dev.computer,
            Device.Speaker => dev.speaker,
            Device.Vacuum => dev.vacuum,
            _ => dev.light,
        };
        // If a SPECIFIC device isn't in this room, don't wander to a fallback (the light) —
        // stay put and let the caller report "there's no X here". Only the room Light
        // may fall back to the room anchor.
        if (target == null)
        {
            if (device == Device.Light) target = dev.anchor;
            if (target == null) yield break;
        }
        _tiger.MoveTo(target.position);
        float t = 0f;
        while (_tiger.IsAutoMoving && t < _walkTimeout) { t += Time.deltaTime; yield return null; }
    }

    // ---------------- lights ----------------

    /// <summary>Turn a room's light on/off. brightness 0..100 (null = keep/100).</summary>
    public bool SetLight(Room room, bool on, int? brightness = null)
    {
        var lamp = _rooms[room].light;
        if (lamp == null) return false;
        var st = LightOf(lamp);
        st.on = on;
        if (brightness.HasValue) st.brightness = Mathf.Clamp(brightness.Value, 0, 100);
        ApplyLight(st);
        return true;
    }

    /// <summary>Set a room's light color (turns it on). colorName is an EN enum value.</summary>
    public bool SetLightColor(Room room, string colorName)
    {
        var lamp = _rooms[room].light;
        if (lamp == null) return false;
        var st = LightOf(lamp);
        if (colorName != null && s_Colors.TryGetValue(colorName.Trim().ToLowerInvariant(), out var c))
            st.color = c;
        st.on = true;
        ApplyLight(st);
        return true;
    }

    // ---------------- screens (TV / computer) ----------------

    public bool SetTv(Room room, bool on)
    {
        var tv = _rooms[room].tv;
        if (tv == null) return false;
        ApplyScreen(ScreenOf(tv), on, new Color(0.35f, 0.55f, 0.9f));
        return true;
    }

    public bool SetComputer(Room room, bool on)
    {
        var pc = _rooms[room].computer;
        if (pc == null) return false;
        ApplyScreen(ScreenOf(pc), on, new Color(0.4f, 0.85f, 0.7f));
        return true;
    }

    // ---------------- speaker ----------------

    public bool SetMusic(Room room, bool on, int? volume = null, string genre = null)
    {
        var sp = _rooms[room].speaker;
        if (sp == null) return false;
        _speakerState ??= new SpeakerState(sp);
        if (volume.HasValue) _speakerState.volume = Mathf.Clamp(volume.Value, 0, 100);
        if (!string.IsNullOrEmpty(genre)) _speakerState.genre = genre;
        _speakerState.SetPlaying(on);
        return true;
    }

    /// <summary>Current music genre on the speaker (empty if none set).</summary>
    public string GetGenre(Room room) => _rooms[room].speaker == null ? null
        : (_speakerState != null ? _speakerState.genre : "");

    public bool SetVolume(Room room, int volume)
    {
        var sp = _rooms[room].speaker;
        if (sp == null) return false;
        _speakerState ??= new SpeakerState(sp);
        _speakerState.volume = Mathf.Clamp(volume, 0, 100);
        _speakerState.ApplyVolume();
        return true;
    }

    /// <summary>Current speaker volume (0-100), or -1 if the room has no speaker.
    /// Pure read: reports SpeakerState's default (50) before any music/volume command
    /// instead of allocating state (and its generated AudioClip) just to answer.</summary>
    public int GetVolume(Room room)
    {
        if (_rooms[room].speaker == null) return -1;
        return _speakerState?.volume ?? 50;
    }

    // ---------------- vacuum ----------------

    /// <summary>Start the robot vacuum patrol (Bedroom robot). room is advisory.</summary>
    public bool StartVacuum(Room? room)
    {
        if (_robotCleaner == null) return false;
        if (_vacuumCo != null) StopCoroutine(_vacuumCo);
        _vacuumCo = StartCoroutine(VacuumPatrol());
        return true;
    }

    private IEnumerator VacuumPatrol()
    {
        var t = _robotCleaner;
        float y = _vacuumDock.y;

        // full-coverage serpentine sweep over the bedroom floor, then back to the dock
        Bounds area = BedroomFloorBounds();
        const float margin = 2.5f, step = 5f;   // keep off walls/furniture; row spacing
        float x0 = area.min.x + margin, x1 = area.max.x - margin;
        float z0 = area.min.z + margin, z1 = area.max.z - margin;

        bool toFar = true;
        for (float x = x0; x <= x1 + 0.01f; x += step)
        {
            yield return VacuumDrive(t, new Vector3(x, y, toFar ? z0 : z1));   // enter the lane
            yield return VacuumDrive(t, new Vector3(x, y, toFar ? z1 : z0));   // sweep the lane
            toFar = !toFar;
        }
        yield return VacuumDrive(t, _vacuumDock);   // return home
        t.position = _vacuumDock;
        _vacuumCo = null;
    }

    /// <summary>Drive the vacuum to a point at constant speed, facing its travel direction.</summary>
    private static IEnumerator VacuumDrive(Transform t, Vector3 target)
    {
        Vector3 from = t.position; target.y = from.y;
        Vector3 flat = target - from; flat.y = 0f;
        if (flat.sqrMagnitude > 0.001f) t.rotation = Quaternion.LookRotation(flat.normalized);
        float dist = flat.magnitude, speed = 8f, dur = Mathf.Max(0.05f, dist / speed), e = 0f;
        while (e < dur) { e += Time.deltaTime; t.position = Vector3.Lerp(from, target, e / dur); yield return null; }
        t.position = target;
    }

    /// <summary>Combined world bounds of the bedroom's floor tiles (fallback: around the robot).</summary>
    private Bounds BedroomFloorBounds()
    {
        var bed = GameObject.Find("Bedroom");
        if (bed != null)
        {
            bool init = false; Bounds b = default;
            foreach (var tr in bed.GetComponentsInChildren<Transform>())
            {
                if (!tr.name.ToLowerInvariant().Contains("floor")) continue;
                var r = tr.GetComponent<Renderer>();
                if (r == null) continue;
                if (!init) { b = r.bounds; init = true; } else b.Encapsulate(r.bounds);
            }
            if (init) return b;
        }
        return new Bounds(_robotCleaner.position, new Vector3(20f, 0f, 20f));
    }

    // ==================== internals ====================

    private sealed class LightState
    {
        public Transform lamp;
        public Light light;
        // (renderer, emissive submaterial index): only the lamp's emissive material glows,
        // driven per-index so the body submaterial stays dark.
        public readonly List<(Renderer rend, int index)> rends = new();
        public bool on;
        public int brightness = 100;
        public Color color = new(1f, 0.85f, 0.6f);
    }
    static MaterialPropertyBlock s_Mpb;

    private LightState LightOf(Transform lamp)
    {
        if (_lightState.TryGetValue(lamp, out var st)) return st;
        st = new LightState { lamp = lamp };
        // reuse the lamp's built-in point light (e.g. "LampGlow") if it has one;
        // otherwise create one, so every room light is controllable AND starts off
        st.light = lamp.GetComponentInChildren<Light>(true);
        if (st.light == null)
        {
            var lgo = new GameObject("AgentLight");
            lgo.transform.SetParent(lamp, false);
            lgo.transform.localPosition = Vector3.up * 1.5f;
            st.light = lgo.AddComponent<Light>();
            st.light.type = LightType.Point;
            st.light.range = 12f;
        }
        st.light.enabled = false;
        // Emission is driven via a per-renderer MaterialPropertyBlock — RoomTransparencyManager
        // swaps renderer.sharedMaterials for fading, which would wipe any tint written to a
        // material instance; an MPB rides on the renderer and survives those swaps. We only
        // need the _EMISSION keyword enabled on the assigned materials.
        // Only the lamp's EMISSIVE submaterial should glow, not the body. The kit's bulb/
        // shade materials are named "lamp_emissive_*", so match those by name and drive
        // emission per submaterial index — a whole-renderer MPB would light every index,
        // including the body (URP Lit body materials also carry _EmissionColor).
        foreach (var r in lamp.GetComponentsInChildren<Renderer>(true))
        {
            var mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (m == null || !m.HasProperty("_EmissionColor")) continue;
                if (m.name.IndexOf("emiss", System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                m.EnableKeyword("_EMISSION");
                st.rends.Add((r, i));
            }
        }
        // Fallback for a lamp with a single combined material (no "emissive"-named sub):
        // light every _EmissionColor submaterial so the lamp still glows.
        if (st.rends.Count == 0)
            foreach (var r in lamp.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                    if (mats[i] != null && mats[i].HasProperty("_EmissionColor"))
                    { mats[i].EnableKeyword("_EMISSION"); st.rends.Add((r, i)); }
            }
        _lightState[lamp] = st;
        return st;
    }

    private static void ApplyLight(LightState st)
    {
        float k = st.brightness / 100f;
        st.light.enabled = st.on;
        st.light.color = st.color;
        st.light.intensity = 0.8f + 4.0f * k;
        // Vivid emission via MPB. Saturated palette colors (low secondary channels) stay
        // colored even at high intensity instead of washing out to white.
        Color emit = st.on ? st.color * (0.7f + 2.0f * k) : Color.black;
        s_Mpb ??= new MaterialPropertyBlock();
        foreach (var (r, idx) in st.rends)
        {
            r.GetPropertyBlock(s_Mpb, idx);
            s_Mpb.SetColor("_EmissionColor", emit);
            r.SetPropertyBlock(s_Mpb, idx);
        }
    }

    private sealed class ScreenState
    {
        // (renderer, screen submaterial index) pairs. Emission rides a per-index
        // MaterialPropertyBlock — like the lights, because RoomTransparencyManager
        // swaps renderer.sharedMaterials for fading, which would discard any
        // material instance the screen glow was written to.
        public readonly List<(Renderer rend, int index)> screens = new();
    }

    private ScreenState ScreenOf(Transform screenRoot)
    {
        if (_screenState.TryGetValue(screenRoot, out var st)) return st;
        st = new ScreenState();
        // The TV/computer meshes use 2 sub-materials: Element 0 = bezel/frame,
        // Element 1 = the screen. Only the screen should light up — leave the frame.
        foreach (var r in screenRoot.GetComponentsInChildren<Renderer>(true))
        {
            var mats = r.sharedMaterials;
            int idx = mats.Length > 1 ? 1 : 0;           // screen submaterial (fallback to 0)
            var m = idx < mats.Length ? mats[idx] : null;
            if (m != null && m.HasProperty("_EmissionColor"))
            {
                m.EnableKeyword("_EMISSION");
                st.screens.Add((r, idx));
            }
        }
        _screenState[screenRoot] = st;
        return st;
    }

    private static void ApplyScreen(ScreenState st, bool on, Color glow)
    {
        s_Mpb ??= new MaterialPropertyBlock();
        foreach (var (r, idx) in st.screens)
        {
            r.GetPropertyBlock(s_Mpb, idx);
            s_Mpb.SetColor("_EmissionColor", on ? glow * 1.4f : Color.black);
            r.SetPropertyBlock(s_Mpb, idx);
        }
    }

    private sealed class SpeakerState
    {
        public readonly Transform speaker;
        public readonly AudioSource src;
        public int volume = 50;
        public bool playing;
        public string genre = "";
        private readonly Vector3 _baseScale;

        public SpeakerState(Transform sp)
        {
            speaker = sp; _baseScale = sp.localScale;
            // `??` does not respect Unity's fake-null, so use an explicit == null check
            src = sp.GetComponent<AudioSource>();
            if (src == null) src = sp.gameObject.AddComponent<AudioSource>();
            src.loop = true; src.playOnAwake = false;
            ApplyClip();
        }
        public void SetPlaying(bool on)
        {
            playing = on;
            ApplyVolume();
            ApplyClip();   // genre may have changed since the last play
            if (on && !src.isPlaying) src.Play();
            if (!on && src.isPlaying) src.Stop();
        }
        public void ApplyVolume() => src.volume = volume / 100f * 0.8f;

        // Per-genre tracks under Assets/Resources/Music/{rock,jazz,lofi}.wav —
        // seamless ~12-15s loops synthesized from scratch (no third-party audio).
        // Missing file (or unknown genre word) falls back to the built-in
        // procedural loop, so the speaker always plays SOMETHING. Swapping the
        // clip mid-play restarts playback.
        static readonly Dictionary<string, AudioClip> s_GenreClips = new();
        static AudioClip s_Fallback;

        void ApplyClip()
        {
            var g = string.IsNullOrEmpty(genre) ? "jazz" : genre.Trim().ToLowerInvariant();
            if (!s_GenreClips.TryGetValue(g, out var clip))
            {
                clip = Resources.Load<AudioClip>("Music/" + g);
                s_GenreClips[g] = clip;   // cache even null so a missing file is probed once
            }
            if (clip == null) clip = s_Fallback ??= MakeClip();
            if (src.clip == clip) return;
            bool wasPlaying = src.isPlaying;
            if (wasPlaying) src.Stop();
            src.clip = clip;
            if (wasPlaying) src.Play();
        }

        private static AudioClip MakeClip()
        {
            const int sr = 22050; float[] f = { 261.63f, 329.63f, 392f, 523.25f, 392f, 329.63f, 440f, 349.23f };
            int n = sr / 4; var d = new float[f.Length * n];
            for (int i = 0; i < f.Length; i++)
                for (int j = 0; j < n; j++)
                {
                    float tt = j / (float)sr, env = Mathf.Exp(-3.5f * tt);
                    d[i * n + j] = env * (0.3f * Mathf.Sin(2 * Mathf.PI * f[i] * tt)
                                          + 0.12f * Mathf.Sin(4 * Mathf.PI * f[i] * tt));
                }
            var c = AudioClip.Create("HomeMusic", d.Length, 1, sr, false); c.SetData(d, 0); return c;
        }
    }

    // recursive find by name including inactive objects (GameObject.Find skips inactive)
    private static Transform Resolve(Transform assigned, string name)
    {
        if (assigned != null) return assigned;
        foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            if (t.name == name && t.gameObject.scene.IsValid())
                return t;
        Debug.LogWarning($"[HomeDeviceController] object '{name}' not found in scene");
        return null;
    }
}
