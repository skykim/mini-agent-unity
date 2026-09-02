using System.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// A speech bubble that floats above Tiger's head and shows the agent's reply.
///
/// It is drawn on its OWN Screen-Space-Overlay canvas (high sortingOrder) and
/// positioned every frame at the screen projection of Tiger's head. Overlay UI
/// always renders on top of ALL 3D geometry, so the bubble is never occluded by
/// walls or furniture — no depth/ZTest tricks needed (URP's Unlit/Text shaders
/// ignore a material _ZTest, which is why the old world-space quad got hidden).
///
/// Call <see cref="Say"/> to show text for a few seconds.
/// </summary>
public class TigerSpeechBubble : MonoBehaviour
{
    [SerializeField] private Transform _tiger;
    [SerializeField] private float _headMargin = 0.6f;   // world-space gap above the head
    [SerializeField] private int _wrap = 18;             // soft-wrap width (chars)
    [SerializeField] private int _sortingOrder = 500;    // above AgentCanvas so it stays on top

    [Header("Scene UI (pre-placed; auto-built if left empty)")]
    [SerializeField] private Canvas _canvas;             // Screen-Space-Overlay canvas
    [SerializeField] private RectTransform _panel;       // rounded bubble background
    [SerializeField] private Text _text;                 // reply text

    private Camera _cam;
    private Font _font;
    private float _headWorldY;     // offset from Tiger origin to the top of its bounds
    private Coroutine _hideCo;

    private static readonly Color s_Bubble = new(0.10f, 0.11f, 0.16f, 0.82f);

    private void Awake()
    {
        if (_tiger == null)
        {
            var t = GameObject.Find("Tiger");
            if (t != null) _tiger = t.transform;
        }
        _cam = Camera.main;
        _font = UiRuntimeAssets.KoreanOsFont(40);
        if (_font == null) _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        MeasureHead();
        if (_panel != null && _text != null) StyleExisting();   // pre-placed in the scene
        else BuildUI();                                         // fallback: build at runtime
        _panel.gameObject.SetActive(false);
    }

    // Apply the runtime look (Korean font, rounded translucent sprite, sort order) to a
    // bubble that was authored in the scene, so no UI GameObjects are created at play time.
    private void StyleExisting()
    {
        if (_canvas == null) _canvas = _panel.GetComponentInParent<Canvas>();
        if (_canvas != null) { _canvas.renderMode = RenderMode.ScreenSpaceOverlay; _canvas.sortingOrder = _sortingOrder; }
        if (_font != null) _text.font = _font;
        var img = _panel.GetComponent<Image>();
        if (img != null) { img.sprite = RoundedSprite(); img.type = Image.Type.Sliced; }
    }

    private void MeasureHead()
    {
        _headWorldY = 2f;
        if (_tiger != null)
        {
            var rends = _tiger.GetComponentsInChildren<Renderer>();
            if (rends.Length > 0)
            {
                var b = rends[0].bounds;
                foreach (var r in rends) b.Encapsulate(r.bounds);
                _headWorldY = Mathf.Max(0.5f, b.max.y - _tiger.position.y);
            }
        }
    }

    private void BuildUI()
    {
        var canvasGo = new GameObject("BubbleCanvas");
        canvasGo.transform.SetParent(transform, false);
        _canvas = canvasGo.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = _sortingOrder;   // render on top of every other canvas / 3D

        // rounded translucent background; auto-sizes to the wrapped text via layout
        var panelGo = new GameObject("Bubble", typeof(RectTransform), typeof(Image),
                                     typeof(HorizontalLayoutGroup), typeof(ContentSizeFitter));
        _panel = (RectTransform)panelGo.transform;
        _panel.SetParent(_canvas.transform, false);
        _panel.pivot = new Vector2(0.5f, 0f);   // anchored by its bottom-center to the head point

        var img = panelGo.GetComponent<Image>();
        img.color = s_Bubble;
        img.sprite = RoundedSprite();
        img.type = Image.Type.Sliced;
        img.raycastTarget = false;

        var lg = panelGo.GetComponent<HorizontalLayoutGroup>();
        lg.padding = new RectOffset(20, 20, 12, 14);
        lg.childControlWidth = true; lg.childControlHeight = true;
        lg.childForceExpandWidth = false; lg.childForceExpandHeight = false;

        var fit = panelGo.GetComponent<ContentSizeFitter>();
        fit.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        var txtGo = new GameObject("Text", typeof(RectTransform), typeof(Text));
        txtGo.transform.SetParent(_panel, false);
        _text = txtGo.GetComponent<Text>();
        _text.font = _font;
        _text.fontSize = 26;
        _text.color = Color.white;
        _text.alignment = TextAnchor.MiddleCenter;
        // we hard-wrap by chars ourselves, so let the generator size to the widest line
        _text.horizontalOverflow = HorizontalWrapMode.Overflow;
        _text.verticalOverflow = VerticalWrapMode.Overflow;
        _text.raycastTarget = false;
    }

    private void LateUpdate()
    {
        if (_tiger == null || _panel == null || !_panel.gameObject.activeSelf) return;
        if (_cam == null) _cam = Camera.main;
        if (_cam == null) return;

        Vector3 world = _tiger.position + Vector3.up * (_headWorldY + _headMargin);
        Vector3 sp = _cam.WorldToScreenPoint(world);
        if (sp.z < 0f) { _panel.position = new Vector3(-10000f, -10000f, 0f); return; }  // behind camera
        _panel.position = new Vector3(sp.x, sp.y, 0f);
    }

    /// <summary>Show a reply above Tiger's head for <paramref name="seconds"/>.</summary>
    public void Say(string text, float seconds = 5f)
    {
        if (_panel == null || string.IsNullOrEmpty(text)) return;
        _text.text = Wrap(text);
        _panel.gameObject.SetActive(true);
        LateUpdate();   // position immediately so it doesn't pop at the origin for a frame
        if (_hideCo != null) StopCoroutine(_hideCo);
        _hideCo = StartCoroutine(Hide(seconds));
    }

    private string Wrap(string text)
    {
        var sb = new System.Text.StringBuilder();
        int lineLen = 0;
        foreach (var ch in text)
        {
            if (ch == '\n') { sb.Append('\n'); lineLen = 0; continue; }
            if (lineLen >= _wrap && (ch == ' ' || lineLen >= _wrap + 6))
            { sb.Append('\n'); lineLen = 0; if (ch == ' ') continue; }
            sb.Append(ch); lineLen++;
        }
        return sb.ToString();
    }

    private IEnumerator Hide(float seconds)
    {
        yield return new WaitForSeconds(seconds);
        if (_panel != null) _panel.gameObject.SetActive(false);
    }

    // rounded-rect 9-slice sprite for the translucent bubble background
    private static Sprite RoundedSprite() => UiRuntimeAssets.RoundedSprite(18f);
}
