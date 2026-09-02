using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Drives a scripted list of commands through <see cref="HomeAgentConnector"/>, one at
/// a time. Put this on a "Test" GameObject in the scene, fill the <see cref="_commands"/>
/// list in the Inspector (executed top to bottom), and set the timings. When the scene
/// starts it waits <see cref="_startDelay"/> seconds, then submits each command, waiting
/// <see cref="_interval"/> seconds between them (and for the agent to finish each turn).
/// The agent logs every turn (incl. the harvest log), so progress is visible there.
/// </summary>
public class MiniAgentTestRunner : MonoBehaviour
{
    [Header("Timing")]
    [Tooltip("Seconds to wait after the scene starts before the first command runs.")]
    [Min(0f)]
    [SerializeField] private float _startDelay = 5f;

    [Tooltip("Seconds to wait between commands (each command also waits for the agent to finish).")]
    [Min(0f)]
    [SerializeField] private float _interval = 5f;

    [Tooltip("Run the command list automatically when the scene starts.")]
    [SerializeField] private bool _autoRun = true;

    [Header("Commands (run in order, top to bottom)")]
    [SerializeField]
    private List<string> _commands = new List<string>
    {
        "마루 불 켜줘",
        "부엌 불 켜줘",
        "티비 켜줘",
        "마루 스피커 켜줘",
        "볼륨 30으로 해줘",
        "락 틀어줘",
        "청소기 돌려줘",
        "거실 불 끄고, 거실 티비 꺼줘",
        "서울 날씨 어때?",
        "지금 몇시야?",
        "안방 컴퓨터 켜줘",
        "오늘 기분 어때?"
    };

    private IEnumerator Start()
    {
        if (!_autoRun) yield break;

        // Wait the fixed delay from scene start, then make sure the agent finished warmup
        // (submitting before it is ready would be ignored).
        yield return new WaitForSeconds(_startDelay);

        var conn = FindAnyObjectByType<HomeAgentConnector>();
        while (conn == null || !conn.IsReady)
        {
            if (conn == null) conn = FindAnyObjectByType<HomeAgentConnector>();
            yield return null;
        }

        yield return Run(conn);
    }

    /// <summary>Submit every command in order. Also callable manually (e.g. from a button).</summary>
    public IEnumerator Run(HomeAgentConnector conn)
    {
        if (conn == null) conn = FindAnyObjectByType<HomeAgentConnector>();
        if (conn == null) { Debug.LogWarning("[TESTRUNNER] no HomeAgentConnector in scene"); yield break; }

        for (int i = 0; i < _commands.Count; i++)
        {
            var cmd = _commands[i];
            if (string.IsNullOrWhiteSpace(cmd)) continue;

            while (conn.IsBusy) yield return null;
            Debug.Log($"[TESTRUNNER] {i + 1}/{_commands.Count}  {cmd}");
            conn.Submit(cmd);

            // let the turn start, then hold for the interval
            yield return null;
            yield return new WaitForSeconds(_interval);
        }

        while (conn.IsBusy) yield return null;
        Debug.Log("[TESTRUNNER] DONE");
    }
}
