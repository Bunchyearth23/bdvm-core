using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace BDVM.Domain;

[DataContract]
public sealed class PersistentDocument
{
    [DataMember(Order = 1)] public string System { get; set; } = "";
    [DataMember(Order = 2)] public string Key { get; set; } = "";
    [DataMember(Order = 3)] public string Value { get; set; } = "";
}

[DataContract]
public sealed class PersistentMutation
{
    [DataMember(Order = 1)] public string System { get; set; } = "";
    [DataMember(Order = 2)] public string Key { get; set; } = "";
    [DataMember(Order = 3)] public string ExpectedHash { get; set; } = "";
    [DataMember(Order = 4)] public string? Value { get; set; }
}

[DataContract]
public sealed class PersistentReceipt
{
    [DataMember(Order = 1)] public string OperationId { get; set; } = "";
    [DataMember(Order = 2)] public string Fingerprint { get; set; } = "";
    [DataMember(Order = 3)] public long Sequence { get; set; }
    [DataMember(Order = 4)] public string RequestHash { get; set; } = "";
}

[DataContract]
public sealed class PersistentTransaction
{
    [DataMember(Order = 1)] public int Version { get; set; } = 1;
    [DataMember(Order = 2)] public string CareerId { get; set; } = "";
    [DataMember(Order = 3)] public string BranchId { get; set; } = "";
    [DataMember(Order = 4)] public long Sequence { get; set; }
    [DataMember(Order = 5)] public string PreviousHash { get; set; } = "";
    [DataMember(Order = 6)] public string OperationId { get; set; } = "";
    [DataMember(Order = 7)] public string Fingerprint { get; set; } = "";
    [DataMember(Order = 8)] public List<PersistentMutation> Changes { get; set; } = new List<PersistentMutation>();
    [DataMember(Order = 9)] public string RequestHash { get; set; } = "";
}

[DataContract]
public sealed class PersistentCheckpoint
{
    [DataMember(Order = 1)] public int Version { get; set; } = 1;
    [DataMember(Order = 2)] public string CareerId { get; set; } = "";
    [DataMember(Order = 3)] public string BranchId { get; set; } = "";
    [DataMember(Order = 4)] public long Sequence { get; set; }
    [DataMember(Order = 5)] public string HeadHash { get; set; } = "";
    [DataMember(Order = 6)] public List<PersistentDocument> Documents { get; set; } = new List<PersistentDocument>();
    [DataMember(Order = 7)] public List<PersistentReceipt> Receipts { get; set; } = new List<PersistentReceipt>();
}

public static class PersistentJournalCodec
{
    [DataContract]
    private sealed class Packet
    {
        [DataMember(Order = 1)] public string Payload { get; set; } = "";
        [DataMember(Order = 2)] public string Sha256 { get; set; } = "";
    }
    public static string Serialize<T>(T value)
        => Serialize(value, typeof(T));
    public static string Serialize(object? value, Type type)
    {
        using (var stream = new MemoryStream())
        {
            new DataContractJsonSerializer(type).WriteObject(stream, value);
            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }
    public static T Deserialize<T>(string text)
        => (T)Deserialize(text, typeof(T))!;
    public static object? Deserialize(string text, Type type)
    {
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(text)))
            return new DataContractJsonSerializer(type).ReadObject(stream);
    }
    public static string Hash(string value)
    {
        using (var hash = SHA256.Create())
            return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
    }
    public static string Pack<T>(T value)
    {
        var payload = Serialize(value);
        return Serialize(new Packet { Payload = payload, Sha256 = Hash(payload) });
    }
    public static T Unpack<T>(string text)
    {
        var packet = Deserialize<Packet>(text);
        if (packet == null || packet.Payload == null || packet.Sha256 != Hash(packet.Payload))
            throw new InvalidDataException("Persistent journal checksum mismatch.");
        return Deserialize<T>(packet.Payload);
    }
}

public interface IPersistentJournalStorage : IDisposable
{
    void WriteOnce(string name, string content);
    string Read(string name);
    IReadOnlyList<string> Transactions();
}

