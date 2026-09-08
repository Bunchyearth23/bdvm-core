using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using BDVM.Common;

namespace BDVM.Core;

public sealed class ModuleStateStore
{
    private readonly BdvmCheckpointEnvelope envelope;

    public ModuleStateStore(string checkpointId)
    {
        if (string.IsNullOrWhiteSpace(checkpointId)) throw new ArgumentException("Checkpoint identity is required.", nameof(checkpointId));
        envelope = new BdvmCheckpointEnvelope { CheckpointId = checkpointId };
    }

    private ModuleStateStore(BdvmCheckpointEnvelope envelope) => this.envelope = envelope;

    public string CheckpointId => envelope.CheckpointId;
    public void Put(string moduleId, string schema, int schemaVersion, string payload)
    {
        if (string.IsNullOrWhiteSpace(moduleId) || string.IsNullOrWhiteSpace(schema) || schemaVersion <= 0 || payload == null)
            throw new ArgumentException("A complete versioned module payload is required.");
        var current = envelope.Modules.SingleOrDefault(x => string.Equals(x.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase));
        if (current == null)
        {
            envelope.Modules.Add(new BdvmModulePayload { ModuleId = moduleId, Schema = schema, SchemaVersion = schemaVersion, Payload = payload });
            return;
        }
        current.Schema = schema;
        current.SchemaVersion = schemaVersion;
        current.Payload = payload;
    }

    public bool TryGet(string moduleId, out BdvmModulePayload payload)
    {
        payload = envelope.Modules.SingleOrDefault(x => string.Equals(x.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase))!;
        return payload != null;
    }

    public string Serialize()
    {
        Validate(envelope);
        using (var stream = new MemoryStream())
        {
            new DataContractJsonSerializer(typeof(BdvmCheckpointEnvelope)).WriteObject(stream, envelope);
            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    public static ModuleStateStore Deserialize(string json)
    {
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json ?? "")))
        {
            var value = new DataContractJsonSerializer(typeof(BdvmCheckpointEnvelope)).ReadObject(stream) as BdvmCheckpointEnvelope
                ?? throw new InvalidDataException("Missing BDVM checkpoint.");
            Validate(value);
            return new ModuleStateStore(value);
        }
    }

    private static void Validate(BdvmCheckpointEnvelope value)
    {
        if (value.Schema != BdvmCheckpointEnvelope.CurrentSchema || value.SchemaVersion != BdvmCheckpointEnvelope.CurrentVersion || string.IsNullOrWhiteSpace(value.CheckpointId))
            throw new InvalidDataException("Unsupported BDVM checkpoint.");
        if (value.Modules.Any(x => string.IsNullOrWhiteSpace(x.ModuleId) || string.IsNullOrWhiteSpace(x.Schema) || x.SchemaVersion <= 0 || x.Payload == null) ||
            value.Modules.GroupBy(x => x.ModuleId, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() != 1))
            throw new InvalidDataException("Invalid or duplicate BDVM module payload.");
    }
}
