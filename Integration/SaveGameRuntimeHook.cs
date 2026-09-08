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
    public static bool Enabled => flags.EnableSaveGameDataHook;
    public static void Configure(SaveGameFeatureFlags configuredFlags, INetworkRoleDetector roleDetector, IHostSaveUpdateHandler updateHandler, Action<string>? information = null, Action<string, Exception>? failure = null) { flags = configuredFlags ?? throw new ArgumentNullException(nameof(configuredFlags)); roles = roleDetector ?? throw new ArgumentNullException(nameof(roleDetector)); handler = updateHandler ?? throw new ArgumentNullException(nameof(updateHandler)); info = information; error = failure; }
    public static void Reset() { flags = SaveGameFeatureFlags.SafeDefaults(); roles = null; handler = null; info = null; error = null; }
    public static void OnUpdateInternalData(SaveGameManager manager)
    {
        if (!Enabled) return;
        if (manager?.data == null || roles == null || handler == null) throw new InvalidOperationException("Save hook is enabled but not completely configured.");
        if (!NetworkAuthorityPolicy.CanExecuteEconomy(roles.Detect(), out var reason)) throw new InvalidOperationException("BDVM SaveGameData write refused: " + reason);
        handler.Update(manager.data);
    }
    public static bool TryOnUpdateInternalData(SaveGameManager manager)
    {
        try { OnUpdateInternalData(manager); if (Enabled) info?.Invoke("Authoritative SaveGameData update completed."); return true; }
        catch (Exception exception) { error?.Invoke("BDVM SaveGameData update refused; vanilla save continues.", exception); return false; }
    }
}
[HarmonyPatch(typeof(SaveGameManager), "UpdateInternalData")]
internal static class SaveGameManagerUpdateInternalDataPatch { private static void Postfix(SaveGameManager __instance) => SaveGameRuntimeHook.TryOnUpdateInternalData(__instance); }

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
