using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;

namespace BDVM.Domain;

[DataContract]
public sealed class PersistentSavedCheckpoint
{
    [DataMember(Order = 1)] public int Version { get; set; } = 1;
    [DataMember(Order = 2)] public string Ticket { get; set; } = "";
    [DataMember(Order = 3)] public string SourcePayloadHash { get; set; } = "";
    [DataMember(Order = 4)] public string PhysicalSaveId { get; set; } = "";
    [DataMember(Order = 5)] public PersistentCheckpoint Checkpoint { get; set; } = null!;
}

// One worker owns the journal and its materialized read model. Unity sends
// confirmed changes as immutable text; replay never runs the original command.
// The complete legacy payload remains in the game save as a portable recovery
// copy, including when the process exits before its async checkpoint finishes.
public sealed class PersistentRuntimeJournal : IDisposable
{
    private readonly string careerDirectory;
    private readonly string checkpointId;
    private readonly PersistentStateOwner owner;
    private VehicleAcquisitionSnapshot state = null!; // accessed by owner only
    private Exception? failure;
    public Task Completion => owner.Completion;

    public PersistentRuntimeJournal(string root, string checkpointId, string initialPayload, string? savedTicket = null)
    {
        this.checkpointId = checkpointId ?? throw new ArgumentNullException(nameof(checkpointId));
        careerDirectory = Path.Combine(Path.GetFullPath(root), "career-" + PersistentJournalCodec.Hash(checkpointId));
        owner = new PersistentStateOwner(() => Initialize(initialPayload, savedTicket));
    }
    private PersistentJournal Initialize(string payload, string? ticket)
    {
        state = VehicleAcquisitionPersistence.Deserialize(payload, checkpointId);
        if (!string.IsNullOrWhiteSpace(ticket)) RequireTicket(ticket!);
        var session = new SegmentedPersistentJournalStorage(Path.Combine(careerDirectory, "sessions", Guid.NewGuid().ToString("N")));
        if (!string.IsNullOrWhiteSpace(ticket))
        {
            var path = Path.Combine(careerDirectory, "snapshots", "save-" + ticket + ".snapshot");
            if (File.Exists(path))
            {
                try
                {
                    var saved = PersistentJournalCodec.Unpack<PersistentSavedCheckpoint>(File.ReadAllText(path, Encoding.UTF8));
                    if (saved.Version != 1 || saved.Ticket != ticket || saved.SourcePayloadHash != PersistentJournalCodec.Hash(payload))
                        throw new InvalidDataException("Saved journal checkpoint does not match this game save.");
                    var restored = PersistentSnapshotDocuments.Restore(saved.Checkpoint.Documents, checkpointId);
                    if (VehicleAcquisitionPersistence.SerializePrepared(restored) != payload)
                        throw new InvalidDataException("Journal projection differs from the embedded recovery copy.");
                    state = restored;
                    return PersistentJournal.Fork(session, saved.Checkpoint, checkpointId);
                }
                catch { session.Dispose(); throw; }
            }
            // An absent sidecar is allowed: a portable/copied save or a process
            // exit before checkpoint completion still has the complete payload.
        }
        return PersistentJournal.Create(session, checkpointId, PersistentSnapshotDocuments.Export(state));
    }
    public Task<long> ObserveSnapshotAsync(string operationId, string payload)
        => owner.Enqueue(journal => Guard(() => Observe(journal, operationId, payload)), Encoding.UTF8.GetByteCount(payload));
    public Task<long> ObservePeriodicAsync(string serializedDelta)
        => owner.Enqueue(journal => Guard(() =>
        {
            var delta = PersistentJournalCodec.Deserialize<PeriodicEconomicDelta>(serializedDelta);
            var operationId = "periodic:" + delta.ClockAction.CommandId;
            var requestHash = PersistentJournalCodec.Hash(serializedDelta);
            var receipt = journal.FindReceipt(operationId);
            if (receipt != null)
            {
                if (receipt.RequestHash != requestHash) throw new InvalidDataException("Periodic event ID reused with different changes.");
                return receipt.Sequence;
            }
            var mutations = PeriodicMutations(journal, delta);
            if (!delta.TryApply(state)) throw new InvalidDataException("Confirmed economic event conflicts with the journal projection.");
            return journal.Commit(operationId, mutations, requestHash).Sequence;
        }), Encoding.UTF8.GetByteCount(serializedDelta));
    public Task<long> CheckpointSaveAsync(string ticket, string physicalSaveId, string payload)
    {
        RequireTicket(ticket);
        return owner.Enqueue(journal => Guard(() =>
        {
            // Save(updateData:false) can write an older staged image. Never
            // rewind the live projection or include future receipts in it.
            var savedState = VehicleAcquisitionPersistence.Deserialize(payload, checkpointId);
            var savedDocuments = PersistentSnapshotDocuments.Export(savedState);
            var point = journal.Checkpoint();
            if (PersistentSnapshotDocuments.Difference(point.Documents, savedDocuments).Count != 0)
                point = new PersistentCheckpoint { CareerId = checkpointId, BranchId = Guid.NewGuid().ToString("N"),
                    HeadHash = PersistentJournalCodec.Hash("genesis:" + checkpointId), Documents = savedDocuments.ToList() };
            var checkpoint = new PersistentSavedCheckpoint { Ticket = ticket, SourcePayloadHash = PersistentJournalCodec.Hash(payload),
                PhysicalSaveId = physicalSaveId, Checkpoint = point };
            using (var snapshots = new FilePersistentJournalStorage(Path.Combine(careerDirectory, "snapshots")))
                snapshots.WriteOnce("save-" + ticket + ".snapshot", PersistentJournalCodec.Pack(checkpoint));
            return checkpoint.Checkpoint.Sequence;
        }), Encoding.UTF8.GetByteCount(payload));
    }
    public Task<string?> ReadAsync(string system, string key)
        => owner.Enqueue(journal => Guard(() => journal.Read(system, key)));
    public Task<PersistentCheckpoint> ReadCheckpointAsync()
        => owner.Enqueue(journal => Guard(journal.Checkpoint));
    private T Guard<T>(Func<T> action)
    {
        if (failure != null) throw new InvalidOperationException("Persistent journal needs recovery after a failed write; the embedded game save remains available.", failure);
        try { return action(); } catch (Exception exception) { failure = exception; throw; }
    }
    private long Observe(PersistentJournal journal, string operationId, string payload)
    {
        var next = VehicleAcquisitionPersistence.Deserialize(payload, checkpointId);
        var documents = PersistentSnapshotDocuments.Export(next);
        var changes = PersistentSnapshotDocuments.Difference(journal.Checkpoint().Documents, documents);
        if (changes.Count > 0) journal.Commit(operationId, changes);
        state = next;
        return journal.Sequence;
    }
    private List<PersistentMutation> PeriodicMutations(PersistentJournal journal, PeriodicEconomicDelta delta)
    {
        var changes = new List<PersistentMutation>();
        foreach (var document in PersistentSnapshotDocuments.Export(new VehicleAcquisitionSnapshot { LeaseClock = delta.Clock }, new[] { "leaseClock" }))
            Put(journal, changes, document.System, document.Key, document.Value);
        Rows(journal, changes, "economy", "$/wallets", state.Economy.Wallets, delta.Wallets, value => value.Account.Key);
        Rows(journal, changes, "industrialStocks", "$", state.IndustrialStocks, delta.Stocks, PeriodicEconomicProjection.StockKey);
        Rows(journal, changes, "industrialRecipes", "$", state.IndustrialRecipes, delta.Recipes, value => value.RecipeId);
        Rows(journal, changes, "industrialContracts", "$", state.IndustrialContracts, delta.Contracts, value => value.ContractId);
        Rows(journal, changes, "fleet", "$", state.Fleet, delta.Fleet, value => value.AssetId);
        Rows(journal, changes, "industrialCargoTags", "$", state.IndustrialCargoTags, delta.Tags, value => value.AssetId);
        Append(journal, changes, "leaseActions", "$", state.LeaseActions.Count, new[] { delta.ClockAction });
        Append(journal, changes, "economy", "$/ledger", state.Economy.Ledger.Count, delta.Ledger);
        Append(journal, changes, "industrialCommands", "$", state.IndustrialCommands.Count, delta.Commands);
        return changes;
    }
    private static void Rows<T>(PersistentJournal journal, List<PersistentMutation> changes, string system, string prefix,
        List<T> before, List<PeriodicRecordChange<T>> records, Func<T, string> key)
    {
        foreach (var record in records)
        {
            var index = before.FindIndex(value => key(value) == record.Key);
            if (index < 0) throw new InvalidDataException("Missing confirmed economic event target.");
            Put(journal, changes, system, prefix + "/" + index.ToString("D10", CultureInfo.InvariantCulture), PersistentJournalCodec.Serialize(record.Value));
        }
    }
    private static void Append<T>(PersistentJournal journal, List<PersistentMutation> changes, string system, string prefix, int count, IEnumerable<T> records)
    {
        var next = count;
        foreach (var record in records) Put(journal, changes, system, prefix + "/" + (next++).ToString("D10", CultureInfo.InvariantCulture), PersistentJournalCodec.Serialize(record));
        if (next != count) Put(journal, changes, system, prefix, "list:" + next.ToString(CultureInfo.InvariantCulture));
    }
    private static void Put(PersistentJournal journal, List<PersistentMutation> changes, string system, string key, string value)
    {
        var before = journal.Read(system, key);
        if (before != value) changes.Add(new PersistentMutation { System = system, Key = key, ExpectedHash = before == null ? "" : PersistentJournalCodec.Hash(before), Value = value });
    }
    private static void RequireTicket(string ticket)
    { if (!Guid.TryParseExact(ticket, "N", out _)) throw new InvalidDataException("Invalid persistent save ticket."); }
    public Task StopAsync(bool drain = true) => owner.StopAsync(drain);
    public void Dispose() => owner.Dispose();
}
