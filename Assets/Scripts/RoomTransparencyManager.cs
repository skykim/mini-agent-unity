using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Manages room and wall transparency based on Tiger's current room location.
/// - In Kitchen: Kitchen and SharedWall_X15 are opaque, other rooms and walls are transparent.
/// - In Living Room: Living Room and SharedWall_X45 are opaque, other rooms and walls are transparent.
/// - In Bedroom: Bedroom is opaque, other rooms and walls are transparent.
/// Transparency level (0% - 100%) and fade speed are fully customizable via Inspector parameters.
/// </summary>
public class RoomTransparencyManager : MonoBehaviour
{
    public enum RoomState
    {
        Kitchen,
        LivingRoom,
        Bedroom
    }

    [Header("Tiger Target")]
    [Tooltip("Tiger character Transform (auto-detected if unassigned)")]
    [SerializeField] private Transform _tiger;

    [Header("Room Boundaries (X Coordinates)")]
    [Tooltip("Boundary X between Kitchen and Living Room (default: 15)")]
    [SerializeField] private float _kitchenToLivingX = 15f;

    [Tooltip("Boundary X between Living Room and Bedroom (default: 45)")]
    [SerializeField] private float _livingToBedroomX = 45f;

    [Header("Transparency Settings")]
    [Range(0f, 100f)]
    [Tooltip("Transparency percent (0% = fully opaque, 100% = fully transparent)")]
    [SerializeField] private float _transparencyPercent = 75f;

    [Tooltip("Transparency fade duration (seconds; 0 = instant)")]
    [SerializeField] private float _fadeDuration = 0.25f;

    [Header("Room Root GameObjects")]
    [SerializeField] private GameObject _kitchenRoot;
    [SerializeField] private GameObject _sharedWallX15;
    [SerializeField] private GameObject _livingRoomRoot;
    [SerializeField] private GameObject _sharedWallX45;
    [SerializeField] private GameObject _bedroomRoot;

    [Header("North Wall Segment GameObjects (Optional)")]
    [SerializeField] private List<GameObject> _kitchenNorthWalls = new List<GameObject>();
    [SerializeField] private List<GameObject> _livingNorthWalls = new List<GameObject>();
    [SerializeField] private List<GameObject> _bedroomNorthWalls = new List<GameObject>();

    [Header("Debug / Current State")]
    [SerializeField] private RoomState _currentRoom = RoomState.Kitchen;

    public float TransparencyPercent
    {
        get => _transparencyPercent;
        set => _transparencyPercent = Mathf.Clamp(value, 0f, 100f);
    }

    public RoomState CurrentRoom => _currentRoom;

    private class ManagedRenderer
    {
        public Renderer renderer;
        public Material[] originalMaterials;
        public Material[] transparentMaterials;
        public Color[] originalColors;
    }

    private class RoomGroup
    {
        public string name;
        public List<ManagedRenderer> renderers = new List<ManagedRenderer>();
        public float currentAlpha = 1f;
        public float targetAlpha = 1f;
    }

    private RoomGroup _kitchenGroup = new RoomGroup { name = "Kitchen" };
    private RoomGroup _wallX15Group = new RoomGroup { name = "WallX15" };
    private RoomGroup _livingGroup = new RoomGroup { name = "LivingRoom" };
    private RoomGroup _wallX45Group = new RoomGroup { name = "WallX45" };
    private RoomGroup _bedroomGroup = new RoomGroup { name = "Bedroom" };

    private readonly List<Material> _allocatedMaterials = new List<Material>();

    private void Awake()
    {
        AutoFindReferences();
        InitializeGroups();
    }

    private void Start()
    {
        UpdateRoomStateImmediate();
    }

    private void Update()
    {
        if (_tiger == null)
        {
            var tigerObj = GameObject.Find("Tiger");
            if (tigerObj != null) _tiger = tigerObj.transform;
            if (_tiger == null) return;
        }

        UpdateCurrentRoom();
        UpdateTargetAlphas();
        ApplyFading();
    }

    private void OnDestroy()
    {
        // Clean up runtime created materials to avoid memory leaks
        foreach (var mat in _allocatedMaterials)
        {
            if (mat != null)
            {
                Destroy(mat);
            }
        }
        _allocatedMaterials.Clear();
    }

