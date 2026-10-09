using System.Buffers.Binary;
using System.Text;

namespace ConsDTR.Runtime;

/// <summary>Length-delimited write-ahead records. An interrupted final record is excluded on recovery.</summary>
public static class JournalStore
{
    private const int CommitMarker = 0x44545231;
    internal static void Append(string path, JournalEvent entry)
    {
        var bytes = DtrCanonical.StrictUtf8.GetBytes(DtrCanonical.Json(entry));
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
            4096, FileOptions.WriteThrough);
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, bytes.Length); stream.Write(word); stream.Write(bytes);
        BinaryPrimitives.WriteInt32BigEndian(word, CommitMarker); stream.Write(word);
        stream.Flush(true);
    }

    public static IReadOnlyList<JournalEvent> ReadCommitted(string path, long maxBytes = 67_108_864)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length > maxBytes) throw new InvalidDataException("WAL exceeds recovery byte allowance.");
        var events = new List<JournalEvent>();
        Span<byte> word = stackalloc byte[4];
        while (stream.Position < stream.Length)
        {
            if (stream.Read(word) != 4) break;
            var length = BinaryPrimitives.ReadInt32BigEndian(word);
            if (length < 0 || length > maxBytes) throw new InvalidDataException("Invalid WAL record length.");
            if (stream.Length - stream.Position < length + 4L) break;
            var bytes = new byte[length]; stream.ReadExactly(bytes); stream.ReadExactly(word);
            if (BinaryPrimitives.ReadInt32BigEndian(word) != CommitMarker) throw new InvalidDataException("Invalid WAL commit marker.");
            var entry = DtrCanonical.Parse<JournalEvent>(DtrCanonical.StrictUtf8.GetString(bytes));
            if (entry.ComputeDigest() != entry.EventDigest) throw new InvalidDataException("Corrupt committed WAL event.");
            events.Add(entry);
        }
        return events.AsReadOnly();
    }

    public static void WriteComplete(string path, IEnumerable<JournalEvent> events)
    {
        var fullPath = Path.GetFullPath(path);
        var temporary = fullPath + ".pending";
        if (File.Exists(temporary)) throw new IOException("Existing pending journal file.");
        try
        {
            using (File.Create(temporary)) { }
            foreach (var entry in events) Append(temporary, entry);
            File.Move(temporary, fullPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
