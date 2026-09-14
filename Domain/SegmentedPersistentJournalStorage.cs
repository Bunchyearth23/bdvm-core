using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace BDVM.Domain;

// Bounded append segments avoid one filesystem entry per economic second.
// Header + payload + footer framing distinguishes a torn terminal append from
// a committed record. The journal packet still verifies content and hash chain.
public sealed class SegmentedPersistentJournalStorage : IPersistentJournalStorage
{
    private const uint Header = 0x314a5642;
    private const uint Footer = 0x454a5642;
    private const long RecordsPerSegment = 256;
    private sealed class Entry { public string Path = ""; public long Offset; public int Length; }
    private readonly string root;
    private readonly FilePersistentJournalStorage files;
    private readonly SortedDictionary<string, Entry> entries = new SortedDictionary<string, Entry>(StringComparer.Ordinal);
    private readonly Dictionary<string, long> ends = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    private bool rescan;
    public SegmentedPersistentJournalStorage(string directory)
    {
        root = Path.GetFullPath(directory);
        files = new FilePersistentJournalStorage(root);
        try { Scan(); } catch { files.Dispose(); throw; }
    }
    private void Scan()
    {
        entries.Clear(); ends.Clear();
        var segments = Directory.GetFiles(root, "*.segment").OrderBy(value => value, StringComparer.Ordinal).ToArray();
        for (var index = 0; index < segments.Length; index++) ScanSegment(segments[index], index == segments.Length - 1);
        rescan = false;
    }
    private void ScanSegment(string path, bool terminal)
    {
        var lastComplete = 0L;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var reader = new BinaryReader(stream, Encoding.UTF8))
        {
            while (stream.Position < stream.Length)
            {
                if (stream.Length - stream.Position < 16) { if (!terminal) throw new InvalidDataException("Truncated nonterminal journal segment."); break; }
                if (reader.ReadUInt32() != Header) throw new InvalidDataException("Invalid journal segment header.");
                var sequence = reader.ReadInt64(); var length = reader.ReadInt32();
                if (sequence <= 0 || length < 0 || length > PersistentJournal.MaximumTransactionBytes || Path.GetFileName(path) != SegmentName(sequence))
                    throw new InvalidDataException("Invalid journal segment record identity or size.");
                if (stream.Length - stream.Position < (long)length + 4) { if (!terminal) throw new InvalidDataException("Truncated nonterminal journal record."); break; }
                var offset = stream.Position; stream.Position += length;
                if (reader.ReadUInt32() != Footer) throw new InvalidDataException("Invalid journal segment commit marker.");
                var name = TransactionName(sequence);
                if (entries.ContainsKey(name)) throw new InvalidDataException("Duplicate journal segment sequence.");
                entries.Add(name, new Entry { Path = path, Offset = offset, Length = length });
                lastComplete = stream.Position;
            }
        }
        ends[path] = lastComplete;
    }
    public void WriteOnce(string name, string content)
    {
        if (!name.EndsWith(".transaction", StringComparison.Ordinal)) { files.WriteOnce(name, content); return; }
        var sequence = ParseSequence(name);
        if (rescan) Scan();
        if (entries.TryGetValue(name, out var existing))
        {
            if (ReadEntry(existing) != content) throw new InvalidDataException("Different journal data already occupies this sequence.");
            using (var durable = new FileStream(existing.Path, FileMode.Open, FileAccess.Write, FileShare.Read)) durable.Flush(true);
            return;
        }
        var payload = Encoding.UTF8.GetBytes(content);
        if (payload.Length > PersistentJournal.MaximumTransactionBytes) throw new InvalidDataException("Journal segment record exceeds its byte limit.");
        var path = Path.Combine(root, SegmentName(sequence)); // generated numeric name under the locked root
        ends.TryGetValue(path, out var end);
        try
        {
            using (var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                // Only bytes beyond the last fully framed append are removed.
                stream.SetLength(end); stream.Position = end;
                writer.Write(Header); writer.Write(sequence); writer.Write(payload.Length); writer.Write(payload); writer.Write(Footer);
                writer.Flush(); stream.Flush(true);
                entries.Add(name, new Entry { Path = path, Offset = end + 16, Length = payload.Length });
                ends[path] = stream.Position;
            }
        }
        catch { rescan = true; throw; }
    }
    public string Read(string name)
    {
        if (!name.EndsWith(".transaction", StringComparison.Ordinal)) return files.Read(name);
        ParseSequence(name); if (rescan) Scan();
        return entries.TryGetValue(name, out var entry) ? ReadEntry(entry) : throw new FileNotFoundException("Journal transaction is absent.", name);
    }
    private static string ReadEntry(Entry entry)
    {
        using (var stream = new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var reader = new BinaryReader(stream, Encoding.UTF8))
        {
            stream.Position = entry.Offset;
            var bytes = reader.ReadBytes(entry.Length);
            if (bytes.Length != entry.Length) throw new InvalidDataException("Journal segment was truncated after indexing.");
            return Encoding.UTF8.GetString(bytes);
        }
    }
    public IReadOnlyList<string> Transactions() { if (rescan) Scan(); return entries.Keys.ToArray(); }
    private static long ParseSequence(string name)
    {
        if (name.Length != 32 || !long.TryParse(name.Substring(0, 20), NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) || sequence <= 0 || name != TransactionName(sequence))
            throw new InvalidDataException("Invalid journal transaction name.");
        return sequence;
    }
    private static string TransactionName(long sequence) => sequence.ToString("D20", CultureInfo.InvariantCulture) + ".transaction";
    private static string SegmentName(long sequence) => ((sequence - 1) / RecordsPerSegment).ToString("D20", CultureInfo.InvariantCulture) + ".segment";
    public void Dispose() => files.Dispose();
}
