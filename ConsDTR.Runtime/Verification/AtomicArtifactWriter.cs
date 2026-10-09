namespace ConsDTR.Runtime;

internal static class AtomicArtifactWriter
{
    public static void Write(string path, byte[] bytes)
    {
        var target = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(target) ?? throw new ArgumentException("Missing artifact directory.", nameof(path));
        Directory.CreateDirectory(directory);
        var temporary = target + ".pending-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static string Read(string path, long maxBytes)
    {
        if (maxBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maxBytes || stream.Length > int.MaxValue) throw new RuntimeResourceException("Artifact input exceeds byte allowance.");
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
        return DtrCanonical.StrictUtf8.GetString(bytes);
    }
}
