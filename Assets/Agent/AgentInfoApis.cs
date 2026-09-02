// Runtime (coroutine/UnityWebRequest) executors for the info tools —
// the play-mode counterpart of the editor-only DeviceApis.
//   get_weather -> open-meteo geocoding + current weather (key-less)
//   get_time    -> geocoded IANA timezone + TimeZoneInfo, or local time
//   get_location-> ip-api.com (IP geolocation stand-in for GPS on desktop)
using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace NeedleTest
{
    public static class AgentInfoApis
    {
        // open-meteo geocoding matches English names only — normalize common
        // Korean city names before the lookup (fallback: raw string).
        static readonly System.Collections.Generic.Dictionary<string, string> CityAlias = new()
        {
            ["서울"] = "Seoul", ["부산"] = "Busan", ["제주"] = "Jeju", ["인천"] = "Incheon",
            ["대전"] = "Daejeon", ["광주"] = "Gwangju", ["대구"] = "Daegu", ["울산"] = "Ulsan",
            ["뉴욕"] = "New York", ["도쿄"] = "Tokyo", ["동경"] = "Tokyo", ["파리"] = "Paris",
            ["런던"] = "London", ["베를린"] = "Berlin", ["시드니"] = "Sydney",
            ["베이징"] = "Beijing", ["북경"] = "Beijing", ["상하이"] = "Shanghai",
            ["방콕"] = "Bangkok", ["싱가포르"] = "Singapore", ["샌프란시스코"] = "San Francisco",
            ["로스앤젤레스"] = "Los Angeles", ["시카고"] = "Chicago", ["하노이"] = "Hanoi",
        };

        // Common English misspellings -> canonical name. The model is trained to correct
        // typos in the city arg, but this is a runtime safety net (matched case-insensitively).
        static readonly System.Collections.Generic.Dictionary<string, string> CityTypo = new(StringComparer.OrdinalIgnoreCase)
        {
            ["seuol"] = "Seoul", ["seoal"] = "Seoul", ["soul"] = "Seoul",
            ["tokio"] = "Tokyo", ["tokoyo"] = "Tokyo", ["newyork"] = "New York",
            ["new yor"] = "New York", ["londun"] = "London", ["londen"] = "London",
            ["parris"] = "Paris", ["beijin"] = "Beijing", ["bejing"] = "Beijing",
            ["shanghi"] = "Shanghai", ["singapor"] = "Singapore", ["bangcock"] = "Bangkok",
            ["sydeny"] = "Sydney", ["berln"] = "Berlin", ["losangeles"] = "Los Angeles",
        };

        public static string NormalizeCity(string city)
        {
            if (string.IsNullOrEmpty(city)) return city;
            var key = city.Trim();
            if (CityAlias.TryGetValue(key, out var en)) return en;
            if (CityTypo.TryGetValue(key, out var fix)) return fix;
            return key;
        }

        [Serializable] class GeoResp { public GeoHit[] results; }
        [Serializable] class GeoHit
        { public float latitude, longitude; public long population; public string name, timezone, country; }
        [Serializable] class WxResp { public Wx current_weather; }
        [Serializable] class Wx { public float temperature, windspeed; public int weathercode; }
        [Serializable] class IpResp { public string city, regionName, country, timezone; }
        [Serializable] class DdgResp { public string AbstractText, Answer, Definition, Heading; }

        static IEnumerator Get(string url, Action<string> ok, Action<string> fail)
        {
            using var req = UnityWebRequest.Get(url);
            req.timeout = 20;
            yield return req.SendWebRequest();
            if (req.result == UnityWebRequest.Result.Success)
            {
                // ok callbacks JsonUtility-parse the payload; an API error page / malformed
                // JSON must degrade to the caller's fail answer, not kill the coroutine chain.
                try { ok(req.downloadHandler.text); }
                catch (Exception e) { fail("unexpected response: " + e.Message); }
            }
            else fail(req.error);
        }

        static IEnumerator Geocode(string city, Action<GeoHit> done)
        {
            // language=en + pick-most-populous: language=ko silently drops the real
            // "New York" from the ranking (top hit becomes York, Nebraska)
            GeoHit hit = null;
            yield return Get(
                "https://geocoding-api.open-meteo.com/v1/search?count=5&language=en&name=" +
                Uri.EscapeDataString(NormalizeCity(city)),
                t =>
                {
                    var g = JsonUtility.FromJson<GeoResp>(t);
                    if (g?.results == null) return;
                    foreach (var r in g.results)
                        if (hit == null || r.population > hit.population) hit = r;
                },
                _ => { });
            done(hit);
        }

        static string WxDesc(int code) => code switch
        {
            0 => "clear", 1 => "mostly clear", 2 => "partly cloudy", 3 => "overcast",
            45 or 48 => "foggy", >= 51 and <= 67 => "rain", >= 71 and <= 86 => "snow",
            >= 95 => "thunderstorm", _ => "precipitation",
        };

        public static IEnumerator GetWeather(string city, Action<string> done)
        {
            GeoHit hit = null;
            yield return Geocode(city, h => hit = h);
            if (hit == null) { done($"Couldn't find the city '{city}'."); yield break; }
            string answer = "(weather API error)";
            yield return Get(
                "https://api.open-meteo.com/v1/forecast?current_weather=true" +
                $"&latitude={hit.latitude}&longitude={hit.longitude}",
                t =>
                {
                    var w = JsonUtility.FromJson<WxResp>(t).current_weather;
                    answer = $"It's currently {w.temperature:F1}°C in {city}, " +
                             $"{WxDesc(w.weathercode)}, wind {w.windspeed:F0} km/h.";
                },
                e => answer = "(weather API error: " + e + ")");
            done(answer);
        }

        public static IEnumerator GetTime(string cityOrNull, Action<string> done)
        {
            if (string.IsNullOrEmpty(cityOrNull))
            {
                // no city -> use the device's current city + timezone via IP (city level)
                string local = null;
                yield return Get("http://ip-api.com/json",
                    t =>
                    {
                        var r = JsonUtility.FromJson<IpResp>(t);
                        try
                        {
                            var tz = TimeZoneInfo.FindSystemTimeZoneById(r.timezone);
                            var tt = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
                            local = $"It's currently {tt:HH:mm} in {r.city}.";
                        }
                        catch { local = $"It's currently {DateTime.Now:HH:mm} in {r.city}."; }
                    },
                    _ => { });
                done(local ?? $"It's currently {DateTime.Now:HH:mm}.");
                yield break;
            }
            GeoHit hit = null;
            yield return Geocode(cityOrNull, h => hit = h);
            if (hit == null) { done($"Couldn't find the city '{cityOrNull}'."); yield break; }
            string answer;
            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById(hit.timezone);
                var t = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
                answer = $"It's currently {t:HH:mm} in {cityOrNull}. ({hit.timezone})";
            }
            catch (Exception e) { answer = "(timezone error: " + e.Message + ")"; }
            done(answer);
        }

        public static IEnumerator GetLocation(Action<string> done)
        {
            string answer = "(location API error)";
            yield return Get("http://ip-api.com/json",
                t =>
                {
                    var r = JsonUtility.FromJson<IpResp>(t);
                    answer = $"You're near {r.city}, {r.regionName}, {r.country}. (based on IP)";
                },
                e => answer = "(location API error: " + e + ")");
            done(answer);
        }

        // web_search RETRIEVAL: query DuckDuckGo (key-less Instant Answer API) and return
        // a compact CONTEXT blob (abstract/answer/definition + a few related-topic snippets),
        // NOT a final answer. The connector then feeds this context to the cloud LLM to
        // SYNTHESIZE a grounded answer (retrieve-then-answer / RAG). Empty string = nothing found.
        public static IEnumerator WebSearchRetrieve(string query, Action<string> done)
        {
            if (string.IsNullOrWhiteSpace(query)) { done(""); yield break; }
            string context = "";
            yield return Get(
                "https://api.duckduckgo.com/?format=json&no_html=1&skip_disambig=1&q=" +
                Uri.EscapeDataString(query),
                t => context = ExtractDdg(t),
                _ => context = "");
            done(context);
        }

        // Build a short context from a DuckDuckGo Instant Answer JSON: the top abstract plus
        // up to five related-topic snippets. JsonUtility can't parse the heterogeneous
        // RelatedTopics array, so the snippets are pulled by scanning for "Text":"..." values.
        static string ExtractDdg(string json)
        {
            var sb = new StringBuilder();
            var d = JsonUtility.FromJson<DdgResp>(json);
            if (d != null)
            {
                if (!string.IsNullOrEmpty(d.AbstractText)) sb.Append("- ").AppendLine(d.AbstractText);
                else if (!string.IsNullOrEmpty(d.Answer)) sb.Append("- ").AppendLine(d.Answer);
                else if (!string.IsNullOrEmpty(d.Definition)) sb.Append("- ").AppendLine(d.Definition);
            }
            int count = 0, i = 0;
            while (count < 5 && (i = json.IndexOf("\"Text\":\"", i, StringComparison.Ordinal)) >= 0)
            {
                i += 8;
                var val = new StringBuilder();
                while (i < json.Length && json[i] != '"')
                {
                    if (json[i] == '\\' && i + 1 < json.Length) { val.Append(json[i + 1]); i += 2; }
                    else { val.Append(json[i]); i++; }
                }
                var s = val.ToString().Trim();
                if (s.Length > 0) { sb.Append("- ").AppendLine(s); count++; }
            }
            var ctx = sb.ToString().Trim();
            return ctx.Length > 1500 ? ctx.Substring(0, 1500) : ctx;
        }
    }
}
