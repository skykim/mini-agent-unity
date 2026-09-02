// Runtime cloud tier for the hybrid agent: when the on-device model can't handle
// a request (refuses, or every call fails the schema gate), escalate to Ollama
// (OpenAI-compatible endpoint) for a real answer instead of a canned refusal.
//
// Uses the local Ollama daemon at localhost:11434, which proxies the cloud model
// gpt-oss:120b-cloud to ollama.com — auth is handled once by `ollama signin` at the
// daemon, so no per-request API key is needed. Coroutine + UnityWebRequest.
// NOTE: localhost is HTTP, so the player must allow insecure HTTP (set in
// AgentChatSceneSetup / GameViewCapture via PlayerSettings.insecureHttpOption).
using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace NeedleTest
{
    public static class AgentCloudClient
    {
        // sensible defaults; the endpoint + model are configurable on HomeAgentConnector
        public const string DefaultEndpoint = "http://localhost:11434/v1/chat/completions";
        public const string DefaultModel = "gpt-oss:120b-cloud";

        // No availability pre-check: if the local Ollama daemon isn't running the request
        // just fails fast and the caller falls back to the canned refusal, so always try.

        [Serializable] class Resp { public Choice[] choices; }
        [Serializable] class Choice { public Msg message; }
        [Serializable] class Msg { public string content; }

        static string J(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if (c == '\\' || c == '"') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < ' ') sb.Append($"\\u{(int)c:x4}");
                else sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>done(answer, elapsedMs). answer starts with "(cloud" on failure.
        /// endpoint/model configurable by the caller (HomeAgentConnector).</summary>
        public static IEnumerator Complete(string system, string user, string endpoint, string model,
                                           Action<string, long> done)
        {
            if (string.IsNullOrEmpty(endpoint)) endpoint = DefaultEndpoint;
            if (string.IsNullOrEmpty(model)) model = DefaultModel;
            var body = "{\"model\":\"" + J(model) + "\",\"messages\":[" +
                       "{\"role\":\"system\",\"content\":\"" + J(system) + "\"}," +
                       "{\"role\":\"user\",\"content\":\"" + J(user) + "\"}]," +
                       // reasoning_effort: gpt-oss thinks BEFORE answering and its thinking spends
                       // max_tokens; "low" keeps the answer inside the budget (message.content is
                       // what we parse — an all-reasoning reply would come back empty). Non-reasoning
                       // models ignore the field.
                       "\"temperature\":0.2,\"top_p\":0.95,\"max_tokens\":512,\"reasoning_effort\":\"low\",\"stream\":false}";

            var sw = System.Diagnostics.Stopwatch.StartNew();
            string answer = null;
            for (int attempt = 0; attempt < 2; attempt++)   // one retry on 5xx / transport
            {
                using var req = new UnityWebRequest(endpoint, "POST");
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = 60;
                yield return req.SendWebRequest();

                if (req.result == UnityWebRequest.Result.Success)
                {
                    try
                    {
                        var parsed = JsonUtility.FromJson<Resp>(req.downloadHandler.text);
                        if (parsed?.choices != null && parsed.choices.Length > 0)
                            answer = parsed.choices[0].message.content?.Trim();
                    }
                    catch (Exception e) { answer = "(cloud parse error: " + e.Message + ")"; }
                    break;
                }
                if (req.responseCode >= 400 && req.responseCode < 500)
                {
                    answer = $"(cloud error {req.responseCode}: {req.downloadHandler.text})";
                    break;
                }
                // transport failure (e.g. ollama not running) -> retry once, then give up
                if (attempt == 1)
                    answer = $"(cloud unavailable: {req.error})";
            }
            sw.Stop();
            done(answer ?? "(cloud unavailable)", sw.ElapsedMilliseconds);
        }
    }
}
