// SPDX-License-Identifier: GPL-3.0-or-later
// Golden values for the Kotlin port: FastEncoder's AudioSync.DetectAsync on a file, written as golden.json.
// Usage: GoldenCs <media> <duration seconds> <outdir>
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using FastEncoder.Core;

Ff.Locate();
var path = args[0];
var dur = double.Parse(args[1], CultureInfo.InvariantCulture);
Directory.CreateDirectory(args[2]);
var sw = Stopwatch.StartNew();
var r = await AudioSync.DetectAsync(path, 0, dur, null, CancellationToken.None);
var json = JsonSerializer.Serialize(new
{
    file = Path.GetFileName(path),
    audio_late = r.AudioLate,
    confidence = r.Confidence.ToString(),
    agreeing = r.Agreeing,
    consistent = r.Consistent,
    wall_ms = sw.ElapsedMilliseconds,
}, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(Path.Combine(args[2], "golden.json"), json);
Console.WriteLine(json);
