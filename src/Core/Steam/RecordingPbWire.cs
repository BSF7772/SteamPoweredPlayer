// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace SteamPoweredPlayer.Core.Steam;

/// <summary>
/// One protobuf field exactly as it was on disk. <see cref="Raw"/> is the complete encoded field (tag + payload), so re-serialising a
/// parsed message is byte-identical even for non-canonical varints and for fields whose meaning is unknown.
/// </summary>
public sealed class PbField
{
    public const int Varint = 0, Fixed64 = 1, LengthDelimited = 2, Fixed32 = 5;

    public required int Number { get; init; }
    public required int WireType { get; init; }
    public required ReadOnlyMemory<byte> Raw { get; init; }
    /// <summary>Value of a varint field (wire type 0), else 0.</summary>
    public ulong VarintValue { get; init; }
    /// <summary>Payload bytes: the length-delimited body, or the 4/8 bytes of a fixed field; empty for varints.</summary>
    public ReadOnlyMemory<byte> Payload { get; init; }

    /// <summary>Number from a varint, a fixed field, or a length-delimited decimal string (Steam ids are sometimes strings). Else null.</summary>
    public ulong? AsUInt64()
    {
        switch (WireType)
        {
            case Varint: return VarintValue;
            case Fixed64: return BinaryPrimitives.ReadUInt64LittleEndian(Payload.Span);
            case Fixed32: return BinaryPrimitives.ReadUInt32LittleEndian(Payload.Span);
            case LengthDelimited:
                var s = Encoding.UTF8.GetString(Payload.Span).Trim();
                return ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
            default: return null;
        }
    }

    public long? AsInt64()
    {
        if (WireType == LengthDelimited)
        {
            var s = Encoding.UTF8.GetString(Payload.Span).Trim();
            return long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l) ? l : null;
        }
        return AsUInt64() is { } u ? unchecked((long)u) : null;
    }

    /// <summary>UTF-8 text of a length-delimited field, or the decimal text of a numeric field (a timeline id may be either).</summary>
    public string? AsString() => WireType switch
    {
        LengthDelimited => Encoding.UTF8.GetString(Payload.Span),
        _ => AsUInt64()?.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>Length-delimited field parsed as a message; null when it is not one (wrong wire type or malformed).</summary>
    public PbMessage? AsMessage()
    {
        if (WireType != LengthDelimited) return null;
        var m = PbMessage.Parse(Payload);
        return m.Error is null ? m : null;
    }
}

/// <summary>
/// A schema-less protobuf message: ordered fields plus any undecodable <see cref="Trailing"/> bytes. Never throws on garbage; an
/// <see cref="Error"/> is recorded and the rest of the input is kept in <see cref="Trailing"/> so <see cref="Serialize"/> still
/// reproduces the input exactly.
/// </summary>
public sealed class PbMessage
{
    public static readonly PbMessage Empty = new([], ReadOnlyMemory<byte>.Empty, null);

    private PbMessage(IReadOnlyList<PbField> fields, ReadOnlyMemory<byte> trailing, string? error)
    {
        Fields = fields; Trailing = trailing; Error = error;
    }

    public IReadOnlyList<PbField> Fields { get; }
    public ReadOnlyMemory<byte> Trailing { get; }
    public string? Error { get; }

    public static PbMessage Parse(ReadOnlyMemory<byte> data)
    {
        var fields = new List<PbField>();
        int pos = 0;
        while (pos < data.Length)
        {
            int start = pos;
            string? err = null;
            if (!TryVarint(data.Span, ref pos, out var tag)) err = "truncated or overlong tag";
            else
            {
                ulong number = tag >> 3; int wt = (int)(tag & 7);
                if (number == 0 || number > 0x1FFFFFFF) err = "invalid field number";
                else
                {
                    switch (wt)
                    {
                        case PbField.Varint:
                            if (!TryVarint(data.Span, ref pos, out var v)) { err = "truncated varint"; break; }
                            fields.Add(new PbField { Number = (int)number, WireType = wt, Raw = data[start..pos], VarintValue = v });
                            break;
                        case PbField.Fixed64 or PbField.Fixed32:
                            int n = wt == PbField.Fixed64 ? 8 : 4;
                            if (data.Length - pos < n) { err = "truncated fixed field"; break; }
                            fields.Add(new PbField { Number = (int)number, WireType = wt, Raw = data[start..(pos + n)], Payload = data.Slice(pos, n) });
                            pos += n;
                            break;
                        case PbField.LengthDelimited:
                            if (!TryVarint(data.Span, ref pos, out var len) || len > (ulong)(data.Length - pos)) { err = "truncated length-delimited field"; break; }
                            fields.Add(new PbField { Number = (int)number, WireType = wt, Raw = data[start..(pos + (int)len)], Payload = data.Slice(pos, (int)len) });
                            pos += (int)len;
                            break;
                        default: err = $"unsupported wire type {wt}"; break;
                    }
                }
            }
            if (err is not null)
                return new(fields, data[start..], $"{err} at offset {start}");
        }
        return new(fields, ReadOnlyMemory<byte>.Empty, null);
    }

    private static bool TryVarint(ReadOnlySpan<byte> s, ref int pos, out ulong value)
    {
        value = 0;
        for (int shift = 0, i = pos; i < s.Length && shift < 70; i++, shift += 7)
        {
            byte b = s[i];
            if (shift == 63 && b > 1) return false;
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) { pos = i + 1; return true; }
        }
        return false;
    }

    public byte[] Serialize()
    {
        var ms = new MemoryStream();
        foreach (var f in Fields) ms.Write(f.Raw.Span);
        ms.Write(Trailing.Span);
        return ms.ToArray();
    }

    public PbField? First(int number)
    {
        foreach (var f in Fields) if (f.Number == number) return f;
        return null;
    }

    public IEnumerable<PbField> All(int number) => Fields.Where(f => f.Number == number);
}
