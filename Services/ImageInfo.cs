namespace Whetstone.Services;

/// <summary>A rectangle in an image's own pixels: the part of a screenshot that matters.</summary>
public record ImageRect(int X, int Y, int W, int H)
{
    /// <summary>Why this rectangle does not fit an image of the given size, or null when it does.</summary>
    public string? ProblemWithin(int width, int height)
    {
        if (W <= 0 || H <= 0) return $"has no area ({W}x{H})";
        if (X < 0 || Y < 0) return $"starts off the image at ({X},{Y})";
        if (X + W > width || Y + H > height)
            return $"runs off the image: ({X},{Y}) {W}x{H} ends at ({X + W},{Y + H}) but the image is {width}x{height}";
        return null;
    }
}

/// <summary>
/// Reads an image's pixel size from its header, without decoding it. Enough to check that a
/// rectangle someone drew on a screenshot actually lies on it - PNG, JPEG, GIF and WebP, the
/// types QA shots and tracker attachments arrive as.
/// </summary>
public static class ImageInfo
{
    public static (int Width, int Height)? Size(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var head = new byte[64];
            var n = f.Read(head, 0, head.Length);
            if (n >= 24 && head[0] == 0x89 && head[1] == 'P' && head[2] == 'N' && head[3] == 'G')
                return (BigEndian(head, 16), BigEndian(head, 20));
            if (n >= 10 && head[0] == 'G' && head[1] == 'I' && head[2] == 'F')
                return (head[6] | head[7] << 8, head[8] | head[9] << 8);
            if (n >= 30 && head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F' && head[8] == 'W' && head[9] == 'E' && head[10] == 'B' && head[11] == 'P')
                return WebP(head);
            if (n >= 2 && head[0] == 0xFF && head[1] == 0xD8)
            {
                f.Position = 2;
                return Jpeg(f);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    private static int BigEndian(byte[] b, int i) => b[i] << 24 | b[i + 1] << 16 | b[i + 2] << 8 | b[i + 3];

    private static (int, int)? WebP(byte[] h)
    {
        var chunk = System.Text.Encoding.ASCII.GetString(h, 12, 4);
        return chunk switch
        {
            "VP8X" => (1 + (h[24] | h[25] << 8 | h[26] << 16), 1 + (h[27] | h[28] << 8 | h[29] << 16)),
            "VP8 " => ((h[26] | h[27] << 8) & 0x3FFF, (h[28] | h[29] << 8) & 0x3FFF),
            "VP8L" => (1 + ((h[21] | h[22] << 8) & 0x3FFF), 1 + ((h[22] >> 6 | h[23] << 2 | h[24] << 10) & 0x3FFF)),
            _ => null,
        };
    }

    // Walk the segments to the first start-of-frame; its height and width follow the precision byte.
    private static (int, int)? Jpeg(Stream f)
    {
        var buf = new byte[7];
        while (f.Read(buf, 0, 4) == 4)
        {
            if (buf[0] != 0xFF) return null;
            var marker = buf[1];
            var len = buf[2] << 8 | buf[3];
            var isSof = marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC;
            if (isSof)
            {
                if (f.Read(buf, 0, 5) != 5) return null;
                return (buf[3] << 8 | buf[4], buf[1] << 8 | buf[2]);
            }
            f.Position += len - 2;
        }
        return null;
    }
}
