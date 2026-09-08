using System;
using DV.JObjectExtstensions;
using BDVM.Domain;
using Newtonsoft.Json.Linq;

namespace BDVM.Adapters;

public sealed class SaveGameDataCheckpointStore : IWorldCheckpointStore
{
    public const string RootKey = "BDVM";
    public const string EnvelopeKey = "IncrementCheckpoint";
    private readonly SaveGameData data;

    public SaveGameDataCheckpointStore(SaveGameData data) => this.data = data ?? throw new ArgumentNullException(nameof(data));

    public void Write(IncrementCheckpointEnvelope envelope)
    {
        var existing = data.GetJObject(RootKey);
        var root = existing == null ? new JObject() : (JObject)existing.DeepClone();
        root[EnvelopeKey] = IncrementCheckpointJson.Serialize(envelope);
        data.SetJObject(RootKey, root);
    }

    public IncrementCheckpointEnvelope? Read()
    {
        var token = data.GetJObject(RootKey)?[EnvelopeKey];
        return token == null || token.Type == JTokenType.Null ? null : IncrementCheckpointJson.Deserialize(token.Value<string>() ?? "");
    }
}
