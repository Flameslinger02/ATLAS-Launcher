using System.Text;
using System.Text.RegularExpressions;

namespace Atlas.Core.Services;

/// <summary>
/// Extracts a mission's required-addon list (<c>addOns[]</c>/<c>addOnsAuto[]</c> from <c>mission.sqm</c>).
/// Accepts either an unpacked mission folder or a packed <c>.pbo</c>, and handles every on-disk shape of
/// the sqm: plain text, an LZSS-compressed PBO entry, and binarized (raP) files.
/// </summary>
internal static partial class MissionSqmReader
{
    public static List<string> ReadAddOns(string missionPath)
    {
        byte[] sqm;
        if (Directory.Exists(missionPath))
        {
            var file = Path.Combine(missionPath, "mission.sqm");
            if (!File.Exists(file))
                throw new InvalidOperationException("No mission.sqm found in the mission folder.");
            sqm = File.ReadAllBytes(file);
        }
        else if (File.Exists(missionPath))
        {
            sqm = ExtractPboEntry(missionPath, "mission.sqm")
                  ?? throw new InvalidOperationException("No mission.sqm entry found inside the PBO.");
        }
        else
        {
            throw new InvalidOperationException($"Mission not found on disk: {missionPath}");
        }

        try
        {
            return IsBinarized(sqm) ? ParseBinarizedAddOns(sqm) : ParseTextAddOns(sqm);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            throw new InvalidOperationException("mission.sqm is malformed (unexpected end of data).", ex);
        }
    }

    // ----- PBO container -----

    /// <summary>Reads one entry's bytes out of a PBO (header walk + sequential data blocks),
    /// LZSS-decompressing it when stored with the Cprs packing method. Returns null if absent.</summary>
    private static byte[]? ExtractPboEntry(string pboPath, string entryFileName)
    {
        const uint MethodVers = 0x56657273;  // product/prefix header entry ("Vers")
        const uint MethodCprs = 0x43707273;  // LZSS-compressed entry ("Cprs")
        const int MaxEntries = 100_000;

        using var fs = File.OpenRead(pboPath);
        using var r = new BinaryReader(fs);

        var entries = new List<(string Name, uint Method, uint OriginalSize, uint DataSize)>();
        while (true)
        {
            if (entries.Count > MaxEntries)
                throw new InvalidOperationException("Not a valid PBO file (runaway header).");

            var name = ReadAsciiz(r);
            var method = r.ReadUInt32();
            var originalSize = r.ReadUInt32();
            r.ReadUInt32();                          // reserved
            r.ReadUInt32();                          // timestamp
            var dataSize = r.ReadUInt32();

            if (name.Length == 0)
            {
                if (method == MethodVers)
                {
                    // Product entry: null-terminated key/value property pairs, ended by an empty key.
                    while (ReadAsciiz(r).Length != 0) ReadAsciiz(r);
                    continue;
                }
                break;                               // empty non-Vers entry terminates the header
            }
            entries.Add((name, method, originalSize, dataSize));
        }

        // Data blocks follow the header back-to-back in entry order.
        var offset = fs.Position;
        foreach (var e in entries)
        {
            if (LastPathSegment(e.Name).Equals(entryFileName, StringComparison.OrdinalIgnoreCase))
            {
                fs.Position = offset;
                var raw = r.ReadBytes(checked((int)e.DataSize));
                return e.Method == MethodCprs
                    ? LzssDecompress(raw, checked((int)e.OriginalSize))
                    : raw;
            }
            offset += e.DataSize;
        }
        return null;
    }

    private static string LastPathSegment(string entryName)
    {
        var i = entryName.LastIndexOfAny(new[] { '\\', '/' });
        return i >= 0 ? entryName[(i + 1)..] : entryName;
    }

