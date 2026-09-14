using System;
using DV.Common;
using DV.JObjectExtstensions;
using DV.UserManagement;
using BDVM.Domain;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BDVM.Adapters;

public interface IHostSaveUpdateHandler { void Update(SaveGameData data); }
public static class SaveGameRuntimeHook
{
    private static SaveGameFeatureFlags flags = SaveGameFeatureFlags.SafeDefaults();
    private static INetworkRoleDetector? roles;
    private static IHostSaveUpdateHandler? handler;
    private static Action<string>? info;
    private static Action<string, Exception>? error;
    private static readonly SaveGameAutomaticUpdateGate automaticUpdateGate = new();
    private static bool automaticUpdateRequired = true;
    private static long mutationVersion;
    public static long MutationVersion => System.Threading.Interlocked.Read(ref mutationVersion);
    public static bool Enabled => flags.EnableSaveGameDataHook;
    public static void Configure(SaveGameFeatureFlags configuredFlags, INetworkRoleDetector roleDetector, IHostSaveUpdateHandler updateHandler, Action<string>? information = null, Action<string, Exception>? failure = null) { (handler as IDisposable)?.Dispose(); flags = configuredFlags ?? throw new ArgumentNullException(nameof(configuredFlags)); roles = roleDetector ?? throw new ArgumentNullException(nameof(roleDetector)); handler = updateHandler ?? throw new ArgumentNullException(nameof(updateHandler)); info = information; error = failure; automaticUpdateGate.Reset(); automaticUpdateRequired = true; }
    public static void Reset() { (handler as IDisposable)?.Dispose(); flags = SaveGameFeatureFlags.SafeDefaults(); roles = null; handler = null; info = null; error = null; automaticUpdateGate.Reset(); automaticUpdateRequired = true; }
    public static void RecordPeriodic(string serializedDelta) => (handler as IJournalSaveUpdateHandler)?.RecordPeriodic(serializedDelta);
    public static void PumpJournal() => (handler as IJournalSaveUpdateHandler)?.Pump();
    public static void ResetCareer()
    {
        (handler as IJournalSaveUpdateHandler)?.ResetCareer();
        MarkDirty(); automaticUpdateGate.Reset();
    }
    internal static PersistentSaveTicket? BeginPhysicalSave(SaveGameData data)
    {
        if (!Enabled || roles == null || !NetworkAuthorityPolicy.CanExecuteEconomy(roles.Detect(), out _)) return null;
        try { return (handler as IJournalSaveUpdateHandler)?.BeginSave(data); }
        catch (Exception exception) { error?.Invoke("Journal save ticket could not be attached; the embedded BDVM recovery payload remains in the game save.", exception); return null; }
    }
    internal static void CompletePhysicalSave(PersistentSaveTicket? ticket, ISaveGame? save)
    {
        if (ticket == null || save == null) return;
        try { (handler as IJournalSaveUpdateHandler)?.CompleteSave(ticket, save.BasePath); }
        catch (Exception exception) { error?.Invoke("Game save completed but its journal checkpoint could not be scheduled.", exception); }
    }
    public static void MarkDirty() { System.Threading.Interlocked.Increment(ref mutationVersion); if (Enabled) automaticUpdateRequired = true; }
    public static void OnUpdateInternalData(SaveGameManager manager)
    {
        if (!Enabled) return;
        if (manager?.data == null || roles == null || handler == null) throw new InvalidOperationException("Save hook is enabled but not completely configured.");
        if (!NetworkAuthorityPolicy.CanExecuteEconomy(roles.Detect(), out var reason)) throw new InvalidOperationException("BDVM SaveGameData write refused: " + reason);
        System.Threading.Interlocked.Increment(ref mutationVersion);
        handler.Update(manager.data);
    }
    public static bool TryOnUpdateInternalData(SaveGameManager manager)
    {
        try { OnUpdateInternalData(manager); if (Enabled && manager?.data != null) { automaticUpdateGate.MarkStaged(manager, manager.data); automaticUpdateRequired = false; } if (Enabled) info?.Invoke("Authoritative SaveGameData update completed."); return true; }
        catch (Exception exception) { error?.Invoke("BDVM SaveGameData update refused; vanilla save continues.", exception); return false; }
    }

