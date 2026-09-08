using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace BDVM.Domain;

[DataContract]
public sealed class IncrementCheckpointEnvelope
{
    public const string CurrentSchema = "bdvm.increment-checkpoint";
    public const int CurrentVersion = 1;

    [DataMember(Name = "schema", Order = 1)] public string Schema { get; set; } = CurrentSchema;
    [DataMember(Name = "schemaVersion", Order = 2)] public int SchemaVersion { get; set; } = CurrentVersion;
    [DataMember(Name = "checkpointId", Order = 3)] public string CheckpointId { get; set; } = "";
    [DataMember(Name = "acquisitionPayload", Order = 4)] public string AcquisitionPayload { get; set; } = "";
    [DataMember(Name = "payloadSha256", Order = 5)] public string PayloadSha256 { get; set; } = "";
}

public interface IWorldCheckpointStore
{
    void Write(IncrementCheckpointEnvelope envelope);
    IncrementCheckpointEnvelope? Read();
}

public sealed class IncrementCheckpointService : IAcquisitionCheckpointSink
{
    private readonly IWorldCheckpointStore store;

    public IncrementCheckpointService(IWorldCheckpointStore store) => this.store = store ?? throw new ArgumentNullException(nameof(store));

    public void Save(string checkpointId, VehicleAcquisitionSnapshot snapshot, AcquisitionState stage)
    {
        if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
        if (!string.Equals(checkpointId, snapshot.CheckpointId, StringComparison.Ordinal))
            throw new InvalidDataException("The acquisition state and world checkpoint must have the same identity.");
        var payload = VehicleAcquisitionPersistence.Serialize(snapshot);
        store.Write(new IncrementCheckpointEnvelope
        {
            CheckpointId = checkpointId,
            AcquisitionPayload = payload,
            PayloadSha256 = Sha256(payload)
        });
    }

    public VehicleAcquisitionSnapshot Restore(string expectedCheckpointId)
    {
        var envelope = store.Read() ?? throw new InvalidDataException("BDVM state is absent from this world checkpoint.");
        Validate(envelope, expectedCheckpointId);
        return VehicleAcquisitionPersistence.Deserialize(envelope.AcquisitionPayload, expectedCheckpointId);
    }

    public IReadOnlyList<AcquisitionRecord> ReconcilePending(VehicleAcquisitionEngine engine)
    {
        if (engine == null) throw new ArgumentNullException(nameof(engine));
        var pending = engine.State.Acquisitions
            .Where(x => x.State != AcquisitionState.Succeeded && x.State != AcquisitionState.Compensated && x.State != AcquisitionState.Rejected)
            .Select(x => x.CommandId).ToArray();
        return pending.Select(engine.Reconcile).ToArray();
    }

    public static void Validate(IncrementCheckpointEnvelope envelope, string expectedCheckpointId)
    {
        if (envelope == null || envelope.Schema != IncrementCheckpointEnvelope.CurrentSchema ||
            envelope.SchemaVersion != IncrementCheckpointEnvelope.CurrentVersion)
            throw new InvalidDataException("Unsupported BDVM checkpoint envelope.");
        if (string.IsNullOrWhiteSpace(expectedCheckpointId) || envelope.CheckpointId != expectedCheckpointId)
            throw new InvalidDataException("Checkpoint mismatch; state from another save or branch is forbidden.");
        if (string.IsNullOrWhiteSpace(envelope.AcquisitionPayload) ||
            !string.Equals(envelope.PayloadSha256, Sha256(envelope.AcquisitionPayload), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("BDVM checkpoint payload is missing or corrupted.");
    }

    private static string Sha256(string value)
    {
        using (var hash = SHA256.Create())
            return string.Concat(hash.ComputeHash(Encoding.UTF8.GetBytes(value)).Select(x => x.ToString("x2")));
    }
}

public static class IncrementCheckpointJson
{
    private static readonly DataContractJsonSerializer Serializer = new DataContractJsonSerializer(typeof(IncrementCheckpointEnvelope));
    public static string Serialize(IncrementCheckpointEnvelope value)
    {
        using (var stream = new MemoryStream()) { Serializer.WriteObject(stream, value); return Encoding.UTF8.GetString(stream.ToArray()); }
    }
    public static IncrementCheckpointEnvelope Deserialize(string json)
    {
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json ?? "")))
            return Serializer.ReadObject(stream) as IncrementCheckpointEnvelope ?? throw new InvalidDataException("Missing checkpoint envelope.");
    }
}

public enum MaintenanceAction { Inspect, Service, Repair, Refuel }

public sealed class ManualMaintenanceRequest
{
    public string CommandId { get; set; } = "";
    public string RequesterId { get; set; } = "";
    public string AssetId { get; set; } = "";
    public MaintenanceAction Action { get; set; }
    public AccountRef? Payer { get; set; }
    public long MaximumAuthorizedCost { get; set; }
    public bool ExplicitUserConfirmation { get; set; }
}

public static class ManualMaintenancePolicy
{
    public static bool Validate(ManualMaintenanceRequest? request, out string reason)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.CommandId) || string.IsNullOrWhiteSpace(request.RequesterId) || string.IsNullOrWhiteSpace(request.AssetId))
            return Fail("complete-manual-request-required", out reason);
        if (!request.ExplicitUserConfirmation) return Fail("automatic-maintenance-forbidden", out reason);
        if (request.Payer == null || string.IsNullOrWhiteSpace(request.Payer.OwnerId)) return Fail("explicit-payer-required", out reason);
        if (request.MaximumAuthorizedCost < 0) return Fail("invalid-maximum-cost", out reason);
        reason = "manual-explicit-account";
        return true;
    }

    private static bool Fail(string value, out string reason) { reason = value; return false; }
}