    private static string ReadAsciiz(BinaryReader r)
    {
        var bytes = new List<byte>();
        byte b;
        while ((b = r.ReadByte()) != 0)
        {
            bytes.Add(b);
            if (bytes.Count > 512)
                throw new InvalidOperationException("Not a valid PBO file (unterminated header string).");
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary>Bohemia's LZSS variant: flag byte (LSB first; 1 = literal), back-references are two bytes
    /// b1,b2 → distance ((b2 &amp; 0xF0) &lt;&lt; 4) + b1, length (b2 &amp; 0x0F) + 3; references reaching
    /// before the start of the output are read as spaces (0x20). A 4-byte additive checksum trails the
    /// stream and is simply never consumed once the target length is reached.</summary>
    private static byte[] LzssDecompress(byte[] input, int targetLength)
    {
        var output = new byte[targetLength];
        int outPos = 0, inPos = 0;
        while (outPos < targetLength && inPos < input.Length)
        {
            int flags = input[inPos++];
            for (var bit = 0; bit < 8 && outPos < targetLength && inPos < input.Length; bit++, flags >>= 1)
            {
                if ((flags & 1) != 0)
                {
                    output[outPos++] = input[inPos++];
                }
                else
                {
                    if (inPos + 1 >= input.Length) return output;
                    int b1 = input[inPos++], b2 = input[inPos++];
                    var rpos = outPos - (((b2 & 0xF0) << 4) + b1);
                    var rlen = (b2 & 0x0F) + 3;
                    for (var i = 0; i < rlen && outPos < targetLength; i++, rpos++)
                        output[outPos++] = rpos < 0 ? (byte)0x20 : output[rpos];
                }
            }
        }
        return output;
    }

    // ----- mission.sqm: plain text -----

    private static List<string> ParseTextAddOns(byte[] sqm)
    {
        var text = Encoding.UTF8.GetString(sqm);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match block in AddOnsArrayRegex().Matches(text))
            foreach (Match s in QuotedStringRegex().Matches(block.Groups[1].Value))
                if (seen.Add(s.Groups[1].Value))
                    result.Add(s.Groups[1].Value);
        return result;
    }

    [GeneratedRegex(@"\baddOns(?:Auto)?\[\]\s*=\s*\{([^}]*)\}", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AddOnsArrayRegex();

    [GeneratedRegex("\"([^\"]+)\"")]
    private static partial Regex QuotedStringRegex();

    // ----- mission.sqm: binarized (raP) -----

    private static bool IsBinarized(byte[] d) =>
        d.Length > 16 && d[0] == 0 && d[1] == (byte)'r' && d[2] == (byte)'a' && d[3] == (byte)'P';

    /// <summary>Walks only the ROOT class of a binarized config — <c>addOns[]</c> lives at the root of
    /// mission.sqm — skipping child classes (stored as name + absolute offset). On any unknown token the
    /// walk stops and returns what was collected (addOns sits near the top, after <c>version</c>).</summary>
    private static List<string> ParseBinarizedAddOns(byte[] d)
    {
        var pos = 16;                                // \0raP + uint32 0 + uint32 8 + uint32 enum-offset
        ReadAsciizAt(d, ref pos);                    // root inherited-class name (empty)
        var count = ReadCompressedInt(d, ref pos);

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Collect(string s) { if (seen.Add(s)) result.Add(s); }

        for (var i = 0; i < count; i++)
        {
            switch (d[pos++])
            {
                case 0:                              // class: name + absolute offset of its body
                    ReadAsciizAt(d, ref pos);
                    pos += 4;
                    break;
                case 1:                              // scalar: subtype, name, payload
                {
                    var sub = d[pos++];
                    ReadAsciizAt(d, ref pos);
                    if (sub == 0) ReadAsciizAt(d, ref pos);         // string
                    else if (sub is 1 or 2) pos += 4;               // float / long
                    else return result;
                    break;
                }
                case 2:                              // array: name, count, elements
                {
                    var name = ReadAsciizAt(d, ref pos);
                    var wanted = IsAddOnsName(name);
                    if (!ReadArray(d, ref pos, wanted ? Collect : null)) return result;
                    break;
                }
                case 3:                              // extern class reference: name only
                case 4:                              // delete class: name only
                    ReadAsciizAt(d, ref pos);
                    break;
                case 5:                              // flagged array: uint32 flags, then like an array
                {
                    pos += 4;
                    var name = ReadAsciizAt(d, ref pos);
                    var wanted = IsAddOnsName(name);
                    if (!ReadArray(d, ref pos, wanted ? Collect : null)) return result;
                    break;
                }
                default:
                    return result;                   // unknown token — stop walking safely
            }
        }
        return result;
    }

    private static bool IsAddOnsName(string name) =>
        name.Equals("addOns", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("addOnsAuto", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads one raP array body; feeds string elements to <paramref name="onString"/> when set.
    /// Returns false when an unsupported element subtype forces the caller to stop walking.</summary>
    private static bool ReadArray(byte[] d, ref int pos, Action<string>? onString)
    {
        var n = ReadCompressedInt(d, ref pos);
        for (var i = 0; i < n; i++)
        {
            switch (d[pos++])
            {
                case 0: { var s = ReadAsciizAt(d, ref pos); onString?.Invoke(s); break; }
                case 1: case 2: pos += 4; break;                    // float / long
                case 3: if (!ReadArray(d, ref pos, null)) return false; break;
                case 4: ReadAsciizAt(d, ref pos); break;            // variable reference
                default: return false;
            }
        }
        return true;
    }

    private static int ReadCompressedInt(byte[] d, ref int pos)
    {
        int value = 0, shift = 0;
        while (true)
        {
            var b = d[pos++];
            value |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value;
            shift += 7;
        }
    }

    private static string ReadAsciizAt(byte[] d, ref int pos)
    {
        var start = pos;
        while (pos < d.Length && d[pos] != 0) pos++;
        var s = Encoding.UTF8.GetString(d, start, pos - start);
        pos++;
        return s;
    }
}
