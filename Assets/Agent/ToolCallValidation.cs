// Runtime whitelist + schema validation for on-device tool calls.
//
// Sits between the model's raw output and the executor: a call is executed ONLY
// if its function name is registered, every argument exists in the schema, all
// required arguments are present, and every value parses/checks against its
// declared type, range, and enum. This is the hard floor under the model: fine-tuned
// SLMs can emit nonexistent functions (call:multiply), nonexistent arguments, and
// out-of-range values, and all of those are rejected here.
//
// Pure C# (no Unity dependency), so it can be unit-tested outside the engine.
// Input format: FunctionGemma flat calls, call:NAME{k:v,k:<escape>free text<escape>}
using System;
using System.Collections.Generic;
using System.Globalization;

namespace NeedleTest
{
    public sealed class ToolParamSpec
    {
        public string Name;
        public string Type = "string";       // "string" | "integer" | "number"
        public bool Required;
        public double? Min, Max;
        public string[] Enum;

        public ToolParamSpec(string name, string type, bool required = false,
                             double? min = null, double? max = null, string[] enumValues = null)
        { Name = name; Type = type; Required = required; Min = min; Max = max; Enum = enumValues; }
    }

    public sealed class ToolSpec
    {
        public string Name;
        public ToolParamSpec[] Params;
        public ToolSpec(string name, params ToolParamSpec[] ps) { Name = name; Params = ps; }
    }

    public struct ParsedCall
    {
        public string Name;
        public List<KeyValuePair<string, string>> Args;
        public string Raw;
    }

    public sealed class ValidationResult
    {
        public bool Ok;
        public string Reason;      // set when rejected
    }

    public sealed class ToolCallValidator
    {
        readonly Dictionary<string, ToolSpec> m_Tools = new(StringComparer.Ordinal);

        public ToolCallValidator(IEnumerable<ToolSpec> tools)
        {
            foreach (var t in tools) m_Tools[t.Name] = t;
        }

        public ValidationResult Validate(ParsedCall call) =>
            Validate(call.Name, call.Args);

