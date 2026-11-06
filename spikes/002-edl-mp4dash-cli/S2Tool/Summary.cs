// SPDX-License-Identifier: GPL-3.0-or-later
// Spike S2: pool numeric results of repeated scenario runs (files named <scenario>-<variant>-r<N>.json) into median / min / max / n.
using System.Text.Json;
using System.Text.RegularExpressions;

namespace S2Tool;

public static class Summary
{
    // summary <dir> [<dir> ...] -> <dir>/summary.json  (key = scenario | variant | flattened json path)
    public static int Run(string[] a)
    {
        foreach (var dir in a.Skip(1))
        {
            var pooled = new SortedDictionary<string, List<double>>();
            var perRep = new Dictionary<string, SortedDictionary<int, List<double>>>();
            var rx = new Regex(@"^(?<scn>[a-z]+)-(?<var>.+)-r(?<rep>\d+)\.json$");
            foreach (var file in Directory.EnumerateFiles(dir, "*-r*.json"))
            {
                var m = rx.Match(Path.GetFileName(file));
                if (!m.Success) continue;
                int rep = int.Parse(m.Groups["rep"].Value);
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                Walk(doc.RootElement, $"{m.Groups["scn"].Value} | {m.Groups["var"].Value} | ", (k, v) =>
                {
                    if (double.IsNaN(v) || double.IsInfinity(v)) return;
                    if (!pooled.TryGetValue(k, out var l)) pooled[k] = l = new();
                    l.Add(v);
                    if (!perRep.TryGetValue(k, out var d)) perRep[k] = d = new();
                    if (!d.TryGetValue(rep, out var rl)) d[rep] = rl = new();
                    rl.Add(v);
                });
            }
            var rows = pooled.Select(kv =>
            {
                var s = kv.Value.OrderBy(x => x).ToArray();
                double med = s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
                var reps = perRep[kv.Key].ToDictionary(r => $"r{r.Key}", r => Math.Round(Med(r.Value), 2));
                return new { key = kv.Key, n = s.Length, median = Math.Round(med, 2), min = Math.Round(s[0], 2), max = Math.Round(s[^1], 2), perRepMedian = reps };
            });
            File.WriteAllText(Path.Combine(dir, "summary.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"{dir}: {pooled.Count} metrics");
        }
        return 0;
    }

    static double Med(List<double> l) { var s = l.OrderBy(x => x).ToArray(); return s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2; }

    static void Walk(JsonElement e, string path, Action<string, double> emit)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Number: emit(path.TrimEnd(' ', '.'), e.GetDouble()); break;
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Name is "utc" or "FAILED" or "extra" or "videoParams" or "audioParams" or "track-list") continue;
                    Walk(p.Value, path.EndsWith(" | ") ? path + p.Name : path + "." + p.Name, emit);
                }
                break;
            case JsonValueKind.Array:
                foreach (var it in e.EnumerateArray())
                {
                    string seg = "[]";
                    if (it.ValueKind == JsonValueKind.Object)
                    {
                        if (it.TryGetProperty("speed", out var sp) && sp.ValueKind == JsonValueKind.Number) seg = $"[speed={sp.GetDouble()}]";
                        else if (it.TryGetProperty("mode", out var md) && md.ValueKind == JsonValueKind.String) seg = $"[{md.GetString()}]";
                        else if (it.TryGetProperty("strategy", out var st) && st.ValueKind == JsonValueKind.String) seg = $"[{st.GetString()}]";
                    }
                    Walk(it, path + seg, emit);
                }
                break;
        }
    }
}
