using System.Buffers.Binary;
using System.Globalization;
using System.Text.Unicode;

namespace DdoddoZip.Core;

public enum NameEncodingKind
{
    /// <summary>Not a ZIP, or the file list could not be read.</summary>
    Unknown,
    /// <summary>Only English letters, digits and symbols: no conversion needed.</summary>
    Ascii,
    /// <summary>UTF-8 (marked in the file, or detected).</summary>
    Utf8,
    /// <summary>A legacy code page such as CP949 (old Korean Windows zips).</summary>
    Legacy,
}

/// <param name="CodePage">Code page to force when opening, or null to let 7-Zip decide.</param>
public sealed record NameEncodingInfo(NameEncodingKind Kind, int? CodePage)
{
    public string Display => Kind switch
    {
        NameEncodingKind.Ascii => "영문",
        NameEncodingKind.Utf8 => "UTF-8",
        NameEncodingKind.Legacy => CodePage == 949 ? "CP949 (한국어)" : $"CP{CodePage}",
        _ => "알 수 없음",
    };
}

/// <summary>
/// Reads the raw file names from a ZIP's central directory and decides whether they are
/// UTF-8 or an old code page like CP949, so Korean names are not garbled (F06).
/// </summary>
public static class EncodingDetector
{
    private const uint EocdSig = 0x06054b50;
    private const uint Zip64LocatorSig = 0x07064b50;
    private const uint Zip64EocdSig = 0x06064b50;
    private const uint CentralSig = 0x02014b50;
    private const int MaxEntriesToCheck = 20000;
    private const int MaxCentralBytes = 32 * 1024 * 1024;

    public static NameEncodingInfo DetectZip(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Analyze(ReadCentralDirectoryNames(fs));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new NameEncodingInfo(NameEncodingKind.Unknown, null);
        }
    }

    internal static NameEncodingInfo Analyze(IEnumerable<(ushort Flags, byte[] Name)> entries)
    {
        bool anyFlagged = false, anyUnflaggedNonAscii = false, allUnflaggedUtf8 = true, allCp949 = true, any = false;

        foreach (var (flags, name) in entries)
        {
            any = true;
            bool utf8Flag = (flags & 0x0800) != 0;
            if (utf8Flag)
            {
                anyFlagged = true;
                continue;
            }
            if (IsAscii(name))
                continue;

            anyUnflaggedNonAscii = true;
            if (!Utf8.IsValid(name))
                allUnflaggedUtf8 = false;
            if (!LooksLikeCp949(name))
                allCp949 = false;
        }

        if (!any)
            return new NameEncodingInfo(NameEncodingKind.Unknown, null);
        if (!anyUnflaggedNonAscii)
            return new NameEncodingInfo(anyFlagged ? NameEncodingKind.Utf8 : NameEncodingKind.Ascii, null);
        if (allUnflaggedUtf8)
            return new NameEncodingInfo(NameEncodingKind.Utf8, 65001); // e.g. zips made on macOS without the flag
        if (anyFlagged)
            return new NameEncodingInfo(NameEncodingKind.Legacy, null); // mixed: let 7-Zip decide per entry

        int legacy = allCp949 ? 949 : CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
        return new NameEncodingInfo(NameEncodingKind.Legacy, legacy);
    }

    private static bool IsAscii(byte[] s)
    {
        foreach (var b in s)
            if (b >= 0x80) return false;
        return true;
    }

    /// <summary>CP949: lead byte 0x81-0xFE followed by trail 0x41-0x5A, 0x61-0x7A or 0x81-0xFE.</summary>
    private static bool LooksLikeCp949(byte[] s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            var b = s[i];
            if (b < 0x80) continue;
            if (b is < 0x81 or > 0xFE || i + 1 >= s.Length) return false;
            var t = s[++i];
            bool ok = t is (>= 0x41 and <= 0x5A) or (>= 0x61 and <= 0x7A) or (>= 0x81 and <= 0xFE);
            if (!ok) return false;
        }
        return true;
    }

    internal static List<(ushort Flags, byte[] Name)> ReadCentralDirectoryNames(Stream fs)
    {
        var result = new List<(ushort, byte[])>();
        long len = fs.Length;
        if (len < 22) return result;

        int tailLen = (int)Math.Min(len, 22 + 65535 + 20);
        var tail = new byte[tailLen];
        fs.Seek(len - tailLen, SeekOrigin.Begin);
        fs.ReadExactly(tail);

        int eocd = -1;
        for (int i = tailLen - 22; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == EocdSig) { eocd = i; break; }
        }
        if (eocd < 0) return result;

        long eocdPos = len - tailLen + eocd;
        ulong cdSize = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 12));
        long cdEnd = eocdPos;

        if (cdSize == 0xFFFFFFFF && eocd >= 20 &&
            BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd - 20)) == Zip64LocatorSig)
        {
            long z64Pos = (long)BinaryPrimitives.ReadUInt64LittleEndian(tail.AsSpan(eocd - 20 + 8));
            var z64 = new byte[56];
            fs.Seek(z64Pos, SeekOrigin.Begin);
            fs.ReadExactly(z64);
            if (BinaryPrimitives.ReadUInt32LittleEndian(z64) != Zip64EocdSig) return result;
            cdSize = BinaryPrimitives.ReadUInt64LittleEndian(z64.AsSpan(40));
            cdEnd = z64Pos;
        }

        // Locate the central directory by its size (works for self-extracting zips with a prefix too).
        long cdStart = cdEnd - (long)cdSize;
        if (cdStart < 0 || cdSize > MaxCentralBytes) return result;

        var cd = new byte[(int)cdSize];
        fs.Seek(cdStart, SeekOrigin.Begin);
        fs.ReadExactly(cd);

        int pos = 0;
        while (pos + 46 <= cd.Length && result.Count < MaxEntriesToCheck)
        {
            var span = cd.AsSpan(pos);
            if (BinaryPrimitives.ReadUInt32LittleEndian(span) != CentralSig) break;
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(span[8..]);
            int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(span[28..]);
            int extraLen = BinaryPrimitives.ReadUInt16LittleEndian(span[30..]);
            int commentLen = BinaryPrimitives.ReadUInt16LittleEndian(span[32..]);
            if (pos + 46 + nameLen > cd.Length) break;
            result.Add((flags, span.Slice(46, nameLen).ToArray()));
            pos += 46 + nameLen + extraLen + commentLen;
        }
        return result;
    }
}