        public ValidationResult Validate(string name, IEnumerable<KeyValuePair<string, string>> args)
        {
            if (string.IsNullOrEmpty(name) || !m_Tools.TryGetValue(name, out var spec))
                return Reject($"unknown function '{name}'");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kv in args)
            {
                ToolParamSpec p = null;
                foreach (var q in spec.Params)
                    if (q.Name == kv.Key) { p = q; break; }
                if (p == null)
                    return Reject($"unknown argument '{kv.Key}' for {name}");
                if (!seen.Add(kv.Key))
                    return Reject($"duplicate argument '{kv.Key}'");

                string v = kv.Value;
                switch (p.Type)
                {
                    case "integer":
                        if (!long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var iv))
                            return Reject($"argument '{kv.Key}' expects an integer, got '{v}'");
                        if (p.Min.HasValue && iv < p.Min.Value || p.Max.HasValue && iv > p.Max.Value)
                            return Reject($"argument '{kv.Key}'={iv} outside [{p.Min},{p.Max}]");
                        break;
                    case "number":
                        if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var dv))
                            return Reject($"argument '{kv.Key}' expects a number, got '{v}'");
                        if (p.Min.HasValue && dv < p.Min.Value || p.Max.HasValue && dv > p.Max.Value)
                            return Reject($"argument '{kv.Key}'={dv} outside [{p.Min},{p.Max}]");
                        break;
                    default: // string
                        if (string.IsNullOrWhiteSpace(v))
                            return Reject($"argument '{kv.Key}' is empty");
                        if (p.Enum != null && Array.IndexOf(p.Enum, v) < 0)
                            return Reject($"argument '{kv.Key}'='{v}' not in enum");
                        break;
                }
            }

            foreach (var q in spec.Params)
                if (q.Required && !seen.Contains(q.Name))
                    return Reject($"missing required argument '{q.Name}'");

            return new ValidationResult { Ok = true };
        }

        static ValidationResult Reject(string reason) =>
            new ValidationResult { Ok = false, Reason = reason };
    }

    /// <summary>Parses every FunctionGemma-format call in a raw generation.</summary>
    public static class FgCallParser
    {
        const string Escape = "<escape>";

        public static List<ParsedCall> ParseAll(string raw)
        {
            var calls = new List<ParsedCall>();
            if (string.IsNullOrEmpty(raw)) return calls;
            int i = 0;
            while ((i = raw.IndexOf("call:", i, StringComparison.Ordinal)) >= 0)
            {
                int n0 = i + 5, n1 = n0;
                while (n1 < raw.Length && (char.IsLetterOrDigit(raw[n1]) || raw[n1] == '_')) n1++;
                string name = raw.Substring(n0, n1 - n0);
                int open = n1 < raw.Length && raw[n1] == '{' ? n1 : -1;
                if (open < 0) { i = n1; continue; }
                int close = FindArgsEnd(raw, open);
                if (close < 0) break;
                string body = raw.Substring(open + 1, close - open - 1);
                calls.Add(new ParsedCall
                {
                    Name = name,
                    Args = ParseArgs(body),
                    Raw = raw.Substring(i, close - i + 1),
                });
                i = close + 1;
            }
            return calls;
        }

        // end of the args block: first '}' not inside an <escape>...<escape> span
        static int FindArgsEnd(string s, int open)
        {
            bool esc = false;
            for (int j = open + 1; j < s.Length; j++)
            {
                if (string.CompareOrdinal(s, j, Escape, 0, Escape.Length) == 0)
                { esc = !esc; j += Escape.Length - 1; continue; }
                if (!esc && s[j] == '}') return j;
            }
            return -1;
        }

        static List<KeyValuePair<string, string>> ParseArgs(string body)
        {
            var args = new List<KeyValuePair<string, string>>();
            int i = 0;
            while (i < body.Length)
            {
                int colon = body.IndexOf(':', i);
                if (colon < 0) break;
                string key = body.Substring(i, colon - i).Trim();
                i = colon + 1;
                string val;
                if (i + Escape.Length <= body.Length && string.CompareOrdinal(body, i, Escape, 0, Escape.Length) == 0)
                {
                    int vs = i + Escape.Length;
                    int ve = body.IndexOf(Escape, vs, StringComparison.Ordinal);
                    if (ve < 0) { val = body.Substring(vs); i = body.Length; }
                    else { val = body.Substring(vs, ve - vs); i = ve + Escape.Length; }
                    int comma = body.IndexOf(',', Math.Min(i, body.Length));
                    i = comma < 0 ? body.Length : comma + 1;
                }
                else
                {
                    int comma = body.IndexOf(',', i);
                    if (comma < 0) { val = body.Substring(i).Trim(); i = body.Length; }
                    else { val = body.Substring(i, comma - i).Trim(); i = comma + 1; }
                }
                if (key.Length > 0)
                    args.Add(new KeyValuePair<string, string>(key, val));
            }
            return args;
        }
    }

    /// <summary>The smart-home tool registry used by the demo/agent.</summary>
    public static class SmartHomeTools
    {
        // 16 tools. Rooms apply ONLY to lights; TV / computer / vacuum / speaker are single
        // devices with no room. Room-less device tools tolerate a stray `room` arg (the
        // connector ignores it). Stateless after construction, built once and shared.
        public static ToolCallValidator Validator { get; } = new(new[]
        {
            new ToolSpec("turn_on_light",
                new ToolParamSpec("room", "string", required: true),
                new ToolParamSpec("brightness", "integer", min: 0, max: 100)),
            new ToolSpec("turn_off_light",
                new ToolParamSpec("room", "string", required: true)),
            new ToolSpec("set_light_color",
                new ToolParamSpec("room", "string", required: true),
                new ToolParamSpec("color", "string", required: true, enumValues: new[]
                    { "red", "orange", "yellow", "green", "blue",
                      "purple", "pink", "white", "warm", "cool" })),

            // single-device tools — NO room in the schema (room tolerated + ignored)
            new ToolSpec("turn_on_tv",       new ToolParamSpec("room", "string")),
            new ToolSpec("turn_off_tv",      new ToolParamSpec("room", "string")),
            new ToolSpec("turn_on_computer", new ToolParamSpec("room", "string")),
            new ToolSpec("turn_off_computer",new ToolParamSpec("room", "string")),
            new ToolSpec("start_vacuum",     new ToolParamSpec("room", "string")),

            // music / volume target the single speaker — no room. genre in {rock,jazz,lofi}
            // (default jazz). room/volume kept optional only to tolerate stray args.
            new ToolSpec("play_music",
                new ToolParamSpec("room", "string"),
                new ToolParamSpec("volume", "integer", min: 0, max: 100),
                new ToolParamSpec("genre", "string", enumValues: new[] { "rock", "jazz", "lofi" })),
            new ToolSpec("stop_music",
                new ToolParamSpec("room", "string")),
            new ToolSpec("set_volume",
                new ToolParamSpec("room", "string"),
                new ToolParamSpec("volume", "integer", required: true, min: 0, max: 100)),
            new ToolSpec("get_volume",
                new ToolParamSpec("room", "string")),

            // info tools
            new ToolSpec("get_weather",
                new ToolParamSpec("city", "string", required: true)),
            new ToolSpec("get_time",
                new ToolParamSpec("city", "string")),
            new ToolSpec("get_location"),
            new ToolSpec("web_search",
                new ToolParamSpec("query", "string", required: true)),
        });
    }
}