    private void AutoFindReferences()
    {
        if (_tiger == null)
        {
            var tigerObj = GameObject.Find("Tiger");
            if (tigerObj != null) _tiger = tigerObj.transform;
        }

        var home = GameObject.Find("Home");
        if (home != null)
        {
            if (_kitchenRoot == null) _kitchenRoot = FindChild(home, "Kitchen");
            if (_sharedWallX15 == null) _sharedWallX15 = FindChild(home, "SharedWall_X15");
            if (_livingRoomRoot == null) _livingRoomRoot = FindChild(home, "LivingRoom");
            if (_sharedWallX45 == null) _sharedWallX45 = FindChild(home, "SharedWall_X45");
            if (_bedroomRoot == null) _bedroomRoot = FindChild(home, "Bedroom");

            var northWall = FindChild(home, "NorthWall");
            if (northWall != null)
            {
                if (_kitchenNorthWalls.Count == 0)
                {
                    AddIfNotNull(_kitchenNorthWalls, FindChild(northWall, "wallN_0"));
                    AddIfNotNull(_kitchenNorthWalls, FindChild(northWall, "wallN_1"));
                    AddIfNotNull(_kitchenNorthWalls, FindChild(northWall, "wallN_2"));
                }
                if (_livingNorthWalls.Count == 0)
                {
                    AddIfNotNull(_livingNorthWalls, FindChild(northWall, "wallN_3"));
                    AddIfNotNull(_livingNorthWalls, FindChild(northWall, "wallN_4"));
                    AddIfNotNull(_livingNorthWalls, FindChild(northWall, "wallN_5"));
                }
                if (_bedroomNorthWalls.Count == 0)
                {
                    AddIfNotNull(_bedroomNorthWalls, FindChild(northWall, "wallN_6"));
                    AddIfNotNull(_bedroomNorthWalls, FindChild(northWall, "wallN_7"));
                    AddIfNotNull(_bedroomNorthWalls, FindChild(northWall, "wallN_8"));
                }
            }
        }
    }

    private GameObject FindChild(GameObject parent, string name)
    {
        var t = parent.transform.Find(name);
        return t != null ? t.gameObject : null;
    }

    private void AddIfNotNull(List<GameObject> list, GameObject go)
    {
        if (go != null && !list.Contains(go)) list.Add(go);
    }

    private void InitializeGroups()
    {
        RegisterGroup(_kitchenGroup, _kitchenRoot, _kitchenNorthWalls);
        RegisterGroup(_wallX15Group, _sharedWallX15, null);
        RegisterGroup(_livingGroup, _livingRoomRoot, _livingNorthWalls);
        RegisterGroup(_wallX45Group, _sharedWallX45, null);
        RegisterGroup(_bedroomGroup, _bedroomRoot, _bedroomNorthWalls);
    }

    private void RegisterGroup(RoomGroup group, GameObject root, List<GameObject> extraObjects)
    {
        var renderers = new List<Renderer>();
        if (root != null)
        {
            renderers.AddRange(root.GetComponentsInChildren<Renderer>(true));
        }
        if (extraObjects != null)
        {
            foreach (var extra in extraObjects)
            {
                if (extra != null)
                {
                    renderers.AddRange(extra.GetComponentsInChildren<Renderer>(true));
                }
            }
        }

        foreach (var rend in renderers)
        {
            if (rend == null) continue;

            var origMats = rend.sharedMaterials;
            var transMats = new Material[origMats.Length];
            var origColors = new Color[origMats.Length];

            for (int i = 0; i < origMats.Length; i++)
            {
                var srcMat = origMats[i];
                if (srcMat == null) continue;

                var transMat = new Material(srcMat);
                _allocatedMaterials.Add(transMat);

                // Configure URP Lit transparency
                transMat.SetFloat("_Surface", 1f); // Transparent
                transMat.SetFloat("_Blend", 0f);   // Alpha
                transMat.SetOverrideTag("RenderType", "Transparent");
                transMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                transMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                transMat.SetInt("_ZWrite", 0);
                transMat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                transMat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

                // HomeDeviceController drives lamp/screen emission through MPBs, which only
                // render while the ACTIVE material has _EMISSION enabled. The clone is made
                // before that controller lazily enables the keyword on the originals, so
                // enable it here or emissive devices go dark whenever their room is faded.
                if (transMat.HasProperty("_EmissionColor")) transMat.EnableKeyword("_EMISSION");

                Color c = transMat.HasProperty("_BaseColor") ? transMat.GetColor("_BaseColor") : Color.white;
                origColors[i] = c;
                transMats[i] = transMat;
            }

            var mr = new ManagedRenderer
            {
                renderer = rend,
                originalMaterials = origMats,
                transparentMaterials = transMats,
                originalColors = origColors
            };

            group.renderers.Add(mr);
        }
    }