    internal static bool TryOnAutomaticUpdateInternalData(SaveGameManager manager)
    {
        if (Enabled && !automaticUpdateRequired && manager?.data != null && automaticUpdateGate.ShouldSkip(manager, manager.data)) return true;
        return TryOnUpdateInternalData(manager!);
    }
}
[HarmonyPatch(typeof(SaveGameManager), "UpdateInternalData")]
internal static class SaveGameManagerUpdateInternalDataPatch { private static void Postfix(SaveGameManager __instance) => SaveGameRuntimeHook.TryOnAutomaticUpdateInternalData(__instance); }

// Save() and autosave both call DoSaveIO. Its non-null result confirms the game
// write, unlike UpdateInternalData. Only immutable strings cross to the worker.
[HarmonyPatch(typeof(SaveGameManager), "DoSaveIO")]
internal static class SaveGameManagerJournalCheckpointPatch
{
    private static void Prefix(SaveGameData __0, out PersistentSaveTicket? __state) => __state = SaveGameRuntimeHook.BeginPhysicalSave(__0);
    private static void Postfix(ISaveGame? __result, PersistentSaveTicket? __state) => SaveGameRuntimeHook.CompletePhysicalSave(__state, __result);
}

public sealed class SaveGameDataAtomicNode : IAtomicSaveGameNode
{
    public const string RootKey = "BDVM"; public const string EnvelopeKey = "SaveGameIntegration"; private readonly SaveGameData data;
    public SaveGameDataAtomicNode(SaveGameData data) => this.data = data ?? throw new ArgumentNullException(nameof(data));
    public string? Read() => data.GetJObject(RootKey)?[EnvelopeKey]?.Value<string>();
    public void Replace(string value) { var existing = data.GetJObject(RootKey); var root = existing == null ? new JObject() : (JObject)existing.DeepClone(); root[EnvelopeKey] = value; data.SetJObject(RootKey, root); }
}

public static class SaveGameCareerIdentityReader
{
    public static CareerIdentityMaterial Read(SaveGameData data)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        var root = data.GetJsonObject() ?? throw new InvalidOperationException("SaveGameData JSON root is unavailable.");
        var session = UserManager.Instance?.CurrentUser?.CurrentSession;
        return ReadFromSources(root, session);
    }

    public static CareerIdentityMaterial ReadFromSources(JObject root, IGameSession? session)
        => ReadFromSources(root, session?.GameMode, session?.World, session?.SessionID ?? 0, session?.Owner?.Signature, session?.GameData);

    public static CareerIdentityMaterial ReadFromSources(JObject root, string? sessionMode, string? sessionWorld, int sessionId, string? ownerSignature, JObject? sessionData)
    {
        if (root == null) throw new ArgumentNullException(nameof(root));
        var sessionAnchor = StableSessionAnchor(sessionMode, sessionWorld, sessionId, ownerSignature);
        var storedStart = CanonicalValue(root[SaveGameKeys.Starting_time_and_date]);
        return new CareerIdentityMaterial
        {
            GameMode = First(CanonicalValue(root[SaveGameKeys.Game_mode]), sessionMode),
            StartingDifficulty = First(CanonicalValue(root[SaveGameKeys.Starting_difficulty]), CanonicalValue(sessionData?[SaveGameKeys.Starting_difficulty]), CanonicalValue(sessionData?[SaveGameKeys.Difficulty_params])),
            StartingTimeAndDate = First(sessionAnchor, storedStart),
            Scenario = First(CanonicalValue(root[SaveGameKeys.Scenario]), sessionWorld),
            IdentitySource = string.IsNullOrWhiteSpace(sessionAnchor) ? "save-data" : "current-session-metadata-v1"
        };
    }

    private static string StableSessionAnchor(string? sessionMode, string? sessionWorld, int sessionId, string? ownerSignature)
        => CareerCheckpointIdentity.SessionAnchor(sessionMode, sessionWorld, sessionId, ownerSignature);

    private static string First(params string?[] values)
    {
        foreach (var value in values)
            if (!string.IsNullOrWhiteSpace(value)) return value!.Trim();
        return "";
    }

    public static string CanonicalValue(JToken? token)
    {
        if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined) return "";
        return token.Type == JTokenType.String
            ? token.Value<string>()?.Trim() ?? ""
            : token.ToString(Formatting.None);
    }
}
