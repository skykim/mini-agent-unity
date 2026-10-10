using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The row of example commands above the input field. Tapping one puts its text in the input field and submits it;
/// the shuffle button deals a new random set. The buttons are disabled while the agent loads or answers.
/// </summary>
public sealed class CommandSuggestions : MonoBehaviour
{
    [SerializeField] HomeAgentConnector _connector;
    [Tooltip("Example buttons, left to right; each shows its command in its child Text.")]
    [SerializeField] Button[] _examples;
    [SerializeField] Button _shuffle;

    static readonly string[] s_Examples =
    {
        "Turn on the living room light",
        "Turn off the bedroom light",
        "Set the kitchen light to blue",
        "Turn on the bedroom light at 30%",
        "Turn on the living room TV",
        "Play some jazz",
        "Stop the music",
        "Set the volume to 60",
        "Start the vacuum",
        "What's the weather in Seoul?",
        "What time is it in New York?",
        "Where am I?",
        "거실 불 켜줘",
        "침실 불 꺼줘",
        "부엌 조명 초록색으로 바꿔줘",
        "거실 티비 켜줘",
        "컴퓨터 켜줘",
        "신나는 음악 틀어줘",
        "볼륨 30으로 줄여줘",
        "청소기 돌려줘",
        "거실 불 켜고 거실 티비 켜줘",
        "서울 날씨 어때?",
        "오늘 기분 어때?",
    };

    Text[] m_Labels;
    readonly HashSet<int> m_Shown = new();

    bool CanSubmit => _connector != null && _connector.IsReady && !_connector.IsBusy;

    void Awake()
    {
        // Same rounded look as the input field and send button (HomeAgentConnector.StyleUI).
        var rounded = UiRuntimeAssets.RoundedSprite(16f);
        m_Labels = new Text[_examples.Length];
        for (var i = 0; i < _examples.Length; i++)
        {
            m_Labels[i] = _examples[i].GetComponentInChildren<Text>();
            Round(_examples[i], rounded);
            var slot = i;
            _examples[i].onClick.AddListener(() => { if (CanSubmit) _connector.Submit(m_Labels[slot].text); });
        }
        Round(_shuffle, rounded);
        _shuffle.onClick.AddListener(Deal);
        Deal();
    }

    static void Round(Button button, Sprite sprite)
    {
        var image = button.GetComponent<Image>();
        image.sprite = sprite;
        image.type = Image.Type.Sliced;
    }

    /// <summary>Shows a new random set of examples, avoiding the ones on screen when the pool allows it.</summary>
    public void Deal()
    {
        var pool = new List<int>();
        for (var i = 0; i < s_Examples.Length; i++)
            if (!m_Shown.Contains(i)) pool.Add(i);
        if (pool.Count < m_Labels.Length)
            for (var i = 0; i < s_Examples.Length; i++)
                if (!pool.Contains(i)) pool.Add(i);
        m_Shown.Clear();
        for (var slot = 0; slot < m_Labels.Length; slot++)
        {
            var pick = Random.Range(slot, pool.Count);
            (pool[slot], pool[pick]) = (pool[pick], pool[slot]);
            m_Shown.Add(pool[slot]);
            m_Labels[slot].text = s_Examples[pool[slot]];
        }
    }

    void Update()
    {
        var enabled = CanSubmit;
        foreach (var button in _examples)
            if (button.interactable != enabled) button.interactable = enabled;
    }
}