// One exclusive writer; a successful append is durable before its projection
// becomes visible. Temporary/unacknowledged writes never replace an older file.
public sealed class FilePersistentJournalStorage : IPersistentJournalStorage
{
    private readonly string root;
    private readonly FileStream writer;
    public FilePersistentJournalStorage(string directory)
    {
        root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        Directory.CreateDirectory(root);
        writer = new FileStream(Path.Combine(root, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private string Resolve(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("Invalid journal file name.");
        var path = Path.GetFullPath(Path.Combine(root, name));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Journal path escapes its directory.");
        return path;
    }
    public void WriteOnce(string name, string content)
    {
        var target = Resolve(name);
        if (File.Exists(target))
        {
            if (!string.Equals(File.ReadAllText(target, Encoding.UTF8), content, StringComparison.Ordinal))
                throw new InvalidDataException("A different journal record already occupies this sequence.");
            return; // Retry after rename succeeded but its acknowledgement was lost.
        }
        var temporary = Resolve("pending-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            File.Move(temporary, target);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public string Read(string name) => File.ReadAllText(Resolve(name), Encoding.UTF8);
    public IReadOnlyList<string> Transactions() => Directory.GetFiles(root, "*.transaction")
        .Select(Path.GetFileName).OrderBy(value => value, StringComparer.Ordinal).ToArray()!;
    public void Dispose() => writer.Dispose();
}

// Its caller is a single background owner. Reading/replaying documents performs
// no domain command and no external effect (Unity, wallet, networking, spawning).
public sealed class PersistentJournal : IDisposable
{
    public const int MaximumTransactionBytes = 16 * 1024 * 1024;
    private readonly int owner = Thread.CurrentThread.ManagedThreadId;
    private readonly IPersistentJournalStorage storage;
    private readonly Dictionary<string, PersistentDocument> documents = new Dictionary<string, PersistentDocument>(StringComparer.Ordinal);
    private readonly Dictionary<string, PersistentReceipt> receipts = new Dictionary<string, PersistentReceipt>(StringComparer.Ordinal);
    public string CareerId { get; }
    public string BranchId { get; }
    public long Sequence { get; private set; }
    public string HeadHash { get; private set; }
    private bool disposed;

    private PersistentJournal(IPersistentJournalStorage storage, PersistentCheckpoint seed, string branch)
    {
        this.storage = storage;
        if (seed == null || seed.Documents == null || seed.Receipts == null || seed.Version != 1 || string.IsNullOrWhiteSpace(seed.CareerId) || seed.Sequence < 0 || !IsHash(seed.HeadHash) || !Guid.TryParseExact(branch, "N", out _))
            throw new InvalidDataException("Invalid persistent checkpoint identity.");
        CareerId = seed.CareerId; BranchId = branch; Sequence = seed.Sequence; HeadHash = seed.HeadHash;
        foreach (var document in seed.Documents)
        {
            var key = Key(document.System, document.Key);
            if (document.Value == null || documents.ContainsKey(key)) throw new InvalidDataException("Invalid or duplicate checkpoint document.");
            documents.Add(key, new PersistentDocument { System = document.System, Key = document.Key, Value = document.Value });
        }
        foreach (var receipt in seed.Receipts)
        {
            if (string.IsNullOrWhiteSpace(receipt.OperationId) || !IsHash(receipt.Fingerprint) || (!string.IsNullOrEmpty(receipt.RequestHash) && !IsHash(receipt.RequestHash)) || receipt.Sequence <= 0 || receipt.Sequence > Sequence || receipts.ContainsKey(receipt.OperationId))
                throw new InvalidDataException("Invalid checkpoint operation receipt.");
            receipts.Add(receipt.OperationId, Copy(receipt));
        }
    }
    public static PersistentJournal Create(IPersistentJournalStorage storage, string careerId, IEnumerable<PersistentDocument> initialDocuments)
        => Fork(storage, new PersistentCheckpoint { CareerId = careerId, HeadHash = PersistentJournalCodec.Hash("genesis:" + careerId),
            Documents = initialDocuments.ToList() }, careerId);
    public static PersistentJournal Fork(IPersistentJournalStorage storage, PersistentCheckpoint checkpoint, string expectedCareerId)
    {
        try
        {
        if (checkpoint.CareerId != expectedCareerId) throw new InvalidDataException("Cross-career journal load refused.");
        var journal = new PersistentJournal(storage, checkpoint, Guid.NewGuid().ToString("N"));
        // A save load forks exactly its checkpoint. Future records in an older
        // session directory are deliberately not consulted here.
        storage.WriteOnce("genesis.snapshot", PersistentJournalCodec.Pack(journal.Checkpoint()));
        return journal;
        }
        catch { storage.Dispose(); throw; }
    }
    public static PersistentJournal Recover(IPersistentJournalStorage storage, string expectedCareerId)
    {
        try
        {
        var seed = PersistentJournalCodec.Unpack<PersistentCheckpoint>(storage.Read("genesis.snapshot"));
        if (seed.CareerId != expectedCareerId) throw new InvalidDataException("Cross-career journal recovery refused.");
        var journal = new PersistentJournal(storage, seed, seed.BranchId);
        foreach (var name in storage.Transactions())
        {
            var transaction = PersistentJournalCodec.Unpack<PersistentTransaction>(storage.Read(name));
            if (name != FileName(transaction.Sequence)) throw new InvalidDataException("Journal file sequence mismatch.");
            journal.Apply(transaction);
        }
        return journal;
        }
        catch { storage.Dispose(); throw; }
    }
    public PersistentReceipt Commit(string operationId, IEnumerable<PersistentMutation> mutations, string requestHash = "")
    {
        AssertOwner();
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 256) throw new ArgumentException("An operation ID is required.", nameof(operationId));
        if (!string.IsNullOrEmpty(requestHash) && !IsHash(requestHash)) throw new ArgumentException("Invalid request fingerprint.", nameof(requestHash));
        var changes = mutations.Select(value => new PersistentMutation { System = value.System, Key = value.Key, ExpectedHash = value.ExpectedHash, Value = value.Value }).ToList();
        var fingerprint = PersistentJournalCodec.Hash(PersistentJournalCodec.Serialize(changes));
        if (receipts.TryGetValue(operationId, out var known))
        {
            if (known.Fingerprint != fingerprint || known.RequestHash != requestHash) throw new InvalidOperationException("Operation ID reused with different changes.");
            return Copy(known);
        }
        var transaction = new PersistentTransaction { CareerId = CareerId, BranchId = BranchId, Sequence = checked(Sequence + 1),
            PreviousHash = HeadHash, OperationId = operationId, Fingerprint = fingerprint, Changes = changes, RequestHash = requestHash };
        Validate(transaction);
        var content = PersistentJournalCodec.Pack(transaction);
        if (Encoding.UTF8.GetByteCount(content) > MaximumTransactionBytes) throw new InvalidOperationException("Journal transaction exceeds its byte limit.");
        storage.WriteOnce(FileName(transaction.Sequence), content);
        Apply(transaction);
        return Copy(receipts[operationId]);
    }
    public string? Read(string system, string key)
    { AssertOwner(); return documents.TryGetValue(Key(system, key), out var document) ? document.Value : null; }
    public PersistentReceipt? FindReceipt(string operationId)
    { AssertOwner(); return receipts.TryGetValue(operationId, out var receipt) ? Copy(receipt) : null; }
    public PersistentCheckpoint Checkpoint()
    {
        AssertOwner();
        return new PersistentCheckpoint { CareerId = CareerId, BranchId = BranchId, Sequence = Sequence, HeadHash = HeadHash,
            Documents = documents.OrderBy(value => value.Key, StringComparer.Ordinal).Select(value => new PersistentDocument
                { System = value.Value.System, Key = value.Value.Key, Value = value.Value.Value }).ToList(),
            Receipts = receipts.Values.OrderBy(value => value.OperationId, StringComparer.Ordinal).Select(Copy).ToList() };
    }
    public string PrepareCheckpoint()
    {
        var checkpoint = PersistentJournalCodec.Pack(Checkpoint());
        storage.WriteOnce("checkpoint-" + PersistentJournalCodec.Hash(checkpoint) + ".snapshot", checkpoint);
        return checkpoint;
    }
    private void Validate(PersistentTransaction transaction)
    {
        if (transaction.Version != 1 || transaction.CareerId != CareerId || transaction.BranchId != BranchId || transaction.Sequence != Sequence + 1 || transaction.PreviousHash != HeadHash ||
            string.IsNullOrWhiteSpace(transaction.OperationId) || transaction.OperationId.Length > 256 || receipts.ContainsKey(transaction.OperationId) ||
            (!string.IsNullOrEmpty(transaction.RequestHash) && !IsHash(transaction.RequestHash)) ||
            transaction.Fingerprint != PersistentJournalCodec.Hash(PersistentJournalCodec.Serialize(transaction.Changes)))
            throw new InvalidDataException("Journal ordering, identity or integrity violation.");
        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in transaction.Changes)
        {
            var key = Key(change.System, change.Key);
            if (!changed.Add(key)) throw new InvalidDataException("A transaction changes the same document twice.");
            var actualHash = documents.TryGetValue(key, out var current) ? PersistentJournalCodec.Hash(current.Value) : "";
            if (actualHash != change.ExpectedHash) throw new InvalidDataException("Journal change conflicts with its expected document version.");
        }
    }
    private void Apply(PersistentTransaction transaction)
    {
        AssertOwner(); Validate(transaction);
        foreach (var change in transaction.Changes)
        {
            var key = Key(change.System, change.Key);
            if (change.Value == null) documents.Remove(key);
            else documents[key] = new PersistentDocument { System = change.System, Key = change.Key, Value = change.Value };
        }
        Sequence = transaction.Sequence;
        HeadHash = PersistentJournalCodec.Hash(PersistentJournalCodec.Serialize(transaction));
        receipts.Add(transaction.OperationId, new PersistentReceipt { OperationId = transaction.OperationId, Fingerprint = transaction.Fingerprint, Sequence = Sequence, RequestHash = transaction.RequestHash });
    }
    private static string FileName(long sequence) => sequence.ToString("D20", System.Globalization.CultureInfo.InvariantCulture) + ".transaction";
    private static string Key(string system, string key)
    {
        if (string.IsNullOrWhiteSpace(system) || string.IsNullOrWhiteSpace(key) || system.Contains("\0") || key.Contains("\0"))
            throw new InvalidDataException("Journal documents need a system and an unambiguous key.");
        return system + "\0" + key;
    }
    private static bool IsHash(string text) => text != null && text.Length == 64 && text.All(Uri.IsHexDigit);
    private static PersistentReceipt Copy(PersistentReceipt value) => new PersistentReceipt { OperationId = value.OperationId, Fingerprint = value.Fingerprint, Sequence = value.Sequence, RequestHash = value.RequestHash };
    private void AssertOwner()
    {
        if (disposed) throw new ObjectDisposedException(nameof(PersistentJournal));
        if (Thread.CurrentThread.ManagedThreadId != owner) throw new InvalidOperationException("Persistent journal accessed outside its owner thread.");
    }
    public void Dispose() { AssertOwner(); disposed = true; storage.Dispose(); }
}
