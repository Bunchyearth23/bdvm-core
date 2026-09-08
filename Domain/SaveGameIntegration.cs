using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace BDVM.Domain;

public sealed class SaveGameFeatureFlags
{
    public bool EnableSaveGameDataHook { get; set; }
    public static SaveGameFeatureFlags SafeDefaults() => new SaveGameFeatureFlags { EnableSaveGameDataHook = false };
}

public sealed class CareerIdentityMaterial
{
    public string GameMode { get; set; } = "";
    public string StartingDifficulty { get; set; } = "";
    public string StartingTimeAndDate { get; set; } = "";
    public string Scenario { get; set; } = "";
    public string IdentitySource { get; set; } = "save-data";
}

public static class CareerCheckpointIdentity
{
    public static string SessionAnchor(string? gameMode, string? world, int sessionId, string? profileSignature)
    {
        if (string.IsNullOrWhiteSpace(gameMode) && string.IsNullOrWhiteSpace(world) && string.IsNullOrWhiteSpace(profileSignature)) return "";
        return "session-v1:" + Hash((gameMode ?? "") + "\n" + (world ?? "") + "\n" + sessionId + "\n" + (profileSignature ?? ""));
    }

    public static string Fingerprint(CareerIdentityMaterial material)
    {
        if (material == null || string.IsNullOrWhiteSpace(material.GameMode) || string.IsNullOrWhiteSpace(material.StartingDifficulty) || string.IsNullOrWhiteSpace(material.StartingTimeAndDate))
            throw new InvalidDataException("Real career identity is incomplete; SaveGameData integration is refused.");
        return Hash("career\n" + Canon(material.GameMode) + "\n" + Canon(material.StartingDifficulty) + "\n" + Canon(material.StartingTimeAndDate) + "\n" + Canon(material.Scenario));
    }
    public static string CheckpointId(string careerFingerprint, string branchToken)
    {
        if (!IsHash(careerFingerprint) || !Guid.TryParse(branchToken, out _)) throw new InvalidDataException("Career fingerprint or branch token is invalid.");
        return Hash("checkpoint\n" + careerFingerprint + "\n" + branchToken.ToLowerInvariant());
    }
    public static bool IsHash(string value) => value?.Length == 64 && value.All(Uri.IsHexDigit);
    internal static string Hash(string value) { using (var sha = SHA256.Create()) return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(value)).Select(x => x.ToString("x2"))); }
    private static string Canon(string value) => value.Trim().Replace("\r", "").Replace("\n", " ");
}

[DataContract]
public sealed class SaveGameIntegrationEnvelope
{
    public const string CurrentSchema = "bdvm.savegame-data";
    public const int CurrentVersion = 2;
    [DataMember(Order = 1)] public string Schema { get; set; } = CurrentSchema;
    [DataMember(Order = 2)] public int SchemaVersion { get; set; } = CurrentVersion;
    [DataMember(Order = 3)] public string CareerFingerprint { get; set; } = "";
    [DataMember(Order = 4)] public string BranchToken { get; set; } = "";
    [DataMember(Order = 5)] public string CheckpointId { get; set; } = "";
    [DataMember(Order = 6)] public string Payload { get; set; } = "";
    [DataMember(Order = 7)] public string PayloadSha256 { get; set; } = "";
    [DataMember(Order = 8)] public List<SaveGameRecoveryCopy> RecoveryCopies { get; set; } = new List<SaveGameRecoveryCopy>();
}

[DataContract]
public sealed class SaveGameRecoveryCopy
{
    [DataMember(Order = 1)] public int SourceVersion { get; set; }
    [DataMember(Order = 2)] public string SerializedEnvelope { get; set; } = "";
    [DataMember(Order = 3)] public string Sha256 { get; set; } = "";
}

public static class SaveGameIntegrationCodec
{
    private static readonly DataContractJsonSerializer Serializer = new DataContractJsonSerializer(typeof(SaveGameIntegrationEnvelope));
    public static string Serialize(SaveGameIntegrationEnvelope value) { using (var stream = new MemoryStream()) { Serializer.WriteObject(stream, value); return Encoding.UTF8.GetString(stream.ToArray()); } }
    public static SaveGameIntegrationEnvelope Deserialize(string json) { using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json ?? ""))) return Serializer.ReadObject(stream) as SaveGameIntegrationEnvelope ?? throw new InvalidDataException("Missing SaveGameData envelope."); }
}

public interface IAtomicSaveGameNode { string? Read(); void Replace(string value); }