    private void UpdateCurrentRoom()
    {
        float x = _tiger.position.x;
        if (x < _kitchenToLivingX)
        {
            _currentRoom = RoomState.Kitchen;
        }
        else if (x < _livingToBedroomX)
        {
            _currentRoom = RoomState.LivingRoom;
        }
        else
        {
            _currentRoom = RoomState.Bedroom;
        }
    }

    private void UpdateTargetAlphas()
    {
        float transparentAlpha = Mathf.Clamp01(1f - (_transparencyPercent / 100f));

        switch (_currentRoom)
        {
            case RoomState.Kitchen:
                // In the Kitchen: keep Kitchen + X15 wall opaque; fade the rest (Living, X45 wall, Bedroom)
                _kitchenGroup.targetAlpha = 1f;
                _wallX15Group.targetAlpha = 1f;
                _livingGroup.targetAlpha = transparentAlpha;
                _wallX45Group.targetAlpha = transparentAlpha;
                _bedroomGroup.targetAlpha = transparentAlpha;
                break;

            case RoomState.LivingRoom:
                // In the Living Room: keep Living + X45 wall opaque; fade the rest (Kitchen, X15 wall, Bedroom)
                _kitchenGroup.targetAlpha = transparentAlpha;
                _wallX15Group.targetAlpha = transparentAlpha;
                _livingGroup.targetAlpha = 1f;
                _wallX45Group.targetAlpha = 1f;
                _bedroomGroup.targetAlpha = transparentAlpha;
                break;

            case RoomState.Bedroom:
                // In the Bedroom: keep only Bedroom opaque; fade the rest (Kitchen, X15 wall, Living, X45 wall)
                _kitchenGroup.targetAlpha = transparentAlpha;
                _wallX15Group.targetAlpha = transparentAlpha;
                _livingGroup.targetAlpha = transparentAlpha;
                _wallX45Group.targetAlpha = transparentAlpha;
                _bedroomGroup.targetAlpha = 1f;
                break;
        }
    }

    private void UpdateRoomStateImmediate()
    {
        if (_tiger != null)
        {
            UpdateCurrentRoom();
            UpdateTargetAlphas();
            SnapGroupAlpha(_kitchenGroup);
            SnapGroupAlpha(_wallX15Group);
            SnapGroupAlpha(_livingGroup);
            SnapGroupAlpha(_wallX45Group);
            SnapGroupAlpha(_bedroomGroup);
        }
    }

    private void SnapGroupAlpha(RoomGroup group)
    {
        group.currentAlpha = group.targetAlpha;
        ApplyGroupAlpha(group);
    }

    private void ApplyFading()
    {
        FadeGroup(_kitchenGroup);
        FadeGroup(_wallX15Group);
        FadeGroup(_livingGroup);
        FadeGroup(_wallX45Group);
        FadeGroup(_bedroomGroup);
    }

    private void FadeGroup(RoomGroup group)
    {
        if (Mathf.Approximately(group.currentAlpha, group.targetAlpha))
            return;

        if (_fadeDuration <= 0.001f)
        {
            group.currentAlpha = group.targetAlpha;
        }
        else
        {
            float step = (1f / _fadeDuration) * Time.deltaTime;
            group.currentAlpha = Mathf.MoveTowards(group.currentAlpha, group.targetAlpha, step);
        }

        ApplyGroupAlpha(group);
    }

    private void ApplyGroupAlpha(RoomGroup group)
    {
        float alpha = group.currentAlpha;
        bool isOpaque = alpha >= 0.999f;

        foreach (var mr in group.renderers)
        {
            if (mr.renderer == null) continue;

            if (isOpaque)
            {
                mr.renderer.sharedMaterials = mr.originalMaterials;
            }
            else
            {
                mr.renderer.sharedMaterials = mr.transparentMaterials;
                for (int i = 0; i < mr.transparentMaterials.Length; i++)
                {
                    var mat = mr.transparentMaterials[i];
                    if (mat != null && mat.HasProperty("_BaseColor"))
                    {
                        var orig = mr.originalColors[i];
                        mat.SetColor("_BaseColor", new Color(orig.r, orig.g, orig.b, orig.a * alpha));
                    }
                }
            }
        }
    }
}
