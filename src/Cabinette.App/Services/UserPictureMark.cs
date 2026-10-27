using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace Cabinette.App.Services;

/// <summary>
/// Marks a category picture the user chose with a PNG "tEXt" chunk ("Cabinette" = "user picture"), so the thread banner never
/// takes it for a generated avatar, however much it looks like one (banner bug hunt F3). Pictures chosen by earlier builds carry
/// no mark; the banner still recognizes avatars by their look for those.
/// </summary>
public static class UserPictureMark
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly byte[] Text = Encoding.Latin1.GetBytes("Cabinette\0user picture");

    /// <summary>The PNG with the mark inserted after its header chunk. Anything that isn't a PNG, or is marked already, comes back as it is.</summary>
    public static byte[] Add(byte[] png)
    {
        if (!IsPng(png) || IsMarked(png))
        {
            return png;
        }

        var chunk = new byte[12 + Text.Length];
        BinaryPrimitives.WriteInt32BigEndian(chunk, Text.Length);
        "tEXt"u8.CopyTo(chunk.AsSpan(4));
        Text.CopyTo(chunk, 8);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + Text.Length), Crc32.HashToUInt32(chunk.AsSpan(4, 4 + Text.Length)));

        var afterHeader = 8 + 12 + BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(8));
        return [.. png.AsSpan(0, afterHeader), .. chunk, .. png.AsSpan(afterHeader)];
    }

    /// <summary>True when the PNG carries the mark (looked for before the image data, where <see cref="Add"/> puts it).</summary>
    public static bool IsMarked(byte[]? png)
    {
        if (png is null || !IsPng(png))
        {
            return false;
        }

        for (var at = 8; at + 12 <= png.Length;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            if (length < 0 || at + 12L + length > png.Length)
            {
                return false;
            }

            var type = png.AsSpan(at + 4, 4);
            if (type.SequenceEqual("IDAT"u8) || type.SequenceEqual("IEND"u8))
            {
                return false;
            }

            if (type.SequenceEqual("tEXt"u8) && png.AsSpan(at + 8, length).SequenceEqual(Text))
            {
                return true;
            }

            at += 12 + length;
        }

        return false;
    }

    private static bool IsPng(byte[] bytes) => bytes.Length > 33 && bytes.AsSpan(0, 8).SequenceEqual(Signature);
}