public sealed class SaveGameDataPersistenceService
{
    private readonly IAtomicSaveGameNode node;
    private readonly Func<string> branchTokenFactory;
    public Action<string>? FailureInjection { get; set; }
    public SaveGameDataPersistenceService(IAtomicSaveGameNode node, Func<string>? branchTokenFactory = null) { this.node = node ?? throw new ArgumentNullException(nameof(node)); this.branchTokenFactory = branchTokenFactory ?? (() => Guid.NewGuid().ToString("D")); }
    public SaveGameIntegrationEnvelope Write(CareerIdentityMaterial identity, string payload) =>
        Write(identity, (_, __) => payload);
    public SaveGameIntegrationEnvelope Write(CareerIdentityMaterial identity, Func<string, string?, string> payloadFactory)
    {
        if (payloadFactory == null) throw new ArgumentNullException(nameof(payloadFactory));
        var fingerprint = CareerCheckpointIdentity.Fingerprint(identity); var original = node.Read();
        var envelope = original == null ? New(fingerprint) : LoadAndValidate(original, fingerprint);
        var payload = payloadFactory(envelope.CheckpointId, string.IsNullOrWhiteSpace(envelope.Payload) ? null : envelope.Payload);
        if (string.IsNullOrWhiteSpace(payload)) throw new InvalidDataException("Empty BDVM payload is forbidden.");
        envelope.Payload = payload; envelope.PayloadSha256 = CareerCheckpointIdentity.Hash(payload); Validate(envelope, fingerprint);
        FailureInjection?.Invoke("before-atomic-replace"); var serialized = SaveGameIntegrationCodec.Serialize(envelope);
        try { node.Replace(serialized); } catch { if (original != null) { try { node.Replace(original); } catch { } } throw; }
        return envelope;
    }
    public SaveGameIntegrationEnvelope Load(CareerIdentityMaterial identity) => LoadAndValidate(node.Read() ?? throw new InvalidDataException("BDVM state is absent from this save."), CareerCheckpointIdentity.Fingerprint(identity));
    public void RestoreRecoveryCopy(SaveGameIntegrationEnvelope envelope, int index)
    {
        if (envelope == null || envelope.RecoveryCopies == null || index < 0 || index >= envelope.RecoveryCopies.Count) throw new InvalidDataException("Recovery copy is absent.");
        var copy = envelope.RecoveryCopies[index];
        if (copy.Sha256 != CareerCheckpointIdentity.Hash(copy.SerializedEnvelope)) throw new InvalidDataException("Recovery copy is corrupt; restore refused.");
        var original = node.Read();
        try { node.Replace(copy.SerializedEnvelope); } catch { if (original != null) { try { node.Replace(original); } catch { } } throw; }
    }
    private SaveGameIntegrationEnvelope New(string fingerprint) { var branch = branchTokenFactory(); return new SaveGameIntegrationEnvelope { CareerFingerprint = fingerprint, BranchToken = branch, CheckpointId = CareerCheckpointIdentity.CheckpointId(fingerprint, branch) }; }
    private static SaveGameIntegrationEnvelope LoadAndValidate(string serialized, string fingerprint)
    {
        var envelope = SaveGameIntegrationCodec.Deserialize(serialized);
        if (envelope.SchemaVersion == 1) { envelope.SchemaVersion = 2; envelope.RecoveryCopies = envelope.RecoveryCopies ?? new List<SaveGameRecoveryCopy>(); envelope.RecoveryCopies.Add(new SaveGameRecoveryCopy { SourceVersion = 1, SerializedEnvelope = serialized, Sha256 = CareerCheckpointIdentity.Hash(serialized) }); }
        Validate(envelope, fingerprint); return envelope;
    }
    public static void Validate(SaveGameIntegrationEnvelope envelope, string expectedFingerprint)
    {
        if (envelope == null || envelope.Schema != SaveGameIntegrationEnvelope.CurrentSchema || envelope.SchemaVersion != SaveGameIntegrationEnvelope.CurrentVersion) throw new InvalidDataException("Unsupported SaveGameData schema or migration path.");
        if (envelope.CareerFingerprint != expectedFingerprint || envelope.CheckpointId != CareerCheckpointIdentity.CheckpointId(expectedFingerprint, envelope.BranchToken)) throw new InvalidDataException("Incompatible career, branch, or save; load refused.");
        if (string.IsNullOrWhiteSpace(envelope.Payload) || envelope.PayloadSha256 != CareerCheckpointIdentity.Hash(envelope.Payload)) throw new InvalidDataException("BDVM payload is corrupt; load refused.");
        foreach (var copy in envelope.RecoveryCopies ?? new List<SaveGameRecoveryCopy>()) if (copy.Sha256 != CareerCheckpointIdentity.Hash(copy.SerializedEnvelope)) throw new InvalidDataException("Recovery copy is corrupt; load refused.");
    }
}
