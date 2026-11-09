// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.Json;
using SteamPoweredPlayer.Core.Library;

namespace SteamPoweredPlayer.Tests.Library;

/// <summary><c>fmp4-cases.json</c> is shared with the Android tests: the C# and Kotlin parsers must give the same answer on these exact bytes.</summary>
public sealed class Fmp4GoldenTests
{
    private static readonly JsonElement Root = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "golden", "fmp4-cases.json"))).RootElement;

    private static IEnumerable<JsonElement> Cases(string name) => Root.GetProperty(name).EnumerateArray();
    private static byte[] Hex(JsonElement c, string name) => Convert.FromHexString(c.GetProperty(name).GetString()!);
    private static string? Text(JsonElement c, string name) => c.GetProperty(name).ValueKind == JsonValueKind.Null ? null : c.GetProperty(name).ToString();

    [Fact]
    public void Head_checks()
    {
        foreach (var c in Cases("head")) Assert.Equal(Text(c, "result"), ScanHeads.Classify(Hex(c, "hex")).ToString());
    }

    [Fact]
    public void First_fragments()
    {
        foreach (var c in Cases("fragments"))
        {
            var bytes = Hex(c, "hex");
            var r = FirstFragment.Find(bytes);
            Assert.Equal(Text(c, "status"), r.Status.ToString());
            Assert.Equal(c.GetProperty("offset").GetInt64(), r.Offset);
            Assert.Equal(c.GetProperty("length").GetInt64(), r.Length);
            Assert.Equal(c.GetProperty("requiredBytes").GetInt64(), r.RequiredBytes);
            Assert.Equal(Text(c, "tfdt"), FirstFragment.BaseMediaDecodeTime(bytes)?.ToString(CultureInfo.InvariantCulture));
        }
    }

    [Fact]
    public void Timescales()
    {
        foreach (var c in Cases("inits")) Assert.Equal(Text(c, "timescale"), FirstFragment.Timescale(Hex(c, "hex"))?.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Tfdt_seconds()
    {
        foreach (var c in Cases("tfdts"))
        {
            var tfdt = FirstFragment.BaseMediaDecodeTime(Hex(c, "head"));
            var scale = FirstFragment.Timescale(Hex(c, "init"));
            double? seconds = tfdt is { } t && scale is { } s ? t / (double)s : null;
            Assert.Equal(c.GetProperty("seconds").ValueKind == JsonValueKind.Null ? null : c.GetProperty("seconds").GetDouble(), seconds);
        }
    }

    [Fact]
    public void Axis_shifts()
    {
        foreach (var c in Cases("axisShift"))
            Assert.Equal(Number(c, "shift"), SessionMedia.AxisShiftFor(Number(c, "tfdtSeconds"), c.GetProperty("chunk").GetInt32(), Number(c, "segmentSeconds")));
    }

    /// <summary>Some cases hold NaN or an infinity, which JSON writes as text.</summary>
    private static double Number(JsonElement c, string name) => double.Parse(c.GetProperty(name).ToString(), CultureInfo.InvariantCulture);
}
