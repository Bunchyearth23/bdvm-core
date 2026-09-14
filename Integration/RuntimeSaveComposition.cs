using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using DV.JObjectExtstensions;
using BDVM.Domain;

namespace BDVM.Adapters;

public sealed class PersistentSaveTicket
{
    public string Id { get; }
    public string Payload { get; }
    internal PersistentSaveTicket(string id, string payload) { Id = id; Payload = payload; }
}

public interface IJournalSaveUpdateHandler : IHostSaveUpdateHandler, IDisposable
{
    void RecordPeriodic(string serializedDelta);
    PersistentSaveTicket? BeginSave(SaveGameData data);
    void CompleteSave(PersistentSaveTicket ticket, string physicalSaveId);
    void Pump();
    void ResetCareer();
}

public sealed class HostSaveGameUpdateHandler : IJournalSaveUpdateHandler
{
    private readonly IRuntimeStatePayloadProvider state;
    private readonly Action<string>? info;
    private readonly string? journalRoot;
    private readonly Action<string, Exception>? error;
    private readonly ConcurrentQueue<Exception> failures = new ConcurrentQueue<Exception>();
    private PersistentRuntimeJournal? journal;
    private string? lastEnvelope;
    private string? lastPayload;
    private bool reportedFailure;
    private int generation;
    private SaveGameData? activeData;
    private const string TicketKey = "PersistentJournalSave";

    public HostSaveGameUpdateHandler(IRuntimeStatePayloadProvider state, Action<string>? info = null, string? journalRoot = null, Action<string, Exception>? error = null)
    {
        this.state = state ?? throw new ArgumentNullException(nameof(state));
        this.info = info;
        this.journalRoot = journalRoot;
        this.error = error;
    }

    public void Update(SaveGameData data)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        if (activeData != null && !ReferenceEquals(activeData, data)) ResetCareer();
        activeData = data;
        var identity = SaveGameCareerIdentityReader.Read(data);
        info?.Invoke("Career identity resolved; source=" + identity.IdentitySource + ", gameModePresent=" + !string.IsNullOrWhiteSpace(identity.GameMode) + ", difficultyPresent=" + !string.IsNullOrWhiteSpace(identity.StartingDifficulty) + ", stableAnchorPresent=" + !string.IsNullOrWhiteSpace(identity.StartingTimeAndDate) + ".");
        var persistence = new SaveGameDataPersistenceService(new SaveGameDataAtomicNode(data));
        var envelope = persistence.Write(identity, state.Provide);
        lastEnvelope = new SaveGameDataAtomicNode(data).Read();
        lastPayload = envelope.Payload;
        if (journalRoot != null)
        {
            if (journal == null)
            {
                var root = data.GetJObject(SaveGameDataAtomicNode.RootKey);
                var savedTicket = (string?)root?[TicketKey];
                journal = new PersistentRuntimeJournal(journalRoot, envelope.CheckpointId, envelope.Payload, savedTicket);
                Observe(journal.Completion);
            }
            else if (!reportedFailure) Observe(journal.ObserveSnapshotAsync("observed:" + Guid.NewGuid().ToString("N"), envelope.Payload));
        }
        info?.Invoke("BDVM state staged in SaveGameData; checkpoint=" + envelope.CheckpointId + ", schema=" + envelope.SchemaVersion);
    }

    public void RecordPeriodic(string serializedDelta)
    { if (journal != null && !reportedFailure) Observe(journal.ObservePeriodicAsync(serializedDelta)); }
    public PersistentSaveTicket? BeginSave(SaveGameData data)
    {
        if (journal == null || reportedFailure || !failures.IsEmpty)
        {
            // A new portable recovery save must not retain a ticket pointing at
            // an earlier payload after journal failure or feature shutdown.
            var prior = data.GetJObject(SaveGameDataAtomicNode.RootKey);
            if (prior?[TicketKey] != null)
            {
                var portable = (Newtonsoft.Json.Linq.JObject)prior.DeepClone();
                portable.Remove(TicketKey); data.SetJObject(SaveGameDataAtomicNode.RootKey, portable);
            }
            return null;
        }
        var current = new SaveGameDataAtomicNode(data).Read();
        var payload = current == lastEnvelope ? lastPayload : SaveGameIntegrationCodec.Deserialize(current ?? throw new InvalidOperationException("Missing save envelope.")).Payload;
        if (string.IsNullOrWhiteSpace(payload)) throw new InvalidOperationException("Missing save payload.");
        var ticket = new PersistentSaveTicket(Guid.NewGuid().ToString("N"), payload!);
        var existing = data.GetJObject(SaveGameDataAtomicNode.RootKey);
        var root = existing == null ? new Newtonsoft.Json.Linq.JObject() : (Newtonsoft.Json.Linq.JObject)existing.DeepClone();
        root[TicketKey] = ticket.Id;
        data.SetJObject(SaveGameDataAtomicNode.RootKey, root);
        return ticket;
    }
    public void CompleteSave(PersistentSaveTicket ticket, string physicalSaveId)
    { if (journal != null && !reportedFailure) Observe(journal.CheckpointSaveAsync(ticket.Id, physicalSaveId, ticket.Payload)); }
    private void Observe(Task task)
    {
        var observedGeneration = System.Threading.Volatile.Read(ref generation);
        _ = task.ContinueWith(completed => {
            var exception = completed.Exception!.GetBaseException();
            if (observedGeneration == System.Threading.Volatile.Read(ref generation)) failures.Enqueue(exception);
        }, System.Threading.CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    public void Pump()
    {
        while (failures.TryDequeue(out var failure))
        {
            if (!reportedFailure)
            {
                error?.Invoke("Persistent journal stopped or fell behind. Make a new game save to preserve the complete portable recovery payload, then reload to restore journal tracking.", failure);
                if (journal != null) Observe(journal.StopAsync(false));
            }
            reportedFailure = true;
        }
    }
    public void Dispose() { journal?.Dispose(); journal = null; }
    public void ResetCareer()
    {
        System.Threading.Interlocked.Increment(ref generation);
        Dispose(); (state as AcquisitionRuntimeStateProvider)?.ResetForLoad();
        lastEnvelope = null; lastPayload = null; activeData = null; reportedFailure = false;
        while (failures.TryDequeue(out _)) { }
    }
}
